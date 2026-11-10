# WatchMod 当前版本移植

日期：2026-11-11。保留手工合成栏左下角（slot 2）放置 RealTimeClockBlock 时显示游戏时间和天数的原有功能。

## 当前接入

- `Plug/WatchMod.cs` 使用 `RegisterUpdateExtension<SubsystemGameWidgets>`，owner 为 WatchMod，持有并释放自己的注册 token。
- `Subsystem/WatchGameWidgetsExtension.cs` 在真实 Views 宿主的 Attached 阶段取得当前世界时间；基类 Update 允许原宿主执行，AfterUpdate 同步本 Mod 的 HUD。
- 每个 GameWidget 读取自己的 PlayerData.ComponentPlayer 和 ComponentCraftingTable，独立持有面板；不再访问 GameWidgets[0]。
- 玩家未出生、重生间隙、取走时钟、移除视图时清理对应面板；世界解绑或 Mod 卸载时清理全部自有面板。
- 保留 64×48 面板、白边、字号、游戏内 HH:mm 与 Day 显示。控件挂在本玩家 LeftControlsContainer，名称为 WatchMod.Panel / WatchMod.Time / WatchMod.Day；不清空或替换其他 Mod 的 UI。
- 无其他 Mod 依赖，不查询 ConsoleMod、联机 Mod 等的类型；不改数据库 Class，不新建 Player.Watch 成员，不自行调用 Host.Update。
- 去掉旧版吞异常后显示 --:-- 的处理。依赖或控件契约缺失由扩展框架明确报告。

## 版本与构建

目标 net10.0，API 元数据 0.2.1.0，Mod 自身版本保持 1.0.1，IsMergeLib=true。生产与测试工程都通过 PackageReference 精确引用已发布的 Engine/EntitySystem/Survivalcraft `[0.2.1.0]`，使用已配置的 cnb-nuget 源 `https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json`；不引用主仓库本地引擎 DLL，不递归构建或覆盖主仓库，不回退旧包。

```powershell
dotnet restore Mod/WatchMod/WatchMod.csproj
dotnet build Mod/WatchMod/WatchMod.csproj -c Release --no-restore
```

两个工程启用 CopyLocalLockFileAssemblies，框架依赖只来自 NuGet；测试额外引用当前 WatchMod DLL，缺失时明确失败。Release 仅混淆 WatchMod，最终 DLL 位于 bin/Release/net10.0/Obfuscar/WatchMod.dll；引擎不是混淆或打包 Module，scmod 只包含 ModInfo.xml 与 Lib/WatchMod.dll。

## 本轮 0.2.1 迁移与验证结果

- 生产及 Tests 三核心依赖均锁定 `[0.2.1.0]`；API 元数据 0.2.1.0，自身版本保持 1.0.1。
- 源码核对继续使用真实 Views 的 `RegisterUpdateExtension<SubsystemGameWidgets>`，每个 PlayerData 独立解析当前实体与合成栏；token 释放与 Detached 清理自有 UI 保持原流程，无需改换入口。
- 未使用旧数据库替换、其他业务 Mod 依赖、直接 Host.Update 或静默默认显示；清理异常收集后明确抛出。
- 本轮 Release 编译与 Obfuscar 已实际通过，0 警告、0 错误。已与 MemoryBankDrawMod、SuAPIShadows 和现有翻译包一起单实例进入原世界，日志无本轮 WatchMod 错误；时钟触发/双玩家/重生等完整界面场景尚未逐项验收；下节 0.2.0 结果仅为历史记录。
- 验收在实际游戏宿主进行：slot 2 时钟出现/移除、双玩家归属、重生、离开/重进世界、与其他 Views 扩展共存、卸载后自有 HUD 清理。现有独立内存测试保留作为可选辅助，不新增独立运行 Mod 的验收要求。

## 上轮 0.2.0 实际验证（历史）

- 已完成 NuGet 0.2.0.0 引用与 API 元数据迁移；生产实现和 12 项测试逻辑保持不变。
- Release 编译与 Obfuscar：通过，0 警告、0 错误。
- 普通与最终混淆 DLL 的测试进程均启动失败：Program.Main 创建 BitmapFont 前抛出 FileNotFoundException，缺少 OpenTK, Version=1.1.0.0, PublicKeyToken=bad199fe84eb3df4；12 项用例均未开始，不能报告通过。
- 旧版 0.1.6/0.1.7 与本版包均未声明或携带 OpenTK。上轮成功测试复制了主仓库完整 DLL；当前纯 NuGet 测试缺少游戏宿主运行依赖。证据见 [OpenTK 运行环境差异](nuget-020-runtime-dependency.md)，不能据此判定 0.2.0.0 回归或游戏无法加载。
- 此前针对本地框架 DLL 的 Release/混淆和 12 项测试通过记录仅为迁移前验证，不能当作本轮 NuGet 0.2.0.0 的结果。
- 测试覆盖触发物品、原显示格式、双玩家 UI 归属、重生、视图增删、另一独立扩展与相反注册顺序、原宿主只调用一次、其他扩展否决、卸载重载、世界隔离及解绑清理。
- 使用 NuGet 真实框架注册器、原生 SubsystemUpdate 和实际 Mod DLL；通过反射组装内存世界与控件。不代表渲染、真实存档加载或联机实机验收。细节见 Tests/README.md。

## 接口边界

当前框架按宿主精确类型匹配。另一 Mod 若仍将 Views 替换为派生子系统，本扩展不会自动挂到替换类；应由该 Mod 迁移自己的挂载方式，不增加 Watch 的跨 Mod 依赖或降级路径。当前工作区 ConsoleMod 已使用共享 Views 扩展。

Watch 所需 API 已具备，本轮未发现必须由框架会话新增的接口。本轮未修改主仓库框架和其他 Mod；已按授权将本 Mod 包复制到指定游戏 Mods 目录。

## 历史实现

旧版 SuWatchComponent 使用 ComponentTemplate + IUpdateable，绕开当时 ConsoleMod 的子系统替换。此次按当前已存在的共享宿主扩展迁移，删除旧注册与实现；旧说明中“ConsoleMod 必然独占 Views”的结论不再适用于当前工作区。

Source: Survivalcraft/SuAPI/UpdateExtensionRegistry.cs:RegisterUpdateExtension；UpdateExtension.cs:Attached/AfterUpdate/Detached；Game/SubsystemGameWidgets.cs:Update；Game/PlayerData.cs:ComponentPlayer；Game/SubsystemTimeOfDay.cs:TimeOfDay/Day。

## 上轮 0.2.0 交付包（历史）

已用最终混淆 DLL 生成 bin/Release/net10.0 下带 [SuAPI] 前缀、API0.2.0.0 后缀的 .scmod。根目录 ModInfo.xml、扁平 Lib/、Content/ 和正斜杠条目检查通过，逐条读取与 CRC 校验通过；包内仅含本 Mod DLL，不含核心库或其他 Mod。三份核心输出 DLL 与 CNB 缓存包的 SHA256 一致。本 Mod 程序集未引用其他业务 Mod。旁边 .verification.json 保存包与程序集哈希及依赖来源。尚未部署或进行实机验收。

## 本轮 0.2.1.0 交付包

最终混淆 DLL 已打包为 `[SuAPI]WatchMod-1.0.1-API0.2.1.0.scmod` 并复制到指定游戏 Mods 目录。根清单、扁平 Lib、正斜杠 ZIP 条目、逐条解压/CRC、最终 DLL 和部署哈希检查通过；没有打入引擎核心库。三份核心构建依赖均来自 CNB 正式0.2.1包，输出DLL与包内DLL哈希一致；Mod程序集没有其他业务Mod引用。

原世界加载检查通过不等于全部业务验收，仍需手工核对手工栏slot2、双玩家归属、重生、世界重进和其它Views扩展组合。本轮没有把独立内存测试环境作为Mod交付前提，也未新增本地引擎DLL依赖。
