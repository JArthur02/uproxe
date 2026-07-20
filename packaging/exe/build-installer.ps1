[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "2.0.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "Installer builds must run on Windows."
}

function Get-InnoCompiler {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repoRoot "src\UProxy.UI\UProxy.UI.csproj"
$solution = Join-Path $repoRoot "UProxyTool.sln"
$artifacts = Join-Path $repoRoot "artifacts\release"
$publishDir = Join-Path $artifacts "publish-win-x64"
$installerDir = Join-Path $artifacts "installer"
$installerName = "uproxy_$($Version)_x64_setup.exe"
$installerPath = Join-Path $installerDir $installerName
$issPath = Join-Path $PSScriptRoot "uproxy.iss"

if (Test-Path $artifacts) {
    Remove-Item $artifacts -Recurse -Force
}
New-Item $publishDir -ItemType Directory -Force | Out-Null
New-Item $installerDir -ItemType Directory -Force | Out-Null

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

$iscc = Get-InnoCompiler
$isccArgs = @(
    "/DMyAppVersion=$Version",
    "/DMySourceDir=$publishDir",
    "/DMyOutputDir=$installerDir",
    $issPath
)
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $installerPath)) {
    throw "Inno Setup failed to create '$installerPath'."
}

$hash = (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLine = "$hash  $installerName"
[System.IO.File]::WriteAllText(
    (Join-Path $artifacts "SHA256SUMS.txt"),
    "$hashLine`r`n",
    [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Unsigned installer created:" -ForegroundColor Green
Write-Host "  $installerPath"
Write-Host "SHA-256: $hash"
