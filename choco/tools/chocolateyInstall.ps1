$ErrorActionPreference = 'Stop'
$toolsDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$version    = $env:ChocolateyPackageVersion
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\StealthPDF'
$installExe = Join-Path $installDir 'StealthPDF.exe'

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileFullPath   = Join-Path $toolsDir 'StealthPDF.exe'
    url64bit       = "https://github.com/aabhpsy/StealthPDF/releases/download/v$version/StealthPDF.exe"
    checksum64     = 'REPLACE_HASH'
    checksumType64 = 'sha256'
}

Get-ChocolateyWebFile @packageArgs

# Per-user install (no admin needed), matching the app's own installer layout.
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $toolsDir 'StealthPDF.exe') $installExe -Force

# Start Menu shortcut (current user)
$startMenuPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\StealthPDF.lnk'
Install-ChocolateyShortcut -ShortcutFilePath $startMenuPath -TargetPath $installExe
