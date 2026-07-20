[CmdletBinding()]
param(
    [string]$SourceCommit,
    [string]$Label = "v3-proxychains-preview",
    [string]$Version = "2.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "ZIP publishing must run on Windows (WinForms publish target)."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$distDir = Join-Path $repoRoot "dist"
$solution = Join-Path $repoRoot "UProxyTool.sln"
$project = Join-Path $repoRoot "src\UProxy.UI\UProxy.UI.csproj"
$stagingRoot = Join-Path $repoRoot "artifacts\publish-win-x64"
$publishDate = (Get-Date).ToString("yyyy-MM-dd")

function Publish-Variant(
    [string]$Name,
    [bool]$SelfContained,
    [string]$OutputDir
) {
    if (Test-Path $OutputDir) {
        Remove-Item $OutputDir -Recurse -Force
    }
    New-Item $OutputDir -ItemType Directory -Force | Out-Null

    $args = @(
        "publish", $project,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", ($(if ($SelfContained) { "true" } else { "false" })),
        "-o", $OutputDir,
        "-p:DebugSymbols=false",
        "-p:DebugType=None"
    )
    if ($SelfContained) {
        $args += @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Name." }

    $dataSrc = Join-Path $repoRoot "Data"
    if (-not (Test-Path $dataSrc)) {
        throw "Expected Data/ beside the solution (Country.mmdb + Source/*.txt)."
    }
    Copy-Item $dataSrc (Join-Path $OutputDir "Data") -Recurse -Force

    $zipName = "uProxyTool-$Version-$Label-win-x64-$Name.zip"
    $zipPath = Join-Path $distDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    return $zipPath
}

Push-Location $repoRoot
try {
    if ($SourceCommit) {
        $current = (git rev-parse HEAD).Trim()
        git checkout $SourceCommit
        if ($LASTEXITCODE -ne 0) { throw "Could not checkout '$SourceCommit'." }
    }

    if (-not $SkipTests) {
        & dotnet test $solution -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    New-Item $distDir -ItemType Directory -Force | Out-Null
    if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }

    $selfZip = Publish-Variant "selfcontained" $true (Join-Path $stagingRoot "selfcontained")
    $fxZip = Publish-Variant "framework-dependent" $false (Join-Path $stagingRoot "framework-dependent")

    $lines = @()
    $artifacts = @()
    foreach ($zip in @($selfZip, $fxZip)) {
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $name = Split-Path $zip -Leaf
        $lines += "$hash  $name"
        $variant = if ($name -match "selfcontained") { "self-contained" } else { "framework-dependent" }
        $artifacts += [ordered]@{
            file = $name
            variant = $variant
            sha256 = $hash
            sizeBytes = (Get-Item $zip).Length
        }
    }

    $checksumPath = Join-Path $distDir "SHA256SUMS.txt"
    [System.IO.File]::WriteAllText($checksumPath, ($lines -join "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))

    $sourceFull = (git rev-parse HEAD).Trim()
    $sourceShort = (git rev-parse --short HEAD).Trim()
    $sourceSubject = (git log -1 --format=%s).Trim()
    $manifest = [ordered]@{
        product = "uProxy Tool"
        version = $Version
        label = $Label
        platform = "win-x64"
        sourceCommit = $sourceFull
        sourceCommitShort = $sourceShort
        sourceCommitSubject = $sourceSubject
        publishedAt = $publishDate
        branch = "cursor/publish-win-x64-zip-35cc"
        artifacts = $artifacts
    }
    $manifestPath = Join-Path $distDir "MANIFEST.json"
    $manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding utf8

    Write-Host ""
    Write-Host "Published to dist/:" -ForegroundColor Green
    foreach ($zip in @($selfZip, $fxZip)) { Write-Host "  $zip" }
    Write-Host "  $checksumPath"
    Write-Host "  $manifestPath"
}
finally {
    if ($SourceCommit) {
        git checkout $current 2>$null
    }
    Pop-Location
}
