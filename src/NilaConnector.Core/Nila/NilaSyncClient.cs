using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Nila;

public sealed class NilaSyncClient
{
    private readonly HttpClient _httpClient;
    private readonly NilaPairingOptions _options;

    public NilaSyncClient(HttpClient httpClient, NilaPairingOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public Task<SyncRunResponse> StartAsync(
        DeviceCredential credential,
        TallyCompany company,
        CancellationToken cancellationToken) =>
        SendAsync<SyncRunResponse>(
            HttpMethod.Post,
            "/api/tally/syncs",
            credential,
            new { company_identity = CompanyIdentity(company) },
            cancellationToken);

    public Task<SyncRunResponse> PutMastersAsync(
        DeviceCredential credential,
        long runId,
        IReadOnlyList<TallyMaster> records,
        bool complete,
        CancellationToken cancellationToken) =>
        SendAsync<SyncRunResponse>(
            HttpMethod.Put,
            $"/api/tally/syncs/{runId}/masters",
            credential,
            new { records, complete },
            cancellationToken);

    public Task<SyncRunResponse> PutVouchersAsync(
        DeviceCredential credential,
        long runId,
        IReadOnlyList<TallySyncedVoucher> records,
        bool complete,
        CancellationToken cancellationToken) =>
        SendAsync<SyncRunResponse>(
            HttpMethod.Put,
            $"/api/tally/syncs/{runId}/vouchers",
            credential,
            new { records, complete },
            cancellationToken);

    public async Task FailAsync(
        DeviceCredential credential,
        long runId,
        string pipeline,
        string error,
        CancellationToken cancellationToken)
    {
        _ = await SendAsync<SyncRunResponse>(
            HttpMethod.Post,
            $"/api/tally/syncs/{runId}/fail",
            credential,
            new { pipeline, error },
            cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        DeviceCredential credential,
        object body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeviceToken);
        request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(timeout.Token)
            ?? throw new InvalidDataException("Nila returned an empty sync response.");
    }

    private static string CompanyIdentity(TallyCompany company) => company.TallyGuid is { Length: > 0 }
        ? $"guid:{company.TallyGuid.ToLowerInvariant()}"
        : $"name:{company.Name.ToLowerInvariant()}";
}

public sealed record SyncRunResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("period_start")] DateOnly PeriodStart,
    [property: JsonPropertyName("period_end")] DateOnly PeriodEnd,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("masters_status")] string MastersStatus,
    [property: JsonPropertyName("vouchers_status")] string VouchersStatus,
    [property: JsonPropertyName("masters_count")] int? MastersCount,
    [property: JsonPropertyName("vouchers_count")] int? VouchersCount);
