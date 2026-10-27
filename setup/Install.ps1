# Subtitler setup, run by Setup.cmd. Installs everything Subtitler needs for the current
# Windows user, without administrator rights: its own Python, recognition and diarization
# environments, FFmpeg, the llama.cpp correction engine and a compiler, all pinned to
# reviewed versions and checked against their SHA-256; then it builds the panel, downloads
# the models of the pipeline tier that suits the graphics card and installs the panel into
# every VEGAS Pro from 22 on. It first shows what it will download and asks to continue. Running it again
# skips what is already installed, so it also updates and repairs an installation.
#
# The console shows one line per step, redrawn as a progress bar or spinner; everything the
# tools print goes to setup.log in the install folder.
[CmdletBinding()]
param(
    # Where the runtime, the worker and the models go; the panel keeps its own settings here too.
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Subtitler'),
    # Another extension folder installs the newest VEGAS Pro's build there, without touching
    # VEGAS (for tests). By default each installed VEGAS Pro gets its own build in its own folder.
    [string]$ExtensionsDirectory,
    # The tier whose models download now, instead of the panel's or the graphics card's.
    [ValidateSet('high', 'medium', 'low')][string]$Tier,
    # Never wait at a prompt: continue without asking, and fail if VEGAS is open.
    [switch]$NoPrompt
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's own progress display slows downloads and is replaced by the step lines.
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'Common.ps1')
$source = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $InstallRoot 'runtime'
$cache = Join-Path $runtime 'cache'
$models = Join-Path $cache 'models'
$tar = Join-Path $env:WINDIR 'System32\tar.exe'
$watch = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------- what setup downloads

# Each archive is pinned to a reviewed release, its size and its SHA-256.
$pins = @{
    Python = @{ Url = 'https://github.com/astral-sh/python-build-standalone/releases/download/20261009/cpython-3.12.15%2B20261009-x86_64-pc-windows-msvc-install_only.tar.gz'
        Hash = '194C8F0D9E0DE6F1C4576E5DFA67819A48C764BE87608B74B8AD43387A57B93B'; Size = 46352482 }
    Compiler = @{ Url = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/5.9.0/microsoft.net.compilers.toolset.5.9.0.nupkg'
        Hash = 'B0227910320C5AF14D80EC32B5E1A759C1E3CC2EC12E7D9CC8862CF826BD9551'; Size = 21936368 }
    FFmpeg = @{ Url = 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip'
        Hash = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'; Size = 109728040 }
    # Qwen3-ForcedAligner Ultra's companion package, from the aligner's pinned revision.
    Aligner = @{ Url = 'https://huggingface.co/17slever17/Qwen3-ForcedAligner-Ultra/resolve/765dc1d714e4c80196782a5df5bfbcb3e1416736/companion/dist/qwen_fa_ultra-0.1.1-py3-none-any.whl'
        Hash = 'EC483201D79BD634C3ABCD78C011AB32C3B73E795470668BC009F6D2EDC6BAA1'; Size = 18371 }
}
# The llama.cpp server that runs the correction model, from the official b11840 release: the
# commit LM Studio's engine ran when Qwen3.5 9B was chosen on the gold set. CUDA 13.4 for
# Turing (GeForce 20) and newer cards with a 580 or newer driver, CUDA 12.4 for older cards
# and drivers, otherwise the CPU build.
$release = 'https://github.com/ggml-org/llama.cpp/releases/download/b11840/'
$llamaBuilds = @{
    'cuda-13.4' = @(@{ Url = $release + 'llama-b11840-bin-win-cuda-13.4-x64.zip'; Hash = '04B57803BDFD99A489A1A8248431537C583FF95A03DF4C3F2E5D232630DDF9F7'; Size = 159930804 },
                    @{ Url = $release + 'cudart-llama-bin-win-cuda-13.4-x64.zip'; Hash = '738F8C251AC22B70C3AE6F83A10CF222725DF0395246A2CF58F32BDB85FBE668'; Size = 423535356 })
    'cuda-12.4' = @(@{ Url = $release + 'llama-b11840-bin-win-cuda-12.4-x64.zip'; Hash = '519357996DFAE171EEE635CFBE9B4CA0C21BDFD85A59356B593578B41C1A2D0F'; Size = 272406409 },
                    @{ Url = $release + 'cudart-llama-bin-win-cuda-12.4-x64.zip'; Hash = '8C79A9B226DE4B3CACFD1F83D24F962D0773BE79F1E7B75C6AF4DED7E32AE1D6'; Size = 391443627 })
    'cpu' = @(,@{ Url = $release + 'llama-b11840-bin-win-cpu-x64.zip'; Hash = 'F805935DEC8A6F527BF3D587419865C1CF486BA6A430DC0381CA97AEDF0DA034'; Size = 24156518 })
}
# Measured on a fresh installation (2026-10-26). pip downloads about 3.6 GB for the speech
# engines, 3.2 GB of it PyTorch's CUDA build; installed they take 8.5 GB.
$engineDownload = 3.61GB; $engineDisk = 8.47GB; $toolsDisk = 1.36GB
# The models, by worker name: Hugging Face folder, pinned revision (as worker.py) and size.
$modelFiles = @{
    'ark-asr-3b' = @{ Label = 'ARK-ASR 3B'; Folder = 'models--Edge0--ARK-ASR-3B'; Revision = '1e28271b79edc97635783bea65abc89195a09ed3'; Size = 7.58GB }
    'qwen3-asr-1.7b' = @{ Label = 'Qwen3-ASR 1.7B'; Folder = 'models--Qwen--Qwen3-ASR-1.7B'; Revision = '7278e1e70fe206f11671096ffdd38061171dd6e5'; Size = 4.38GB }
    'parakeet-tdt-0.6b-v2' = @{ Label = 'Parakeet TDT 0.6B v2'; Folder = 'models--nvidia--parakeet-tdt-0.6b-v2'; Revision = 'ae9ad07059c7c739ffaf932226a8fe64ae2620b0'; Size = 2.30GB }
    'qwen3-ultra' = @{ Label = 'Qwen3-ForcedAligner Ultra'; Folder = 'models--17slever17--Qwen3-ForcedAligner-Ultra'; Revision = '765dc1d714e4c80196782a5df5bfbcb3e1416736'; Size = 1.73GB }
}
$tiers = @{
    high = @{ Label = 'High'; Model = 'ark-asr-3b' }
    medium = @{ Label = 'Medium'; Model = 'qwen3-asr-1.7b' }
    low = @{ Label = 'Low'; Model = 'parakeet-tdt-0.6b-v2' }
}

# ---------------------------------------------------------------- console and log

$script:log = $null
function Write-Log([string]$text) { if ($script:log) { $script:log.WriteLine($text) } }
function Format-Size([double]$bytes) { if ($bytes -ge 1GB) { '{0:N1} GB' -f ($bytes / 1GB) } else { '{0:N0} MB' -f [Math]::Max(1.0, $bytes / 1MB) } }
function Format-Bar([double]$fraction) {
    $fraction = [Math]::Max(0.0, [Math]::Min(1.0, $fraction))
    $filled = [int][Math]::Floor($fraction * 20)
    return '[' + ('#' * $filled) + ('-' * (20 - $filled)) + ']' + ('{0,4}%' -f [int][Math]::Floor($fraction * 100))
}
# One item of the plan: its name and size, and a description wrapped beside them so every
# line fits an 80-column console.
function Write-Item([string]$name, [string]$size, [string]$text) {
    $lines = New-Object Collections.Generic.List[string]
    $current = ''
    foreach ($word in $text -split ' ') {
        if ($current -and $current.Length + 1 + $word.Length -gt 50) { $lines.Add($current); $current = $word } else { $current = ($current + ' ' + $word).Trim() }
    }
    $lines.Add($current)
    Write-Host ('  {0,-16}{1,8}  {2}' -f $name, $size, $lines[0])
    foreach ($line in ($lines | Select-Object -Skip 1)) { Write-Host ((' ' * 28) + $line) }
}
# Joins phrases as a sentence: "a, b and c".
function Join-Phrases([string[]]$phrases) {
    if ($phrases.Count -le 1) { return $phrases -join '' }
    return ($phrases[0..($phrases.Count - 2)] -join ', ') + ' and ' + $phrases[-1]
}
# A spinner and the step's running time, for work without a measurable total.
function Format-Busy([string]$text) {
    $elapsed = $script:stepWatch.Elapsed
    return '{0} {1}  {2}:{3:00}' -f $text, '|/-\'[[int]($elapsed.TotalMilliseconds / 250) % 4], [int][Math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds
}
$script:step = 0; $script:shown = 0; $script:title = ''; $script:active = $false
$script:stepWatch = [Diagnostics.Stopwatch]::new(); $script:redraw = [Diagnostics.Stopwatch]::StartNew()
$script:status = { '' }
function Start-Step([string]$title) {
    $script:step++
    $script:title = ('[{0}/6] {1}' -f $script:step, $title).PadRight(33)
    $script:stepWatch.Restart(); $script:shown = 0; $script:active = $true
    Write-Log ''; Write-Log "== $title"
    Write-Status ''
}
# Redraws the current step's line in place.
function Write-Status([string]$text) {
    if (-not $script:active) { return }
    $line = $script:title + $text
    try { $width = [Console]::WindowWidth - 1 } catch { $width = 79 }
    if ($width -gt 20 -and $line.Length -gt $width) { $line = $line.Substring(0, $width) }
    [Console]::Write("`r" + $line + (' ' * [Math]::Max(0, $script:shown - $line.Length)))
    $script:shown = $line.Length
}
function Update-Status([switch]$Force) {
    if (-not $Force -and $script:redraw.ElapsedMilliseconds -lt 150) { return }
    $script:redraw.Restart()
    Write-Status (& $script:status)
}
function Complete-Step([string]$text) {
    Write-Status $text
    [Console]::WriteLine()
    $script:shown = 0; $script:active = $false
    Write-Log "$text ($([int]$script:stepWatch.Elapsed.TotalSeconds) s)"
}

# Quotes one argument for a Windows command line.
function ConvertTo-Argument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $escaped = [regex]::Replace($value, '(\\*)"', { param($m) ($m.Groups[1].Value * 2) + '\"' })
    $escaped = [regex]::Replace($escaped, '(\\+)$', { param($m) $m.Groups[1].Value * 2 })
    return '"' + $escaped + '"'
}
# Runs a program with its output in the log rather than on the console, redrawing the step
# line while it works. onLine sees each line of its standard output.
function Invoke-Quiet([string]$failure, [string]$file, [string[]]$arguments, [scriptblock]$onLine) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $file
    $info.Arguments = ($arguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' '
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
    $info.StandardErrorEncoding = New-Object Text.UTF8Encoding $false
    $info.EnvironmentVariables['PYTHONIOENCODING'] = 'utf-8'
    $info.EnvironmentVariables['HF_HUB_DISABLE_SYMLINKS_WARNING'] = '1'
    Write-Log "> $file $($info.Arguments)"
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $next = $process.StandardOutput.ReadLineAsync()
        while ($true) {
            if ($next.Wait(150)) {
                $text = $next.Result
                if ($null -eq $text) { break }
                if (-not $text.StartsWith('Progress ')) { Write-Log $text }
                if ($onLine) { & $onLine $text }
                $next = $process.StandardOutput.ReadLineAsync()
            }
            Update-Status
        }
        $process.WaitForExit()
        Write-Log $errors.Result
        if ($process.ExitCode -ne 0) { throw $failure }
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}
# Downloads a pinned archive into a new, empty folder and checks its SHA-256 before anything
# uses it. onBytes sees the bytes received so far.
function Save-Pinned($pin, [string]$folder, [scriptblock]$onBytes) {
    $path = Join-Path $folder ([Uri]::UnescapeDataString(($pin.Url -split '/')[-1]))
    Write-Log "Downloading $($pin.Url)"
    $request = [Net.HttpWebRequest]::Create($pin.Url)
    $request.UserAgent = 'Subtitler-Setup'
    $response = $request.GetResponse()
    try {
        $stream = $response.GetResponseStream()
        $file = [IO.File]::Create($path)
        try {
            $buffer = New-Object byte[] (1MB)
            $received = 0
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $file.Write($buffer, 0, $read)
                $received += $read
                & $onBytes $received
                Update-Status
            }
        } finally { $file.Dispose(); $stream.Dispose() }
    } finally { $response.Dispose() }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $pin.Hash) {
        Remove-Item -LiteralPath $path
        throw "$(Split-Path -Leaf $path) does not match the reviewed version."
    }
    return $path
}

# ---------------------------------------------------------------- installed components

function New-Staging { $folder = Join-Path $runtime ('staging-' + [Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $folder | Out-Null; return $folder }
# A component is current when its folder holds the marker setup wrote for this version.
function Test-Marker([string]$folder, [string]$wanted) {
    $marker = Join-Path $folder 'subtitler-build.txt'
    return (Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq $wanted
}
# Replaces a component's folder with a staged one and marks it.
function Install-Component([string]$staged, [string]$folder, [string]$wanted) {
    Set-Content -LiteralPath (Join-Path $staged 'subtitler-build.txt') -Value $wanted -Encoding ASCII
    if (Test-Path -LiteralPath $folder) {
        try { Remove-Item -LiteralPath $folder -Recurse -Force }
        catch { throw "$(Split-Path -Leaf $folder) is in use. Close VEGAS, then run Setup.cmd again. $($_.Exception.Message)" }
    }
    Move-Item -LiteralPath $staged -Destination $folder
}
# Environments made from another Python 3.12 are moved onto the bundled one: a virtual
# environment finds its interpreter through the home line of pyvenv.cfg.
function Set-EnvironmentHome([string]$venv) {
    $config = Join-Path $venv 'pyvenv.cfg'
    if (-not (Test-Path -LiteralPath $config)) { return }
    $lines = [IO.File]::ReadAllLines($config)
    $oldHome = ($lines | Where-Object { $_ -match '^home\s*=' } | Select-Object -First 1) -replace '^home\s*=\s*', ''
    $version = ($lines | Where-Object { $_ -match '^version(_info)?\s*=' } | Select-Object -First 1) -replace '^version(_info)?\s*=\s*', ''
    if ([IO.Path]::GetFullPath($oldHome).TrimEnd('\') -eq [IO.Path]::GetFullPath($baseRoot).TrimEnd('\')) { return }
    if (-not $version.StartsWith('3.12.')) { throw "$venv was made with Python $version, not 3.12. Delete it and run Setup.cmd again." }
    $updated = $lines | ForEach-Object {
        if ($_ -match '^home\s*=') { "home = $baseRoot" }
        elseif ($_ -match '^executable\s*=' -and $_.Contains($oldHome)) { "executable = $basePython" }
        else { $_ }
    }
    [IO.File]::WriteAllLines($config, [string[]]$updated)
    Write-Log "Moved $(Split-Path -Leaf $venv) onto the bundled Python (was $oldHome)."
}
# Copies a file into an extension folder. Windows permits renaming a loaded assembly even
# when overwriting it is blocked; a running VEGAS keeps its old copy until it restarts.
function Install-ExtensionFile([string]$file, [string]$folder, [string]$name) {
    $target = [IO.Path]::GetFullPath((Join-Path $folder $name))
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $file).Hash -eq (Get-FileHash -LiteralPath $target).Hash) { return }
    $staged = $target + '.new-' + [Guid]::NewGuid().ToString('N')
    $backup = $target + '.previous-' + [Guid]::NewGuid().ToString('N')
    $movedPrevious = $false
    try {
        Copy-Item -LiteralPath $file -Destination $staged
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup; $movedPrevious = $true }
        Move-Item -LiteralPath $staged -Destination $target
    } catch {
        if ($movedPrevious -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
        throw "Could not update $name. Close VEGAS, then run Setup.cmd again. $($_.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged }
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -ErrorAction SilentlyContinue }
}

try {
    Write-Host ''
    Write-Host 'Subtitler setup for VEGAS Pro' -ForegroundColor White
    Write-Host ''

    # ------------------------------------------------------------ this PC
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Subtitler needs 64-bit Windows.' }
    # Each VEGAS Pro gets a panel built against its own API, in its own extension folder.
    $installs = Get-VegasInstalls
    if (-not $installs) { throw 'VEGAS Pro 22 or newer is not installed. Install it, then run Setup.cmd again.' }
    $targets = if ($ExtensionsDirectory) { @($installs[0] | Select-Object Version, Api, @{ Name = 'Extensions'; Expression = { $ExtensionsDirectory } }) } else { $installs }
    $versions = ($targets | ForEach-Object { $_.Version }) -join ' and '
    # Environments and the extension cannot be replaced while VEGAS uses them.
    if (-not $ExtensionsDirectory) {
        while (Get-RunningVegas $installs) {
            if ($NoPrompt) { throw 'VEGAS Pro is open. Close it, then run Setup.cmd again.' }
            Read-Host 'VEGAS Pro is open. Save your work and close it, then press Enter to continue' | Out-Null
        }
    }
    $gpu = $null
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($smi) {
        # A driver that fails to answer counts as no NVIDIA card.
        try {
            $gpu = & $smi.Source --query-gpu=name,memory.total,driver_version,compute_cap --format=csv,noheader,nounits 2>$null |
                ForEach-Object { $name, $memory, $driver, $capability = $_.Split(',') | ForEach-Object { $_.Trim() }
                    [pscustomobject]@{ Name = $name; Memory = [int]$memory
                        Driver = [double]::Parse($driver, [Globalization.CultureInfo]::InvariantCulture)
                        Capability = [double]::Parse($capability, [Globalization.CultureInfo]::InvariantCulture) } } |
                Sort-Object Memory -Descending | Select-Object -First 1
        } catch { $gpu = $null }
    }
    # The tier the panel will use: the one it saved, or else the one the graphics card suits.
    $panelSettings = Join-Path $InstallRoot 'settings.json'
    $newDefault = $false
    if (-not $Tier -and (Test-Path -LiteralPath $panelSettings)) {
        $saved = (Get-Content -LiteralPath $panelSettings -Raw | ConvertFrom-Json).Pipeline
        $Tier = if ($saved -and $tiers.ContainsKey($saved)) { $saved } else { 'high' }
    }
    if (-not $Tier) {
        $Tier = if ($gpu -and $gpu.Memory -ge 15000) { 'high' } elseif ($gpu -and $gpu.Memory -ge 7500) { 'medium' } else { 'low' }
        $newDefault = $true
    }
    $recognizer = $modelFiles[$tiers[$Tier].Model]
    $variant = 'cpu'
    if ($gpu -and $gpu.Driver -ge 580 -and $gpu.Capability -ge 7.5) { $variant = 'cuda-13.4' } elseif ($gpu -and $gpu.Driver -ge 528) { $variant = 'cuda-12.4' }

    # ------------------------------------------------------------ what is missing
    $baseRoot = Join-Path $runtime 'base'
    $basePython = Join-Path $baseRoot 'python.exe'
    $compilerRoot = Join-Path $runtime 'compiler'
    $ffmpegRoot = Join-Path $runtime 'ffmpeg'
    $llamaRoot = Join-Path $runtime 'llama'
    $recognition = Join-Path $runtime 'recognition'
    $recognitionPython = Join-Path $recognition 'Scripts\python.exe'
    $diarization = Join-Path $runtime 'diarization'
    $diarizationPython = Join-Path $diarization 'Scripts\python.exe'
    $lock = Join-Path $source 'src\Subtitler.Worker\requirements-lock.txt'
    $packages = 'torch 2.8.0 cu128; qwen-asr 0.0.6; qwen-fa-ultra 0.1.1; lock ' + (Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash
    $overlay = 'transformers 5.20.2; huggingface-hub 1.31.0; tokenizers 0.23.1'
    $tools = @()
    if (-not (Test-Path -LiteralPath $basePython)) { $tools += @{ Pin = $pins.Python; Name = 'Python' } }
    if (-not (Test-Marker $compilerRoot 'roslyn 5.9.0')) { $tools += @{ Pin = $pins.Compiler; Name = 'C# compiler' } }
    if (-not (Test-Marker $ffmpegRoot '8.1.2 essentials')) { $tools += @{ Pin = $pins.FFmpeg; Name = 'FFmpeg' } }
    if (-not (Test-Marker $llamaRoot "b11840 $variant")) { foreach ($pin in $llamaBuilds[$variant]) { $tools += @{ Pin = $pin; Name = 'llama.cpp' } } }
    $toolsDownload = [double]($tools | ForEach-Object { $_.Pin.Size } | Measure-Object -Sum).Sum
    $freshEngines = -not (Test-Path -LiteralPath $recognitionPython)
    $missingModels = @(@($tiers[$Tier].Model, 'qwen3-ultra') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $models "$($modelFiles[$_].Folder)\snapshots\$($modelFiles[$_].Revision)")) })
    $modelDownload = [double]($missingModels | ForEach-Object { $modelFiles[$_].Size } | Measure-Object -Sum).Sum
    $download = $toolsDownload + $modelDownload + $(if ($freshEngines) { $engineDownload } else { 0 })
    $disk = $modelDownload + $(if ($freshEngines) { $engineDisk + $toolsDisk } else { $toolsDownload })

    # ------------------------------------------------------------ the plan
    $card = if ($gpu) { '{0} ({1:N0} GB)' -f $gpu.Name, ($gpu.Memory / 1024) } else { 'no NVIDIA card found: recognition runs on the CPU, which is much slower' }
    Write-Host "VEGAS Pro       $versions"
    Write-Host "Graphics card   $card"
    Write-Host "Pipeline        $($tiers[$Tier].Label) ($($recognizer.Label))$(if ($newDefault) { ', which suits this graphics card' })"
    Write-Host "Install folder  $InstallRoot"
    Write-Host ''
    if ($download -ge 50MB) {
        Write-Host ("Setup will download about {0} and use about {1} of disk space:" -f (Format-Size $download), (Format-Size $disk))
        Write-Host ''
        if ($freshEngines) {
            Write-Item 'Speech engines' (Format-Size $engineDownload) ("PyTorch with CUDA and NVIDIA NeMo run the models on the graphics card; {0} installed." -f (Format-Size $engineDisk))
        }
        if ($missingModels) {
            $roles = @{ 'qwen3-ultra' = 'Qwen3-ForcedAligner Ultra times each word' }
            Write-Item "$($tiers[$Tier].Label) models" (Format-Size $modelDownload) ((($missingModels | ForEach-Object { if ($roles[$_]) { $roles[$_] } else { "$($modelFiles[$_].Label) turns speech into text" } }) -join '; ') + '.')
        }
        if ($tools) {
            $roles = [ordered]@{ 'Python' = 'Python'; 'FFmpeg' = 'FFmpeg to read audio'; 'llama.cpp' = 'llama.cpp for text correction'; 'C# compiler' = 'a compiler to build the panel' }
            $names = @($tools | ForEach-Object { $_.Name } | Select-Object -Unique)
            Write-Item 'Tools' (Format-Size $toolsDownload) ((Join-Phrases @($roles.Keys | Where-Object { $names -contains $_ } | ForEach-Object { $roles[$_] })) + '.')
        }
        Write-Host ''
        Write-Host 'Downloaded later, only when you first turn them on in the panel:'
        if ($Tier -eq 'high') { Write-Item 'Text correction' '6.9 GB' 'Qwen3.5 9B fixes punctuation and homophones.' }
        Write-Item 'Speaker labels' '0.6 GB' 'Nemotron 3 tells speakers apart.'
        Write-Item 'Other pipelines' '2-8 GB' 'The models of each other pipeline you switch to.'
        Write-Host ''
        Write-Host 'Everything runs on this PC: no account needed, and your audio never leaves it.'
        if ($freshEngines) { Write-Host 'On a fast connection this takes about 10 minutes.' }
        Write-Host ''
        $free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($InstallRoot)).Substring(0, 1))).Free
        if ($free -lt $disk + 2GB) { throw ('{0} has {1} free; setup needs about {2}. Free some space and run Setup.cmd again.' -f [IO.Path]::GetPathRoot($InstallRoot), (Format-Size $free), (Format-Size ($disk + 2GB))) }
        if ($NoPrompt) { Write-Host 'Continue? [Y/N] Y' }
        elseif ((Read-Host 'Continue? [Y/N]') -notmatch '^\s*(y|yes)\s*$') {
            Write-Host 'Setup cancelled. Nothing was downloaded or installed.'
            exit 0
        }
    } else {
        Write-Host 'Subtitler is installed; setup updates it and repairs anything missing.'
    }
    Write-Host ''

    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    $logPath = Join-Path $InstallRoot 'setup.log'
    $script:log = New-Object IO.StreamWriter $logPath, $true, (New-Object Text.UTF8Encoding $false)
    $script:log.AutoFlush = $true
    Write-Log ''; Write-Log "Subtitler setup, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), from $source"
    Write-Log "VEGAS Pro: $(($targets | ForEach-Object { '{0} into {1}' -f $_.Version, $_.Extensions }) -join '; '); graphics card: $card; tier: $Tier; llama.cpp: $variant"
    if ($newDefault) { [IO.File]::WriteAllText($panelSettings, (@{ Pipeline = $Tier } | ConvertTo-Json)) }

    # ------------------------------------------------------------ 1. tools
    Start-Step 'Downloading tools'
    if ($tools) {
        $progress = @{ Done = 0.0; Current = 0.0; Text = 'downloading' }
        $script:status = { if ($progress.Text -eq 'downloading') { (Format-Bar (($progress.Done + $progress.Current) / $toolsDownload)) + '  ' + (Format-Size ($progress.Done + $progress.Current)) + ' / ' + (Format-Size $toolsDownload) } else { Format-Busy $progress.Text } }
        $staging = New-Staging
        try {
            $archives = foreach ($tool in $tools) {
                $path = Save-Pinned $tool.Pin $staging { param($bytes) $progress.Current = $bytes }
                $progress.Done += $tool.Pin.Size; $progress.Current = 0
                @{ Name = $tool.Name; Path = $path }
            }
            $progress.Text = 'unpacking'
            foreach ($archive in $archives) {
                $folder = Join-Path $staging ('unpacked-' + [IO.Path]::GetFileNameWithoutExtension($archive.Path))
                New-Item -ItemType Directory -Path $folder | Out-Null
                Invoke-Quiet "$($archive.Name) could not be unpacked." $tar @('-xf', $archive.Path, '-C', $folder)
                $archive.Folder = $folder
            }
            $unpacked = @{}
            foreach ($archive in $archives) {
                if (-not $unpacked.ContainsKey($archive.Name)) { $unpacked[$archive.Name] = New-Object Collections.Generic.List[string] }
                $unpacked[$archive.Name].Add($archive.Folder)
            }
            if ($unpacked['Python']) { Move-Item -LiteralPath (Join-Path $unpacked['Python'][0] 'python') -Destination $baseRoot }
            if ($unpacked['C# compiler']) { Install-Component (Join-Path $unpacked['C# compiler'][0] 'tasks\net472') $compilerRoot 'roslyn 5.9.0' }
            if ($unpacked['FFmpeg']) { Install-Component (Join-Path $unpacked['FFmpeg'][0] 'ffmpeg-8.1.2-essentials_build') $ffmpegRoot '8.1.2 essentials' }
            if ($unpacked['llama.cpp']) {
                # The server and its CUDA runtime come in separate archives; they share one folder.
                $engine = $unpacked['llama.cpp'][0]
                foreach ($other in ($unpacked['llama.cpp'] | Select-Object -Skip 1)) { Get-ChildItem -LiteralPath $other | Move-Item -Destination $engine -Force }
                if (-not (Test-Path -LiteralPath (Join-Path $engine 'llama-server.exe'))) { throw 'The llama.cpp release has no llama-server.exe.' }
                Install-Component $engine $llamaRoot "b11840 $variant"
            }
        } finally { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    $compiler = Join-Path $compilerRoot 'csc.exe'
    $ffmpeg = Join-Path $ffmpegRoot 'bin\ffmpeg.exe'
    $llamaServer = Join-Path $llamaRoot 'llama-server.exe'
    $script:status = { Format-Busy 'checking' }
    Invoke-Quiet 'FFmpeg does not start.' $ffmpeg @('-nostdin', '-hide_banner', '-version')
    Invoke-Quiet 'The correction engine does not start.' $llamaServer @('--version')
    Complete-Step $(if ($tools) { 'done ({0})' -f (Format-Size $toolsDownload) } else { 'up to date' })

    # ------------------------------------------------------------ 2. the panel
    Start-Step 'Building the panel'
    $build = Join-Path $runtime 'build'
    foreach ($target in $targets) {
        $script:status = [scriptblock]::Create("Format-Busy 'compiling for VEGAS Pro $($target.Version)'")
        Update-Status -Force
        & (Join-Path $PSScriptRoot 'Build.ps1') -Compiler $compiler -Api $target.Api -Output (Join-Path $build $target.Version) *>&1 | ForEach-Object { Write-Log "$_" }
    }
    Complete-Step 'done'

    # ------------------------------------------------------------ 3. speech engines
    # ARK-ASR, Qwen3-ASR, Parakeet, the Qwen aligner and the correction stage share one
    # environment. PyTorch's CUDA 12.8 build runs on GeForce 20 to 50 series cards and falls
    # back to the CPU. qwen-asr declares its demo server's dependencies (gradio, Flask, an
    # older accelerate); its forced aligner needs only what the lock file installed.
    Start-Step 'Installing speech engines'
    Set-EnvironmentHome $recognition
    if (-not (Test-Marker $recognition $packages)) {
        $pip = @{ Done = 0.0; Current = 0.0; Text = 'preparing' }
        $script:status = { if ($pip.Text -eq 'downloading' -and $freshEngines) {
                $received = $pip.Done + $pip.Current; $total = [Math]::Max($engineDownload, $received)
                (Format-Bar ([Math]::Min(.99, $received / $total))) + '  ' + (Format-Size $received) + ' / ' + (Format-Size $total)
            } else { Format-Busy $pip.Text } }
        # pip's raw progress gives each download's bytes; the total is the measured size.
        $onPip = { param($line)
            if ($line -match '^Progress (\d+) of (\d+)$') {
                $pip.Text = 'downloading'
                if ($matches[1] -eq $matches[2]) { $pip.Done += [double]$matches[2]; $pip.Current = 0 } else { $pip.Current = [double]$matches[1] }
            } elseif ($line.StartsWith('Installing collected packages')) { $pip.Text = 'installing packages' } }
        if (-not (Test-Path -LiteralPath $recognitionPython)) {
            $pip.Text = 'creating the environment'
            Invoke-Quiet 'The recognition environment could not be created.' $basePython @('-m', 'venv', $recognition)
        }
        $quiet = @('-m', 'pip', 'install', '--disable-pip-version-check', '--progress-bar', 'raw')
        Invoke-Quiet 'PyTorch could not be installed.' $recognitionPython ($quiet + @('torch==2.8.0', 'torchaudio==2.8.0', 'torchvision==0.23.0', '--index-url', 'https://download.pytorch.org/whl/cu128')) $onPip
        Invoke-Quiet 'The speech engines could not be installed.' $recognitionPython ($quiet + @('-r', $lock)) $onPip
        $staging = New-Staging
        try {
            $wheel = Save-Pinned $pins.Aligner $staging { param($bytes) }
            Invoke-Quiet 'The Qwen3 aligner could not be installed.' $recognitionPython ($quiet + @('--no-deps', 'qwen-asr==0.0.6', $wheel)) $onPip
        } finally { Remove-Item -LiteralPath $staging -Recurse -Force }
        $pip.Text = 'checking'
        Invoke-Quiet 'The speech engines do not import cleanly.' $recognitionPython @('-W', 'ignore', '-c', 'import torch, nemo.collections.asr, faster_whisper, qwen_asr, qwen_fa_ultra; print(torch.cuda.is_available())')
        Set-Content -LiteralPath (Join-Path $recognition 'subtitler-build.txt') -Value $packages -Encoding ASCII
        Complete-Step $(if ($freshEngines) { 'done ({0})' -f (Format-Size $engineDisk) } else { 'updated' })
    } else { Complete-Step 'up to date' }

    # ------------------------------------------------------------ 4. diarization
    # Nemotron 3 needs a newer Transformers than the speech engines: an overlay that shares
    # the recognition environment's PyTorch, so no CUDA libraries are duplicated.
    Start-Step 'Installing speaker labels'
    $script:status = { Format-Busy 'installing' }
    Set-EnvironmentHome $diarization
    if (-not (Test-Path -LiteralPath $diarizationPython)) { Invoke-Quiet 'The diarization environment could not be created.' $recognitionPython @('-m', 'venv', $diarization) }
    [IO.File]::WriteAllText((Join-Path $diarization 'Lib\site-packages\subtitler-torch.pth'), (Join-Path $recognition 'Lib\site-packages') + [Environment]::NewLine)
    if (-not (Test-Marker $diarization $overlay)) {
        Invoke-Quiet 'The diarization packages could not be installed.' $diarizationPython @('-m', 'pip', 'install', '--disable-pip-version-check', '--no-deps', 'transformers==5.20.2', 'huggingface-hub==1.31.0', 'tokenizers==0.23.1')
        Invoke-Quiet 'The diarization environment does not import cleanly.' $diarizationPython @('-W', 'ignore', '-c', 'from transformers import Nemotron3DiarizationForAudioFrameClassification; import pyannote.audio')
        Set-Content -LiteralPath (Join-Path $diarization 'subtitler-build.txt') -Value $overlay -Encoding ASCII
        Complete-Step 'done'
    } else { Complete-Step 'up to date' }

    # ------------------------------------------------------------ 5. models
    # The worker runs from the install folder, so the setup folder can be deleted afterwards.
    $worker = Join-Path $InstallRoot 'worker'
    New-Item -ItemType Directory -Path $worker -Force | Out-Null
    Get-ChildItem -LiteralPath $worker -Filter '*.py' | Remove-Item
    Copy-Item -Path (Join-Path $source 'src\Subtitler.Worker\*.py') -Destination $worker
    # The tier's recognizer and the aligner download now; other tiers, diarization and
    # correction download on first use. The worker reports each model's percentage.
    Start-Step "Downloading $($tiers[$Tier].Label) models"
    $wanted = @($tiers[$Tier].Model, 'qwen3-ultra')
    $sizes = @{}; foreach ($name in $wanted) { $sizes[$modelFiles[$name].Label] = $(if ($missingModels -contains $name) { $modelFiles[$name].Size } else { 0 }) }
    $fetch = @{ Done = 0.0; Current = 0.0; Text = 'checking' }
    $script:status = { if ($modelDownload -gt 0 -and $fetch.Text -eq 'downloading') { (Format-Bar (($fetch.Done + $fetch.Current) / $modelDownload)) + '  ' + (Format-Size ($fetch.Done + $fetch.Current)) + ' / ' + (Format-Size $modelDownload) } else { Format-Busy $fetch.Text } }
    $onModel = { param($line)
        if (-not $line.StartsWith('{')) { return }
        $message = $line | ConvertFrom-Json
        $size = [double]$sizes[$message.ModelName]
        if ($message.Stage -eq 'Downloading' -and $null -ne $message.Percent) { $fetch.Text = 'downloading'; $fetch.Current = $size * $message.Percent / 100 }
        elseif ($message.Stage -eq 'Downloaded') { $fetch.Done += $size; $fetch.Current = 0 } }
    Invoke-Quiet 'The models could not be downloaded.' $recognitionPython (@((Join-Path $worker 'worker.py'), '--download') + $wanted + @('--cache', $cache)) $onModel
    # Qwen3.5 9B, the correction model, downloads on first use. A copy LM Studio already
    # downloaded is reused if it is the reviewed file: hard-linked, so it takes no space, or
    # copied from another drive. Nothing reads LM Studio's folder after this.
    $corrector = Join-Path $models 'models--lmstudio-community--Qwen3.5-9B-GGUF\snapshots\1379f25c6b505a3fc737bd7818cb09389cf807c1\Qwen3.5-9B-Q6_K.gguf'
    $downloaded = Join-Path $env:USERPROFILE '.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q6_K.gguf'
    if (-not (Test-Path -LiteralPath $corrector) -and (Test-Path -LiteralPath $downloaded) -and (Get-Item -LiteralPath $downloaded).Length -eq 7359259040) {
        Write-Status (Format-Busy 'checking LM Studio''s Qwen3.5 9B')
        if ((Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash -eq 'B2CCC477F0A2449DE299BEF560B63E498CAE94A2F3A3BAE13DF01168D77773E6') {
            New-Item -ItemType Directory -Path (Split-Path $corrector) -Force | Out-Null
            try { New-Item -ItemType HardLink -Path $corrector -Target $downloaded | Out-Null }
            catch { Copy-Item -LiteralPath $downloaded -Destination $corrector }
            Write-Log 'Reused the Qwen3.5 9B model LM Studio downloaded for text correction.'
        }
    }
    Complete-Step $(if ($modelDownload -gt 0) { 'done ({0})' -f (Format-Size $modelDownload) } else { 'up to date' })

    # ------------------------------------------------------------ 6. VEGAS
    Start-Step 'Installing into VEGAS'
    $settings = [ordered]@{ PythonPath = $recognitionPython; DiarizationPythonPath = $diarizationPython; LlamaServerPath = $llamaServer
        WorkerPath = (Join-Path $worker 'worker.py'); FFmpegPath = $ffmpeg; Device = 'auto'; CacheDirectory = $cache }
    $settingsPath = Join-Path $runtime 'worker-settings.json'
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json))
    foreach ($target in $targets) {
        New-Item -ItemType Directory -Path $target.Extensions -Force | Out-Null
        foreach ($name in @('Subtitler.Core.dll', 'Subtitler.Vegas.dll')) { Install-ExtensionFile (Join-Path $build "$($target.Version)\$name") $target.Extensions $name }
        Install-ExtensionFile $settingsPath $target.Extensions 'Subtitler.Worker.settings.json'
        # Removes copies earlier updates left behind while VEGAS had the extension loaded.
        Get-ChildItem -LiteralPath $target.Extensions -Filter 'Subtitler.*' | Where-Object { $_.Name -match '\.(previous|new)-[0-9a-f]{32}$' } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
        Write-Log "Installed into $($target.Extensions)"
    }
    Complete-Step 'done'

    Write-Host ''
    Write-Host ('Subtitler is ready ({0}:{1:00}).' -f [int][Math]::Floor($watch.Elapsed.TotalMinutes), $watch.Elapsed.Seconds) -ForegroundColor Green
    Write-Host 'Open VEGAS Pro, choose View > Extensions > Subtitler, select audio events and click Generate.'
    Write-Log "Finished in $([int]$watch.Elapsed.TotalSeconds) s."
} catch {
    if ($script:shown) { [Console]::WriteLine() }
    Write-Host ''
    Write-Host "Setup did not finish: $($_.Exception.Message)" -ForegroundColor Red
    Write-Log "Failed: $($_.Exception.Message)"; Write-Log $_.ScriptStackTrace
    if ($script:log) { Write-Host "Details are in $logPath. Run Setup.cmd again to retry; finished steps are skipped." }
    exit 1
} finally {
    if ($script:log) { $script:log.Dispose() }
}
