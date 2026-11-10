# NuGet 独立测试的 OpenTK 运行环境差异

日期：2026-11-11。本文为 0.2.0 升级的历史记录。当前工程已改为精确 `[0.2.1.0]`，本轮Release/混淆和游戏宿主原世界加载已通过，完整HUD业务场景尚未逐项验收；以下旧包内容和运行结果不代表已核实 0.2.1。

## 已确认结果

WatchMod 使用 net10.0，Engine、EntitySystem、Survivalcraft 三包均精确引用 `[0.2.0.0]`。Release 编译及仅 WatchMod 的 Obfuscar 混淆通过，0 警告、0 错误：发布 API 可供当前 Mod 编译。

普通 DLL 与最终混淆 DLL 两次测试运行均在 Program.Main 创建 BitmapFont 前抛出 FileNotFoundException，缺失程序集身份为：

```text
OpenTK, Version=1.1.0.0, Culture=neutral, PublicKeyToken=bad199fe84eb3df4
```

12 项内存业务集成用例均未开始。因此当前结果是“编译可用，运行测试未通过”，没有业务用例通过结论。未通过复制本地框架 DLL、添加未经核对的 OpenTK 版本或跳过字体初始化绕过失败。

## 发布来源与依赖证据

- 使用已配置的 cnb-nuget：`https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json`。
- 三包 `.nupkg.metadata` 的 source 均指向 CNB；repository commit 均为 `771afab7707b42156cda08593bba29cee6e84c69`。
- Engine 0.2.0.0 的 nuspec 仅声明 System.Drawing.Common 8.0，`lib/net10.0/` 只有 Engine.dll，没有 OpenTK。
- EntitySystem 和 Survivalcraft 0.2.0.0 也没有声明 OpenTK 依赖。
- 测试工程只引用上述已发布 NuGet 和当前 WatchMod DLL，不读取、复制或构建主仓库框架 bin。

该证据定位到独立 NuGet 消费者的运行依赖闭包缺失；正式游戏原本自带 OpenTK，因此不能据此断言正式游戏无法加载 WatchMod，也不能将游戏自带依赖当成本测试已通过。

## 复现

以下是上轮使用的命令。当前工程版本已经升级，直接执行会验证 0.2.1，不能复现旧包版本结论；独立程序仅作辅助，业务验收使用游戏宿主：

```powershell
dotnet restore Mod/WatchMod/WatchMod.csproj
dotnet build Mod/WatchMod/WatchMod.csproj -c Release --no-restore
dotnet run --project Mod/WatchMod/Tests/WatchMod.IntegrationTests.csproj -c Release
$watchObfuscatedDll = (Resolve-Path -LiteralPath 'Mod/WatchMod/bin/Release/net10.0/Obfuscar/WatchMod.dll').Path
dotnet run --project Mod/WatchMod/Tests/WatchMod.IntegrationTests.csproj -c Release "-p:WatchAssemblyPath=$watchObfuscatedDll"
```

本轮后两条运行都报告上述缺失程序集，尚未进入任何 Test 调用。测试不启动 Survivalcraft.exe、不打开窗口、不读写真实世界。

## 旧版本为什么可以运行

已检查来自同一 CNB 源的缓存包元数据和实际 Engine.dll AssemblyReference：

| Engine 包版本（NuGet 规范化） | 目标框架 | 包内 DLL | nuspec 依赖 | Engine.dll 对 OpenTK 的引用 |
|---|---|---|---|---|
| 0.1.6 | net8.0 | 仅 Engine.dll | System.Drawing.Common 8.0.0 | 1.1.0.0，token bad199fe84eb3df4 |
| 0.1.7 | net10.0 | 仅 Engine.dll | System.Drawing.Common 8.0.0 | 同上 |
| 0.2.0 | net10.0 | 仅 Engine.dll | System.Drawing.Common 8.0.0 | 同上 |

因此 OpenTK 未随 NuGet 提供不是 0.2.0.0 新增的差异。旧版 Mod 经 NuGet 编译后放进游戏运行，会使用游戏宿主本来就提供的 OpenTK；编译成功不要求将游戏所有运行依赖都打入 Mod。

上轮本会话 Watch 普通/混淆 DLL 的 12 项成功记录也不是纯 NuGet 消费测试：当时 Tests csproj 以 HintPath 引用主仓库三件套，并用 `None Include="$(EngineDirectory)\*.dll" CopyToOutputDirectory="PreserveNewest"` 复制整个框架输出目录的 DLL。该目录实际 OpenTK.dll 的程序集身份与本轮异常完全一致。此次移除整目录复制，改为纯 NuGet 消费，测试进程才暴露宿主依赖不足。

这里核实的是两轮工程配置、包内容和程序集身份；没有把未保存的历史逐程序集加载追踪当作证据。也没有在这次调查中重新运行旧包消费者。

## 后续协作范围

三个 Mod 使用 NuGet 0.2.0.0 的 Release 编译与混淆已完成；本问题不阻塞 Mod 的编译交付，也不能推断新包 API 不兼容或正式游戏无法加载。

若目标包含完全独立运行 NuGet 消费者，发布会话需要确认是否把匹配的 OpenTK 托管运行资产或依赖纳入包的支持范围。若包定位为游戏内 Mod 的编译引用，则独立测试工程应明确配置宿主运行依赖。两种运行契约应明确选定，不能悄悄拷贝 DLL 或随意更换 OpenTK 版本来掩盖测试环境差异。

当前测试保留纯 NuGet 模式并如实记录未执行用例；真实游戏图形、世界加载、联机及 Android 均未验收。
