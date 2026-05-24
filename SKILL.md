---
name: su-api-examples
description: SuAPI Example Mod Set 管理技能。当用户要添加 Mod 到示例集、同步 Mod 项目到示例仓库、管理 su-api-example-mod-set 仓库时触发。触发词：「添加示例」「同步到示例」「add to examples」「example mod set」「示例集」。
---

> **Gitee**: git@gitee.com:SC-SPM/su-api-example-mod-set.git
> **GitHub**: git@github.com:SCAPI24/su-api-example-mod-set.git
> 此文件是 AI Agent 技能配置，可用于 QClaw/OpenClaw 导入。

# SuAPI Example Mod Set

仓库：`P:\UGIT\Survivalcraft\Mod`
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
StringInterceptor
MemoryBankDrawMod
```

- 列出的文件夹 → Git 可追踪
- 未列出的文件夹 → Git 忽略
- 已追踪文件夹内的 `bin/`、`obj/`、`.vs/`、`.scmod`、`pack-temp/` 自动排除
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
Add-Content "P:\UGIT\Survivalcraft\Mod\SYNC_LIST" "MyNewMod"

# 2. 重新生成 .gitignore
pwsh "P:\UGIT\Survivalcraft\Mod\sync-gitignore.ps1"

# 3. 提交推送（双平台）
cd "P:\UGIT\Survivalcraft\Mod"
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
ModName/.vs/
```

关键：`bin/` `obj/` `.vs/` 排除必须在 `!ModName/**` **之后**声明，Git 同文件中后匹配的规则优先。

## 运行时铁律（从迁移经验中总结）

### 1. ModLoader 依赖加载

ModLoader 只加载 .scmod 内两种 DLL：(1) 与 ModInfo Identifier 同名的 DLL，(2) `<Dependencies>` 中声明的 DLL。未声明的依赖 DLL 即使在 ZIP 中也会被静默跳过，导致 `ReflectionTypeLoadException: Unable to load one or more of the requested types`。

```xml
<!-- ModInfo.xml 必须声明所有非主 Identifier 的 DLL -->
<Dependencies>
    <Dependency>
        <ModInfo>
            <Identifier>Comms</Identifier>
        </ModInfo>
    </Dependency>
</Dependencies>
```

### 2. LoadingManager.ReplaceItem 精确匹配 name

MAUI net10.0 版替换 Screen 加载步骤时，`ReplaceItem(name, action)` 的 name 必须精确匹配原始 `QueueItem` 的 name（如 "Initialize PlayScreen"），不是 Screen 名（如 "Play"）。禁止用 `QueueItem` 添加同名 `AddScreen`——会导致 `ArgumentException: An item with the same key has already been added`。

- `LoadingManager` 是 **static class**，不能声明变量，直接 `Game.LoadingManager.QueueItem/ReplaceItem`
- `QueueItem(string name, Action action)` 只有 2 个参数
- `ReplaceItem` 返回 `bool`：`true` = 替换成功，`false` = 未找到

### 3. EventBus 静默吞异常

`TriggerEvent` 的回调异常只写 `Console.WriteLine`，不记入 `Game.log`。调试时在 handler 外围 try-catch 打 `Log.Error()` 或加步进标记。

### 4. Loading.Initialize 事件参数

MAUI net10.0 版：`TriggerEvent("Loading.Initialize", new object[] { typeof(LoadingManager) })`，传 `typeof(LoadingManager)` 而非 `List<Action>` 实例。

## 编译规则

- 目标框架: net10.0-android + net10.0-windows10.0.19041.0
- 条件编译: ANDROID / WINDOWS 符号（`#if WINDOWS` 包裹 Windows 专属 API）
- Windows 端用 ProjectReference（EntitySystem.csproj，非 GameEntitySystem.csproj）
- Android 端用 DLL Reference（HintPath 指向游戏 bin/Debug/net10.0-android/）
- SDK 样式 csproj 自动包含 `**/*.cs`，显式 `<Compile Include>` 须删除（否则 NETSDK1022）
- Obfuscar 仅 Windows 端（PostBuild Target Condition）
- 编译工具: `dotnet build`（net10.0 项目）

## 常见问题

- **添加 Mod 后 bin/obj 仍被追踪**：可能是历史缓存，用 `git rm -r --cached ModName/bin ModName/obj` 清除
- **推送被拒**：远程仓库可能有新提交，先 `git pull origin master --rebase`
- **SSH 认证失败**：检查 `~/.ssh/Gitee` / `~/.ssh/Ugit1` 密钥，`~/.ssh/config` 是否已配置
- **Mod 目录是独立 Git 仓库无法同步**：移除 `.git` 子目录（`Remove-Item -Recurse -Force ModName\.git`）后重新 add
- **忘记推送 GitHub**：`git push github master` 补推
- **pwsh 未安装导致 sync-gitignore.ps1 无法运行**：可用 Python 脚本替代生成 .gitignore
- **.scmod/pack-temp 不应入库**：已在 .gitignore 全局排除，新添加的 Mod 也应排除这些文件
- **ModInfo.xml 必须声明依赖 DLL**：未声明 → `ReflectionTypeLoadException`，见铁律1
- **ReplaceItem name 不匹配**：name 是 QueueItem 注册名（"Initialize PlayScreen"）不是 Screen 名（"Play"），见铁律2