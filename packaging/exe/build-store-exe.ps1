[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "2.0.0",
    [string]$Publisher = "uproxy",
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$CertificateThumbprint,
    [ValidateSet("CurrentUser", "LocalMachine")]
    [string]$CertificateStoreLocation = "CurrentUser",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipTests,
    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-WindowsSdkTool([string]$ToolName) {
    $onPath = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        throw "$ToolName was not found. Install the Windows 10/11 SDK."
    }

    $escapedToolName = [regex]::Escape($ToolName)
    $candidate = Get-ChildItem $kitsRoot -Filter $ToolName -File -Recurse |
        Where-Object { $_.FullName -match "[\\/]x64[\\/]$escapedToolName$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw "$ToolName was not found under '$kitsRoot'. Install the Windows 10/11 SDK."
    }

    return $candidate.FullName
}

function Get-InnoCompiler {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php."
}

function Invoke-SignFile(
    [string]$SignTool,
    [string]$Thumbprint,
    [string]$FilePath,
    [string]$TimestampServer,
    [bool]$UseMachineStore
) {
    $signArguments = @(
        "sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampServer,
        "/s", "My", "/sha1", $Thumbprint
    )
    if ($UseMachineStore) {
        $signArguments += "/sm"
    }
    $signArguments += $FilePath

    & $SignTool @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Signing failed for '$FilePath'."
    }

    & $SignTool verify /pa /all $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signature verification failed for '$FilePath'."
    }
}

if ($env:OS -ne "Windows_NT") {
    throw "The Store EXE build must run on Windows."
}

if ([string]::IsNullOrWhiteSpace($Publisher)) {
    throw "Publisher must not be empty."
}

if (-not $SkipSigning -and
    [string]::IsNullOrWhiteSpace($PfxPath) -and
    [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "A trusted signing certificate is required. Pass -PfxPath or -CertificateThumbprint, or use -SkipSigning for a local test build that cannot be submitted to the Store."
}

if (-not $SkipSigning -and
    -not [string]::IsNullOrWhiteSpace($PfxPath) -and
    -not (Test-Path $PfxPath)) {
    throw "Code-signing PFX not found at '$PfxPath'."
}

if (-not [string]::IsNullOrWhiteSpace($PfxPath) -and
    -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "Pass either -PfxPath or -CertificateThumbprint, not both."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repoRoot "src\UProxy.UI\UProxy.UI.csproj"
$solution = Join-Path $repoRoot "UProxyTool.sln"
$artifacts = Join-Path $repoRoot "artifacts\store-exe"
$publishDir = Join-Path $artifacts "publish-win-x64"
$installerDir = Join-Path $artifacts "installer"
$installerName = "uproxy_$($Version)_x64_setup.exe"
$installerPath = Join-Path $installerDir $installerName
$issPath = Join-Path $PSScriptRoot "uproxy.iss"
$thumbprint = $null
$certificatesToRemove = @()
$signTool = $null
$useMachineStore = $false

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

try {
    if (-not $SkipSigning) {
        $signTool = Get-WindowsSdkTool "signtool.exe"
        if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
            $thumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
            $useMachineStore = $CertificateStoreLocation -eq "LocalMachine"
            $certificatePath = "Cert:\$CertificateStoreLocation\My\$thumbprint"
            $signingCertificate = Get-Item $certificatePath -ErrorAction SilentlyContinue
            if (-not $signingCertificate) {
                throw "No certificate with thumbprint '$thumbprint' was found in Cert:\$CertificateStoreLocation\My."
            }
        }
        else {
            $existingThumbprints = @(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint)
            $securePassword = if ([string]::IsNullOrEmpty($PfxPassword)) {
                [System.Security.SecureString]::new()
            }
            else {
                ConvertTo-SecureString $PfxPassword -AsPlainText -Force
            }

            $imported = @(Import-PfxCertificate `
                -FilePath (Resolve-Path $PfxPath).Path `
                -CertStoreLocation Cert:\CurrentUser\My `
                -Password $securePassword)

            $certificatesToRemove = @($imported |
                Where-Object { $existingThumbprints -notcontains $_.Thumbprint } |
                ForEach-Object Thumbprint)
            $signingCertificate = $imported |
                Where-Object {
                    $_.HasPrivateKey -and
                    ($_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3")
                } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1

            if ($signingCertificate) {
                $thumbprint = $signingCertificate.Thumbprint
            }
        }

        if (-not $signingCertificate) {
            throw "The selected certificate was not found or the PFX does not contain a usable Code Signing certificate."
        }

        if (-not $signingCertificate.HasPrivateKey -or
            $signingCertificate.EnhancedKeyUsageList.ObjectId -notcontains "1.3.6.1.5.5.7.3.3") {
            throw "The selected certificate must have a private key and the Code Signing enhanced key usage."
        }

        $peFiles = @(Get-ChildItem $publishDir -Recurse -File |
            Where-Object { $_.Extension -in @(".exe", ".dll") })
        foreach ($peFile in $peFiles) {
            Invoke-SignFile $signTool $thumbprint $peFile.FullName $TimestampUrl $useMachineStore
        }
    }

    $iscc = Get-InnoCompiler
    $isccArgs = @(
        "/DMyAppVersion=$Version",
        "/DMyAppPublisher=$Publisher",
        "/DMySourceDir=$publishDir",
        "/DMyOutputDir=$installerDir"
    )

    if (-not $SkipSigning) {
        $machineStoreSwitch = if ($useMachineStore) { " /sm" } else { "" }
        $innoSignCommand = "`$q$signTool`$q sign /fd SHA256 /td SHA256 /tr `$q$TimestampUrl`$q /s My /sha1 $thumbprint$machineStoreSwitch `$f"
        $isccArgs += "/DSignInstaller=1"
        $isccArgs += "/Sstore-sign=$innoSignCommand"
    }

    $isccArgs += $issPath
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $installerPath)) {
        throw "Inno Setup failed to create '$installerPath'."
    }

    if (-not $SkipSigning) {
        & $signTool verify /pa /all $installerPath
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed for '$installerPath'."
        }
    }

    $hash = (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashLine = "$hash  $installerName"
    [System.IO.File]::WriteAllText(
        (Join-Path $artifacts "SHA256SUMS.txt"),
        "$hashLine`r`n",
        [System.Text.UTF8Encoding]::new($false))

    Write-Host ""
    Write-Host "Store EXE installer created:" -ForegroundColor Green
    Write-Host "  $installerPath"
    Write-Host "SHA-256: $hash"
    if ($SkipSigning) {
        Write-Warning "This is an unsigned test installer. It is not eligible for Microsoft Store submission."
    }
    else {
        Write-Host "Authenticode signatures were applied and verified."
    }
}
finally {
    foreach ($certificateToRemove in $certificatesToRemove) {
        Remove-Item "Cert:\CurrentUser\My\$certificateToRemove" -Force -ErrorAction SilentlyContinue
    }
}
