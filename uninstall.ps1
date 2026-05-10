#Requires -Version 7.0
<#
.SYNOPSIS
  Delete the installed ws-search-mcp files and print the command to unregister
  it from Claude Code.

.PARAMETER InstallDir
  Install directory to delete. Default: %LOCALAPPDATA%\Programs\ws-search-mcp.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ws-search-mcp')
)

$ErrorActionPreference = 'Stop'

if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir ..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force -LiteralPath $InstallDir
    Write-Host "Removed." -ForegroundColor Green
} else {
    Write-Host "Install directory not found (already clean): $InstallDir" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "If you registered the server with Claude Code, unregister it with:" -ForegroundColor Cyan
Write-Host "  claude mcp remove ws-search --scope user"
Write-Host "(or whichever --scope you used when registering)"
