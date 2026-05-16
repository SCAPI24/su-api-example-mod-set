# sync-gitignore.ps1 — 从 SYNC_LIST 重新生成 .gitignore 的同步部分
# 用法: pwsh sync-gitignore.ps1
# 原理: SYNC_LIST 列出要同步的 Mod 文件夹名，脚本据此生成 .gitignore
#       根 .gitignore 用 /* 忽略所有文件夹，再用 ! 反忽略列出的 Mod
#       每个 Mod 文件夹后紧跟 bin/ 和 obj/ 排除规则

$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$syncListPath = Join-Path $rootDir "SYNC_LIST"
$gitignorePath = Join-Path $rootDir ".gitignore"

if (-not (Test-Path $syncListPath)) {
    Write-Error "SYNC_LIST not found at $syncListPath"
    exit 1
}

# Read SYNC_LIST: non-empty, non-comment lines
$mods = Get-Content $syncListPath | Where-Object {
    $_ -match '\S' -and $_ -notmatch '^\s*#'
} | ForEach-Object { $_.Trim() }

if ($mods.Count -eq 0) {
    Write-Warning "SYNC_LIST is empty, no mods will be synced"
}

# Build the header (everything before the generated section)
$header = @(
    "# SuAPI Example Mod Set — .gitignore",
    "# 此文件由 SYNC_LIST 驱动，修改 SYNC_LIST 后运行 sync-gitignore.ps1 更新",
    "",
    "## 根目录：默认忽略所有文件夹",
    "/*",
    "",
    "## 保留项目根文件",
    "!.gitignore",
    "!SYNC_LIST",
    "!sync-gitignore.ps1",
    "!README.md",
    "!SKILL.md",
    "!AGENT-PROFILE.md",
    ""
)

# Build the generated section
$generated = @(
    "## ===== 以下由 SYNC_LIST 生成，请勿手动修改 ====="
)
foreach ($mod in $mods) {
    $generated += "## sync:$mod"
    $generated += "!$mod/"
    $generated += "!$mod/**"
    # bin/ and obj/ must come AFTER !mod/** to override the un-ignore
    $generated += "$mod/bin/"
    $generated += "$mod/obj/"
}

# Write .gitignore
$content = ($header + $generated) -join "`n"
[System.IO.File]::WriteAllText($gitignorePath, $content, [System.Text.UTF8Encoding]::new($false))

Write-Output "Updated .gitignore with $($mods.Count) mod(s): $($mods -join ', ')"
Write-Output ""
Write-Output "NOTE: If files were previously tracked, run:"
Write-Output "  git rm -r --cached ."
Write-Output "  git add -A"
