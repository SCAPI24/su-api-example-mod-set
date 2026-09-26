# PlayerAiMod\Instance —— 实例内容的仓库种子（2026-09-26 建）

## 为什么需要它

`PlayerAiMod` / `PlayerAiEditor` 的**实例内容**放在游戏实例根下：

```
PC      : <仓库>\publish\[SuAPI]Survivalcraft\PlayerAi\{BehaviorTrees,Questions,Scripts}
Android : /sdcard/Download/Survivalcraft/PlayerAi\{BehaviorTrees,Questions,Scripts}
```

这两处**都不被任何仓库跟踪**（`publish/` 被根仓库 `.gitignore` 忽略，设备上的更是本地文件）。
所以一旦删掉 `publish`，实例里那些**在实例中新建/改过**的行为树包、动作包、问题库、动作脚本就没了。

本目录就是那份"能被 git 跟踪的种子"：

```
PlayerAiMod/Instance/
├── BehaviorTrees/   *.scbtpak / *.scatpak
├── Questions/       *.qbank
└── Scripts/         *.aeact
```

建立时（2026-09-26）与 PC 实例**逐字节一致**：24 个文件（BehaviorTrees 10 + Questions 3 + Scripts 11）。

> 注意与"出厂模板"的区别：出厂模板写在代码里
> （`PlayerAiMod/Package/PackageTemplates.cs`、`State/QuestionBankTemplates.cs`、`Action/ActionScriptTemplates.cs`），
> 首次运行只做"缺什么补什么"，且那是**构建时**的版本。
> 实例里被改过的版本不会自动回来 —— 那才是本种子存在的意义。

## 怎么用

脚本在 `PlayerAiMod\Tools\sync-player-ai-instance.ps1`（`-Root` 指**实例根**，即含 `Survivalcraft.exe` 与 `PlayerAi\` 的那层）。

### 部署（删掉 publish 之后恢复）

```powershell
# 1) 先按常规重新发布游戏（publish/[SuAPI]Survivalcraft 会出现，其中 PlayerAi 是空的或只有出厂模板）
# 2) 把种子铺回去（默认只补缺失，不动实例里已有的同名文件）
powershell -File Mod\PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action deploy `
    -Root "P:\Ugit\Survivalcraft\publish\[SuAPI]Survivalcraft"

# 需要以仓库版本为准覆盖同名文件时加 -Force
powershell -File Mod\PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action deploy -Force `
    -Root "P:\Ugit\Survivalcraft\publish\[SuAPI]Survivalcraft"
```

平板同理（先在临时目录铺好，再整体推到设备的实例根）：

```powershell
powershell -File Mod\PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action deploy -Force -Root "$env:TEMP\pai-instance"
adb push "$env:TEMP\pai-instance\PlayerAi" /sdcard/Download/Survivalcraft/
```

### 回收（把实例里现在的版本存回仓库）

```powershell
powershell -File Mod\PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action capture `
    -Root "P:\Ugit\Survivalcraft\publish\[SuAPI]Survivalcraft"
git -C Mod status PlayerAiMod/Instance     # 看有哪些变化，然后 add/commit
```

建议在"改完树/包/库/脚本并在游戏里验证通过"之后跑一次 `capture`，让种子保持是最新可用版本。

## 约束

- `sync-player-ai-instance.ps1` **只写 ASCII**：Windows PowerShell 5.1 会把无 BOM 的 `.ps1` 按 ANSI 读，
  脚本里出现中文会当场损坏（本项目已踩过这个坑）。中文说明一律放本文件（`.md`，UTF-8）里。
- 种子里的 `*.scbtpak` / `*.scatpak` 是 ZIP 二进制；仓库的 `.gitattributes` 是 `* -text`（不做行尾转换），
  所以入库不会被损坏。
