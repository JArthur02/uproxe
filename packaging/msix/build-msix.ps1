[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = "2.0.0.1",

    [ValidateSet("x64")]
    [string]$Architecture = "x64",

    [ValidateNotNullOrEmpty()]
    [string]$PublisherDisplayName = "leekmadeek",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "MSIX builds must run on Windows because they use MakeAppx.exe from the Windows SDK."
}

function Get-MakeAppx {
    $onPath = Get-Command MakeAppx.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem $kitsRoot -Filter MakeAppx.exe -File -Recurse |
            Where-Object { $_.FullName -match "\\x64\\MakeAppx\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "MakeAppx.exe was not found. Install the Windows 10/11 SDK."
}

$versionParts = @($Version.Split(".") | ForEach-Object { [int]$_ })
if ($versionParts.Count -eq 3) {
    $versionParts += 0
}
if ($versionParts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw "Each MSIX version component must be between 0 and 65535."
}
$packageVersion = $versionParts -join "."

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repoRoot "src\UProxy.UI\UProxy.UI.csproj"
$testProject = Join-Path $repoRoot "tests\UProxy.Core.Tests\UProxy.Core.Tests.csproj"
$manifestTemplate = Join-Path $PSScriptRoot "AppxManifest.xml"
$assetSource = Join-Path $PSScriptRoot "Assets"
$artifacts = Join-Path $repoRoot "artifacts\release"
$publishDir = Join-Path $artifacts "publish-msix-$Architecture"
$stagingDir = Join-Path $artifacts "msix-staging-$Architecture"
$outputDir = Join-Path $artifacts "msix"
$packageName = "uproxy_$($packageVersion)_$Architecture.msix"
$packagePath = Join-Path $outputDir $packageName
$runtimeIdentifier = "win-$Architecture"

foreach ($directory in @($publishDir, $stagingDir)) {
    if (Test-Path $directory) {
        Remove-Item $directory -Recurse -Force
    }
    New-Item $directory -ItemType Directory -Force | Out-Null
}
New-Item $outputDir -ItemType Directory -Force | Out-Null
if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & dotnet test $testProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    & dotnet publish $project `
        -c Release `
        -r $runtimeIdentifier `
        --self-contained true `
        -o $publishDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}
finally {
    Pop-Location
}

$publishedExe = Join-Path $publishDir "uproxy.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Expected publish output '$publishedExe' was not created."
}

Copy-Item (Join-Path $publishDir "*") $stagingDir -Recurse -Force
Copy-Item $assetSource (Join-Path $stagingDir "Assets") -Recurse -Force

$stagedManifest = Join-Path $stagingDir "AppxManifest.xml"
Copy-Item $manifestTemplate $stagedManifest
[xml]$manifest = Get-Content $stagedManifest -Raw
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$identity = $manifest.SelectSingleNode("/m:Package/m:Identity", $namespaceManager)
$publisher = $manifest.SelectSingleNode("/m:Package/m:Properties/m:PublisherDisplayName", $namespaceManager)
if ($null -eq $identity -or $null -eq $publisher) {
    throw "The MSIX manifest template is missing required identity nodes."
}
$identity.SetAttribute("Version", $packageVersion)
$identity.SetAttribute("ProcessorArchitecture", $Architecture)
$publisher.InnerText = $PublisherDisplayName
$manifest.Save($stagedManifest)

$makeAppx = Get-MakeAppx
& $makeAppx pack /d $stagingDir /p $packagePath /o
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $packagePath)) {
    throw "MakeAppx failed to create '$packagePath'."
}

$hash = (Get-FileHash $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    (Join-Path $outputDir "SHA256SUMS.txt"),
    "$hash  $packageName`r`n",
    [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Unsigned Microsoft Store package created:" -ForegroundColor Green
Write-Host "  $packagePath"
Write-Host "Identity: leekmadeek.uproxy"
Write-Host "Publisher: CN=F60517ED-816A-4235-AB58-62B0A8BA554D"
Write-Host "Version: $packageVersion"
Write-Host "SHA-256: $hash"
Write-Host ""
Write-Host "Upload the unsigned .msix to Partner Center; Microsoft signs it after certification."
