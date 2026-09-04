param(
    [switch]$Clean,
    [switch]$Strict,
    [switch]$SkipAppBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'GHelperAutoMode.csproj'
$installerProject = Join-Path $repoRoot 'installer\GHelperAutoMode.Installer.wixproj'
$bootstrapperProject = Join-Path $repoRoot 'installer\bootstrapper\GHelperAutoMode.Setup.csproj'
$publishDirectory = Join-Path $repoRoot 'dist\self-contained'
$installerDirectory = Join-Path $repoRoot 'dist\installer'
$bootstrapperPublishDirectory = Join-Path $repoRoot 'installer\bin\bootstrapper-publish'

function Invoke-Checked {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Remove-GeneratedDirectory {
    param([Parameter(Mandatory=$true)][string]$Path)

    $rootPrefix = $repoRoot.TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Clean target escapes the source root: $resolved"
    }
    if (Test-Path $resolved -PathType Container) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

[xml]$projectXml = Get-Content $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The application version could not be read from GHelperAutoMode.csproj.'
}

if (-not $SkipAppBuild) {
    $buildScript = Join-Path $repoRoot 'scripts\build.ps1'
    $buildArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $buildScript, '-SelfContained')
    if ($Clean) { $buildArguments += '-Clean' }
    if ($Strict) { $buildArguments += '-Strict' }
    Invoke-Checked -FilePath 'powershell.exe' -Arguments $buildArguments
}

$publishedExe = Join-Path $publishDirectory 'GHelperAutoMode.exe'
if (-not (Test-Path $publishedExe -PathType Leaf)) {
    throw "The self-contained application build is missing: $publishedExe"
}

if ($Clean) {
    foreach ($generatedDirectory in @(
        $installerDirectory,
        (Join-Path $repoRoot 'installer\bin'),
        (Join-Path $repoRoot 'installer\obj'),
        (Join-Path $repoRoot 'installer\bootstrapper\bin'),
        (Join-Path $repoRoot 'installer\bootstrapper\obj')
    )) {
        Remove-GeneratedDirectory $generatedDirectory
    }
}

$arguments = @(
    'build', $installerProject,
    '-c', 'Release',
    "-p:InstallerVersion=$version",
    "-p:PublishDir=$publishDirectory",
    '-p:ContinuousIntegrationBuild=true'
)
if ($Strict) { $arguments += '-p:TreatWarningsAsErrors=true' }

Invoke-Checked -FilePath 'dotnet' -Arguments $arguments

$msi = Join-Path $installerDirectory "GHelperAutoMode-$version-win-x64.msi"
if (-not (Test-Path $msi -PathType Leaf)) {
    throw "WiX completed without the expected MSI: $msi"
}

if (Test-Path $bootstrapperPublishDirectory -PathType Container) {
    Remove-Item -LiteralPath $bootstrapperPublishDirectory -Recurse -Force
}

$bootstrapperArguments = @(
    'publish', $bootstrapperProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:InstallerVersion=$version",
    "-p:InstallerMsi=$msi",
    '-p:ContinuousIntegrationBuild=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $bootstrapperPublishDirectory
)
if ($Strict) { $bootstrapperArguments += '-p:TreatWarningsAsErrors=true' }
Invoke-Checked -FilePath 'dotnet' -Arguments $bootstrapperArguments

$publishedSetup = Join-Path $bootstrapperPublishDirectory 'GHelperAutoMode.Setup.exe'
if (-not (Test-Path $publishedSetup -PathType Leaf)) {
    throw "Bootstrapper publish completed without the expected executable: $publishedSetup"
}

$setup = Join-Path $installerDirectory "GHelperAutoMode-$version-win-x64-setup.exe"
Copy-Item -LiteralPath $publishedSetup -Destination $setup -Force

$msiHash = (Get-FileHash $msi -Algorithm SHA256).Hash
$setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'Installer builds complete:'
Write-Host $msi
Write-Host "SHA256: $msiHash"
Write-Host $setup
Write-Host "SHA256: $setupHash"
