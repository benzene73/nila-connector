namespace NilaConnector.Core.Nila;

public sealed record DeviceCredential(int DeviceId, string DeviceToken, Uri WebSocketEndpoint);
