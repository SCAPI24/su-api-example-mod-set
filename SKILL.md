---
name: su-api-examples
description: SuAPI Example Mod Set 管理技能。当用户要添加 Mod 到示例集、同步 Mod 项目到示例仓库、管理 su-api-example-mod-set 仓库时触发。触发词：「添加示例」「同步到示例」「add to examples」「example mod set」「示例集」。
---

> **Gitee**: git@gitee.com:SC-SPM/su-api-example-mod-set.git
> **GitHub**: git@github.com:SCAPI24/su-api-example-mod-set.git
> 此文件是 AI Agent 技能配置，可用于 QClaw/OpenClaw 导入。

# SuAPI Example Mod Set

仓库：`{REPO_ROOT}`
- origin → Gitee: `git@gitee.com:SC-SPM/su-api-example-mod-set.git`
- github → GitHub: `git@github.com:SCAPI24/su-api-example-mod-set.git`

## 同步机制

通过 `SYNC_LIST` 文件控制哪些 Mod 文件夹被 Git 追踪：

```
# SYNC_LIST 格式：每行一个 Mod 文件夹名
ConsoleMod
RainWithoutDawn
TemperatureImmunity
Comms
ScMultiplayer
SurvivalcraftMiniMap
```

- 列出的文件夹 → Git 可追踪
- 未列出的文件夹 → Git 忽略
- 已追踪文件夹内的 `bin/`、`obj/`、`.vs/` 自动排除
- 根目录保留文件：`.gitignore` / `SYNC_LIST` / `sync-gitignore.ps1` / `README.md` / `SKILL.md` / `AGENT-PROFILE.md` / `images/`（若新增根文件需同步更新 sync-gitignore.ps1）

## 添加 Mod 到示例集

```
1. 编辑 SYNC_LIST，追加 Mod 文件夹名（一行一个）
2. 运行 sync-gitignore.ps1 重新生成 .gitignore
3. 确认新 Mod 目录内无 .git 子目录（独立仓库需先移除 .git）
4. git add -A && git commit && git push origin master && git push github master
```

### 详细步骤

```powershell
# 1. 追加 Mod 文件夹名到 SYNC_LIST
Add-Content "{REPO_ROOT}\SYNC_LIST" "MyNewMod"

# 2. 重新生成 .gitignore
pwsh "{REPO_ROOT}\sync-gitignore.ps1"

# 3. 提交推送（双平台）
cd "{REPO_ROOT}"
git add -A
git commit -m "add MyNewMod to example set"
git push origin master    # Gitee
git push github master   # GitHub
```

### 移除 Mod 同步

从 `SYNC_LIST` 删除对应行，运行 `sync-gitignore.ps1`，然后 `git commit && git push`。已追踪的历史文件不受影响。

## SSH 配置

```powershell
# Gitee
git config core.sshCommand "ssh -i ~/.ssh/Gitee -o IdentitiesOnly=yes"
# 或使用全局 ~/.ssh/config
```

> 若已配置 `~/.ssh/config`（Gitee → Gitee key，GitHub → Ugit1 key），无需 repo 级 SSHCommand。

## .gitignore 生成规则

`sync-gitignore.ps1` 为每个 SYNC_LIST 条目生成：

```
!ModName/
!ModName/**
ModName/bin/
ModName/obj/
```

关键：`bin/` `obj/` 排除必须在 `!ModName/**` **之后**声明，Git 同文件中后匹配的规则优先。

## 常见问题

- **添加 Mod 后 bin/obj 仍被追踪**：可能是历史缓存，用 `git rm -r --cached ModName/bin ModName/obj` 清除
- **推送被拒**：远程仓库可能有新提交，先 `git pull origin master --rebase`
- **SSH 认证失败**：检查 `~/.ssh/Gitee` / `~/.ssh/Ugit1` 密钥，`~/.ssh/config` 是否已配置
- **Mod 目录是独立 Git 仓库无法同步**：移除 `.git` 子目录（`Remove-Item -Recurse -Force ModName\.git`）后重新 add
- **忘记推送 GitHub**：`git push github master` 补推
- **pwsh 未安装导致 sync-gitignore.ps1 无法运行**：可用 Python 脚本替代生成 .gitignore
