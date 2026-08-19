# Nila Connector

Nila Connector is the Windows desktop agent that will bridge `nila.tax` with a
locally running TallyPrime instance. The application targets .NET 10 and uses a
small WPF shell; transport and accounting logic live outside the UI project.

The current development slice includes:

- a small WPF/tray shell with structured local diagnostics;
- read-only TallyPrime health and loaded-company discovery over bounded,
  hardened HTTP/XML requests; and
- an authenticated Phoenix WebSocket with heartbeats, reconnect backoff, and
  live connection state; and
- browser-based machine linking with an expiring approval request and a
  per-device credential protected by Windows DPAPI for the current user.

Voucher sync and mutation jobs are intentionally not implemented yet.

## Projects

- `NilaConnector.Core` — UI-independent state, Tally XML client, and Phoenix
  connection monitor. Job and synchronization logic comes later.
- `NilaConnector.Desktop` — WPF window, tray lifecycle, dependency injection,
  and local structured logging.
- `NilaConnector.Core.Tests` — unit tests for the core library.

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/NilaConnector.Desktop
```

Local development defaults to TallyPrime at `http://127.0.0.1:9000` and Nila at
`http://127.0.0.1:4000`. Start both services, launch the connector, then choose
**Link to Nila**. The connector opens an expiring approval page in the browser;
after approval it stores only a Windows-user-protected device credential. The
same button becomes **Unlink**, which revokes the device in Nila before removing
the local credential.

Environment credentials remain available strictly as a development fallback:

```powershell
$env:NILA_DEVICE_ID = "1"
$env:NILA_DEVICE_TOKEN = "the-one-time-device-token"
```

Plain `ws://` is accepted only for loopback development; remote deployments
must use `wss://`. The connector never stores the user's Nila password.

Logs are written as JSON Lines under
`%LOCALAPPDATA%\Nila\Connector\logs`.
