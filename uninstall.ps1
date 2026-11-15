#Requires -Version 7.0
<#
.SYNOPSIS
  Delete the installed ws-search-mcp files, remove the HTTP logon task, and print the command
  to unregister it from Claude Code.

.PARAMETER InstallDir
  Install directory to delete. Default: %LOCALAPPDATA%\Programs\ws-search-mcp.

.PARAMETER RemoveData
  Also delete the network-hosting state of the current user: the minted client public keys
  (CurrentUser\WsSearchMcp.Clients store), the server TLS certificate and its key
  (CurrentUser\My), and %APPDATA%\ws-search-mcp (config + usage). Clients holding tokens will
  stop working. Off by default so a reinstall keeps your clients and certificate.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ws-search-mcp'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$taskName = 'ws-search-mcp-http'

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Write-Host "Removing scheduled task '$taskName' ..." -ForegroundColor Cyan
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

$installed = foreach ($n in 'ws-search-mcp.exe', 'ws-search-mcp-http.exe', 'ws-search-mcp-admin.exe') { Join-Path $InstallDir $n }
Get-Process -Name 'ws-search-mcp', 'ws-search-mcp-http', 'ws-search-mcp-admin' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            $p = $_.Path
            $installed | Where-Object { [string]::Equals($p, $_, [System.StringComparison]::OrdinalIgnoreCase) }
        } catch { $false }
    } |
    ForEach-Object {
        Write-Host "Stopping running $($_.ProcessName) process $($_.Id) ..." -ForegroundColor Cyan
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }

if ($RemoveData) {
    $dataDir = if ($env:WS_SEARCH_HOME) { $env:WS_SEARCH_HOME } else { Join-Path $env:APPDATA 'ws-search-mcp' }
    $configFile = Join-Path $dataDir 'config.json'
    if (Test-Path $configFile) {
        $thumb = (Get-Content -Raw $configFile | ConvertFrom-Json).certThumbprint
        if ($thumb) {
            $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $thumb
            if ($cert) {
                Write-Host "Removing server TLS certificate $thumb ..." -ForegroundColor Cyan
                try {
                    $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert)
                    if ($key -is [System.Security.Cryptography.ECDsaCng]) { $key.Key.Delete() }
                } catch { }
                Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $thumb | Remove-Item
            }
        }
    }
    if (Test-Path 'Cert:\CurrentUser\WsSearchMcp.Clients') {
        Write-Host "Removing minted client keys ..." -ForegroundColor Cyan
        Get-ChildItem 'Cert:\CurrentUser\WsSearchMcp.Clients' | Remove-Item
    }
    if (Test-Path $dataDir) {
        Write-Host "Removing $dataDir ..." -ForegroundColor Cyan
        Remove-Item -Recurse -Force -LiteralPath $dataDir
    }
}

if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir ..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force -LiteralPath $InstallDir
    Write-Host "Removed." -ForegroundColor Green
} else {
    Write-Host "Install directory not found (already clean): $InstallDir" -ForegroundColor Yellow
}

if (-not $RemoveData) {
    Write-Host ""
    Write-Host "Kept your network-hosting clients, TLS certificate and config. Re-run with -RemoveData to delete them." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "If you registered the server with Claude Code, unregister it with:" -ForegroundColor Cyan
Write-Host "  claude mcp remove ws-search --scope user"
Write-Host "(or whichever --scope you used when registering)"
