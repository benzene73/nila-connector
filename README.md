# Nila Connector

Nila Connector is the Windows desktop agent that will bridge `nila.tax` with a
locally running TallyPrime instance. The application targets .NET 10 and uses a
small WPF shell; transport and accounting logic live outside the UI project.

The current connector includes:

- a small WPF/tray shell with structured local diagnostics;
- read-only TallyPrime health and loaded-company discovery over bounded,
  hardened HTTP/XML requests; and
- an authenticated Phoenix WebSocket with heartbeats, reconnect backoff, and
  live connection state; and
- browser-based machine linking with an expiring approval request and a
  per-device credential protected by Windows DPAPI for the current user; and
- durable, acknowledged Payment voucher jobs with structured Tally diagnostics
  and indeterminate handling for ambiguous network outcomes; and
- company-scoped, validated multi-voucher imports for approved Nila accounting
  handoffs, with partial or ambiguous Tally outcomes blocked from blind retry.

## Projects

- `NilaConnector.Core` — UI-independent state, Tally XML client, and Phoenix
  connection monitor and voucher job runner.
- `NilaConnector.Desktop` — WPF window, tray lifecycle, dependency injection,
  and local structured logging.
- `NilaConnector.Diagnostics` — read-only `nila-tally` CLI with mandatory
  company and voucher-period context.
- `NilaConnector.Core.Tests` — unit tests for the core library.

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/NilaConnector.Desktop
```

TallyPrime defaults to `http://127.0.0.1:9000`. Installed builds connect to
`https://nila.tax`; local development can override Nila before launch:

```powershell
$env:NILA_BASE_URL = "http://127.0.0.1:4000"
```

Start both local services, launch the connector, then choose
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

## Read-only Tally diagnostics

Use the diagnostic CLI instead of sending ad hoc XML to Tally. Voucher reads
cannot be issued without an explicit company and date scope, and every result
prints the resolved company GUID and effective period.

```powershell
dotnet run --project src/NilaConnector.Diagnostics -- companies

dotnet run --project src/NilaConnector.Diagnostics -- ledgers `
  --company "Nila Demo"

dotnet run --project src/NilaConnector.Diagnostics -- voucher-types `
  --company "Nila Demo"

dotnet run --project src/NilaConnector.Diagnostics -- stock-items `
  --company "Nila Demo"

dotnet run --project src/NilaConnector.Diagnostics -- numbering `
  --company "Nila Demo"

dotnet run --project src/NilaConnector.Diagnostics -- vouchers `
  --company "Nila Demo" --from 2025-12-01 --to 2025-12-31

dotnet run --project src/NilaConnector.Diagnostics -- voucher `
  --company "Nila Demo" --date 2025-12-04 --master-id 1

dotnet run --project src/NilaConnector.Diagnostics -- export-voucher `
  --company "Nila Demo" --date 2025-12-04 --master-id 1

dotnet run --project src/NilaConnector.Diagnostics -- gst-exceptions `
  --company "Nila Demo" --from 2025-12-01 --to 2025-12-31
```

The endpoint defaults to `http://127.0.0.1:9000/`. Override it with
`--endpoint URL` or `NILA_TALLY_ENDPOINT`. The CLI exposes no write, import,
alter, cancel, or delete command. `export-voucher` emits the connector's
structured voucher JSON, including GST rate and valuation records; it is not a
raw XML escape hatch. `gst-exceptions` reports structural GST field omissions
found by Nila. Classification mismatches still require confirmation in Tally's
native GST report.

## Windows installer

Install Inno Setup, then create a self-contained x64 setup executable:

```powershell
winget install --id JRSoftware.InnoSetup --exact
./scripts/build-installer.ps1 -Version 0.1.0
```

The setup executable is written to `artifacts/installer`. It installs per user
without elevation, supports in-place upgrades and clean uninstall, creates a
Start Menu shortcut, and offers optional desktop and start-on-login shortcuts.

The release workflow builds the same artifact for version tags. To sign the
application and installer with a certificate already installed in the Windows
certificate store, pass its SHA-1 thumbprint and, if needed, the Windows SDK
signing-tool path:

```powershell
./scripts/build-installer.ps1 `
  -Version 0.1.0 `
  -SigningCertificateSha1 0123456789ABCDEF0123456789ABCDEF01234567 `
  -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"
```

Unsigned builds are suitable for development but will trigger Windows trust
warnings and must not be presented as a trusted public release. Inno Setup is
freeware; JRSoftware also offers an optional commercial license for companies
that want to financially support its development.
