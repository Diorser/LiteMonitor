param([switch]$UpdateLocks, [switch]$SmokeViaDotNet)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot)
$root = (Get-Location).Path
$output = Join-Path $root 'artifacts'
$plan = Get-Content (Join-Path $output 'plan.json') -Raw | ConvertFrom-Json
if ($plan.version -notmatch '^\d+\.\d+\.\d+$' -or $plan.repository -notmatch '^[\w.-]+/[\w.-]+$') { throw '无效的构建计划' }
$common = @("-p:ReleaseVersion=$($plan.version)", "-p:ReleaseRepository=$($plan.repository)", '-p:ContinuousIntegrationBuild=true')
$locked = if ($UpdateLocks) { '-p:RestoreLockedMode=false' } else { '-p:RestoreLockedMode=true' }

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 失败（退出码 $LASTEXITCODE）：$($Arguments -join ' ')" }
}
function Restore-Project([string]$Project, [string]$SelfContained = '') {
    [string[]]$extra = @()
    if ($SelfContained) { $extra = @("-p:SelfContained=$($SelfContained.ToLowerInvariant())") }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & dotnet restore $Project -r win-x64 $locked @extra @common
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -eq 3) { throw "NuGet 还原失败：$Project" }
        Start-Sleep -Seconds (2 * $attempt)
    }
}
function Reset-OutputDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '输出目录超出 artifacts 范围' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Path $full | Out-Null
}

Restore-Project 'LiteMonitor.sln'
Invoke-DotNet (@('build', 'LiteMonitor.sln', '-c', 'Release', '--no-restore') + $common)
# 这些项目是可执行回归程序，不能用没有测试适配器的 dotnet test 代替运行。
foreach ($project in (Get-ChildItem 'test' -Filter '*.csproj' -Recurse | Sort-Object FullName)) {
    Restore-Project $project.FullName
    Invoke-DotNet (@('run', '--project', $project.FullName, '-c', 'Release', '-r', 'win-x64', '--no-restore') + $common)
}

$package = Join-Path $output 'package'
$updater = Join-Path $output 'updater'
Reset-OutputDirectory $package
Reset-OutputDirectory $updater
Restore-Project 'LiteMonitor.csproj' $true
Invoke-DotNet (@('publish', 'LiteMonitor.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', $package) + $common)
Restore-Project 'LiteMonitor.Updater/LiteMonitor.Updater.csproj' $true
Invoke-DotNet (@('publish', 'LiteMonitor.Updater/LiteMonitor.Updater.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', $updater) + $common)
Copy-Item -LiteralPath (Join-Path $updater 'LiteMonitor.Updater.exe') -Destination (Join-Path $package 'resources/LiteMonitor.Updater.exe') -Force
Get-ChildItem $package -Filter '*.pdb' -Recurse | Remove-Item -Force

# 使用无驱动、无网络、无用户配置的专用入口验证运行时与 WinForms 消息循环。
$smokeResult = Join-Path $package 'smoke-test-result.json'
$start = @{ WorkingDirectory = $package; PassThru = $true; WindowStyle = 'Hidden' }
if ($SmokeViaDotNet) {
    $start.FilePath = (Get-Command dotnet).Source
    $start.ArgumentList = @('exec', ('"' + (Join-Path $package 'LiteMonitor.dll') + '"'), '--smoke-test')
} else {
    $start.FilePath = Join-Path $package 'LiteMonitor.exe'
    $start.ArgumentList = @('--smoke-test')
}
$process = Start-Process @start
try {
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw '主程序启动冒烟测试超时' }
    if ($process.ExitCode -ne 0 -or !(Test-Path $smokeResult)) {
        $details = if (Test-Path $smokeResult) { [IO.File]::ReadAllText($smokeResult) } else { '进程未生成诊断文件' }
        throw "主程序启动冒烟测试失败：$($process.ExitCode)；$details"
    }
    $result = Get-Content $smokeResult -Raw | ConvertFrom-Json
    if ($result.version -ne $plan.version) { throw '启动后的版本不一致' }
} finally { if (Test-Path $smokeResult) { Remove-Item -LiteralPath $smokeResult -Force } }
$process = Start-Process -FilePath (Join-Path $package 'resources/LiteMonitor.Updater.exe') -ArgumentList '--smoke-test' -PassThru -WindowStyle Hidden
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Updater 启动超时' }
if ($process.ExitCode -ne 0) { throw 'Updater 无法独立启动' }

$files = [ordered]@{}
foreach ($file in (Get-ChildItem $package -File -Recurse | Sort-Object FullName)) {
    $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ version = $plan.version; files = $files } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $package 'release-files.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $package -Version $plan.version

$zip = Join-Path $output "LiteMonitor-v$($plan.version)-win-x64.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false, [Text.Encoding]::UTF8)
# 统一 ZIP 时间戳，避免文件写入时间影响重复打包结果。
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Update)
try { foreach ($entry in $archive.Entries) { $entry.LastWriteTime = [DateTimeOffset]::Parse($plan.date + 'T00:00:00Z') } } finally { $archive.Dispose() }
$unpacked = Join-Path $output 'unpacked'
Reset-OutputDirectory $unpacked
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpacked)
& (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $unpacked -Version $plan.version
$digest = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding utf8NoBOM
@{
    version = $plan.version
    changelog = [IO.File]::ReadAllText((Join-Path $output 'notes.md'))
    releaseDate = $plan.date
    downloadUrl = "https://github.com/$($plan.repository)/releases/download/v$($plan.version)/$([IO.Path]::GetFileName($zip))"
    sha256 = $digest
} | ConvertTo-Json | Set-Content (Join-Path $output 'version.json') -Encoding utf8NoBOM
if ($env:GITHUB_STEP_SUMMARY) {
    "Windows x64 包验证通过：$([IO.Path]::GetFileName($zip))；$($files.Count) 个文件；SHA256：$digest。主程序和 Updater 冒烟测试通过。" | Add-Content $env:GITHUB_STEP_SUMMARY
}
Write-Output "验证通过：$zip ($digest)"
# 临时目录不进入上传的构建产物。
foreach ($path in @($package, $updater, $unpacked)) {
    $full = [IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '清理路径越界' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
