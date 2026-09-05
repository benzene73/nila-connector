using System.Globalization;
using System.Text;
using NilaConnector.Core.Models;
using TallyConnector.Core.Exceptions;
using TallyConnector.Core.Models.Request;
using TallyConnector.Core.Models.Response;
using TallyConnector.Models.Common;
using TallyConnector.Models.TallyPrime.V7;
using TallyConnector.Models.TallyPrime.V7.Masters;
using TallyConnector.Models.TallyPrime.V7.Masters.Inventory;
using TallyConnector.Services;
using TallyConnector.Services.TallyPrime.V7;
using MailingDetail = TallyConnector.Models.Base.Masters.MailingDetail;
using TallyLedger = TallyConnector.Models.TallyPrime.V7.Masters.Ledger;

namespace NilaConnector.Core.Tally;

/// <summary>
/// Adapts Accounting-Companion's typed Tally models to Nila's stable sync contract.
/// Voucher writes deliberately remain in <see cref="TallyClient"/>.
/// </summary>
public sealed class TallyConnectorReadClient : ITallyReadClient, ITallyMasterClient, IDisposable
{
    private readonly TallyClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly BaseTallyService _baseService;
    private readonly TallyPrimeService _service;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    public TallyConnectorReadClient(TallyClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = new HttpClient(new BoundedResponseHandler(options.MaxResponseBytes));
        _baseService = new BaseTallyService(_httpClient);
        _service = new TallyPrimeService(_baseService);
    }

    public async Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async token =>
        {
            var companies = await _service.GetCompaniesAsync(token);
            var result = new List<TallyCompany>(companies.Count);

            foreach (var company in companies)
            {
                var name = Clean(company.Name);
                if (name is null)
                {
                    continue;
                }

                var request = new RequestOptions().SetCompany(name);
                var registrations = await _service.GetObjectsAsync<GSTRegistration>(request, token);
                result.Add(new TallyCompany(
                    name,
                    Clean(company.GUID),
                    Clean(registrations.Select(registration => registration.GSTIN)
                        .FirstOrDefault(gstin => !string.IsNullOrWhiteSpace(gstin)))));
            }

            return result
                .DistinctBy(
                    company => (company.Name.ToUpperInvariant(), company.TallyGuid?.ToUpperInvariant()))
                .OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);

    public async Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
        string company,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);

        return await ExecuteAsync(async token =>
        {
            var request = new RequestOptions().SetCompany(company);
            var ledgers = await _service.GetLedgersAsync(request, token);
            var voucherTypes = await _service.GetVoucherTypesAsync(request, token);
            var stockItems = await _service.GetStockItemsAsync(request, token);
            var numberingMethods = await GetVoucherNumberingMethodsAsync(company, token);

            return ledgers.Select(MapLedger)
                .Concat(voucherTypes.Select(voucherType =>
                    MapVoucherType(
                        voucherType,
                        numberingMethods.GetValueOrDefault(voucherType.Name))))
                .Concat(stockItems.Select(MapStockItem))
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
        string company,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        if (toDate < fromDate)
        {
            throw new ArgumentException("The voucher end date cannot precede the start date.", nameof(toDate));
        }

        return await ExecuteAsync(async token =>
        {
            var request = new RequestOptions
            {
                Company = company,
                FromDate = fromDate.ToDateTime(TimeOnly.MinValue),
                ToDate = toDate.ToDateTime(TimeOnly.MinValue),
            };
            var vouchers = await _service.GetVouchersAsync(request, token);
            return vouchers.Select(MapVoucher).ToArray();
        }, cancellationToken);
    }

    public async Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
        string company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterId);

        return await ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new StringContent(
                    TallyRequests.VoucherFingerprint(company, voucherDate, masterId),
                    Encoding.UTF8,
                    "text/xml"),
            };
            using var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await TallyResponseParser.ParseVoucherFingerprintAsync(
                stream,
                _options.MaxResponseBytes,
                token);
        }, cancellationToken);
    }

    public async Task<TallyMasterWriteResult> CreateLedgerAsync(
        CreateLedgerCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await ExecuteAsync(async token =>
        {
            var responses = await _service.PostObjectsAsyncNew(
                [BuildLedger(command)],
                new PostRequestOptions
                {
                    Company = command.Company,
                    StopatFirstError = true,
                },
                token);

            return ParseLedgerResponse(command, responses);
        }, cancellationToken);
    }

    internal static TallyMasterWriteResult ParseLedgerResponse(
        CreateLedgerCommand command,
        IReadOnlyList<PostResponse> responses)
    {
        if (responses.Count != 1)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned {responses.Count} results after creating one ledger.");
        }

        var response = responses[0];
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally rejected ledger '{command.Name}': {response.Error.Trim()}");
        }

        if (!string.Equals(response.ObjectType, "Ledger", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.Name, command.Name, StringComparison.OrdinalIgnoreCase) ||
            response.MasterId is null or 0 ||
            response.MasterId > long.MaxValue)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally did not identify the ledger it created.");
        }

        return new TallyMasterWriteResult(
            response.ObjectType,
            response.Name!,
            checked((long)response.MasterId.Value),
            Clean(response.GUID),
            Clean(response.RemoteId));
    }

    public async Task<VoucherInspection?> GetVoucherInspectionAsync(
        string company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterId);

        return await ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new StringContent(
                    TallyRequests.VoucherInspection(company, voucherDate, masterId),
                    Encoding.UTF8,
                    "text/xml"),
            };
            using var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var inspection = await TallyResponseParser.ParseVoucherInspectionAsync(
                stream,
                _options.MaxResponseBytes,
                token);
            // Object export can ignore the period variables: never leak a different date/ID.
            if (inspection is not null && inspection.MasterId != masterId)
                throw new InvalidDataException("Tally returned a different voucher MasterID.");
            return inspection?.Date == voucherDate ? inspection : null;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _requestLock.Dispose();
        _httpClient.Dispose();
        _disposed = true;
    }

    private async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken);

        try
        {
            ConfigureEndpoint();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            return await action(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TallyClientException(TallyErrorKind.Timeout, "Tally did not respond in time.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TallyClientException(TallyErrorKind.Unreachable, "Tally is not reachable.", exception);
        }
        catch (TallyResponseTooLargeException exception)
        {
            throw new TallyClientException(
                TallyErrorKind.ResponseTooLarge,
                $"Tally returned more than {_options.MaxResponseBytes} bytes.",
                exception);
        }
        catch (TallyConnectivityException exception)
        {
            throw new TallyClientException(
                TallyErrorKind.Unreachable,
                "Tally is not reachable.",
                exception);
        }
        catch (TallyClientException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally returned data that the connector could not read.",
                exception);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private void ConfigureEndpoint()
    {
        var endpoint = _options.Endpoint;
        var baseUrl = $"{endpoint.Scheme}://{endpoint.Host}";
        _baseService.Setup(baseUrl, endpoint.Port);
    }

    internal static TallyLedger BuildLedger(CreateLedgerCommand command)
    {
        var applicableFrom = command.ApplicableFrom.ToDateTime(TimeOnly.MinValue);

        return new TallyLedger
        {
            Name = command.Name,
            Group = command.Parent,
            IsBillWise = true,
            MailingDetails =
            [
                new MailingDetail
                {
                    ApplicableFrom = applicableFrom,
                    MailingName = command.Name,
                    Country = command.Country,
                    State = command.State,
                    AdressLines = [],
                },
            ],
            GSTRegistrationDetails =
            [
                new LedgerGSTRegistrationDetail
                {
                    ApplicableFrom = applicableFrom,
                    GSTRegistrationType = RegistrationType(command.RegistrationType),
                    State = command.State,
                    PlaceOfSupply = command.State,
                    GSTIN = command.Gstin,
                    IsOtherTerritoryAssesse = false,
                    ConsiderPurchaseForExport = false,
                    IsTransporter = false,
                    IsCommonParty = false,
                },
            ],
        };
    }

    private static GSTRegistrationType RegistrationType(string value) => value switch
    {
        "Regular" => GSTRegistrationType.Regular,
        "Unregistered" => GSTRegistrationType.Unregistered,
        _ => throw new InvalidDataException($"Unsupported GST registration type '{value}'."),
    };

    internal static TallyMaster MapLedger(Ledger ledger) =>
        Master(
            "ledger",
            ledger.Name,
            ledger.GUID,
            ledger.AlterId,
            new Dictionary<string, object?>
            {
                ["parent"] = Clean(ledger.Group),
                ["bill_wise"] = YesNo(ledger.IsBillWise),
                ["numbering_method"] = null,
                ["is_active"] = null,
                ["base_units"] = null,
                ["currency"] = Clean(ledger.Currency),
                ["tax_type"] = ledger.TaxType?.ToString(),
                ["gst_duty_head"] = Clean(ledger.GSTDutyHead?.ToString()),
                ["gst_type"] = ledger.GSTTaxType?.ToString(),
                ["gst_type_of_supply"] = ledger.GSTTypeOfSupply?.ToString(),
                ["gstin"] = Clean(ledger.GSTRegistrationDetails?.FirstOrDefault()?.GSTIN),
                ["state"] = Clean(ledger.GSTRegistrationDetails?.FirstOrDefault()?.State),
                ["rate_of_tax"] = ledger.RateofTax,
                ["pan"] = Clean(ledger.PANNumber),
                ["bank_name"] = Clean(ledger.BankName),
                ["account_number"] = Clean(ledger.AccountNumber),
                ["ifsc"] = Clean(ledger.IFSCODE),
            });

    private async Task<IReadOnlyDictionary<string, string>> GetVoucherNumberingMethodsAsync(
        string company,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(
                TallyRequests.VoucherNumberingMethods(company),
                Encoding.UTF8,
                "text/xml"),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await TallyResponseParser.ParseVoucherNumberingMethodsAsync(
            stream,
            _options.MaxResponseBytes,
            cancellationToken);
    }

    private static TallyMaster MapVoucherType(
        VoucherType voucherType,
        string? seriesNumberingMethod) =>
        Master(
            "voucher_type",
            voucherType.Name,
            voucherType.GUID,
            voucherType.AlterId,
            new Dictionary<string, object?>
            {
                ["parent"] = Clean(voucherType.Parent),
                ["bill_wise"] = null,
                // TallyPrime 7 stores the effective value on VOUCHERNUMBERSERIES.LIST.
                // Its legacy top-level NUMBERINGMETHOD can misleadingly remain "None".
                ["numbering_method"] = Clean(seriesNumberingMethod) ?? Clean(voucherType.NumberingMethod),
                ["is_active"] = YesNo(voucherType.IsActive),
                ["base_units"] = null,
                ["core_voucher_type"] = voucherType.CoreVoucherType?.ToString(),
                ["affects_stock"] = YesNo(voucherType.EffectStock),
            });

    private static TallyMaster MapStockItem(StockItem stockItem) =>
        Master(
            "stock_item",
            stockItem.Name,
            stockItem.GUID,
            stockItem.AlterId,
            new Dictionary<string, object?>
            {
                ["parent"] = Clean(stockItem.StockGroup),
                ["bill_wise"] = null,
                ["numbering_method"] = null,
                ["is_active"] = null,
                ["base_units"] = Clean(stockItem.BaseUnit),
                ["stock_category"] = Clean(stockItem.StockCategory),
                ["description"] = Clean(stockItem.Description),
                ["gst_applicable"] = Clean(stockItem.GSTApplicable),
                ["gst_type_of_supply"] = Clean(stockItem.GSTTypeOfSupply),
                ["additional_units"] = Clean(stockItem.AdditionalUnits),
            });

    private static TallyMaster Master(
        string kind,
        string name,
        string? guid,
        ulong alterId,
        IReadOnlyDictionary<string, object?> data)
    {
        var cleanName = name.Trim();
        var cleanGuid = Clean(guid);
        var identity = cleanGuid is null
            ? $"name:{cleanName.ToLowerInvariant()}"
            : $"guid:{cleanGuid.ToLowerInvariant()}";

        return new TallyMaster(
            kind,
            identity,
            cleanName,
            cleanGuid,
            alterId == 0 ? null : alterId.ToString(CultureInfo.InvariantCulture),
            data);
    }

    private static TallySyncedVoucher MapVoucher(Voucher voucher)
    {
        var guid = Clean(voucher.GUID);
        var remoteAltGuid = Clean(voucher.RemoteId);
        var masterId = voucher.MasterId == 0 || voucher.MasterId > long.MaxValue
            ? (long?)null
            : (long)voucher.MasterId;
        var voucherNumber = Clean(voucher.VoucherNumber);
        var date = DateOnly.FromDateTime(voucher.Date);
        var voucherType = voucher.VoucherType.Trim();
        var identity = guid is not null
            ? $"guid:{guid.ToLowerInvariant()}"
            : masterId is not null
                ? $"master:{masterId}"
                : $"voucher:{date:yyyyMMdd}:{voucherType.ToLowerInvariant()}:{voucherNumber?.ToLowerInvariant()}";

        var ledgerEntries = voucher.LedgerEntries.Select(entry => (object)new Dictionary<string, object?>
        {
            ["ledger_name"] = Clean(entry.LedgerName),
            ["amount"] = entry.Amount?.ToString(),
            ["is_deemed_positive"] = YesNo(entry.IsDeemedPositive),
            ["is_party_ledger"] = YesNo(entry.IsPartyLedger),
        }).ToArray();

        var lineItems = voucher.InventoryAllocations.Select(entry => (object)new Dictionary<string, object?>
        {
            ["stock_item_name"] = Clean(entry.StockItemName),
            ["description"] = entry.UserDescriptions?.FirstOrDefault(),
            ["amount"] = entry.Amount?.ToString(),
            ["quantity"] = entry.BilledQuantity?.ToString(),
            ["actual_quantity"] = entry.ActualQuantity?.ToString(),
            ["rate"] = entry.Rate?.ToString(),
            ["hsn_sac"] = null,
            ["gst_rate"] = entry.GSTRateDetails?.FirstOrDefault()?.GSTRate.ToString(CultureInfo.InvariantCulture),
        }).ToArray();

        return new TallySyncedVoucher(
            identity,
            masterId,
            guid,
            remoteAltGuid,
            date,
            voucherType,
            voucherNumber,
            Clean(voucher.Narration),
            new Dictionary<string, object?>
            {
                ["alter_id"] = voucher.AlterId == 0
                    ? null
                    : voucher.AlterId.ToString(CultureInfo.InvariantCulture),
                ["party_ledger_name"] = Clean(voucher.PartyName),
                ["buyer_name"] = Clean(voucher.ConsigneeName),
                ["party_gstin"] = Clean(voucher.PartyGSTIN),
                ["place_of_supply"] = Clean(voucher.PlaceOfSupply),
                ["recipient_registration"] = Clean(voucher.RegistrationType),
                ["is_invoice"] = YesNo(voucher.IsInvoice),
                ["persisted_view"] = voucher.View.ToString(),
                ["ledger_entries"] = ledgerEntries,
                ["line_items"] = lineItems,
            });
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? YesNo(bool? value) => value switch
    {
        true => "Yes",
        false => "No",
        null => null,
    };

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private sealed class BoundedResponseHandler : DelegatingHandler
    {
        private readonly int _maxResponseBytes;

        public BoundedResponseHandler(int maxResponseBytes)
            : base(new HttpClientHandler())
        {
            if (maxResponseBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResponseBytes),
                    "The response limit must be positive.");
            }

            _maxResponseBytes = maxResponseBytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentLength > _maxResponseBytes)
            {
                response.Dispose();
                throw new TallyResponseTooLargeException();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(chunk, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                if (buffer.Length + bytesRead > _maxResponseBytes)
                {
                    response.Dispose();
                    throw new TallyResponseTooLargeException();
                }

                await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
            }

            var originalContent = response.Content;
            var boundedContent = new ByteArrayContent(buffer.ToArray());
            foreach (var header in originalContent.Headers)
            {
                boundedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = boundedContent;
            originalContent.Dispose();
            return response;
        }
    }

    private sealed class TallyResponseTooLargeException : Exception;
}
