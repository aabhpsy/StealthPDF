$ErrorActionPreference = 'Stop'

# Let the installer remove itself. The previous version deleted the install folder and the
# shortcut by hand, which left the Add/Remove Programs entry and the PDF handler
# registration behind. Inno registers a proper uninstaller, so find it and run it.

$key = Get-UninstallRegistryKey -SoftwareName 'StealthPDF*'

if (-not $key) {
    Write-Warning 'StealthPDF is not registered as installed. Nothing to remove.'
    return
}

if ($key.Count -gt 1) {
    Write-Warning "Found $($key.Count) StealthPDF entries. Remove them from Add/Remove Programs instead."
    return
}

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileType       = 'EXE'
    file           = $key.UninstallString -replace '"', ''
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    validExitCodes = @(0)
}

Uninstall-ChocolateyPackage @packageArgs
