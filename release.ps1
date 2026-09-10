#Requires -Version 5.1
<#
.SYNOPSIS
    StealthPDF release script: build → sign → verify → hash → update BuildInfo → publish summary.
.DESCRIPTION
    1. Locates pdfium.dll in the NuGet cache, hashes it, and writes BuildInfo.cs so the
       embedded integrity check at startup knows the expected value.
    2. Publishes using FolderProfile1 (net48, win-x64); bundle-source.ps1 also runs.
    3. Signs StealthPDF.exe. Prefers CertThumbprint (exact match) over CertName (CN match).
       Retries the timestamp across three TSA endpoints if the first attempt fails.
    4. Runs "signtool verify /pa /v" as a post-sign gate — aborts if the cert chain
       is not trusted to an accepted root.
    5. Prints thumbprint, SHA256s, and paste targets in the summary.

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of your code-signing certificate (40 hex chars, no spaces).
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Thumbprint, Subject
    Omit if using CertName instead.

.PARAMETER CertName
    Fallback. CN (Subject) of your certificate as it appears in the Windows cert store.
    Ignored when CertThumbprint is supplied. There is deliberately NO default: this project
    was forked from KillerPDF and the script used to default to the original author's
    certificate, which is not ours to sign with.

.PARAMETER SimplySign
    Wait for Certum SimplySign Desktop before signing. Only needed for that specific token;
    off by default so the script never blocks on a prompt in CI.

.PARAMETER SkipSign
    Skip signing. Writes all-zeros into BuildInfo.cs (disables runtime pdfium check).
    Useful for local test builds. Prints a red warning banner.

.EXAMPLE
    .\release.ps1 -CertThumbprint "AABBCC..."
.EXAMPLE
    .\release.ps1 -CertName "Open Source Developer, YOUR NAME" -SimplySign
.EXAMPLE
    .\release.ps1 -SkipSign
#>
param(
    [string]$CertThumbprint = "",
    [string]$CertName       = "",
    [switch]$SimplySign,
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj         = Join-Path $PSScriptRoot "StealthPDF.csproj"
$buildInfoPath = Join-Path $PSScriptRoot "BuildInfo.cs"
$publishDir   = Join-Path $PSScriptRoot "bin\Release\net48\publish"
$exe          = Join-Path $publishDir "StealthPDF.exe"

# TSA endpoints — tried in order; first success wins.
$tsaList = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://ts.ssl.com"
)

# ── 0. Signing identity + token preflight ────────────────────────────────────
# Fail before doing any work rather than after a full build: an unattended run with no cert
# selector would otherwise get all the way to signtool and abort there.
if (-not $SkipSign -and -not $CertThumbprint -and -not $CertName) {
    throw @"
No signing certificate specified.

Pass one of:
  -CertThumbprint "<40 hex chars>"   (preferred - exact match)
  -CertName       "<certificate CN>"

List what is available with:
  Get-ChildItem Cert:\CurrentUser\My | Select-Object Thumbprint, Subject

Or use -SkipSign for a local test build (produces an UNSHIPPABLE binary: the installer
refuses to install an EXE without a valid Authenticode signature).
"@
}

if (-not $SkipSign -and $SimplySign) {
    $ssProc = Get-Process -Name "SimplySignDesktop" -ErrorAction SilentlyContinue
    if (-not $ssProc) {
        Write-Host ""
        Write-Warning "SimplySign Desktop does not appear to be running."
        Write-Host    "    Start it and wait for it to show 'Connected', then press Enter to continue."
        Write-Host    "    Or press Ctrl+C to abort."
        $null = Read-Host
    } else {
        Write-Host "`n==> SimplySign Desktop is running (PID $($ssProc.Id))." -ForegroundColor Green
    }
}

# ── 1. Hash pdfium.dll and update BuildInfo.cs ──────────────────────────────
Write-Host "`n==> Locating pdfium.dll for integrity pre-hash..." -ForegroundColor Cyan

# Look in the NuGet package cache for Docnet.Core's pdfium
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$pdfiumNuget = Get-ChildItem "$nugetCache\docnet.core\*\runtimes\win-x64\native\pdfium.dll" `
                   -ErrorAction SilentlyContinue |
               Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

# Also check the build output as a fallback
$pdfiumBuild = Join-Path $PSScriptRoot "bin\Release\net48\win-x64\pdfium.dll"

$pdfiumPath = $null
if ($pdfiumNuget -and (Test-Path $pdfiumNuget)) {
    $pdfiumPath = $pdfiumNuget
    Write-Host "    Using NuGet cache: $pdfiumPath"
} elseif (Test-Path $pdfiumBuild) {
    $pdfiumPath = $pdfiumBuild
    Write-Host "    Using build output: $pdfiumPath"
} else {
    Write-Warning "    pdfium.dll not found — BuildInfo.cs will retain all-zeros (check disabled)."
}

$pdfiumHash = "0000000000000000000000000000000000000000000000000000000000000000"
if ($pdfiumPath) {
    $pdfiumHash = (Get-FileHash $pdfiumPath -Algorithm SHA256).Hash
    Write-Host "    pdfium SHA256: $pdfiumHash" -ForegroundColor Green
}

if ($SkipSign) {
    # Leave all-zeros so the runtime check is disabled
    $pdfiumHash = "0000000000000000000000000000000000000000000000000000000000000000"
    Write-Host "    SkipSign: BuildInfo.cs will keep all-zeros (check disabled)." -ForegroundColor Yellow
}

Write-Host "`n==> Writing BuildInfo.cs..." -ForegroundColor Cyan
$buildInfoContent = @"
namespace StealthPDF
{
    /// <summary>
    /// Build-time constants written or verified by release.ps1.
    /// </summary>
    internal static class BuildInfo
    {
        /// <summary>
        /// SHA256 of pdfium.dll (original bytes, before Costura compression).
        /// Updated by release.ps1 immediately before each build.
        /// All-zeros means the check is disabled (dev / SkipSign builds).
        /// </summary>
        internal const string PdfiumSha256 = "$pdfiumHash";

        internal const string PdfiumSha256Disabled = "0000000000000000000000000000000000000000000000000000000000000000";
    }
}
"@
[System.IO.File]::WriteAllText($buildInfoPath, $buildInfoContent, [System.Text.UTF8Encoding]::new($false))
Write-Host "    BuildInfo.cs updated." -ForegroundColor Green

# ── 2. Build / Publish ──────────────────────────────────────────────────────
Write-Host "`n==> Building (Release, net48, win-x64)..." -ForegroundColor Cyan

$msbuild = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}
if (-not $msbuild) { $msbuild = "dotnet" }

if ($msbuild -eq "dotnet") {
    & dotnet publish $proj /p:PublishProfile=FolderProfile1 -c Release
} else {
    & $msbuild $proj /t:Publish /p:PublishProfile=FolderProfile1 /p:Configuration=Release /m /nologo /v:m
}

if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
Write-Host "    EXE: $exe" -ForegroundColor Green

# ── 3. Sign ─────────────────────────────────────────────────────────────────
if (-not $SkipSign) {
    Write-Host "`n==> Locating signtool..." -ForegroundColor Cyan
    $signtool = $null
    $kitBase  = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitBase) {
        $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }
    Write-Host "    $signtool"

    # Build cert selector args
    $certArgs = if ($CertThumbprint) {
        Write-Host "`n==> Signing with thumbprint $CertThumbprint..." -ForegroundColor Cyan
        @("/sha1", $CertThumbprint)
    } else {
        Write-Host "`n==> Signing with CN: $CertName..." -ForegroundColor Cyan
        @("/n", $CertName)
    }

    # Everything we build gets signed, not only the main EXE. The TWAIN helper is a separate
    # executable that ships beside it and is launched as its own process, so leaving it unsigned
    # would be the weak link - the installer's trust gate only inspects the main EXE.
    $signTargets = @()
    $twainExe = Join-Path $publishDir "TwainHelper\StealthPDF.TwainHelper.exe"
    if (Test-Path $twainExe) {
        $signTargets += $twainExe
    } else {
        Write-Warning "TwainHelper missing from the publish output - this build has no TWAIN scanning."
    }
    $signTargets += $exe

    foreach ($target in $signTargets) {
        $name   = [System.IO.Path]::GetFileName($target)
        $signed = $false
        foreach ($tsa in $tsaList) {
            Write-Host "    $name via $tsa"
            & $signtool sign `
                /fd  sha256 `
                /tr  $tsa `
                /td  sha256 `
                @certArgs `
                /d   "StealthPDF" `
                /du  "https://stealthpdf.com" `
                /v   $target

            if ($LASTEXITCODE -eq 0) {
                Write-Host "    Signed and timestamped $name via $tsa" -ForegroundColor Green
                $signed = $true
                break
            }
            Write-Warning "    TSA $tsa failed (exit $LASTEXITCODE). Trying next..."
            Start-Sleep -Seconds 3
        }
        if (-not $signed) { throw "Signing failed on all TSA endpoints for $name." }
    }

    # ── Post-sign verification gate ─────────────────────────────────────────
    Write-Host "`n==> Verifying signature chain (/pa)..." -ForegroundColor Cyan
    foreach ($target in $signTargets) {
        & $signtool verify /pa /v $target
        if ($LASTEXITCODE -ne 0) {
            throw "signtool verify FAILED for $target. Does not pass trust validation. DO NOT RELEASE."
        }
    }
    Write-Host "    Signature chain OK." -ForegroundColor Green

    # Print the thumbprint of the cert that was actually used
    try {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($exe)
        $cert2 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert)
        $actualThumb = $cert2.Thumbprint
        $actualCN    = $cert2.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        Write-Host "    Signer : $actualCN" -ForegroundColor Green
        Write-Host "    Thumbprint: $actualThumb" -ForegroundColor Green
    } catch {
        Write-Warning "    Could not read signer info from signed EXE: $_"
        $actualThumb = "(unknown)"
        $actualCN    = "(unknown)"
    }
} else {
    Write-Host ""
    Write-Host "  #####################################################" -ForegroundColor Red
    Write-Host "  ##  WARNING: -SkipSign is set. EXE IS NOT SIGNED.  ##" -ForegroundColor Red
    Write-Host "  ##  DO NOT DISTRIBUTE this build as a release.     ##" -ForegroundColor Red
    Write-Host "  #####################################################" -ForegroundColor Red
    $actualThumb = "(not signed)"
    $actualCN    = "(not signed)"
}

# ── 4. SHA256 (final EXE) ─────────────────────────────────────────────────
Write-Host "`n==> Computing final EXE SHA256..." -ForegroundColor Cyan
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "    StealthPDF.exe : $exeHash" -ForegroundColor Green
if ($pdfiumPath) {
    Write-Host "    pdfium.dll    : $pdfiumHash" -ForegroundColor Green
}

# ── 5. Source zip ────────────────────────────────────────────────────────────
$srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($srcZip) {
    Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
} else {
    Write-Host "`n    (No source zip found — did bundle-source.ps1 run?)" -ForegroundColor Yellow
}

# ── 6. Write SHA256SUMS.txt ──────────────────────────────────────────────────
$sumsPath = Join-Path $PSScriptRoot "SHA256SUMS.txt"
$lines    = [System.Collections.Generic.List[string]]::new()
# Pad to a fixed column, then always emit a separating space: the source zip name
# ("StealthPDF-2.0.0-src.zip") is itself 24 chars, so padding alone would run the
# name straight into the hash with nothing between them.
$lines.Add(("{0} {1}" -f "StealthPDF.exe".PadRight(23), $exeHash))
if ($pdfiumPath) { $lines.Add(("{0} {1}" -f "pdfium.dll".PadRight(23), $pdfiumHash)) }
if ($srcZip) {
    $srcHash = (Get-FileHash $srcZip.FullName -Algorithm SHA256).Hash
    $lines.Add(("{0} {1}" -f $srcZip.Name.PadRight(23), $srcHash))
}
[System.IO.File]::WriteAllLines($sumsPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "`n==> SHA256SUMS.txt written to: $sumsPath" -ForegroundColor Green

# ── 6b. Stamp the landing page ──────────────────────────────────────
# pdf-landing carries the download facts in index.html's hero panel and the
# version badge in every page footer. Each value is marked with data-stamp="..."
# so this runs off attributes rather than line numbers. The committed files hold
# REPLACE_* sentinels; pdf-landing/deploy-check.ps1 refuses to deploy while any
# of them survive, so a release that skips this step cannot ship silently.
$landingDir    = Join-Path $PSScriptRoot "pdf-landing"
$landingIndex  = Join-Path $landingDir "index.html"
$landingStatus = "skipped (pdf-landing not found)"

if (Test-Path $landingIndex) {
    $projText = [System.IO.File]::ReadAllText($proj)
    $verMatch = [regex]::Match($projText, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $verMatch.Success) { throw "Could not read <Version> from $proj" }
    $appVersion = $verMatch.Groups[1].Value

    $exeItem  = Get-Item $exe
    $sizeText = "~{0:N2} MB exe" -f ($exeItem.Length / 1MB)
    $dateText = (Get-Date).ToString('yyyy-MM-dd')
    # Split the hash across two lines so the fixed-width hero panel does not overflow.
    $hashHtml = $exeHash.ToLower().Insert(32, '<br>')

    $stampValues = [ordered]@{
        'version'       = "StealthPDF v$appVersion"
        'released'      = $dateText
        'size'          = $sizeText
        'sha256'        = $hashHtml
        'version-badge' = "v$appVersion"
    }
    # Keys that index.html must carry; a rename there would otherwise ship stale facts.
    $requiredOnIndex = @('version', 'released', 'size', 'sha256')

    $stampedTotal = 0
    foreach ($page in Get-ChildItem $landingDir -Filter *.html) {
        $html    = [System.IO.File]::ReadAllText($page.FullName)
        $changed = $false
        foreach ($key in $stampValues.Keys) {
            # Capture the opening tag carrying data-stamp="<key>" and swap only its inner text.
            $pattern = '(?s)(<span[^>]*\bdata-stamp="' + [regex]::Escape($key) + '"[^>]*>).*?(</span>)'
            if (-not [regex]::IsMatch($html, $pattern)) {
                if ($page.Name -eq 'index.html' -and $requiredOnIndex -contains $key) {
                    throw "index.html is missing its data-stamp=`"$key`" span: $($page.FullName)"
                }
                continue
            }
            $value   = $stampValues[$key]
            $html    = [regex]::Replace($html, $pattern, { param($m) $m.Groups[1].Value + $value + $m.Groups[2].Value })
            $changed = $true
            $stampedTotal++
        }
        if ($changed) {
            [System.IO.File]::WriteAllText($page.FullName, $html, [System.Text.UTF8Encoding]::new($false))
            Write-Host "    stamped: $($page.Name)" -ForegroundColor Green
        }
    }

    $landingStatus = "v$appVersion / $dateText / $sizeText ($stampedTotal values)"
    Write-Host "`n==> Landing page stamped: $landingStatus" -ForegroundColor Green
} else {
    Write-Host "`n    (pdf-landing not found - skipped landing stamp.)" -ForegroundColor Yellow
}

# ── 7. Summary ───────────────────────────────────────────────────────────────
Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "  StealthPDF release artifacts" -ForegroundColor White
Write-Host   "  EXE  : $exe"
if ($srcZip) { Write-Host "  SRC  : $($srcZip.FullName)" }
Write-Host   ""
Write-Host   "  SHA256 (EXE)       : $exeHash" -ForegroundColor Green
if ($pdfiumPath) {
Write-Host   "  SHA256 (pdfium.dll): $pdfiumHash" -ForegroundColor Green }
Write-Host   ""
Write-Host   "  Signer : $actualCN"
Write-Host   "  Thumbprint: $actualThumb"
Write-Host   ""
Write-Host   "  Landing page stamped: $landingStatus"
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
