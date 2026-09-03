param(
    [switch]$SelfContained,
    [switch]$Clean,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'GHelperAutoMode.csproj'
$exampleConfig = Join-Path $repoRoot 'examples\config.example.json'

function Invoke-Dotnet {
    param([Parameter(Mandatory=$true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory=$true)][bool]$Condition,
        [Parameter(Mandatory=$true)][string]$Message
    )

    if (-not $Condition) { throw "Release preflight failed: $Message" }
}

function Remove-BuildDirectory {
    param([Parameter(Mandatory=$true)][ValidateSet('bin', 'obj', 'dist')][string]$Name)

    $rootPrefix = $repoRoot.TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath((Join-Path $repoRoot $Name))
    Assert-True $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) "clean target escapes source root: $target"
    Assert-True ([IO.Path]::GetFileName($target) -eq $Name) "clean target is not the expected '$Name' directory: $target"

    if (Test-Path -LiteralPath $target -PathType Container) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

function Test-SourceManifest {
    $manifestPath = Join-Path $repoRoot 'SOURCE_MANIFEST.sha256'
    $entries = @{}
    $rootPrefix = $repoRoot.TrimEnd('\') + '\'
    $lineNumber = 0

    foreach ($line in Get-Content $manifestPath) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $match = [regex]::Match($line, '^(?<Hash>[A-Fa-f0-9]{64})  (?<Path>.+)$')
        Assert-True $match.Success "invalid source-manifest line $lineNumber."

        $relativePath = $match.Groups['Path'].Value
        Assert-True (-not [IO.Path]::IsPathRooted($relativePath)) "manifest path must be relative: $relativePath"

        $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        Assert-True ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "manifest path escapes source root: $relativePath"
        Assert-True (Test-Path $fullPath -PathType Leaf) "manifest file is missing: $relativePath"
        Assert-True (-not $entries.ContainsKey($relativePath)) "duplicate manifest entry: $relativePath"

        $actualHash = (Get-FileHash $fullPath -Algorithm SHA256).Hash
        Assert-True ($actualHash -eq $match.Groups['Hash'].Value) "source hash mismatch: $relativePath"
        $entries[$relativePath] = $true
    }

    $sourceFiles = @(
        Get-ChildItem -LiteralPath $repoRoot -File -Force |
            Where-Object Name -ne 'SOURCE_MANIFEST.sha256'
        foreach ($directory in @('src', 'assets', 'scripts', 'examples', 'docs')) {
            Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File -Force
        }
    )

    foreach ($sourceFile in $sourceFiles) {
        $relativePath = [IO.Path]::GetRelativePath($repoRoot, $sourceFile.FullName).Replace('\', '/')
        Assert-True $entries.ContainsKey($relativePath) "source file is not tracked by manifest: $relativePath"
    }

    Assert-True ($entries.Count -eq $sourceFiles.Count) 'source manifest contains stale or duplicate entries.'
}

function Test-ReleasePreflight {
    foreach ($required in @(
        'GHelperAutoMode.csproj', 'examples\config.example.json', 'assets\automode.ico',
        'README.md', 'CHANGELOG.md', 'docs\ARCHITECTURE.md', 'docs\RELEASE_VALIDATION.md',
        'SOURCE_MANIFEST.sha256'
    )) {
        Assert-True (Test-Path (Join-Path $repoRoot $required) -PathType Leaf) "missing required source file: $required"
    }

    $config = Get-Content $exampleConfig -Raw | ConvertFrom-Json
    [xml]$projectXml = Get-Content $project -Raw
    $propertyGroup = $projectXml.Project.PropertyGroup
    $projectVersion = [string]$propertyGroup.Version
    $t = $config.Thresholds

    Assert-True ($config.SchemaVersion -eq 7) 'config.example.json must use schema 7.'
    Assert-True ($projectVersion -eq '4.4.2') "project version is '$projectVersion', expected 4.4.2."
    Assert-True ([string]$propertyGroup.AssemblyVersion -eq '4.4.2.0') 'AssemblyVersion must be 4.4.2.0.'
    Assert-True ([string]$propertyGroup.FileVersion -eq '4.4.2.0') 'FileVersion must be 4.4.2.0.'
    Assert-True ($config.PollIntervalMilliseconds -ge 500) 'poll interval is below the supported floor.'
    Assert-True ([string]$config.KeyboardLighting.Mode -eq 'Unmanaged') 'example lighting mode must be opt-in (Unmanaged).'
    Assert-True ($config.KeyboardLighting.AccentPollIntervalSeconds -ge 2) 'accent poll interval is below the supported floor.'

    $configThresholdNames = @($t.PSObject.Properties.Name | Sort-Object)
    $configSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\AppConfig.cs') -Raw
    $thresholdBlock = [regex]::Match(
        $configSource,
        '(?s)internal sealed class ThresholdConfig\s*\{(?<Body>.*?)\n\}')
    Assert-True $thresholdBlock.Success 'could not locate ThresholdConfig for config parity check.'
    $sourceThresholdNames = @(
        [regex]::Matches(
            $thresholdBlock.Groups['Body'].Value,
            'public\s+(?:int|double|bool)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*\{') |
            ForEach-Object { $_.Groups['Name'].Value } |
            Sort-Object
    )
    Assert-True (($configThresholdNames -join '|') -eq ($sourceThresholdNames -join '|')) 'config.example.json Thresholds do not exactly match ThresholdConfig.'

    $controllerSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\GHelperController.cs') -Raw
    $wakeGuardPosition = $controllerSource.IndexOf('if (!displayState.AllowsInputInjection())', [StringComparison]::Ordinal)
    $sendInputPosition = $controllerSource.IndexOf('var sendInput = TrySendInputHotkey', [StringComparison]::Ordinal)
    Assert-True ($wakeGuardPosition -ge 0 -and $sendInputPosition -gt $wakeGuardPosition) 'display-power input guard must precede every normal SendInput request.'
    Assert-True ($controllerSource.Contains('TryPostDirectHotkeyToGHelperWindows', [StringComparison]::Ordinal)) 'non-waking WM_HOTKEY route is missing.'

    $lightingSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\KeyboardLightingManager.cs') -Raw
    Assert-True ($lightingSource.Contains('DwmGetColorizationColor', [StringComparison]::Ordinal)) 'Windows accent-color API is missing.'
    Assert-True ($lightingSource.Contains('skip_aura', [StringComparison]::Ordinal)) 'G-Helper Aura ownership switch is missing.'
    Assert-True ($lightingSource.Contains('ControlledByForegroundApp', [StringComparison]::Ordinal)) 'foreground-app Dynamic Lighting takeover policy is missing.'
    Assert-True ($lightingSource.Contains('forceGHelperReload: true', [StringComparison]::Ordinal)) 'session ownership recovery must release G-Helper before Windows reacquires lighting.'
    Assert-True (-not $lightingSource.Contains('SendInput(', [StringComparison]::Ordinal)) 'keyboard lighting must never synthesize input.'

    $powerSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\PowerNotificationWindow.cs') -Raw
    Assert-True ($powerSource.Contains('WTSRegisterSessionNotification', [StringComparison]::Ordinal)) 'session unlock/logon notification registration is missing.'
    Assert-True ($powerSource.Contains('WtsSessionUnlock', [StringComparison]::Ordinal)) 'session unlock ownership recovery is missing.'

    $startupSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\StartupManager.cs') -Raw
    Assert-True ($startupSource.Contains('TaskLogonInteractiveToken', [StringComparison]::Ordinal)) 'startup task must use an interactive user token.'
    Assert-True ($startupSource.Contains('TaskRunLevelHighest', [StringComparison]::Ordinal)) 'startup task must request HighestAvailable.'
    Assert-True ($startupSource.Contains('RegisterTaskDefinition', [StringComparison]::Ordinal)) 'Task Scheduler registration is missing.'
    Assert-True (-not $startupSource.Contains('key.SetValue(ValueName', [StringComparison]::Ordinal)) 'startup must not be registered through HKCU Run.'

    $integritySource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\ProcessIntegrity.cs') -Raw
    Assert-True ($integritySource.Contains('TokenIntegrityLevel', [StringComparison]::Ordinal)) 'process-integrity comparison is missing.'

    Assert-True ($t.BalancedCpuResetPercent -lt $t.BalancedCpuPercent) 'Balanced CPU reset must be below its enter threshold.'
    Assert-True ($t.BalancedGpuResetPercent -lt $t.BalancedGpuPercent) 'Balanced GPU reset must be below its enter threshold.'
    Assert-True ($t.TurboCpuPercent -gt $t.BalancedCpuPercent) 'Turbo CPU threshold must be strictly above the Balanced threshold.'
    Assert-True ($t.TurboGpuPercent -gt $t.BalancedGpuPercent) 'Turbo GPU threshold must be strictly above the Balanced threshold.'
    Assert-True ($t.TurboCpuResetPercent -lt $t.TurboCpuPercent) 'Turbo CPU reset must be below its enter threshold.'
    Assert-True ($t.TurboGpuResetPercent -lt $t.TurboGpuPercent) 'Turbo GPU reset must be below its enter threshold.'
    Assert-True ($t.FastTurboCpuPercent -ge $t.TurboCpuPercent) 'Fast Turbo CPU threshold must be at least the normal Turbo threshold.'
    Assert-True ($t.FastTurboGpuPercent -ge $t.TurboGpuPercent) 'Fast Turbo GPU threshold must be at least the normal Turbo threshold.'
    Assert-True ($t.FastTurboCpuResetPercent -lt $t.FastTurboCpuPercent) 'Fast Turbo CPU reset must be below its enter threshold.'
    Assert-True ($t.FastTurboGpuResetPercent -lt $t.FastTurboGpuPercent) 'Fast Turbo GPU reset must be below its enter threshold.'
    Assert-True ($t.ForegroundBalancedCoreEquivalentResetPercent -lt $t.ForegroundBalancedCoreEquivalentPercent) 'Foreground Balanced reset must be below its enter threshold.'
    Assert-True ($t.ForegroundTurboCoreEquivalentResetPercent -lt $t.ForegroundTurboCoreEquivalentPercent) 'Foreground Turbo reset must be below its enter threshold.'
    Assert-True ($t.ForegroundTurboCoreEquivalentPercent -gt $t.ForegroundBalancedCoreEquivalentPercent) 'Foreground Turbo threshold must be strictly above the foreground Balanced threshold.'

    Assert-True ($t.TurboThermalTempC -gt $t.BalancedThermalTempC) 'Turbo thermal threshold must be strictly above the Balanced thermal threshold.'
    Assert-True ($t.TurboExitMaxTempC -lt $t.TurboThermalTempC) 'Turbo-exit temperature must sit below the Turbo thermal trigger.'
    Assert-True ($t.SilentCpuTempMaxC -lt $t.BalancedThermalTempC) 'Silent CPU temperature ceiling must sit below the Balanced thermal trigger.'
    Assert-True ($t.SilentGpuTempMaxC -lt $t.BalancedThermalTempC) 'Silent GPU temperature ceiling must sit below the Balanced thermal trigger.'

    Assert-True ($t.TurboExitCpuBelowPercent -lt $t.TurboCpuPercent) 'Turbo-exit CPU load must sit below the Turbo CPU trigger.'
    Assert-True ($t.TurboExitGpuBelowPercent -lt $t.TurboGpuPercent) 'Turbo-exit GPU load must sit below the Turbo GPU trigger.'
    Assert-True ($t.SilentCpuBelowPercent -lt $t.BalancedCpuPercent) 'Silent CPU ceiling must sit below the Balanced CPU trigger.'
    Assert-True ($t.SilentGpuBelowPercent -lt $t.BalancedGpuPercent) 'Silent GPU ceiling must sit below the Balanced GPU trigger.'
    Assert-True ($t.DisplayOffSilentSeconds -le $t.ActiveDisplaySilentSeconds) 'display-off Silent qualification should not be slower than active-display qualification.'
    Assert-True ($t.BalancedCpuSeconds -gt 0 -and $t.BalancedGpuSeconds -gt 0 -and $t.ForegroundBalancedSeconds -gt 0) 'Balanced evidence durations must be positive.'
    Assert-True ($t.TurboCpuSeconds -gt 0 -and $t.TurboGpuSeconds -gt 0 -and $t.ForegroundTurboSeconds -gt 0) 'Turbo evidence durations must be positive.'
    Assert-True ($t.BalancedThermalSeconds -gt 0 -and $t.TurboThermalSeconds -gt 0) 'thermal evidence durations must be positive.'
    Assert-True ($t.TurboExitSeconds -gt 0 -and $t.ActiveDisplaySilentSeconds -gt 0 -and $t.DisplayOffSilentSeconds -gt 0) 'downshift durations must be positive.'
    Assert-True ($t.BalancedBeforeTurboSeconds -ge 0) 'Balanced-before-Turbo settling duration cannot be negative.'

    Test-SourceManifest
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'dotnet was not found. Install the .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0'
}

$version = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
    throw 'dotnet --version failed.'
}
if (-not $version.StartsWith('10.')) {
    throw "The active .NET SDK is $version. This project targets .NET 10. Install/activate a 10.x SDK before building."
}

Test-ReleasePreflight

if ($Clean) {
    Remove-BuildDirectory 'bin'
    Remove-BuildDirectory 'obj'
    Remove-BuildDirectory 'dist'
}

$publishKind = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$outDir = Join-Path (Join-Path $repoRoot 'dist') $publishKind

Write-Host "Release preflight passed. Restoring with .NET SDK $version..."
Invoke-Dotnet -Arguments @('restore', $project, '-r', 'win-x64')

Write-Host 'Building Release...'
$buildArgs = @(
    'build', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--no-restore',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:ContinuousIntegrationBuild=true'
)
if ($Strict) { $buildArgs += '-p:TreatWarningsAsErrors=true' }
Invoke-Dotnet -Arguments $buildArgs

Write-Host "Publishing $publishKind single-file build..."
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $selfContainedValue,
    '-p:PublishSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:ContinuousIntegrationBuild=true',
    '--no-restore',
    '-o', $outDir
)
if ($Strict) { $publishArgs += '-p:TreatWarningsAsErrors=true' }
Invoke-Dotnet -Arguments $publishArgs

$exe = Join-Path $outDir 'GHelperAutoMode.exe'
if (-not (Test-Path $exe -PathType Leaf)) {
    throw "Publish completed without expected executable: $exe"
}

foreach ($required in @(
    'README.md', 'CHANGELOG.md', 'SOURCE_MANIFEST.sha256',
    'examples\config.example.json', 'docs\ARCHITECTURE.md', 'docs\RELEASE_VALIDATION.md'
)) {
    $published = Join-Path $outDir $required
    if (-not (Test-Path $published -PathType Leaf)) {
        throw "Publish output is missing required file: $published"
    }
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'Build complete:'
Write-Host $exe
Write-Host "SHA256: $hash"
