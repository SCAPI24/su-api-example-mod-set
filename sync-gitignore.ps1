# sync-gitignore.ps1 — 由 SYNC_LIST 生成 .gitignore 的「生成区」
#
# 需求（用户定）：
#   1) SYNC_LIST 是唯一事实来源：列出的文件夹才进入跟踪范围（!X/ 与 !X/**）
#   2) 编译工具产物（bin/、obj/、.vs/ 等）永不进入仓库跟踪
#
# 做法：
#   · 只替换 .gitignore 里「## ===== 以下由 SYNC_LIST 生成 …」与「## ===== 生成区结束 =====」
#     之间的内容，文件里其余规则（根目录规则、全局忽略）原样保留 → 重复运行幂等。
#   · 生成后做哨兵自检：关键规则缺失就报错退出，并且**不写文件**（宁可没生效，不可静默改坏）。
#   · 读写都用显式 UTF-8：PowerShell 5.1 的 Get-Content 会按 ANSI 读无 BOM 的 UTF-8，中文会乱码。
#   · 输出统一 CRLF（仓库 .gitattributes 是 `* -text`，blob 必须与工作区字节一致）。
#
# 用法：powershell -File sync-gitignore.ps1   或   pwsh sync-gitignore.ps1
# 新增 Mod：把文件夹名加进 SYNC_LIST，再运行本脚本即可。

$ErrorActionPreference = 'Stop'

$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$syncListPath = Join-Path $rootDir 'SYNC_LIST'
$gitignorePath = Join-Path $rootDir '.gitignore'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$startMarker = '## ===== 以下由 SYNC_LIST 生成'
$endMarker = '## ===== 生成区结束 ====='

if (-not (Test-Path -LiteralPath $syncListPath)) { throw "SYNC_LIST not found at $syncListPath" }
if (-not (Test-Path -LiteralPath $gitignorePath)) { throw ".gitignore not found at $gitignorePath" }

# ── 1. 读 SYNC_LIST（唯一事实来源） ──
$syncText = [System.IO.File]::ReadAllText($syncListPath, $utf8NoBom)
$mods = @($syncText -split "`r?`n" |
    Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*#' } |
    ForEach-Object { $_.Trim() })
if ($mods.Count -eq 0) { throw 'SYNC_LIST 里没有任何文件夹名，已中止（未写文件）' }

# ── 2. 生成区内容 ──
$generated = New-Object System.Collections.Generic.List[string]
$generated.Add("$startMarker，请勿手动修改（改 SYNC_LIST 后重新运行本脚本）=====")
foreach ($mod in $mods) {
    $generated.Add("## sync:$mod")
    $generated.Add("!$mod/")
    $generated.Add("!$mod/**")
    # 编译产物：即使上面的 !mod/** 放开了整目录，这些子目录仍被排除
    $generated.Add("$mod/bin/")
    $generated.Add("$mod/obj/")
    $generated.Add("$mod/.vs/")
}
$generated.Add($endMarker)

# ── 3. 替换生成区，其余原样保留 ──
$current = [System.IO.File]::ReadAllText($gitignorePath, $utf8NoBom)
$lines = @($current -split "`r?`n")
$startIndex = -1
$endIndex = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($startIndex -lt 0) {
        if ($lines[$i].StartsWith($startMarker)) { $startIndex = $i }
    }
    elseif ($lines[$i] -eq $endMarker) { $endIndex = $i; break }
}
if ($startIndex -lt 0) { throw "在 .gitignore 里找不到起始标记（$startMarker…），已中止（未写文件）" }
if ($endIndex -lt 0) { throw "在 .gitignore 里找不到结束标记（$endMarker），已中止（未写文件）" }

$before = @()
if ($startIndex -gt 0) { $before = $lines[0..($startIndex - 1)] }
$after = @()
if ($endIndex -lt ($lines.Count - 1)) { $after = $lines[($endIndex + 1)..($lines.Count - 1)] }

$newLines = @($before) + @($generated) + @($after)
$newContent = ($newLines -join "`r`n").TrimEnd([char[]]@("`r", "`n")) + "`r`n"

# ── 4. 哨兵自检：缺关键规则就报错，不写文件 ──
$required = @('/*', '!.gitignore', '!.gitattributes', '!README_EN.md', '*.scmod')
foreach ($needle in $required) {
    if ($newContent.IndexOf($needle) -lt 0) {
        throw "自检失败：生成结果里没有 '$needle'，已中止（未写文件）"
    }
}
if ($newContent.IndexOf('/bin/') -lt 0) {
    throw "自检失败：生成结果里没有任何编译产物排除规则（*/bin/），已中止（未写文件）"
}

# ── 5. 写回（UTF-8 无 BOM + CRLF） ──
$changed = ($current -ne $newContent)
[System.IO.File]::WriteAllText($gitignorePath, $newContent, $utf8NoBom)

if ($changed) {
    Write-Output "已更新 .gitignore 生成区：$($mods.Count) 个文件夹"
} else {
    Write-Output ".gitignore 已是最新：$($mods.Count) 个文件夹（内容未变）"
}
Write-Output "每个文件夹自动排除：bin/  obj/  .vs/"
Write-Output "新增 Mod 的流程：改 SYNC_LIST → 运行本脚本 → git add"
