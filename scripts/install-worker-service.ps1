[CmdletBinding()]
param([string]$PublishPath = "$PSScriptRoot\..\publish\worker")
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer manually in an elevated PowerShell terminal.'
}
$executable = Join-Path (Resolve-Path $PublishPath) 'CodeEvolver.Worker.exe'
if (-not (Test-Path $executable)) { throw 'Publish the Worker project before installing the service.' }
if (Get-Service 'CodeEvolver.Worker' -ErrorAction SilentlyContinue) { throw 'Service already exists. Stop it before updating its published files.' }
# Collect the service identity locally, never in a chat prompt or command argument.
$credential = Get-Credential -Message 'Dedicated account with repository access and an authenticated Copilot CLI profile'
New-Service -Name 'CodeEvolver.Worker' -BinaryPathName ('"' + $executable + '"') -DisplayName 'Code Evolver background worker' -StartupType Automatic -Credential $credential
sc.exe failure CodeEvolver.Worker reset= 86400 actions= restart/10000/restart/30000/restart/60000
Write-Host 'Installed (not started). Configure MySQL and Copilot for this service account, then start CodeEvolver.Worker. Keep secrets outside source control.'