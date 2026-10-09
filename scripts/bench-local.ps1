# Runs the benchmarks on this (Windows) machine, one in-process BenchmarkDotNet run per (class, file), one after
# another, each pinned to a single performance core, then prints the combined result tables.
#
# On hybrid Intel CPUs an unpinned benchmark thread can migrate to an efficiency core mid-run (Snappier compression
# is about 3.5x slower there), so every process is pinned to one P-core. Runs are sequential on purpose: parallel
# runs share turbo budget, caches and memory bandwidth, which the remote script avoids with NUMA-separate cores.
#
# Usage: scripts/bench-local.ps1 [-Framework net10.0|net8.0] [-Classes Block,Stream,...] [-Job medium] [-Files a,b]
#                                [-Cpu 2,4,6] [-NoBuild] [-BdnArgs '--runtimes','net10.0']
#   Classes  Block, Stream, SmallBlock, Messages, Corpus, Crc (default Block,Stream,SmallBlock,Corpus)
#   Files    restrict the per-file classes (Block, Stream, SmallBlock) to these corpus files
#   Cpu      logical processors to use; default: one logical processor per performance core (auto-detected)
# Results go to bench-results/<timestamp>-local-<framework>/ (ignored by git). Close other programs first and use the
# High performance power plan; a machine on battery or at its thermal limit gives noisy and lower numbers.
[CmdletBinding()]
param(
    [string] $Framework = 'net10.0',
    [string[]] $Classes = @('Block', 'Stream', 'SmallBlock', 'Corpus'),
    [string] $Job = 'medium',
    [string[]] $Files = @(),
    [string[]] $Cpu = @(),
    [switch] $NoBuild,
    [string[]] $BdnArgs = @()
)

$ErrorActionPreference = 'Stop'
# powershell -File passes 'a,b' as one string; accept both forms
$Classes = @($Classes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$cpuList = @($Cpu | ForEach-Object { "$_" -split "," } | Where-Object { $_ } | ForEach-Object { [int]$_ })
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

# One logical processor per performance core, from the Windows CPU set information (EfficiencyClass: higher is faster)
function Get-PerformanceCpus {
    Add-Type -Namespace Native -Name CpuSets -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetSystemCpuSetInformation(System.IntPtr info, uint length, out uint returned, System.IntPtr process, uint flags);
'@
    $len = [uint32]0
    [void][Native.CpuSets]::GetSystemCpuSetInformation([IntPtr]::Zero, 0, [ref]$len, [IntPtr]::Zero, 0)
    $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal([int]$len)
    try {
        if (-not [Native.CpuSets]::GetSystemCpuSetInformation($buf, $len, [ref]$len, [IntPtr]::Zero, 0)) { return @() }
        $sets = @(); $off = 0
        while ($off -lt $len) {
            $size = [Runtime.InteropServices.Marshal]::ReadInt32($buf, $off)
            $type = [Runtime.InteropServices.Marshal]::ReadInt32($buf, $off + 4)
            if ($type -eq 0) {
                $sets += [pscustomobject]@{
                    Group = [Runtime.InteropServices.Marshal]::ReadInt16($buf, $off + 12)
                    Logical = [Runtime.InteropServices.Marshal]::ReadByte($buf, $off + 14)
                    Core = [Runtime.InteropServices.Marshal]::ReadByte($buf, $off + 15)
                    Efficiency = [Runtime.InteropServices.Marshal]::ReadByte($buf, $off + 18)
                }
            }
            $off += $size
        }
    } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buf) }
    $best = ($sets | Measure-Object Efficiency -Maximum).Maximum
    $cores = @($sets | Where-Object { $_.Group -eq 0 -and $_.Efficiency -eq $best } | Group-Object Core |
        ForEach-Object { ($_.Group | Sort-Object Logical)[0].Logical } | Sort-Object)
    # Leave the first core to the OS (interrupts, the shell) when there are enough
    if ($cores.Count -gt 2) { $cores = $cores[1..($cores.Count - 1)] }
    return $cores
}

if ($cpuList.Count -eq 0) { $cpuList = @(Get-PerformanceCpus | ForEach-Object { [int]$_ }) }
if ($cpuList.Count -eq 0) { throw 'Could not detect performance cores; pass -Cpu explicitly.' }
if ($cpuList | Where-Object { $_ -ge 64 }) { throw 'Only processor group 0 (logical processors 0-63) is supported.' }
Write-Host "Pinning to logical processors: $($cpuList -join ', ')"

if (-not $NoBuild) {
    & $dotnet build "$root\benchmarks\Snappiest.Benchmarks" -c Release -f $Framework -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$dll = "$root\benchmarks\Snappiest.Benchmarks\bin\Release\$Framework\Snappiest.Benchmarks.dll"

$allFiles = 'alice29.txt', 'asyoulik.txt', 'fireworks.jpeg', 'geo.protodata', 'html', 'html_x_4', 'kppkn.gtb',
    'lcet10.txt', 'paper-100k.pdf', 'plrabn12.txt', 'urls.10K', 'json_api.json', 'json_indented.json', 'events.ndjson'
$streamFiles = 'alice29.txt', 'fireworks.jpeg', 'html_x_4', 'urls.10K', 'json_api.json', 'events.ndjson'
$smallFiles = 'html', 'fireworks.jpeg'
function Select-Files($default) { if ($Files.Count) { @($default | Where-Object { $Files -contains $_ }) } else { $default } }

$jobs = @()
foreach ($c in $Classes) {
    switch ($c) {
        'Block'      { Select-Files $allFiles    | ForEach-Object { $jobs += , @('BlockBenchmarks', $_) } }
        'Stream'     { Select-Files $streamFiles | ForEach-Object { $jobs += , @('StreamBenchmarks', $_) } }
        'SmallBlock' { Select-Files $smallFiles  | ForEach-Object { $jobs += , @('SmallBlockBenchmarks', $_) } }
        'Messages'   { $jobs += , @('MessageMixBenchmarks', '') }
        'Corpus'     { $jobs += , @('CorpusBenchmarks', '') }
        'Crc'        { $jobs += , @('Crc32CBenchmarks', '') }
        default      { $jobs += , @($c, '') }
    }
}

$run = Join-Path $root "bench-results\$(Get-Date -Format yyyyMMdd-HHmmss)-local-$Framework"
New-Item -ItemType Directory -Force $run | Out-Null
Write-Host "Running $($jobs.Count) jobs (job=$Job) -> $run"

$i = 0
foreach ($j in $jobs) {
    $cls, $file = $j
    $name = if ($file) { "$cls-$file" } else { $cls }
    $core = $cpuList[$i % $cpuList.Count]
    $env:BENCH_FILES = $file
    $env:DOTNET_TC_CallCountingDelayMs = '0'   # a pinned process sees one CPU, which stretches the tiering delay 10x
    $bdn = @("`"$dll`"", '--filter', "Snappiest.Benchmarks.$cls.*", '--inProcess', '--job', $Job,
        '--artifacts', "`"$run\$name`"", '--exporters', 'github') + $BdnArgs
    Write-Host ("[{0}/{1}] {2} on cpu {3}" -f ($i + 1), $jobs.Count, $name, $core)
    $p = Start-Process $dotnet -ArgumentList $bdn -NoNewWindow -PassThru -RedirectStandardOutput "$run\$name.log" -RedirectStandardError "$run\$name.err"
    try { $p.ProcessorAffinity = [IntPtr]([int64]1 -shl $core); $p.PriorityClass = 'High' } catch { Write-Warning "Could not pin $name : $_" }
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { Write-Warning "FAILED: $name (see $run\$name.log)" }
    $i++
}
Remove-Item Env:BENCH_FILES, Env:DOTNET_TC_CallCountingDelayMs -ErrorAction SilentlyContinue

# One combined table per class: the header once, then the rows of every file
foreach ($cls in ($jobs | ForEach-Object { $_[0] } | Select-Object -Unique)) {
    Write-Host "`n### $cls ($Framework)"
    $first = $true
    foreach ($f in Get-ChildItem "$run\$cls*\results\*-report-github.md" -ErrorAction SilentlyContinue | Sort-Object FullName) {
        $table = (Get-Content $f.FullName) | Where-Object { $_ -match '^\|' }
        if ($first) { $table[0..1]; $first = $false }
        $table | Select-Object -Skip 2
    }
}
$env1 = Get-ChildItem "$run\*\results\*-report-github.md" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($env1) {
    Write-Host "`nEnvironment:"
    (Get-Content $env1.FullName) | Where-Object { $_ -match '^(BenchmarkDotNet|\d+th Gen|.*CPU|.NET SDK|\s+\[Host\])' } | Select-Object -First 5
}
