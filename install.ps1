#Requires -Version 7.0
<#
.SYNOPSIS
  Publish ws-search-mcp into a per-user install directory.

.DESCRIPTION
  Verifies the .NET 10 SDK is installed (offers to install via winget if missing),
  runs `dotnet publish` for the three executables (ws-search-mcp, ws-search-mcp-http,
  ws-search-mcp-admin), and drops them into %LOCALAPPDATA%\Programs\ws-search-mcp
  (by default). Does NOT register the server with any AI tool — see the README for
  per-tool registration (Claude Code, VS Code, Copilot CLI, Cursor).

.PARAMETER InstallDir
  Destination directory. Default: %LOCALAPPDATA%\Programs\ws-search-mcp.

.PARAMETER RegisterHttpTask
  Also register a per-user Scheduled Task ("ws-search-mcp-http") that starts the authenticated
  HTTPS server (ws-search-mcp-http.exe) whenever the current user logs on, and start it now.
  It runs as the current user so Windows Search returns that user's index. Before it can start,
  create a TLS certificate and at least one client: see "Network hosting" in the README.

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
    [switch]$Yes,
    [switch]$RegisterHttpTask
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# stdio server (ws-search-mcp.exe), authenticated HTTPS server, and the admin CLI all install side by side.
$projects = @(
    Join-Path $here 'ws-search-mcp.csproj'
    Join-Path $here 'src\WsSearchMcp.Http\WsSearchMcp.Http.csproj'
    Join-Path $here 'src\WsSearchMcp.Admin\WsSearchMcp.Admin.csproj'
)
$taskName = 'ws-search-mcp-http'

function Test-DotNet10Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

function Update-SessionPath {
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
    Update-SessionPath
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
$httpExe = Join-Path $InstallDir 'ws-search-mcp-http.exe'
$adminExe = Join-Path $InstallDir 'ws-search-mcp-admin.exe'

# A registered logon task may be running the HTTP server; stop the task first, remember to restart it.
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}

$installed = @($exe, $httpExe, $adminExe)
$runningServers = Get-Process -Name 'ws-search-mcp', 'ws-search-mcp-http', 'ws-search-mcp-admin' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            $p = $_.Path
            $installed | Where-Object { [string]::Equals($p, $_, [System.StringComparison]::OrdinalIgnoreCase) }
        } catch {
            $false
        }
    }

foreach ($server in $runningServers) {
    Write-Host "Stopping running $($server.ProcessName) process $($server.Id) ..." -ForegroundColor Cyan
    Stop-Process -Id $server.Id -Force -ErrorAction Stop
    $server.WaitForExit()
}

foreach ($csproj in $projects) {
    Write-Host "Publishing $(Split-Path -Leaf $csproj) to $InstallDir ..." -ForegroundColor Cyan
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
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $csproj (exit $LASTEXITCODE)" }
}

foreach ($binary in $installed) {
    if (-not (Test-Path $binary)) {
        throw "Expected binary not found after publish: $binary"
    }
}

if ($RegisterHttpTask) {
    $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $httpExe -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'ws-search-mcp authenticated HTTPS MCP server (runs as the signed-in user)' -Force | Out-Null
    Write-Host "Registered scheduled task '$taskName' (runs at logon as $user)." -ForegroundColor Green
    $existingTask = $true
}

if ($existingTask) {
    Start-ScheduledTask -TaskName $taskName
    Write-Host "Started scheduled task '$taskName'. (It exits immediately until a TLS certificate exists:" -ForegroundColor Cyan
    Write-Host "  ws-search-mcp-admin cert init, then re-run: Start-ScheduledTask $taskName)"
}

Write-Host ""
Write-Host "Installed: $exe" -ForegroundColor Green
Write-Host ""
Write-Host "Also installed: $httpExe" -ForegroundColor Green
Write-Host "                $adminExe"
Write-Host ""
Write-Host "Local clients: register ws-search-mcp.exe (stdio) with your AI tool. Network clients: see" -ForegroundColor Cyan
Write-Host "'Network hosting' in the README (ws-search-mcp-admin cert init / client mint, then run ws-search-mcp-http)." -ForegroundColor Cyan
Write-Host "  https://github.com/andrey-savov/windows-search-mcp#readme"
