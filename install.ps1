#Requires -Version 7.0
<#
.SYNOPSIS
  Publish ws-search-mcp into a per-user install directory.

.DESCRIPTION
  Verifies the .NET 10 SDK is installed (offers to install via winget if missing),
  runs `dotnet publish`, and drops the binary into %LOCALAPPDATA%\Programs\ws-search-mcp
  (by default). Does NOT register the server with any AI tool — see the README for
  per-tool registration (Claude Code, VS Code, Copilot CLI, Cursor).

.PARAMETER InstallDir
  Destination directory. Default: %LOCALAPPDATA%\Programs\ws-search-mcp.

.PARAMETER SelfContained
  Publish with the .NET runtime bundled in (larger; no .NET runtime needed at runtime).
  Default: framework-dependent (smaller; requires .NET 10 runtime on the machine).

.PARAMETER Yes
  Skip the confirmation prompt before installing missing prerequisites (for unattended use).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ws-search-mcp'),
    [switch]$SelfContained,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $here 'ws-search-mcp.csproj'

function Test-DotNet10Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

function Refresh-Path {
    $env:PATH = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('Path','User')
}

function Install-DotNet10Sdk {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget not found. Install .NET 10 SDK manually from " +
              "https://dotnet.microsoft.com/download/dotnet/10.0 and re-run."
    }
    Write-Host "Installing Microsoft.DotNet.SDK.10 via winget..." -ForegroundColor Cyan
    & winget install --id Microsoft.DotNet.SDK.10 `
        --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "winget install failed (exit $LASTEXITCODE). " +
              "Install .NET 10 SDK manually from https://dotnet.microsoft.com/download/dotnet/10.0."
    }
    Refresh-Path
}

if (-not (Test-DotNet10Sdk)) {
    Write-Host ".NET 10 SDK was not found." -ForegroundColor Yellow
    if (-not $Yes) {
        $answer = Read-Host "Install Microsoft.DotNet.SDK.10 via winget now? [Y/n]"
        if ($answer -and $answer -notmatch '^[yY]') {
            throw "Aborted. Install .NET 10 SDK manually and re-run install.ps1."
        }
    } else {
        Write-Host "(-Yes set; installing without prompt)" -ForegroundColor DarkGray
    }
    Install-DotNet10Sdk
    if (-not (Test-DotNet10Sdk)) {
        throw "winget reported success but .NET 10 SDK is still not detected on PATH. " +
              "Open a new shell and re-run install.ps1, or verify with 'dotnet --list-sdks'."
    }
    Write-Host ".NET 10 SDK ready." -ForegroundColor Green
}

$exe = Join-Path $InstallDir 'ws-search-mcp.exe'
$runningServers = Get-Process -Name 'ws-search-mcp' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            [string]::Equals($_.Path, $exe, [System.StringComparison]::OrdinalIgnoreCase)
        } catch {
            $false
        }
    }

foreach ($server in $runningServers) {
    Write-Host "Stopping running ws-search-mcp process $($server.Id) ..." -ForegroundColor Cyan
    Stop-Process -Id $server.Id -Force -ErrorAction Stop
    $server.WaitForExit()
}

Write-Host "Publishing to $InstallDir ..." -ForegroundColor Cyan
$publishArgs = @(
    'publish', $csproj,
    '-c', 'Release',
    '-o', $InstallDir,
    '--nologo'
)
if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true')
} else {
    $publishArgs += @('--self-contained', 'false')
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

if (-not (Test-Path $exe)) {
    throw "Expected binary not found after publish: $exe"
}

Write-Host ""
Write-Host "Installed: $exe" -ForegroundColor Green
Write-Host ""
Write-Host "Next: register the server with your AI tool. See the README for instructions:" -ForegroundColor Cyan
Write-Host "  https://github.com/andrey-savov/windows-search-mcp#readme"
