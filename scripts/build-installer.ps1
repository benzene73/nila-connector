param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "0.1.0",

    [Parameter()]
    [string]$Configuration = "Release",

    [Parameter()]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SigningCertificateSha1,

    [Parameter()]
    [string]$SignToolPath,

    [Parameter()]
    [uri]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repository "artifacts\publish\win-x64"
$installerDirectory = Join-Path $repository "artifacts\installer"
$project = Join-Path $repository "src\NilaConnector.Desktop\NilaConnector.Desktop.csproj"
$script = Join-Path $repository "installer\NilaConnector.iss"

function Invoke-CodeSigning {
    param(
        [Parameter(Mandatory)]
        [string]$Artifact
    )

    if (-not $SigningCertificateSha1) {
        return
    }

    $resolvedSignTool = if ($SignToolPath) {
        $SignToolPath
    } else {
        (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    }

    if (-not $resolvedSignTool -or -not (Test-Path -LiteralPath $resolvedSignTool)) {
        throw "Authenticode signing was requested, but signtool.exe was not found."
    }

    & $resolvedSignTool sign /sha1 $SigningCertificateSha1 /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $Artifact
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Artifact."
    }
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerDirectory -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "The self-contained connector publish failed."
}

Invoke-CodeSigning -Artifact (Join-Path $publishDirectory "NilaConnector.exe")

$compilerCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$compiler = $compilerCandidates | Select-Object -First 1
if (-not $compiler) {
    throw "Inno Setup Compiler was not found. Install JRSoftware.InnoSetup with winget."
}

& $compiler "/DAppVersion=$Version" $script
if ($LASTEXITCODE -ne 0) {
    throw "The Windows installer build failed."
}

$installer = Get-ChildItem -LiteralPath $installerDirectory -Filter '*.exe' |
    Sort-Object LastWriteTime |
    Select-Object -Last 1

if (-not $installer) {
    throw "The installer build completed without producing an executable."
}

Invoke-CodeSigning -Artifact $installer.FullName

Write-Output $installer.FullName
