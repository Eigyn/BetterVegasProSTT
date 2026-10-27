# Removes Subtitler, run by Uninstall.cmd: the panel from every VEGAS Pro and, if confirmed,
# the runtime and downloaded models. Presets, panel options and logs in the install folder
# are kept, so a later setup starts where this one left off.
[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Subtitler'),
    # Only this extension folder instead of every VEGAS Pro's (for tests).
    [string]$ExtensionsDirectory,
    # Delete the runtime and models without asking.
    [switch]$All
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
try {
    $folders = @($ExtensionsDirectory)
    if (-not $ExtensionsDirectory) {
        $installs = Get-VegasInstalls
        if ($installs) {
            while (Get-RunningVegas $installs) {
                Read-Host 'VEGAS Pro is open. Save your work and close it, then press Enter to continue' | Out-Null
            }
        }
        # Every version's folder, including one whose VEGAS has since been uninstalled.
        $folders = @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'VEGAS Pro\*\Application Extensions') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    $removed = @()
    foreach ($folder in $folders | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
        $files = @(Get-ChildItem -LiteralPath $folder -Filter 'Subtitler.*' |
            Where-Object { $_.Name -match '^Subtitler\.(Core\.dll|Vegas\.dll|Worker\.settings\.json)(\.(previous|new)-[0-9a-f]{32})?$' })
        if (-not $files) { continue }
        $files | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
        $removed += Split-Path -Leaf (Split-Path -Parent $folder)
    }
    if ($removed) { Write-Host "Removed the Subtitler panel from VEGAS Pro $($removed -join ' and ')." }
    else { Write-Host 'The Subtitler panel was not installed in VEGAS Pro.' }
    $runtime = Join-Path $InstallRoot 'runtime'
    $worker = Join-Path $InstallRoot 'worker'
    if (Test-Path -LiteralPath $runtime) {
        $answer = if ($All) { 'y' } else { Read-Host "Also delete Subtitler's runtime and downloaded models in $runtime (14-33 GB, depending on the models downloaded)? Type Y to delete, or press Enter to keep them" }
        if ($answer -match '^(y|yes)$') {
            # rmdir removes a folder junction without following it, unlike Windows PowerShell's Remove-Item.
            cmd /c rmdir /s /q "$runtime"
            if (Test-Path -LiteralPath $runtime) { throw "Some of $runtime is in use and was not deleted." }
            if (Test-Path -LiteralPath $worker) { Remove-Item -LiteralPath $worker -Recurse -Force }
            Write-Host 'Deleted the runtime and models.'
        } else { Write-Host "Kept the runtime and models in $runtime; running Setup.cmd again reuses them." }
    }
    if (Test-Path -LiteralPath $InstallRoot) { Write-Host "Presets and options stay in $InstallRoot." }
} catch {
    Write-Host "Uninstall did not finish: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
