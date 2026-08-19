using System.Net;

namespace NilaConnector.Core.Nila;

public sealed class MachineLinker(
    NilaPairingClient pairingClient,
    IDeviceCredentialStore credentialStore,
    IExternalBrowser browser)
{
    public bool IsLinked => credentialStore.Load() is not null;

    public async Task<DeviceCredential> LinkAsync(
        string deviceName,
        CancellationToken cancellationToken)
    {
        var pairing = await pairingClient.StartAsync(deviceName, cancellationToken);
        browser.Open(pairing.VerificationUrl);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(pairing.ExpiresIn);
        var retryAfter = TimeSpan.FromSeconds(Math.Clamp(pairing.RetryAfter, 1, 10));

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(retryAfter, cancellationToken);
            PairingExchangeResponse exchange;

            try
            {
                exchange = await pairingClient.ExchangeAsync(pairing.DeviceCode, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (HttpRequestException)
            {
                continue;
            }

            if (exchange.Status == "pending")
            {
                retryAfter = TimeSpan.FromSeconds(Math.Clamp(exchange.RetryAfter ?? 2, 1, 10));
                continue;
            }

            if (exchange is
                {
                    Status: "linked",
                    DeviceId: > 0,
                    DeviceToken: { Length: > 0 },
                    WebSocketUrl: not null,
                })
            {
                if (!string.Equals(exchange.DeviceToken, pairing.DeviceCode, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Nila returned a mismatched device credential.");
                }

                var credential = new DeviceCredential(
                    exchange.DeviceId.Value,
                    exchange.DeviceToken,
                    exchange.WebSocketUrl);
                try
                {
                    credentialStore.Save(credential);
                }
                catch
                {
                    try
                    {
                        await pairingClient.RevokeAsync(credential, CancellationToken.None);
                    }
                    catch (Exception exception)
                        when (exception is HttpRequestException or OperationCanceledException)
                    {
                    }

                    throw;
                }

                return credential;
            }

            throw new InvalidDataException("Nila returned an invalid pairing state.");
        }

        throw new TimeoutException("The Nila pairing request expired before it was approved.");
    }

    public async Task UnlinkAsync(CancellationToken cancellationToken)
    {
        var credential = credentialStore.Load();
        if (credential is null)
        {
            return;
        }

        try
        {
            await pairingClient.RevokeAsync(credential, cancellationToken);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
        }

        credentialStore.Clear();
    }
}
