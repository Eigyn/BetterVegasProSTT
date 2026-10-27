[CmdletBinding()]
param(
    # Roslyn's compiler for .NET Framework, which Setup.cmd installs into the runtime.
    [string]$Compiler = (Join-Path $env:LOCALAPPDATA 'Subtitler\runtime\compiler\csc.exe'),
    # The ScriptPortal.Vegas.dll of the VEGAS Pro to build for: the API is strong-named per
    # version, so a build loads only in the version it was compiled against. The newest
    # installed version by default.
    [string]$Api,
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bin')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'The compiler is not installed. Run Setup.cmd first.' }
$root = Split-Path -Parent $PSScriptRoot
# The installed VEGAS API and the Windows framework assemblies: no NuGet restore or
# targeting pack is needed.
if (-not $Api) {
    $newest = Get-VegasInstalls | Select-Object -First 1
    if (-not $newest) { throw 'VEGAS Pro 22 or newer is not installed.' }
    $Api = $newest.Api
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Subtitler.Core') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
& $Compiler /nologo /noconfig /nostdlib+ /langversion:latest /target:library /deterministic+ /optimize+ ("/out:$Output\Subtitler.Core.dll") @references @coreSources
if ($LASTEXITCODE -ne 0) { throw 'Core compilation failed.' }
$vegasSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Subtitler.Vegas') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
& $Compiler /nologo /noconfig /nostdlib+ /langversion:latest /platform:x64 /target:library /deterministic+ /optimize+ ("/out:$Output\Subtitler.Vegas.dll") @references ("/reference:$Api") ("/reference:$Output\Subtitler.Core.dll") @vegasSources
if ($LASTEXITCODE -ne 0) { throw 'VEGAS extension compilation failed.' }
Write-Output "Build complete: $Output"
