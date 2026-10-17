[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [switch]$NoWorker,
    [switch]$Simulate,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $env:ConnectionStrings__CodeEvolver) {
    throw 'Set ConnectionStrings__CodeEvolver in this terminal to your local MySQL database. Credentials are never written by this launcher.'
}
$dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path $dotnet
if ($Simulate) { $env:Agent__Provider = 'local' } else { $env:Agent__Provider = 'copilot' }
$runtime = Join-Path $root '.runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null

function Test-Endpoint([string]$Uri) {
    try { return (Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 2).StatusCode -eq 200 }
    catch { return $false }
}

if (-not (Test-Endpoint 'http://127.0.0.1:5278/api/health')) {
    Push-Location (Join-Path $root 'src\code-evolver-web')
    try {
        & npm.cmd ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed. Node 22.12+ is required.' }
    } finally { Pop-Location }
    & $dotnet publish (Join-Path $root 'src\CodeEvolver.Api') -c Release -o (Join-Path $runtime 'api')
    if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
    New-Item -ItemType Directory -Path (Join-Path $runtime 'api\wwwroot') -Force | Out-Null
    Copy-Item (Join-Path $root 'src\code-evolver-web\dist\*') (Join-Path $runtime 'api\wwwroot') -Recurse -Force
    $api = Start-Process -FilePath (Join-Path $runtime 'api\CodeEvolver.Api.exe') -ArgumentList '--urls', 'http://127.0.0.1:5278' -WorkingDirectory (Join-Path $runtime 'api') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime 'api.log') -RedirectStandardError (Join-Path $runtime 'api-error.log') -PassThru
    $api.Id | Set-Content (Join-Path $runtime 'api.pid')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Endpoint 'http://127.0.0.1:5278/api/health')) {
        if ($api.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'API did not become ready. Check .runtime/api-error.log and MySQL configuration.' }
        Start-Sleep -Milliseconds 500
    }
}

if (-not $NoWorker) {
    $state = Invoke-RestMethod 'http://127.0.0.1:5278/api/service'
    if (-not $state.workerOnline) {
        $installed = Get-Service 'CodeEvolver.Worker' -ErrorAction SilentlyContinue
        if ($installed) {
            if ($installed.Status -ne 'Running') { throw 'The installed Worker is stopped. Start it from Windows Services with appropriate privileges.' }
            Write-Host 'Installed Worker is running; waiting for its next heartbeat.'
        } else {
            $pidPath = Join-Path $runtime 'worker.pid'
            $existingWorker = $null
            if (Test-Path $pidPath) {
                $candidate = Get-Process -Id ([int](Get-Content $pidPath)) -ErrorAction SilentlyContinue
                if ($candidate -and $candidate.Path -eq (Join-Path $runtime 'worker\CodeEvolver.Worker.exe')) { $existingWorker = $candidate }
            }
            if (-not $existingWorker) {
                & $dotnet publish (Join-Path $root 'src\CodeEvolver.Worker') -c Release -o (Join-Path $runtime 'worker')
                if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
                $worker = Start-Process -FilePath (Join-Path $runtime 'worker\CodeEvolver.Worker.exe') -WorkingDirectory (Join-Path $runtime 'worker') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime 'worker.log') -RedirectStandardError (Join-Path $runtime 'worker-error.log') -PassThru
                $worker.Id | Set-Content $pidPath
                Write-Host "Detached Worker PID $($worker.Id). Install as a Windows service for reboot/logoff survival."
            }
        }
    }
    $workerDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $state = Invoke-RestMethod 'http://127.0.0.1:5278/api/service'
        if ($state.workerOnline) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $workerDeadline)
    if (-not $state.workerOnline) { Write-Warning 'Worker heartbeat not detected. Commands are saved, but check .runtime/worker-error.log or the Windows Service account configuration.' }
}
Write-Host 'Control UI: http://127.0.0.1:5278. Closing the browser or this launcher does not stop the Worker.' -ForegroundColor Green
if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:5278' }