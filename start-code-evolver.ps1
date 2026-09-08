[CmdletBinding()]
param(
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue

if (-not $dotnet) {
    throw '.NET 10 SDK is required. Install it and ensure dotnet is available on PATH.'
}

if (-not $npm) {
    throw 'Node.js 22 or newer and npm are required. Install them and ensure npm is available on PATH.'
}

function Test-Endpoint([string]$Uri) {
    try {
        return (Invoke-WebRequest -Uri $Uri -TimeoutSec 1).StatusCode -eq 200
    }
    catch {
        return $false
    }
}

$apiReady = Test-Endpoint 'http://localhost:5278/api/health'
$webReady = Test-Endpoint 'http://localhost:5173'
if ($apiReady -and $webReady) {
    Write-Host 'Code Evolver is already running at http://localhost:5173.' -ForegroundColor Green
    if (-not $NoBrowser) { Start-Process 'http://localhost:5173' }
    return
}
if ($apiReady -or $webReady) {
    throw 'Only part of Code Evolver is already running. Stop the process using port 5278 or 5173, then run this script again.'
}

Write-Host 'Starting Code Evolver API at http://localhost:5278...'
$apiProcess = Start-Process -FilePath $dotnet.Source `
    -ArgumentList 'run', '--project', '.\src\CodeEvolver.Api' `
    -WorkingDirectory $root `
    -PassThru

Write-Host 'Starting Code Evolver web app at http://localhost:5173...'
$webProcess = Start-Process -FilePath $npm.Source `
    -ArgumentList 'run', 'dev', '--prefix', '.\src\code-evolver-web' `
    -WorkingDirectory $root `
    -PassThru

$ready = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    if ($apiProcess.HasExited) { throw "The API exited with code $($apiProcess.ExitCode)." }
    if ($webProcess.HasExited) { throw "The web app exited with code $($webProcess.ExitCode)." }

    if ((Test-Endpoint 'http://localhost:5278/api/health') -and (Test-Endpoint 'http://localhost:5173')) {
        $ready = $true
        break
    }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) {
    throw 'The web app did not become ready within 30 seconds. Check the API and web process windows for errors.'
}

Write-Host 'Code Evolver is ready. Close the API and web process windows to stop it.' -ForegroundColor Green
if (-not $NoBrowser) {
    Start-Process 'http://localhost:5173'
}