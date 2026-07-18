#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the bundled PyMuPDF compression helper (portable Python + PyMuPDF) into PdfHelper\bundle\.

.DESCRIPTION
    The ~55 MB bundle is NOT in source control. Run this once on a dev machine to (re)build it
    before a release build. It:
      1. Downloads the Python 3.12 embeddable (x64) from python.org.
      2. pip-installs PyMuPDF into a throwaway venv, then copies ONLY the runtime files
         (mupdfcpp64.dll, _mupdf.pyd, the .py sources) into bundle\python\Lib\site-packages\pymupdf\.
         Build artifacts (.lib, .h, __pycache__) are stripped.
      3. Writes python312._pth so the embeddable finds the bundled site-packages.
      4. Trims files the compression script does not need (crypto/ssl/sqlite PDBs).

    The build's CopyPdfHelperOutput MSBuild target then deploys PdfHelper\bundle\** into the
    app output next to StealthPDF.exe at build time.

.PARAMETER Force
    Re-download even if bundle\ already exists.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundle = Join-Path $here 'bundle'

if ((Test-Path $bundle) -and -not $Force) {
    Write-Host "PdfHelper bundle already exists at $bundle"
    Write-Host "Re-run with -Force to rebuild."
    return
}

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundle | Out-Null

# ── 1. Python embeddable ────────────────────────────────────────────────────
$pyVer = '3.12.10'
$url = "https://www.python.org/ftp/python/$pyVer/python-$pyVer-embed-amd64.zip"
$zip = Join-Path $bundle '_python-embed.zip'
$pyDir = Join-Path $bundle 'python'
Write-Host "Downloading Python $pyVer embeddable..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
Expand-Archive -Path $zip -DestinationPath $pyDir -Force
Remove-Item $zip

# Strip the PDBs and catalog we don't ship.
Get-ChildItem $pyDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $pyDir 'python.cat') -Force -ErrorAction SilentlyContinue

# ── 2. PyMuPDF via a throwaway venv ─────────────────────────────────────────
$venv = Join-Path $here '.venv-build'
if (Test-Path $venv) { Remove-Item $venv -Recurse -Force }
$sysPy = (Get-Command python.exe -ErrorAction SilentlyContinue) ? 'python.exe' : 'py'
& $sysPy -m venv $venv
$venvPy = Join-Path $venv 'Scripts\python.exe'
Write-Host "Installing PyMuPDF into build venv..."
& $venvPy -m pip install --quiet --disable-pip-version-check 'pymupdf==1.25.3'
if ($LASTEXITCODE -ne 0) { throw "pip install pymupdf failed" }

$srcPkg = Join-Path $venv 'Lib\site-packages\pymupdf'
$dstPkg = Join-Path $pyDir 'Lib\site-packages\pymupdf'
New-Item -ItemType Directory -Force -Path (Split-Path $dstPkg) | Out-Null
Copy-Item -Path (Join-Path $srcPkg '*') -Destination $dstPkg -Recurse -Force

# Strip build artifacts not needed at runtime.
Remove-Item (Join-Path $dstPkg 'mupdf-devel') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dstPkg '__pycache__') -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $dstPkg -Filter '*.lib' | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $dstPkg -Filter '*.h' | Where-Object { $_.Directory.Name -eq 'pymupdf' } | Remove-Item -Force -ErrorAction SilentlyContinue

Remove-Item $venv -Recurse -Force

# ── 3. python312._pth so the embeddable finds the bundled site-packages ─────
$pth = Join-Path $pyDir "python$($pyVer -replace '\.','')._pth"
$pthLines = @('python312.zip', '.', 'Lib', 'Lib\site-packages')
[System.IO.File]::WriteAllLines($pth, $pthLines, (New-Object System.Text.UTF8Encoding $false))

# ── 4. Verify the bundle works ──────────────────────────────────────────────
Write-Host "Verifying bundle..."
& (Join-Path $pyDir 'python.exe') -c "import pymupdf; print('PyMuPDF', pymupdf.__version__, 'OK')"
if ($LASTEXITCODE -ne 0) { throw "Bundle verification failed" }

$total = (Get-ChildItem $bundle -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("PdfHelper bundle ready: {0:N1} MB at {1}" -f ($total/1MB), $bundle)
