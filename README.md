# Nila Connector

For repeatable disposable GST voucher experiments, see the
[fixture harness and native export runbook](docs/gst-fixtures.md).

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

Normal builds, including `--configuration Release`, show **Development build**
and a short source commit in the main window. **Modified** means the checkout had
local changes when it was built. In Settings, hover over the version or choose
**Copy build info** to get the full commit. These labels are
embedded at build time; they do not depend on Git being installed on the user's PC.

TallyPrime defaults to `http://127.0.0.1:9000`. Installed builds connect to
`https://nila.tax`. To run the current source against local Nila:

```powershell
./scripts/run-local.ps1
```

The launcher passes the local URL only to its child process and stores the local
device credential, Tally settings, and logs under
`%LOCALAPPDATA%\Nila\Connector.Local`. Production state under
`%LOCALAPPDATA%\Nila\Connector` is not changed. Quit any running installed
connector before using the local launcher because only one connector instance
may run at a time.

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

Production logs are written as JSON Lines under
`%LOCALAPPDATA%\Nila\Connector\logs`; the local launcher uses
`%LOCALAPPDATA%\Nila\Connector.Local\logs`.

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

TallyPrime behavior verified during connector development is recorded in
[`docs/tally-behavior.md`](docs/tally-behavior.md). Read it before changing
voucher identity, retry, batching, or company-discovery logic.

## Windows installer

Install Inno Setup, then create a self-contained x64 setup executable:

```powershell
winget install --id JRSoftware.InnoSetup --exact
./scripts/build-installer.ps1 -Tag v0.5.2
```

The setup executable is written to `artifacts/installer`. It installs per user
without elevation, supports in-place upgrades and clean uninstall, creates a
Start Menu shortcut, and offers optional desktop and start-on-login shortcuts.

The tag must already exist, point to the checked-out commit, and the checkout
must be clean. The version comes from that tag; `v0.5.2` is an example, not a
version to reuse. The app and installer both receive `0.5.2`, and the app also
embeds the source commit. To sign the
application and installer with a certificate already installed in the Windows
certificate store, pass its SHA-1 thumbprint and, if needed, the Windows SDK
signing-tool path:

```powershell
./scripts/build-installer.ps1 `
  -Tag v0.5.2 `
  -SigningCertificateSha1 0123456789ABCDEF0123456789ABCDEF01234567 `
  -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"
```

Unsigned builds are suitable for development but will trigger Windows trust
warnings and must not be presented as a trusted public release. Inno Setup is
freeware; JRSoftware also offers an optional commercial license for companies
that want to financially support its development.

## Publishing and upgrading

Pushing to `main` runs build and tests. It does not publish an installer.
For a release, choose the next unused `vMAJOR.MINOR.PATCH` tag: increment PATCH
for fixes, MINOR for features, or MAJOR for breaking changes. Preserve existing
tags and releases; never move a released tag or replace its installer.

After the intended changes are committed, pushed to `main`, and CI passes:

```powershell
git tag -a v0.5.2 -m "Nila Connector 0.5.2"
git push origin v0.5.2
```

Use a new version if that example tag already exists. The release workflow checks
that the tag is on `main`, runs tests again, builds the installer, and publishes
to [Nila downloads](https://github.com/KaguyaHQ/nila-downloads/releases).
It rejects an existing public release instead of overwriting its files. Manual
workflow runs must select an existing version tag and follow the same checks.
Publication requires the `NILA_DOWNLOADS_TOKEN` repository secret with write
access to `KaguyaHQ/nila-downloads`. A failed publication requires inspection of
any draft release before retrying; do not replace a published release.

To upgrade an installed connector:

1. In Settings, choose **Download latest release** and download the setup executable.
2. Quit the connector using its system-tray **Quit** command.
3. Run the installer and launch Nila Connector from the Start Menu.
4. Check the version at the bottom of the main window.

The installer upgrades the existing per-user installation. The saved device link,
Tally settings, and logs are retained. Updates are installed manually; the app
does not download or install updates in the background. Running a development
build from this repo does not update the installed copy or its shortcuts.
