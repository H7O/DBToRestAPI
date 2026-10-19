#Requires -Version 7.2
<#
.SYNOPSIS
    Benchmarks DbToRestAPI against a hand-written ASP.NET Core Minimal API that runs the same SQL.

.DESCRIPTION
    Publishes both servers and the load generator into benchmarks/.out, creates the data (a SQLite file,
    and with -SqlServer the DbToRestApiBench database), checks that both servers answer every scenario
    with the same JSON, then runs each scenario against each server and writes the results to
    benchmarks/results/<date>.md. Each server runs alone, pinned to one logical CPU with
    DOTNET_PROCESSOR_COUNT=1, as on a 1 vCPU machine; the load generator runs on the other CPUs.
    See README.md in this folder.

.EXAMPLE
    ./run.ps1                                  # SQLite only
    ./run.ps1 -SqlServer 'localhost\SQLEXPRESS' # SQLite and SQL Server (Windows authentication)
#>
[CmdletBinding()]
param(
    # A SQL Server instance reachable with Windows authentication. Leave it out to run SQLite only.
    [string]$SqlServer,
    [int[]]$Concurrency = @(1, 32),
    [int]$WarmupSeconds = 3,
    [int]$DurationSeconds = 10,
    [int]$Rows = 10000,
    # The logical CPU each server is pinned to. The load generator avoids it and its SMT sibling.
    [int]$ServerCpu = 2,
    [ValidateSet('ping', 'by_id', 'list_100', 'list_1000', 'insert')]
    [string[]]$Scenario = @('ping', 'by_id', 'list_100', 'list_1000', 'insert'),
    # Reuse the last build in benchmarks/.out.
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$out = Join-Path $root '.out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'

# --- Build --------------------------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host 'Building...'
    $builds = @(
        @('publish', [IO.Path]::Combine($root, '..', 'DBToRestAPI', 'DBToRestAPI.csproj'), (Join-Path $out 'engine')),
        @('publish', [IO.Path]::Combine($root, 'MinimalApi', 'MinimalApi.csproj'), (Join-Path $out 'minimal')),
        @('build', [IO.Path]::Combine($root, 'LoadGenerator', 'LoadGenerator.csproj'), (Join-Path $out 'loadgen'))
    )
    foreach ($b in $builds) {
        dotnet $b[0] $b[1] -c Release -o $b[2] --nologo -v q | Out-Null
        if ($LASTEXITCODE) { throw "dotnet $($b[0]) $($b[1]) failed" }
    }
}
$loadgen = Join-Path $out 'loadgen' 'LoadGenerator.dll'
function Invoke-LoadGenerator {
    $output = & dotnet $loadgen @args
    if ($LASTEXITCODE) { throw "LoadGenerator $($args[0]) failed: $output" }
    $output
}

# --- Data ---------------------------------------------------------------------------------------
Write-Host 'Creating the data...'
$sqlitePath = Join-Path $out 'bench.db'
$setup = @('setup', '--sqlite', $sqlitePath, '--rows', $Rows)
if ($SqlServer) { $setup += @('--sqlserver', $SqlServer) }
$setupOutput = Invoke-LoadGenerator @setup
$setupOutput | Where-Object { $_ -notlike 'sqlserver-version:*' } | ForEach-Object { Write-Host "  $_" }
$sqlServerVersion = @($setupOutput | Where-Object { $_ -like 'sqlserver-version:*' }) -replace '^sqlserver-version: ', ''
$sqlServerConnection = if ($SqlServer) {
    "Data Source=$SqlServer;Initial Catalog=DbToRestApiBench;Integrated Security=True;Trust Server Certificate=True"
} else { 'Data Source=not-configured' }
$databases = @('sqlite') + @(if ($SqlServer) { 'sqlserver' })

# The engine's configuration: the benchmark's endpoints and connection strings only.
$engineConfig = Join-Path $out 'engine' 'config'
$settings = (Get-Content (Join-Path $root 'engine' 'settings.xml') -Raw).Replace('__SQLITE__', $sqlitePath).Replace('__SQLSERVER__', $sqlServerConnection)
Set-Content -Path (Join-Path $engineConfig 'settings.xml') -Value $settings -NoNewline
Copy-Item (Join-Path $root 'engine' 'sql.xml') $engineConfig -Force

# --- Servers and scenarios ----------------------------------------------------------------------
$servers = @(
    @{ Name = 'engine'; Label = 'DbToRestAPI'; Dir = (Join-Path $out 'engine'); Dll = 'DBToRestAPI.dll'; Port = 5101
       Env = @{ Kestrel__Endpoints__Http__Url = 'http://127.0.0.1:5101'; Kestrel__Endpoints__Https__Url = 'https://127.0.0.1:5102' } },
    @{ Name = 'minimal'; Label = 'Minimal API'; Dir = (Join-Path $out 'minimal'); Dll = 'MinimalApi.dll'; Port = 5201
       Env = @{ ASPNETCORE_URLS = 'http://127.0.0.1:5201'; Bench__Sqlite = "Data Source=$sqlitePath"; Bench__SqlServer = $sqlServerConnection } }
)
# {id}, {after} and {n} are filled per request by the load generator; the check uses fixed values.
$scenarios = [ordered]@{
    ping      = @{ Method = 'GET'; Path = 'ping' }
    by_id     = @{ Method = 'GET'; Path = 'contacts/{id}' }
    list_100  = @{ Method = 'GET'; Path = 'contacts?after={after}&take=100' }
    list_1000 = @{ Method = 'GET'; Path = 'contacts?after={after}&take=1000' }
    insert    = @{ Method = 'POST'; Path = 'writes'; Body = '{"name":"write {n}","amount":12.5}' }
}
$selected = @($scenarios.Keys | Where-Object { $_ -in $Scenario })

$cpus = [Environment]::ProcessorCount
$all = if ($cpus -ge 63) { -1L } else { (1L -shl $cpus) - 1 }
$clientMask = '{0:X}' -f ($all -band (-bnot (3L -shl ($ServerCpu - ($ServerCpu % 2)))))

function Start-Server($server) {
    $variables = $server.Env + @{ DOTNET_PROCESSOR_COUNT = '1'; ASPNETCORE_ENVIRONMENT = 'Production' }
    $saved = @{}
    foreach ($name in $variables.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $variables[$name])
    }
    try {
        $process = Start-Process dotnet -ArgumentList $server.Dll -WorkingDirectory $server.Dir -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $out "$($server.Name).log") -RedirectStandardError (Join-Path $out "$($server.Name).err.log")
    }
    finally {
        # PowerShell passes $null to a string parameter as "", which would leave the variable set, empty.
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }
    }
    $process.ProcessorAffinity = [IntPtr](1L -shl $ServerCpu)
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$($server.Port)/bench/sqlite/ping" -TimeoutSec 5 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { return $process }
        }
        catch { }
        if ($process.HasExited) { throw "$($server.Label) exited; see $out/$($server.Name).log" }
        if ((Get-Date) -gt $deadline) { throw "$($server.Label) didn't answer within 60 seconds" }
        Start-Sleep -Milliseconds 500
    }
}

function Get-Answer($server, $db, $name) {
    $s = $scenarios[$name]
    $url = "http://127.0.0.1:$($server.Port)/bench/$db/" + $s.Path.Replace('{id}', '42').Replace('{after}', '100')
    $fetch = @('fetch', '--url', $url, '--method', $s.Method)
    if ($s.ContainsKey('Body')) { $fetch += @('--body', $s.Body.Replace('{n}', 'check')) }
    $status, $body = Invoke-LoadGenerator @fetch
    $json = $body | ConvertFrom-Json -Depth 20
    # An insert answers a new id each time: compare its shape only.
    if ($name -eq 'insert') { $json = ($json.PSObject.Properties.Name -join ',') }
    "$status " + ($json | ConvertTo-Json -Depth 20 -Compress)
}

$results = Join-Path $out "results-$stamp.jsonl"
$answers = @{}
foreach ($server in $servers) {
    Write-Host "Starting $($server.Label)..."
    $process = Start-Server $server
    try {
        foreach ($db in $databases) {
            foreach ($name in $selected) { $answers["$($server.Name)|$db|$name"] = Get-Answer $server $db $name }
        }
        foreach ($db in $databases) {
            foreach ($name in $selected) {
                $s = $scenarios[$name]
                foreach ($c in $Concurrency) {
                    $run = @('run', '--server', $server.Name, '--db', $db, '--scenario', $name,
                        '--url', "http://127.0.0.1:$($server.Port)/bench/$db/$($s.Path)", '--method', $s.Method,
                        '--concurrency', $c, '--warmup', $WarmupSeconds, '--duration', $DurationSeconds, '--rows', $Rows,
                        '--pid', $process.Id, '--affinity', $clientMask, '--out', $results)
                    if ($s.ContainsKey('Body')) { $run += @('--body', $s.Body) }
                    $line = Invoke-LoadGenerator @run | ConvertFrom-Json
                    Write-Host ("  {0,-9} {1,-9} c={2,-3} {3,9:N0} req/s  p50 {4,7:N2} ms  p99 {5,7:N2} ms  errors {6}" -f `
                        $db, $name, $c, $line.rps, $line.p50_ms, $line.p99_ms, $line.errors)
                }
            }
        }
    }
    finally {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
}

# --- Report -------------------------------------------------------------------------------------
$mismatches = @(foreach ($db in $databases) {
    foreach ($name in $selected) {
        $a = $answers["engine|$db|$name"]; $b = $answers["minimal|$db|$name"]
        if ($a -ne $b) { "- $db $name`n  - DbToRestAPI: ``$a```n  - Minimal API: ``$b``" }
    }
})
if ($mismatches) { Write-Warning "The servers' answers differ:`n$($mismatches -join "`n")" }

$records = Get-Content $results | ForEach-Object { $_ | ConvertFrom-Json }
function Find($server, $db, $name, $c) { $records | Where-Object { $_.server -eq $server -and $_.db -eq $db -and $_.scenario -eq $name -and $_.concurrency -eq $c } | Select-Object -Last 1 }

$cpuName = if ($IsWindows) { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() }
           elseif (Test-Path /proc/cpuinfo) { ((Get-Content /proc/cpuinfo | Where-Object { $_ -like 'model name*' } | Select-Object -First 1) -split ':\s*', 2)[1] }
           else { 'unknown CPU' }
$engineVersion = (Get-Item (Join-Path $out 'engine' 'DBToRestAPI.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
$runtime = (& dotnet --list-runtimes | Where-Object { $_ -like 'Microsoft.AspNetCore.App 10.*' } | Select-Object -Last 1) -replace ' \[.*$', ''

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# Benchmark results, $(Get-Date -Format 'yyyy-MM-dd')")
$md.Add('')
$md.Add("DbToRestAPI $engineVersion against a hand-written Minimal API running the same SQL ([README](../README.md)).")
$md.Add('')
$md.Add("- Machine: $cpuName, $cpus logical CPUs, $([math]::Round((Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).TotalPhysicalMemory / 1GB)) GB, $([Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim()).")
$md.Add("- Runtime: $runtime. Each server pinned to logical CPU $ServerCpu with ``DOTNET_PROCESSOR_COUNT=1``; the load generator on the other CPUs, the database wherever its own service runs.")
if ($SqlServer) { $md.Add("- SQL Server: $sqlServerVersion, on the same machine.") }
$md.Add("- $Rows contacts. Each run: $WarmupSeconds s warmup, then $DurationSeconds s measured, from 1 and from $(($Concurrency | Measure-Object -Maximum).Maximum) connections.")
$md.Add('- CPU per request is the server process''s CPU time divided by the requests it answered: what a request costs on a 1 vCPU machine.')
if ($mismatches) { $md.Add(''); $md.Add('**The servers'' answers differed:**'); $md.Add(''); $mismatches | ForEach-Object { $md.Add($_) } }

$titles = @{ ping = '`SELECT 1`'; by_id = 'One row by id'; list_100 = '100 rows'; list_1000 = '1,000 rows'; insert = 'Insert from a JSON body' }
foreach ($db in $databases) {
    $md.Add('')
    $md.Add("## $(if ($db -eq 'sqlite') { 'SQLite' } else { 'SQL Server' })")
    $md.Add('')
    $md.Add('| Scenario | Connections | DbToRestAPI req/s | Minimal API req/s | DbToRestAPI / Minimal API | DbToRestAPI p50 / p99 (ms) | Minimal API p50 / p99 (ms) | DbToRestAPI CPU per request (µs) | Minimal API CPU per request (µs) |')
    $md.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|')
    foreach ($name in $selected) {
        foreach ($c in $Concurrency) {
            $e = Find 'engine' $db $name $c; $m = Find 'minimal' $db $name $c
            if (-not $e -or -not $m) { continue }
            $ratio = if ($m.rps) { '{0:N0}%' -f ($e.rps / $m.rps * 100) } else { '' }
            $errors = if ($e.errors -or $m.errors) { " (errors: $($e.errors) / $($m.errors))" } else { '' }
            $md.Add(('| {0}{1} | {2} | {3:N0} | {4:N0} | {5} | {6:N2} / {7:N2} | {8:N2} / {9:N2} | {10:N0} | {11:N0} |' -f `
                $titles[$name], $errors, $c, $e.rps, $m.rps, $ratio, $e.p50_ms, $e.p99_ms, $m.p50_ms, $m.p99_ms,
                $e.server_cpu_us_per_request, $m.server_cpu_us_per_request))
        }
    }
}

$md.Add('')
$md.Add('## Memory')
$md.Add('')
$md.Add('| Server | Working set at the end (MB) | Peak working set (MB) | Private bytes at the end (MB) |')
$md.Add('|---|---:|---:|---:|')
foreach ($server in $servers) {
    $last = $records | Where-Object { $_.server -eq $server.Name } | Select-Object -Last 1
    if ($last) { $md.Add(('| {0} | {1:N0} | {2:N0} | {3:N0} |' -f $server.Label, $last.server_working_set_mb, $last.server_peak_working_set_mb, $last.server_private_mb)) }
}

$resultsDir = Join-Path $root 'results'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
$report = Join-Path $resultsDir "$stamp.md"
Set-Content -Path $report -Value ($md -join [Environment]::NewLine)
Write-Host ''
Write-Host "Results: $report"
