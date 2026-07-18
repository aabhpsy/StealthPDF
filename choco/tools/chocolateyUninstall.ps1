$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\StealthPDF'
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

$startMenuPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\StealthPDF.lnk'
if (Test-Path $startMenuPath) { Remove-Item $startMenuPath -Force }
