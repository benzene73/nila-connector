[CmdletBinding()]
param(
    [Uri] $BaseUrl = 'http://127.0.0.1:4000'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $BaseUrl.IsLoopback -or $BaseUrl.Scheme -ne 'http') {
    throw '-BaseUrl must be an http:// loopback URL for local development.'
}

if (Get-Process -Name 'NilaConnector' -ErrorAction SilentlyContinue) {
    throw 'Nila Connector is already running. Quit it from the notification area, then run this script again.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\NilaConnector.Desktop\NilaConnector.Desktop.csproj'

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'dotnet'
$startInfo.WorkingDirectory = $repositoryRoot
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add('run')
$startInfo.ArgumentList.Add('--project')
$startInfo.ArgumentList.Add($projectPath)
$startInfo.ArgumentList.Add('--configuration')
$startInfo.ArgumentList.Add('Debug')
$startInfo.Environment['NILA_BASE_URL'] = $BaseUrl.AbsoluteUri.TrimEnd('/')
$startInfo.Environment['NILA_CONNECTOR_PROFILE'] = 'local'

Write-Host "Starting Nila Connector against $($startInfo.Environment['NILA_BASE_URL'])"
Write-Host "Local state: $env:LOCALAPPDATA\Nila\Connector.Local"

$process = [System.Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) {
    throw 'Failed to start Nila Connector.'
}

$process.WaitForExit()
exit $process.ExitCode
