[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "2.0.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "Portable ZIP builds must run on Windows."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $repoRoot "UProxyTool.sln"
$project = Join-Path $repoRoot "src\UProxy.UI\UProxy.UI.csproj"
$staging = Join-Path $repoRoot "artifacts\release\portable-staging"
$distDir = Join-Path $repoRoot "artifacts\release\portable"
$zipName = "uProxyTool-$Version-win-x64-portable.zip"
$zipPath = Join-Path $distDir $zipName

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item $staging -ItemType Directory -Force | Out-Null
New-Item $distDir -ItemType Directory -Force | Out-Null

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & dotnet test $solution -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    & dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $staging `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    if (-not (Test-Path (Join-Path $staging "uproxy.exe"))) {
        throw "Expected uproxy.exe in publish output."
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Portable ZIP: $zipPath"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
