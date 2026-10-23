$port = 18923
$up = $false
try { $c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); $up = $true } catch {}
if (-not $up) {
  $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
  $svc = Join-Path $dir 'run-service.ps1'
  Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList ('-NoProfile -MTA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $svc + '"')
}
