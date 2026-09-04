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

function Test-ContainsOrdinal {
    param(
        [Parameter(Mandatory=$true)][string]$Text,
        [Parameter(Mandatory=$true)][string]$Value
    )

    return $Text.IndexOf($Value, [StringComparison]::Ordinal) -ge 0
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
        foreach ($directory in @('src', 'assets', 'scripts', 'examples', 'docs', 'installer')) {
            Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File -Force |
                Where-Object FullName -NotMatch '\\(?:bin|obj)\\'
        }
    )

    foreach ($sourceFile in $sourceFiles) {
        Assert-True ($sourceFile.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "source file escapes repository root: $($sourceFile.FullName)"
        $relativePath = $sourceFile.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        Assert-True $entries.ContainsKey($relativePath) "source file is not tracked by manifest: $relativePath"
    }

    Assert-True ($entries.Count -eq $sourceFiles.Count) 'source manifest contains stale or duplicate entries.'
}

function Test-ReleasePreflight {
    foreach ($required in @(
        'GHelperAutoMode.csproj', 'examples\config.example.json', 'assets\automode.ico',
        'README.md', 'CHANGELOG.md', 'docs\ARCHITECTURE.md', 'docs\RELEASE_VALIDATION.md',
        'SOURCE_MANIFEST.sha256', 'scripts\build-installer.ps1',
        'installer\GHelperAutoMode.Installer.wixproj', 'installer\GHelperAutoMode.wxs',
        'installer\bootstrapper\GHelperAutoMode.Setup.csproj', 'installer\bootstrapper\Program.cs'
    )) {
        Assert-True (Test-Path (Join-Path $repoRoot $required) -PathType Leaf) "missing required source file: $required"
    }

    $config = Get-Content $exampleConfig -Raw | ConvertFrom-Json
    [xml]$projectXml = Get-Content $project -Raw
    $propertyGroup = $projectXml.Project.PropertyGroup
    $projectVersion = [string]$propertyGroup.Version
    $t = $config.Thresholds

    Assert-True ($config.SchemaVersion -eq 8) 'config.example.json must use schema 8.'
    Assert-True ($projectVersion -eq '5.0.0') "project version is '$projectVersion', expected 5.0.0."
    Assert-True ([string]$propertyGroup.AssemblyVersion -eq '5.0.0.0') 'AssemblyVersion must be 5.0.0.0.'
    Assert-True ([string]$propertyGroup.FileVersion -eq '5.0.0.0') 'FileVersion must be 5.0.0.0.'
    Assert-True ($projectXml.Project.ItemGroup.Compile.Remove -contains 'installer\**\*.cs') 'the main project must exclude installer C# sources and generated files.'
    Assert-True ($config.PollIntervalMilliseconds -ge 500) 'poll interval is below the supported floor.'
    Assert-True ([string]$config.KeyboardLighting.Mode -eq 'Unmanaged') 'example lighting mode must be opt-in (Unmanaged).'
    Assert-True ($config.KeyboardLighting.AccentPollIntervalSeconds -ge 2) 'accent poll interval is below the supported floor.'
    Assert-True ($config.KeyboardLighting.OwnershipHeartbeatSeconds -ge 5) 'ownership heartbeat is below the supported floor.'
    Assert-True ($config.Telemetry.NvidiaSmiPollIntervalSeconds -ge 1) 'nvidia-smi interval is below the supported floor.'
    Assert-True ($config.Telemetry.NvidiaSmiTimeoutSeconds -ge 1) 'nvidia-smi timeout is below the supported floor.'

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

    foreach ($configParity in @(
        @{ Json = $config.KeyboardLighting; Class = 'KeyboardLightingConfig'; Types = 'int|KeyboardLightingMode'; Label = 'KeyboardLighting' },
        @{ Json = $config.Telemetry; Class = 'TelemetryConfig'; Types = 'int'; Label = 'Telemetry' },
        @{ Json = $config.Logging; Class = 'LoggingConfig'; Types = 'int|bool'; Label = 'Logging' }
    )) {
        $jsonNames = @($configParity.Json.PSObject.Properties.Name | Sort-Object)
        $classBlock = [regex]::Match(
            $configSource,
            "(?s)internal sealed class $($configParity.Class)\s*\{(?<Body>.*?)\n\}")
        Assert-True $classBlock.Success "could not locate $($configParity.Class) for config parity check."
        $sourceNames = @(
            [regex]::Matches(
                $classBlock.Groups['Body'].Value,
                "public\s+(?:$($configParity.Types))\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*\{") |
                ForEach-Object { $_.Groups['Name'].Value } |
                Sort-Object
        )
        Assert-True (($jsonNames -join '|') -eq ($sourceNames -join '|')) "config.example.json $($configParity.Label) does not exactly match $($configParity.Class)."
    }

    $controllerSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\GHelperController.cs') -Raw
    $wakeGuardPosition = $controllerSource.IndexOf('if (!displayState.AllowsInputInjection())', [StringComparison]::Ordinal)
    $sendInputPosition = $controllerSource.IndexOf('var sendInput = TrySendInputHotkey', [StringComparison]::Ordinal)
    Assert-True ($wakeGuardPosition -ge 0 -and $sendInputPosition -gt $wakeGuardPosition) 'display-power input guard must precede every normal SendInput request.'
    Assert-True (Test-ContainsOrdinal $controllerSource 'TryPostDirectHotkeyToGHelperWindows') 'non-waking WM_HOTKEY route is missing.'

    $lightingSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\KeyboardLightingManager.cs') -Raw
    Assert-True (Test-ContainsOrdinal $lightingSource 'DwmGetColorizationColor') 'Windows accent-color API is missing.'
    Assert-True (Test-ContainsOrdinal $lightingSource 'skip_aura') 'G-Helper Aura ownership switch is missing.'
    Assert-True (Test-ContainsOrdinal $lightingSource 'ControlledByForegroundApp') 'foreground-app Dynamic Lighting takeover policy is missing.'
    Assert-True (Test-ContainsOrdinal $lightingSource 'forceGHelperReload: true') 'session ownership recovery must release G-Helper before Windows reacquires lighting.'
    Assert-True (-not (Test-ContainsOrdinal $lightingSource 'SendInput(')) 'keyboard lighting must never synthesize input.'

    $powerSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\PowerNotificationWindow.cs') -Raw
    Assert-True (Test-ContainsOrdinal $powerSource 'WTSRegisterSessionNotification') 'session unlock/logon notification registration is missing.'
    Assert-True (Test-ContainsOrdinal $powerSource 'WtsSessionUnlock') 'session unlock ownership recovery is missing.'

    $startupSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\StartupManager.cs') -Raw
    Assert-True (Test-ContainsOrdinal $startupSource 'TaskLogonInteractiveToken') 'startup task must use an interactive user token.'
    Assert-True (Test-ContainsOrdinal $startupSource 'TaskRunLevelHighest') 'startup task must request HighestAvailable.'
    Assert-True (Test-ContainsOrdinal $startupSource 'RegisterTaskDefinition') 'Task Scheduler registration is missing.'
    Assert-True (-not (Test-ContainsOrdinal $startupSource 'key.SetValue(ValueName')) 'startup must not be registered through HKCU Run.'

    $programSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\Program.cs') -Raw
    Assert-True (Test-ContainsOrdinal $programSource '--prepare-uninstall') 'MSI uninstall preparation command is missing.'
    Assert-True (Test-ContainsOrdinal $programSource 'ExitEventPrefix') 'cross-session uninstall shutdown event is missing.'
    Assert-True (Test-ContainsOrdinal $programSource 'FindInstalledInstances') 'targeted uninstall process discovery is missing.'
    Assert-True (Test-ContainsOrdinal $programSource 'instance.Kill(entireProcessTree: false)') 'targeted uninstall fallback is missing.'

    $powerWindowSource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\PowerNotificationWindow.cs') -Raw
    Assert-True (Test-ContainsOrdinal $powerWindowSource 'ChangeWindowMessageFilterEx') 'cross-integrity uninstall shutdown message filter is missing.'
    Assert-True (Test-ContainsOrdinal $powerWindowSource 'TryRequestExit') 'cross-integrity uninstall shutdown request is missing.'

    $installerSource = Get-Content (Join-Path $repoRoot 'installer\GHelperAutoMode.wxs') -Raw
    Assert-True (Test-ContainsOrdinal $installerSource 'Scope="perUserOrMachine"') 'MSI must remain a dual-purpose package.'
    Assert-True (Test-ContainsOrdinal $installerSource 'ProductCode="{EDFEB7D7-FF3E-4258-A9A1-B41B51B98DC3}"') 'v5 MSI ProductCode must remain stable for WinGet detection and reproducible builds.'
    Assert-True (Test-ContainsOrdinal $installerSource 'Id="ARPINSTALLLOCATION"') 'MSI install-location registration is missing.'
    Assert-True (Test-ContainsOrdinal $installerSource '<MajorUpgrade') 'MSI major-upgrade handling is missing.'
    Assert-True (Test-ContainsOrdinal $installerSource 'ExeCommand="--prepare-uninstall"') 'MSI uninstall cleanup action is missing.'

    $setupSource = Get-Content (Join-Path $repoRoot 'installer\bootstrapper\Program.cs') -Raw
    Assert-True (Test-ContainsOrdinal $setupSource 'TokenElevation') 'setup must inspect the real process elevation token.'
    Assert-True (Test-ContainsOrdinal $setupSource 'ALLUSERS=1') 'setup per-machine MSI selection is missing.'
    Assert-True ((Test-ContainsOrdinal $setupSource 'ALLUSERS=2') -and
        (Test-ContainsOrdinal $setupSource 'MSIINSTALLPERUSER=1')) 'setup per-user MSI selection is missing.'

    $integritySource = Get-Content (Join-Path $repoRoot 'src\GHelperAutoMode\ProcessIntegrity.cs') -Raw
    Assert-True (Test-ContainsOrdinal $integritySource 'TokenIntegrityLevel') 'process-integrity comparison is missing.'

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
