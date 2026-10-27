# Shared by Setup.cmd, Uninstall.cmd and the test runners.

# Every VEGAS Pro from 22 on, newest first: its version ('22.0'), program folder, API, program
# file, process name and the extension folder it loads for the current user. Each version
# records its folder in the registry when it is installed.
function Get-VegasInstalls {
    $key = 'HKLM:\SOFTWARE\VEGAS Creative Software\VEGAS Pro'
    $found = @()
    foreach ($entry in Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue) {
        $version = $entry.PSChildName
        $number = 0.0
        if (-not [double]::TryParse($version, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -lt 22) { continue }
        $folder = (Get-ItemProperty -LiteralPath $entry.PSPath -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
        if (-not $folder -or -not (Test-Path -LiteralPath (Join-Path $folder 'ScriptPortal.Vegas.dll'))) { continue }
        $folder = $folder.TrimEnd('\')
        # vegas220.exe for 22.0, vegas230.exe for 23.0.
        $program = Get-ChildItem -LiteralPath $folder -Filter 'vegas*.exe' | Where-Object { $_.Name -match '^vegas\d+\.exe$' } | Select-Object -First 1
        if (-not $program) { continue }
        $found += [pscustomobject]@{ Version = $version; Number = $number; Directory = $folder; Api = Join-Path $folder 'ScriptPortal.Vegas.dll'
            Executable = $program.FullName; Process = $program.BaseName
            Extensions = Join-Path $env:LOCALAPPDATA "VEGAS Pro\$version\Application Extensions" }
    }
    return @($found | Sort-Object Number -Descending)
}

# The VEGAS Pro processes that are running, of the versions given.
function Get-RunningVegas($installs) {
    return @(Get-Process -Name @($installs | ForEach-Object { $_.Process }) -ErrorAction SilentlyContinue)
}

# pip and NeMo log to stderr, which Windows PowerShell turns into errors when output is
# redirected; success is judged by the exit code instead.
function Invoke-Native([string]$failure, [scriptblock]$command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw $failure }
}
