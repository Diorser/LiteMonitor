# Watchdog for the LiteMonitor Bluetooth battery HTTP service.
# English-only comments (avoid UTF-8 BOM-less corruption).
# Idempotent: only starts the server when the port is FREE, so running
# multiple copies is harmless.
#
# v2.2: writes a heartbeat to watchdog.log so a death is never a black box
#       again. The HTTP server also re-launches this watchdog (see
#       Ensure-Supervisors in BluetoothBattery.ps1), so the two watch each
#       other and a single death can no longer take the service down.

$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$svc  = Join-Path $dir 'BluetoothBattery.ps1'
$ps   = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$port = 18923
$log  = Join-Path $dir 'watchdog.log'

function Say($m) {
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m
    try { Add-Content -Path $log -Value $line -Encoding UTF8 -ErrorAction Stop } catch { }
    try {
        $fi = Get-Item $log -ErrorAction SilentlyContinue
        if ($fi -and $fi.Length -gt 200KB) {
            Get-Content $log -Tail 400 | Set-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue
        }
    } catch { }
}

# Singleton guard: only ONE watchdog at a time (Run key + two Startup .lnk all
# fire at logon). Prevents duplicate resident loops.
$mtx = New-Object System.Threading.Mutex($false, 'LiteMonitorBtBatteryWatchdog')
if (-not $mtx.WaitOne(0)) {
    Say 'another watchdog already holds the mutex - exiting'
    exit 0
}
Say ("watchdog started (pid {0})" -f $PID)

function Test-Port($p) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $c.Connect('127.0.0.1', $p)
        $c.Close()
        return $true
    } catch {
        return $false
    }
}

$lastState = ''
$beat = 0
while ($true) {
    try {
        if (Test-Port $port) {
            if ($lastState -ne 'up') { Say 'serve is up'; $lastState = 'up' }
        } else {
            if ($lastState -ne 'down') { Say 'port DOWN - starting serve'; $lastState = 'down' }
            try {
                Start-Process -FilePath $ps -WindowStyle Hidden -ArgumentList "-NoProfile -MTA -ExecutionPolicy Bypass -File `"$svc`" -Serve -Port $port"
                Say 'serve launch requested'
            } catch {
                Say ('serve start FAILED: ' + $_.Exception.Message)
            }
        }
    } catch {
        Say ('loop error: ' + $_.Exception.Message)
    }
    $beat++
    if ($beat % 40 -eq 0) { Say 'alive (heartbeat)' }
    Start-Sleep -Seconds 15
}
