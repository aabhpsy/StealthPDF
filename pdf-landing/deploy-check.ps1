#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-deploy gate for the stealthpdf.com landing site.
.DESCRIPTION
    Run this immediately before publishing pdf-landing/. It fails the deploy if:
      1. Any REPLACE_* sentinel survives - release.ps1 stamps the version, date,
         size and SHA256 into the pages, so a surviving sentinel means the site
         is about to advertise a build that was never stamped.
      2. A page references a local asset that is not present (the KillerPDF ->
         StealthPDF rename shipped exactly this bug: every logo 404'd).
      3. The footer version badge disagrees with <Version> in StealthPDF.csproj.
      4. A sitemap <loc> points at a page that does not exist.
      5. Dead KillerPDF asset paths or the retired killerpdf.net domain reappear.
         Prose attribution ("fork of KillerPDF by Steve the Killer") is expected
         and is not flagged - only asset paths and the old domain are.

    Exits 0 when clean, 1 when anything above fails.
.EXAMPLE
    pwsh ./pdf-landing/deploy-check.ps1
#>
[CmdletBinding()]
param(
    [string] $LandingDir  = $PSScriptRoot,
    [string] $ProjectFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'StealthPDF.csproj')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$problems = [System.Collections.Generic.List[string]]::new()
$pages    = @(Get-ChildItem -Path $LandingDir -Filter *.html -File)

if ($pages.Count -eq 0) { throw "No .html pages found in $LandingDir" }

Write-Host "==> Checking $($pages.Count) page(s) in $LandingDir" -ForegroundColor Cyan

# ── 1. Unstamped release sentinels ──────────────────────────────────────────
foreach ($page in $pages) {
    $text = [System.IO.File]::ReadAllText($page.FullName)
    foreach ($m in [regex]::Matches($text, 'REPLACE_[A-Z_]+')) {
        $problems.Add("$($page.Name): unstamped sentinel '$($m.Value)' - run release.ps1 first")
    }
}

# ── 2. Local assets referenced but missing ──────────────────────────────────
foreach ($page in $pages) {
    $text = [System.IO.File]::ReadAllText($page.FullName)
    # Inline scripts build markup by concatenation (e.g. '<img src="' + src + '">'),
    # which is not a real asset reference.
    # Keep the opening tag so <script src="..."> is still checked; drop only the body.
    $markup = [regex]::Replace($text, '(?is)(<script\b[^>]*>).*?</script>', '$1')
    foreach ($m in [regex]::Matches($markup, '(?:src|href)="([^"]+)"')) {
        $ref = $m.Groups[1].Value
        # Skip absolute URLs, anchors, and protocol-relative links.
        if ($ref -match '^(https?:|mailto:|data:|//|#)') { continue }
        # Strip query string and fragment before resolving on disk.
        $clean = ($ref -split '[?#]')[0]
        if ([string]::IsNullOrWhiteSpace($clean)) { continue }
        # A root-relative path resolves against the deploy root, i.e. the landing dir.
        $rel  = $clean.TrimStart('/')
        $path = Join-Path $LandingDir ($rel -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path)) {
            # /download/StealthPDF.exe is uploaded alongside the site, not tracked in git.
            if ($clean -like '/download/*') {
                Write-Host "    note: $($page.Name) -> $clean (uploaded at deploy time, not in repo)" -ForegroundColor Yellow
                continue
            }
            $problems.Add("$($page.Name): references missing asset '$ref'")
        }
    }
}

# ── 3. Footer version badge vs csproj ───────────────────────────────────────
if (Test-Path -LiteralPath $ProjectFile) {
    $projText = [System.IO.File]::ReadAllText($ProjectFile)
    $verMatch = [regex]::Match($projText, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $verMatch.Success) {
        $problems.Add("Could not read <Version> from $ProjectFile")
    } else {
        $expected = "v$($verMatch.Groups[1].Value)"
        foreach ($page in $pages) {
            $text = [System.IO.File]::ReadAllText($page.FullName)
            $badge = [regex]::Match($text, '(?s)<span[^>]*\bdata-stamp="version-badge"[^>]*>(.*?)</span>')
            if (-not $badge.Success) { continue }
            $actual = $badge.Groups[1].Value.Trim()
            if ($actual -ne $expected) {
                $problems.Add("$($page.Name): footer badge is '$actual' but csproj says '$expected'")
            }
        }
    }
} else {
    Write-Host "    note: $ProjectFile not found - skipped version cross-check." -ForegroundColor Yellow
}

# ── 4. Sitemap entries resolve ──────────────────────────────────────────────
$sitemap = Join-Path $LandingDir 'sitemap.xml'
if (Test-Path -LiteralPath $sitemap) {
    foreach ($m in [regex]::Matches([System.IO.File]::ReadAllText($sitemap), '<loc>\s*([^<]+?)\s*</loc>')) {
        $loc  = $m.Groups[1].Value
        $path = ([uri]$loc).AbsolutePath.TrimStart('/')
        if ([string]::IsNullOrWhiteSpace($path)) { $path = 'index.html' }
        if (-not (Test-Path -LiteralPath (Join-Path $LandingDir $path))) {
            $problems.Add("sitemap.xml: <loc>$loc</loc> has no matching file ('$path')")
        }
    }
}

# ── 5. Retired KillerPDF asset paths / domain ───────────────────────────────
foreach ($file in Get-ChildItem -Path $LandingDir -Include *.html,*.css,*.js,*.xml,*.txt -File -Recurse) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($pattern in @('killerpdf-logo', 'killerpdf\.net', 'brand/')) {
        if ([regex]::IsMatch($text, $pattern, 'IgnoreCase')) {
            $problems.Add("$($file.Name): retired reference matching '$pattern'")
        }
    }
}

# ── Result ──────────────────────────────────────────────────────────────────
if ($problems.Count -gt 0) {
    Write-Host "`n==> Deploy check FAILED ($($problems.Count) problem(s)):" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "    - $p" -ForegroundColor Red }
    exit 1
}

Write-Host "`n==> Deploy check passed - pdf-landing is ready to publish." -ForegroundColor Green
exit 0
