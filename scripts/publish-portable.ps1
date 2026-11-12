#requires -Version 5.1
<#
.SYNOPSIS
    构建 Pasteup Windows 自包含单目录便携版，并打包为可直接发给朋友的 zip。

.DESCRIPTION
    流程：
      0. 检查 publish\pasteup 是否被运行中的实例占用（避免 publish 半途失败留下新旧混杂产物）。
      1. dotnet publish：Release / win-x64 / 自包含 / ReadyToRun（去掉 PDB，分发不需要）。
      2. 写入「使用说明.txt」（来自 packaging\使用说明.txt）与「版本信息.txt」。
      3. 复制到以版本命名的暂存目录，压缩为 publish\Pasteup-<版本>-win-x64-portable.zip。
      4. 校验 zip 条目、输出 SHA256（同时写 .sha256 文件）。

.PARAMETER SkipPublish
    跳过 dotnet publish，直接用现有 publish\pasteup 重新打包（调脚本时用，省一次构建）。

.PARAMETER NoZip
    只生成发布目录与暂存目录，不压缩（调脚本时用）。

.EXAMPLE
    pwsh -File scripts\publish-portable.ps1

.EXAMPLE
    pwsh -File scripts\publish-portable.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Text)
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Text)
    Write-Host "警告：$Text" -ForegroundColor Yellow
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot 'Pasteup.sln'))) {
    throw "无法定位仓库根目录（脚本应位于 <仓库>\scripts\ 下）：$PSScriptRoot"
}

$publishDir = Join-Path $repoRoot 'publish\pasteup'
$outRoot = Join-Path $repoRoot 'publish'
$readmeTemplate = Join-Path $repoRoot 'packaging\使用说明.txt'
$project = Join-Path $repoRoot 'src\Pasteup.App\Pasteup.App.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet 命令：需要安装 .NET 10 SDK 才能构建。'
}

# ---- 0. 占用检查：发布目录里的实例在运行时 publish 会被锁，且可能留下新旧混杂产物 ----
$runningFromPublish = @()
foreach ($proc in (Get-Process -Name Pasteup -ErrorAction SilentlyContinue)) {
    try {
        if ($proc.Path -and $proc.Path.StartsWith($publishDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            $runningFromPublish += ("PID {0}：{1}" -f $proc.Id, $proc.Path)
        }
    }
    catch {
        # 受保护进程取不到 Path：与发布目录无关，忽略
    }
}
if ($runningFromPublish.Count -gt 0) {
    throw ("发布目录中的 Pasteup 正在运行，请先退出再打包（否则文件被锁、产物会新旧混杂）：`n  " +
        ($runningFromPublish -join "`n  "))
}

# ---- 1. 发布 ----
if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Write-Step '清理旧发布目录 publish\pasteup'
        Remove-Item $publishDir -Recurse -Force
    }
    # 强制清理 Release 的 bin/obj：增量构建可能复用旧 DLL，把构建机绝对 PDB 路径（含用户名）
    # 留在 PE 调试目录里——即使发布时不带 PDB，这个路径字符串仍会进入分发包。
    Write-Step '清理 Release 中间产物（避免复用带本机路径的旧 DLL）'
    foreach ($proj in @('src\Pasteup.App', 'src\Pasteup.Core', 'src\Pasteup.Themes')) {
        Remove-Item (Join-Path $repoRoot "$proj\bin\Release") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $repoRoot "$proj\obj\Release") -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Step 'dotnet publish：Release / win-x64 / 自包含 / ReadyToRun（不含 PDB）'
    & dotnet publish $project -c Release -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:DebugType=None -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（exit code $LASTEXITCODE）"
    }
}
else {
    Write-Step '已指定 -SkipPublish：复用现有 publish\pasteup'
}

$exePath = Join-Path $publishDir 'Pasteup.exe'
foreach ($required in @('Pasteup.exe', 'Pasteup.dll', 'Pasteup.runtimeconfig.json', 'Pasteup.deps.json')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "发布产物缺少必要文件：$required（目录：$publishDir）"
    }
}

# ---- 1.5 隐私校验：发布产物不得含构建机绝对路径 / 用户目录（防止用户名、仓库路径随包分发） ----
$privacyNeedles = @($repoRoot, 'C:\Users\')
$privacyTargets = @(
    'Pasteup.exe', 'Pasteup.dll', 'Pasteup.Core.dll', 'Pasteup.Themes.dll',
    'Pasteup.deps.json', 'Pasteup.runtimeconfig.json', '使用说明.txt', '版本信息.txt'
)
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
foreach ($name in $privacyTargets) {
    $path = Join-Path $publishDir $name
    if (-not (Test-Path $path)) { continue }
    $content = $latin1.GetString([System.IO.File]::ReadAllBytes($path))
    foreach ($needle in $privacyNeedles) {
        if ($content.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "发布产物包含本机路径信息（$needle）：$name。请清理 Release 产物后重新发布；分发包不得带构建机绝对路径。"
        }
    }
}
if ($SkipPublish) { Write-Warn '已跳过发布（-SkipPublish），请自行确认现有 publish\pasteup 通过上述隐私校验。' }

# ---- 2. 使用说明 + 版本信息 ----
if (-not (Test-Path $readmeTemplate)) {
    throw "缺少使用说明模板：$readmeTemplate"
}
Copy-Item -Path $readmeTemplate -Destination (Join-Path $publishDir '使用说明.txt') -Force

$versionInfo = (Get-Item $exePath).VersionInfo
$productVersion = $versionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    $productVersion = $versionInfo.FileVersion
}
$versionParts = $productVersion -split '\+', 2
$semVer = $versionParts[0].Trim()
$commit = ''
if ($versionParts.Count -gt 1) {
    $commit = $versionParts[1].Trim()
}
if ([string]::IsNullOrWhiteSpace($semVer)) {
    throw '无法从 Pasteup.exe 读取版本号（ProductVersion/FileVersion 均为空）。'
}
if ([string]::IsNullOrWhiteSpace($commit)) {
    try { $commit = (& git -C $repoRoot rev-parse --short HEAD 2>$null).Trim() } catch { }
}

$gitDirty = $false
try {
    $gitStatus = & git -C $repoRoot status --porcelain 2>$null
    if ($gitStatus) { $gitDirty = $true }
}
catch {
    # 没有 git 或不是 git 仓库：不阻塞打包
}

$runtimeVersion = ''
try {
    $runtimeConfig = Get-Content (Join-Path $publishDir 'Pasteup.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimeVersion = (($runtimeConfig.runtimeOptions.includedFrameworks |
        ForEach-Object { "$($_.name) $($_.version)" }) -join '；')
}
catch {
    Write-Warn '读取 Pasteup.runtimeconfig.json 失败，版本信息中的运行时版本留空。'
}

$publishFiles = Get-ChildItem $publishDir -Recurse -File
# 排除可能已存在的旧「版本信息.txt」后 +1（本文件自身；它在本段之后才写入）。
# 直接 .Count + 1 只在全新 publish 目录成立，-SkipPublish 复用目录时会多算 1。
$fileCount = @($publishFiles | Where-Object { $_.Name -ne '版本信息.txt' }).Count + 1
$totalMB = [math]::Round((($publishFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
$buildTime = (Get-Item $exePath).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss zzz')
$dirtyNote = if ($gitDirty) { '（构建时工作树含未提交改动）' } else { '' }
$versionLines = @(
    'Pasteup 便携版 构建信息',
    '========================',
    "产品版本：$semVer",
    "源码版本：$commit$dirtyNote",
    "构建时间：$buildTime",
    '目标平台：Windows 64 位（win-x64）',
    "运行时：.NET 自包含（$runtimeVersion）",
    '发布命令：dotnet publish src\Pasteup.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:DebugType=None -o publish\pasteup',
    "目录文件数：$fileCount（含本文件；不含本文件时约 $totalMB MB）",
    '',
    '本目录为免安装便携版：完整解压后双击 Pasteup.exe 即可使用。',
    '历史记录/设置保存在 %LOCALAPPDATA%\Pasteup；删除程序目录不会删除历史记录。'
)
$utf8Bom = [System.Text.UTF8Encoding]::new($true)
[System.IO.File]::WriteAllLines((Join-Path $publishDir '版本信息.txt'), $versionLines, $utf8Bom)

# ---- 3. 暂存目录 + zip（zip 根目录为版本化文件夹，解压后是一个完整文件夹而不是一堆散文件） ----
$packageName = "Pasteup-$semVer-win-x64-portable"
$stageRoot = Join-Path $outRoot '_stage'
$stagePath = Join-Path $stageRoot $packageName
if (Test-Path $stageRoot) {
    Remove-Item $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot | Out-Null
Write-Step "复制发布产物到暂存目录：$stagePath"
Copy-Item -Path $publishDir -Destination $stagePath -Recurse -Force

if ($NoZip) {
    Write-Step "已指定 -NoZip：仅生成发布目录与暂存目录（$stagePath）"
    Write-Host ("耗时 {0:N0} 秒" -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
    return
}

$zipPath = Join-Path $outRoot ($packageName + '.zip')
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Write-Step "压缩为 $zipPath"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# 用 ZipArchive 逐文件写入，而不是 ZipFile.CreateFromDirectory：
# .NET Framework 的 CreateFromDirectory 会把条目分隔符写成反斜杠 '\'（部分非 Windows 解压器会解坏），
# 这里显式用 '/'，并统一加 $packageName/ 前缀（等价 includeBaseDirectory = true）。
$zipBaseFull = (Get-Item $stagePath).FullName
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $stagePath -Recurse -File)) {
        $relative = $file.FullName.Substring($zipBaseFull.Length + 1).Replace('\', '/')
        $entryName = "$packageName/$relative"
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

# ---- 4. 校验 zip + SHA256 ----
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($must in @(
            "$packageName/Pasteup.exe",
            "$packageName/使用说明.txt",
            "$packageName/版本信息.txt",
            "$packageName/Pasteup.runtimeconfig.json")) {
        if ($entries -notcontains $must) {
            throw "zip 校验失败：缺少条目 $must"
        }
    }
    $entryFileCount = @($entries | Where-Object { -not $_.EndsWith('/') }).Count
}
finally {
    $zip.Dispose()
}

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
$shaPath = "$zipPath.sha256"
[System.IO.File]::WriteAllText($shaPath, "$hash  $packageName.zip`n", [System.Text.Encoding]::ASCII)

Remove-Item $stageRoot -Recurse -Force

$zipMB = [math]::Round(((Get-Item $zipPath).Length / 1MB), 1)
Write-Host ''
Write-Host '打包完成' -ForegroundColor Green
Write-Host ("  发布目录：{0}" -f $publishDir)
Write-Host ("  压缩包  ：{0}（{1} MB，{2} 个文件）" -f $zipPath, $zipMB, $entryFileCount)
Write-Host ("  SHA256  ：{0}" -f $hash)
Write-Host ("  校验文件：{0}" -f $shaPath)
Write-Host ("  耗时    ：{0:N0} 秒" -f $stopwatch.Elapsed.TotalSeconds)
