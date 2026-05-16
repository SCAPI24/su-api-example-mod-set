---
name: su-api-examples
description: SuAPI Example Mod Set 管理技能。当用户要添加 Mod 到示例集、同步 Mod 项目到示例仓库、管理 su-api-example-mod-set 仓库时触发。触发词：「添加示例」「同步到示例」「add to examples」「example mod set」「示例集」。
---


> **仓库地址**：git@gitee.com:SC-SPM/su-api-example-mod-set.git
> 此文件是 AI Agent 技能配置，可用于 QClaw/OpenClaw 导入。
> {REPO_ROOT} = 本仓库根目录（示例 Mod 集合）


# SuAPI Example Mod Set

仓库：`{REPO_ROOT}` → `git@gitee.com:SC-SPM/su-api-example-mod-set.git`

## 同步机制

通过 `SYNC_LIST` 文件控制哪些 Mod 文件夹被 Git 追踪：

```
# SYNC_LIST 格式：每行一个 Mod 文件夹名
ConsoleMod
```

- 列出的文件夹 → Git 可追踪
- 未列出的文件夹 → Git 忽略
- 已追踪文件夹内的 `bin/`、`obj/` 自动排除

## 添加 Mod 到示例集

```
1. 编辑 SYNC_LIST，追加 Mod 文件夹名（一行一个）
2. 运行 sync-gitignore.ps1 重新生成 .gitignore
3. git add -A && git commit && git push
```

### 详细步骤

```powershell
# 1. 追加 Mod 文件夹名到 SYNC_LIST
Add-Content "SYNC_LIST" "MyNewMod"

# 2. 重新生成 .gitignore
pwsh "sync-gitignore.ps1"

# 3. 提交推送
cd "{REPO_ROOT}"
git add -A
git commit -m "add MyNewMod to example set"
git push
```

### 移除 Mod 同步

从 `SYNC_LIST` 删除对应行，运行 `sync-gitignore.ps1`，然后 `git commit && git push`。已追踪的历史文件不受影响。

## SSH 配置

```powershell
git config core.sshCommand "ssh -i ~/.ssh/Gitee -o IdentitiesOnly=yes"
```

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
- **SSH 认证失败**：检查 `~/.ssh/Gitee` 密钥是否正确，`~/.ssh/config` 是否有 Gitee 配置
