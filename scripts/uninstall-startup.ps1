param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'dist\self-contained\GHelperAutoMode.exe'
}
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "GHelperAutoMode.exe was not found. Pass its path explicitly with -ExePath: $ExePath"
}

$resolved = (Resolve-Path -LiteralPath $ExePath).Path
if ([System.IO.Path]::GetFileName($resolved) -ne 'GHelperAutoMode.exe') {
    throw "Expected GHelperAutoMode.exe: $resolved"
}

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$startArgs = @{
    FilePath = $resolved
    ArgumentList = @('--manage-startup', 'disable')
    Wait = $true
    PassThru = $true
    WindowStyle = 'Hidden'
}
if (-not $isAdministrator) {
    $startArgs.Verb = 'RunAs'
}

$process = Start-Process @startArgs
if ($process.ExitCode -ne 0) {
    throw "GHelperAutoMode's startup remover failed with exit code $($process.ExitCode)."
}

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = 'GHelperAutoMode_' + $sid
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    throw "Scheduled Task '$taskName' still exists after disabling startup."
}

Write-Host "Windows-login startup disabled; owned legacy entries were cleaned as well."
