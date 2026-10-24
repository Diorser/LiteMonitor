param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$required = @('LiteMonitor.exe', 'LiteMonitor.dll', 'LiteMonitor.deps.json', 'LiteMonitor.runtimeconfig.json', 'LibreHardwareMonitorLib.dll', 'System.Windows.Forms.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'resources/LiteMonitor.Updater.exe', 'resources/LiteMonitorFPS.exe', 'resources/PawnIO_setup.exe', 'resources/driver.zip', 'release-files.json', '使用说明(必读).txt')
foreach ($path in $required) {
    $file = Join-Path $Directory $path
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "缺少运行文件：$path" }
}
foreach ($path in @('LiteMonitor.exe', 'LiteMonitor.dll', 'resources/LiteMonitor.Updater.exe')) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory $path))
    if ($info.FileVersion -ne "$Version.0" -or $info.ProductVersion -ne $Version) { throw "版本不一致：$path ($($info.FileVersion), $($info.ProductVersion))" }
}
$manifest = Get-Content (Join-Path $Directory 'release-files.json') -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.version -ne $Version) { throw '文件清单版本不一致' }
foreach ($entry in $manifest.files.GetEnumerator()) {
    $path = Join-Path $Directory $entry.Key
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "文件校验失败：$($entry.Key)" }
}
$actual = @(Get-ChildItem $Directory -File -Recurse)
if ($actual.Count -ne $manifest.files.Count + 1) { throw '包内存在清单之外的文件' }
foreach ($file in $actual) {
    if ($file.Extension -in @('.pdb', '.log', '.tmp') -or $file.Name -match '^(settings|TrafficHistory|smoke-test-result)\.json$') { throw "包内含有调试文件或用户数据：$($file.Name)" }
}
foreach ($folder in @('themes', 'lang', 'plugins')) {
    $source = Join-Path (Split-Path $PSScriptRoot) "resources/$folder"
    foreach ($file in Get-ChildItem $source -Filter '*.json') {
        $target = Join-Path $Directory "resources/$folder/$($file.Name)"
        if (!(Test-Path -LiteralPath $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) { throw "运行资源缺失或改变：$folder/$($file.Name)" }
    }
}
$runtime = Get-Content (Join-Path $Directory 'LiteMonitor.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
if (!$runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.ContainsKey('framework')) { throw '主程序没有包含 .NET 运行时' }
Write-Output "文件完整性与版本验证通过：$Directory"
