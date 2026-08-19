using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyResponseParser
{
    public static async Task<TallyVoucherResult> ParseVoucherImportAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ThrowLineError(document);
        ValidateOptionalStatus(document);

        var created = RequiredCounter(document, "CREATED");
        var errors = RequiredCounter(document, "ERRORS");
        var lastVoucherId = RequiredCounter(document, "LASTVCHID");

        if (created != 1 || errors != 0 || lastVoucherId <= 0)
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally did not create exactly one voucher (created={created}, errors={errors}, last_voucher_id={lastVoucherId}).");
        }

        return new TallyVoucherResult(created, errors, lastVoucherId);
    }

    public static async Task<IReadOnlyList<TallyCompany>> ParseOpenCompaniesAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ValidateApplicationStatus(document);

        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("COMPANY", StringComparison.OrdinalIgnoreCase))
            .Select(ParseCompany)
            .Where(company => company is not null)
            .Cast<TallyCompany>()
            .DistinctBy(company => (company.Name, company.TallyGuid), CompanyIdentityComparer.Instance)
            .OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IReadOnlyList<TallyMaster>> ParseMastersAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ValidateApplicationStatus(document);

        return document.Descendants()
            .Where(element =>
                MasterKind(element.Name.LocalName) is not null &&
                element.Parent?.Name.LocalName.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase) == true)
            .Select(ParseMaster)
            .Where(master => master is not null)
            .Cast<TallyMaster>()
            .DistinctBy(master => (master.Kind, master.Identity), StringTupleComparer.Instance)
            .ToArray();
    }

    public static async Task<IReadOnlyList<TallySyncedVoucher>> ParseVouchersAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ValidateApplicationStatus(document);

        return document.Descendants()
            .Where(element => element.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
            .Select(ParseVoucher)
            .Where(voucher => voucher is not null)
            .Cast<TallySyncedVoucher>()
            .DistinctBy(voucher => voucher.Identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<TallyVoucherBatchResult> ParseVoucherBatchImportAsync(
        Stream response,
        int maxCharacters,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ThrowLineError(document);
        ValidateOptionalStatus(document);

        var result = new TallyVoucherBatchResult(
            RequiredCounter(document, "CREATED"),
            RequiredCounter(document, "ALTERED"),
            RequiredCounter(document, "COMBINED"),
            RequiredCounter(document, "IGNORED"),
            RequiredCounter(document, "ERRORS"),
            RequiredCounter(document, "EXCEPTIONS"),
            OptionalCounter(document, "LASTVCHID"));

        var accounted = result.Created + result.Altered + result.Combined + result.Ignored;
        if (result.Errors != 0 || result.Exceptions != 0 || accounted != expectedCount)
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally did not account for the complete voucher batch (expected={expectedCount}, accounted={accounted}, errors={result.Errors}, exceptions={result.Exceptions}).");
        }

        return result;
    }

    private static async Task<XDocument> LoadDocumentAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = maxCharacters,
        };

        try
        {
            using var reader = XmlReader.Create(response, settings);
            return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException exception)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally returned malformed or unsafe XML.",
                exception);
        }
    }

    private static int RequiredCounter(XDocument document, string name)
    {
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (values.Length != 1 || !int.TryParse(values[0], out var value))
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned an invalid {name} import counter.");
        }

        return value;
    }

    private static int OptionalCounter(XDocument document, string name)
    {
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (values.Length == 0)
        {
            return 0;
        }

        if (values.Length != 1 || !int.TryParse(values[0], out var value) || value < 0)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned an invalid {name} import counter.");
        }

        return value;
    }

    private static void ValidateOptionalStatus(XDocument document)
    {
        var statuses = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (statuses.Length > 1 || (statuses.Length == 1 && statuses[0] != "1"))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                "Tally returned a failed voucher import status.");
        }
    }

    private static void ThrowLineError(XDocument document)
    {
        var lineError = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(lineError))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally rejected the request: {Truncate(lineError.Trim(), 300)}");
        }
    }

    private static TallyCompany? ParseCompany(XElement element)
    {
        var name = AttributeValue(element, "NAME") ?? DescendantValue(element, "NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tallyGuid = AttributeValue(element, "GUID") ?? DescendantValue(element, "GUID");
        return new TallyCompany(name.Trim(), Normalize(tallyGuid));
    }

    private static TallyMaster? ParseMaster(XElement element)
    {
        var kind = MasterKind(element.Name.LocalName);
        var name = AttributeValue(element, "NAME") ?? DescendantValue(element, "NAME");
        if (kind is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var guid = Normalize(DescendantValue(element, "GUID") ?? AttributeValue(element, "GUID"));
        var identity = guid is null ? $"name:{name.Trim().ToLowerInvariant()}" : $"guid:{guid.ToLowerInvariant()}";
        var data = new Dictionary<string, object?>
        {
            ["parent"] = Normalize(DescendantValue(element, "PARENT")),
            ["bill_wise"] = Normalize(DescendantValue(element, "ISBILLWISEON")),
            ["numbering_method"] = Normalize(DescendantValue(element, "NUMBERINGMETHOD")),
            ["is_active"] = Normalize(DescendantValue(element, "ISACTIVE")),
            ["base_units"] = Normalize(DescendantValue(element, "BASEUNITS")),
        };

        return new TallyMaster(
            kind,
            identity,
            name.Trim(),
            guid,
            Normalize(DescendantValue(element, "ALTERID")),
            data);
    }

    private static TallySyncedVoucher? ParseVoucher(XElement element)
    {
        var dateText = DescendantValue(element, "DATE")?.Trim();
        var voucherType = DescendantValue(element, "VOUCHERTYPENAME")?.Trim()
            ?? AttributeValue(element, "VCHTYPE")?.Trim();

        if (!DateOnly.TryParseExact(
                dateText,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date) || string.IsNullOrWhiteSpace(voucherType))
        {
            return null;
        }

        var guid = Normalize(DescendantValue(element, "GUID"));
        var masterId = long.TryParse(DescendantValue(element, "MASTERID")?.Trim(), out var parsedMasterId)
            ? parsedMasterId
            : (long?)null;
        var remoteId = Normalize(AttributeValue(element, "REMOTEID") ?? DescendantValue(element, "REMOTEID"));
        var voucherNumber = Normalize(DescendantValue(element, "VOUCHERNUMBER"));
        var identity = guid is not null
            ? $"guid:{guid.ToLowerInvariant()}"
            : masterId is not null
                ? $"master:{masterId}"
                : $"voucher:{date:yyyyMMdd}:{voucherType.ToLowerInvariant()}:{voucherNumber?.ToLowerInvariant()}";

        var entries = element.Descendants()
            .Where(child => child.Name.LocalName.Equals("ALLLEDGERENTRIES.LIST", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (object)new Dictionary<string, object?>
            {
                ["ledger_name"] = Normalize(DescendantValue(entry, "LEDGERNAME")),
                ["amount"] = Normalize(DescendantValue(entry, "AMOUNT")),
                ["is_deemed_positive"] = Normalize(DescendantValue(entry, "ISDEEMEDPOSITIVE")),
            })
            .ToArray();

        return new TallySyncedVoucher(
            identity,
            masterId,
            guid,
            remoteId,
            date,
            voucherType,
            voucherNumber,
            Normalize(DescendantValue(element, "NARRATION")),
            new Dictionary<string, object?>
            {
                ["alter_id"] = Normalize(DescendantValue(element, "ALTERID")),
                ["ledger_entries"] = entries,
            });
    }

    private static string? MasterKind(string localName) => localName.ToUpperInvariant() switch
    {
        "LEDGER" => "ledger",
        "VOUCHERTYPE" => "voucher_type",
        "STOCKITEM" => "stock_item",
        _ => null,
    };

    private static void ValidateApplicationStatus(XDocument document)
    {
        var lineError = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(lineError))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally rejected the request: {Truncate(lineError.Trim(), 300)}");
        }

        var statuses = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (statuses.Length != 1)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally returned an invalid response envelope.");
        }

        if (statuses[0] is "0" or "-1")
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally returned application status {statuses[0]}.");
        }

        if (statuses[0] != "1")
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned invalid status {Truncate(statuses[0], 20)}.");
        }
    }

    private static string? AttributeValue(XElement element, string name) => element
        .Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private static string? DescendantValue(XElement element, string name) => element
        .Descendants()
        .FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class CompanyIdentityComparer : IEqualityComparer<(string Name, string? TallyGuid)>
    {
        public static CompanyIdentityComparer Instance { get; } = new();

        public bool Equals((string Name, string? TallyGuid) x, (string Name, string? TallyGuid) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.TallyGuid, y.TallyGuid);

        public int GetHashCode((string Name, string? TallyGuid) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
            value.TallyGuid is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.TallyGuid));
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Kind, string Identity)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string Kind, string Identity) x, (string Kind, string Identity) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Kind, y.Kind) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Identity, y.Identity);

        public int GetHashCode((string Kind, string Identity) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Kind),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Identity));
    }
}
