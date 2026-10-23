<#
  LiteMonitor - Bluetooth Battery Service (v2.2)
  v2.2: ONLY report devices that are CURRENTLY CONNECTED and readable.
        (v2.1 read cached levels for disconnected devices, which flooded the
         panel with stale rows - e.g. one device paired under two addresses
         showing 100% and 33% side by side.)
  v2.1: de-duplicate same-named devices (one row per device name).

  Two-process design (fixes "slow scan -> HTTP timeout -> X card"):
    -Scanner : background loop scanning BT battery levels into snapshot.json
    -Serve   : HTTP server that only reads snapshot.json (instant reply), and
               auto-revives the scanner process if it died.

  Usage (MUST use -MTA; STA makes all WinRT async callbacks time out):
    powershell -NoProfile -MTA -ExecutionPolicy Bypass -File BluetoothBattery.ps1 -Serve -Port 18923
    powershell -NoProfile -MTA -ExecutionPolicy Bypass -File BluetoothBattery.ps1 -Once
#>
param(
    [int]$Port = 18923,
    [int]$ScanInterval = 45,
    [switch]$Once,
    [switch]$Serve,
    [switch]$Scanner,
    [switch]$Fast
)

$ErrorActionPreference = 'Continue'
$script:AsTaskTpl = $null
$script:ToArrayFn = $null

$stateDir = Join-Path $env:LOCALAPPDATA 'LiteMonitorBtBattery'
if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
$script:SnapFile = Join-Path $stateDir 'snapshot.json'

Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction SilentlyContinue
$null = [Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime]
$null = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType = WindowsRuntime]
$null = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic, Windows.Devices.Bluetooth.GenericAttributeProfile, ContentType = WindowsRuntime]

$BATTERY_SERVICE = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService]::ConvertShortIdToUuid(0x180F)
$BATTERY_LEVEL = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic]::ConvertShortIdToUuid(0x2A19)

function AwaitOp($op, [type]$rt, [int]$timeoutMs = 5000) {
    if ($null -eq $script:AsTaskTpl) {
        $script:AsTaskTpl = [System.WindowsRuntimeSystemExtensions].GetMethods() |
            Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } |
            Select-Object -First 1
    }
    $task = $script:AsTaskTpl.MakeGenericMethod($rt).Invoke($null, [object[]]@($op))
    $task.Wait($timeoutMs) | Out-Null
    if (-not $task.IsCompleted) { return $null }
    return $task.Result
}

function BufToArray($buf) {
    if ($null -eq $script:ToArrayFn) {
        $script:ToArrayFn = [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions].GetMethods() |
            Where-Object { $_.Name -eq 'ToArray' -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    }
    return $script:ToArrayFn.Invoke($null, [object[]]@($buf))
}

function Read-Level($char, $mode) {
    $to = if ($mode -eq [Windows.Devices.Bluetooth.BluetoothCacheMode]::Uncached) { 6000 } else { 3000 }
    $rr = AwaitOp ($char.ReadValueAsync($mode)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattReadResult]) $to
    if ($null -ne $rr -and $rr.Status -eq 'Success' -and $null -ne $rr.Value -and $rr.Value.Length -gt 0) {
        $v = [int](BufToArray $rr.Value)[0]
        if ($v -gt 0) { return $v }
    }
    return -1
}

function New-Device($name, $mac, $level, $state, $live = $false) {
    return [pscustomobject]@{ name = [string]$name; mac = [string]$mac; level = [int]$level; state = [string]$state; live = [bool]$live }
}

function Read-Device($mac) {
    try {
        $addr = [Convert]::ToUInt64($mac, 16)
        $dev = AwaitOp ([Windows.Devices.Bluetooth.BluetoothLEDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothLEDevice]) 3000
        if ($null -eq $dev) { return New-Device $mac $mac -1 'offline' $false }
        $name = $dev.Name
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $mac }
        # Connected -> read live (Uncached). Disconnected -> read last cached level (no forced reconnect, no battery drain).
        $connected = ($dev.ConnectionStatus -eq 'Connected')
        $mode = if ($connected) { [Windows.Devices.Bluetooth.BluetoothCacheMode]::Uncached } else { [Windows.Devices.Bluetooth.BluetoothCacheMode]::Cached }
        $sr = AwaitOp ($dev.GetGattServicesForUuidAsync($BATTERY_SERVICE)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult]) 4000
        if ($null -eq $sr -or $sr.Services.Count -eq 0) {
            $dev.Dispose()
            return New-Device $name $mac -1 'offline' $connected
        }
        $level = -1
        foreach ($svc in $sr.Services) {
            $cr = AwaitOp ($svc.GetCharacteristicsForUuidAsync($BATTERY_LEVEL)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult]) 3000
            if ($null -eq $cr) { continue }
            foreach ($c in $cr.Characteristics) {
                $v = Read-Level $c $mode
                if ($v -ge 0) { $level = $v; break }
            }
            if ($level -ge 0) { break }
        }
        $dev.Dispose()
        if ($level -lt 0) { return New-Device $name $mac -1 'offline' $connected }
        return New-Device $name $mac $level 'ok' $connected
    } catch {
        return New-Device $mac $mac -1 'offline' $false
    }
}

function Get-Snapshot {
    $seen = @{}
    $list = New-Object System.Collections.Generic.List[object]
    $devs = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue
    foreach ($d in $devs) {
        if ($d.InstanceId -notmatch '\{0000180F-0000-1000-8000-00805F9B34FB\}') { continue }
        if ($d.InstanceId -notmatch '_([0-9A-F]{12})\\') { continue }
        $mac = $Matches[1]
        if ($seen.ContainsKey($mac)) { continue }
        $seen[$mac] = $true
        $dev = Read-Device $mac
        # v2.3: report every device Windows can give us a battery level for.
        # The old v2.2 rule required ConnectionStatus == Connected, but BLE
        # devices do NOT hold a GATT connection while idle - so that filter
        # hid every real device (panel showed only the HID++ mouse).
        # A device only lands here when a level was actually read; devices
        # that are gone fail the read and are dropped.
        if ($null -ne $dev -and $dev.state -eq 'ok') { $list.Add($dev) }
    }

    # De-duplicate by device NAME: same-named entries collapse to a single row.
    # (A device can be paired twice / show up with two addresses under one name.)
    # Keep the most representative one: currently-connected (live) first, then an
    # 'ok' reading, then the higher level. First-seen order is preserved.
    $order = New-Object System.Collections.Generic.List[string]
    $best = @{}
    foreach ($dev in $list) {
        $k = ([string]$dev.name).Trim()
        if ([string]::IsNullOrWhiteSpace($k)) { $k = [string]$dev.mac }
        if (-not $best.ContainsKey($k)) {
            $best[$k] = $dev
            $order.Add($k)
            continue
        }
        $cur = $best[$k]
        $take = $false
        if ($dev.live -and -not $cur.live) {
            $take = $true
        } elseif ([bool]$dev.live -eq [bool]$cur.live) {
            $devOk = ($dev.state -eq 'ok')
            $curOk = ($cur.state -eq 'ok')
            if ($devOk -and -not $curOk) { $take = $true }
            elseif ($devOk -eq $curOk -and [int]$dev.level -gt [int]$cur.level) { $take = $true }
        }
        if ($take) { $best[$k] = $dev }
    }
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($k in $order) { $out.Add($best[$k]) }
    return $out
}

function Write-Snapshot($snap) {
    $payload = @{ ts = [DateTime]::Now.ToString('o'); count = ($snap | Measure-Object).Count; devices = @($snap) }
    $json = $payload | ConvertTo-Json -Depth 5 -Compress
    $tmp = $script:SnapFile + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -Path $tmp -Destination $script:SnapFile -Force
}

function Read-Snapshot {
    try {
        if (-not (Test-Path $script:SnapFile)) { return $null }
        $txt = [System.IO.File]::ReadAllText($script:SnapFile, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($txt)) { return $null }
        return ($txt | ConvertFrom-Json)
    } catch { return $null }
}

function Make-Json($payload, [int]$warn) {
    $o = @{ count = 0; ok_count = 0 }
    for ($i = 1; $i -le 6; $i++) {
        $o["dev${i}_name"] = ''; $o["dev${i}_text"] = ''; $o["dev${i}_color"] = '0'
    }
    if ($null -eq $payload) { return ($o | ConvertTo-Json -Compress) }
    $o['count'] = [int]$payload.count
    $oks = @($payload.devices | Where-Object { $_.state -eq 'ok' } | Sort-Object level)
    $o['ok_count'] = $oks.Count
    for ($i = 1; $i -le 6; $i++) {
        if ($i -le $oks.Count) {
            $d = $oks[$i - 1]
            $c = '0'
            if ($warn -gt 0 -and $d.level -lt $warn) { $c = '2' }
            elseif ($warn -gt 0 -and $d.level -lt ($warn + 20)) { $c = '1' }
            $o["dev${i}_name"] = [string]$d.name
            $o["dev${i}_text"] = "$($d.level)%"
            $o["dev${i}_color"] = $c
        }
    }
    return ($o | ConvertTo-Json -Compress)
}

function Ensure-Supervisors {
    # One query for both children. The scanner and the watchdog now watch EACH
    # OTHER: if either one dies, the other brings it back within seconds, so a
    # single death can no longer take the whole service down.
    $ps = Get-CimInstance Win32_Process -Filter "name='powershell.exe'" -ErrorAction SilentlyContinue
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

    $hasScanner = @($ps | Where-Object { $_.CommandLine -like '*BluetoothBattery.ps1*' -and $_.CommandLine -like '*-Scanner*' }).Count -gt 0
    if (-not $hasScanner) {
        # NOTE: quote the script path - it contains a space
        $arg = '-NoProfile -MTA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Scanner -ScanInterval ' + $ScanInterval
        try { Start-Process -FilePath $psExe -WindowStyle Hidden -ArgumentList $arg } catch { }
    }

    $hasWd = @($ps | Where-Object { $_.CommandLine -like '*run-service.ps1*' }).Count -gt 0
    if (-not $hasWd) {
        $wd = Join-Path $env:LOCALAPPDATA 'LiteMonitorBtBattery\run-service.ps1'
        if (Test-Path $wd) {
            $arg2 = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $wd + '"'
            try { Start-Process -FilePath $psExe -WindowStyle Hidden -ArgumentList $arg2 } catch { }
        }
    }
}

# ---------------- mode dispatch ----------------

if ($Once) {
    $snap = Get-Snapshot
    Write-Snapshot $snap
    $snap | ConvertTo-Json
    exit 0
}

if ($Scanner) {
    # background scan loop - nobody waits for it
    while ($true) {
        try {
            $snap = Get-Snapshot
            Write-Snapshot $snap
        } catch { }
        Start-Sleep -Seconds $ScanInterval
    }
    exit 0
}

if (-not $Serve) {
    Write-Host 'usage: -Serve (http service) | -Once (snapshot) | -Scanner (internal)'
    exit 0
}

try { Ensure-Supervisors } catch { }

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
try {
    $listener.Start()
} catch {
    Write-Host "START FAILED: $($_.Exception.Message)"
    exit 1
}
Write-Host "Bluetooth battery service on http://127.0.0.1:$Port/ (scan every ${ScanInterval}s)"

while ($true) {
    $ctx = $null
    try { $ctx = $listener.GetContext() } catch {
        if (-not $listener.IsListening) { break }
        Start-Sleep -Milliseconds 200
        continue
    }
    $res = $ctx.Response
    try {
        Ensure-Supervisors
        $q = $ctx.Request.Url.Query
        $warn = 20
        if ($q -match '[?&]warn=([^&]*)') { [void][int]::TryParse([System.Uri]::UnescapeDataString($Matches[1]), [ref]$warn) }
        $payload = Read-Snapshot
        if ($ctx.Request.Url.AbsolutePath -eq '/all') {
            $json = if ($payload) { $payload | ConvertTo-Json -Depth 5 -Compress } else { '{"count":0,"devices":[]}' }
        } else {
            $json = Make-Json $payload $warn
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $res.ContentType = 'application/json; charset=utf-8'
        $res.ContentLength64 = $bytes.Length
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
    } catch {
        try { $res.StatusCode = 500 } catch { }
    }
    try { $res.Close() } catch { }
}
