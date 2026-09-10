$ErrorActionPreference = 'Stop'

# Run the real installer rather than dropping a lone EXE into place.
#
# This package used to Get-ChocolateyWebFile a bare StealthPDF.exe and Copy-Item it into
# %LOCALAPPDATA%\Programs\StealthPDF. That produced a crippled install: TwainHelper\ and
# PdfHelper\ sit beside the executable and are resolved from AppContext.BaseDirectory, so
# copying the EXE alone silently removed TWAIN scanning and PDF compression. It also hand
# rolled its own shortcut and left nothing in Add/Remove Programs.
#
# The setup EXE ships both helper folders, registers an uninstaller, creates the Start Menu
# entry, and offers StealthPDF as a PDF handler without seizing the user's default.

$version = $env:ChocolateyPackageVersion

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileType       = 'EXE'
    url64bit       = "https://github.com/aabhpsy/StealthPDF/releases/download/v$version/StealthPDF-Setup-$version.exe"
    checksum64     = 'REPLACE_HASH'
    checksumType64 = 'sha256'
    # Inno Setup switches. /NORESTART is belt and braces - this installer never asks for one.
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
    validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
