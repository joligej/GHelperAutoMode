param(
    [Parameter(Mandatory=$true)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $ExePath).Path
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "Executable path is not a file: $resolved"
}
if ([System.IO.Path]::GetExtension($resolved) -ne '.exe') {
    throw "Expected a .exe file: $resolved"
}
if ([System.IO.Path]::GetFileName($resolved) -ne 'GHelperAutoMode.exe') {
    throw "Expected GHelperAutoMode.exe: $resolved"
}

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$startArgs = @{
    FilePath = $resolved
    ArgumentList = @('--manage-startup', 'enable')
    Wait = $true
    PassThru = $true
    WindowStyle = 'Hidden'
}
if (-not $isAdministrator) {
    $startArgs.Verb = 'RunAs'
}

$process = Start-Process @startArgs
if ($process.ExitCode -ne 0) {
    throw "GHelperAutoMode's startup installer failed with exit code $($process.ExitCode)."
}

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = 'GHelperAutoMode_' + $sid
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
if ($task.State -eq 'Disabled') {
    throw "Scheduled Task '$taskName' exists but is disabled."
}

Write-Host "Windows-login startup enabled through Task Scheduler: $taskName"
Write-Host "Executable: $resolved"
Write-Host 'Run level: HighestAvailable; logon type: InteractiveToken'
