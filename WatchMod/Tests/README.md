# WatchMod 内存业务集成测试

本项目通过 PackageReference 精确引用 CNB 已发布的 Engine、EntitySystem、Survivalcraft `[0.2.1.0]`，并引用已经构建好的 WatchMod Release DLL；不含引擎 ProjectReference，不读取或复制主仓库 bin，不构建、覆盖或修改主仓库框架。主 WatchMod 工程必须排除 `Tests/**/*.cs`，测试程序不进入生产 DLL 或 scmod。

## 本轮状态

生产与 Tests 已同步锁定 NuGet `[0.2.1.0]`，API 元数据为 0.2.1.0。本轮 Release 编译与 Obfuscar 已实际通过，0 警告、0 错误；独立测试未运行；指定游戏宿主已与MemoryBankDrawMod、光影及现有翻译包成功进入原世界，完整HUD/双玩家业务场景尚未逐项验收；下述 0.2.0 的 OpenTK 失败仅作历史环境边界，不能当作 0.2.1 的通过或失败结果。最终业务验收使用实际游戏宿主；本内存程序保留为可选辅助，不要求独立运行 Mod，不复制主仓库 DLL 或添加未经核对的运行依赖。

## 可选辅助执行

使用已配置的 cnb-nuget 源（https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json）。由主代理完成 WatchMod 的 Release 构建后，再执行：

```powershell
dotnet run --project Mod/WatchMod/Tests/WatchMod.IntegrationTests.csproj -c Release
```

默认 WatchMod 来源为 `Mod/WatchMod/bin/Release/net10.0/WatchMod.dll`；验证最终混淆 DLL 时传入 `-p:WatchAssemblyPath=<absolute-obfuscated-dll-path>`。缺少 WatchMod 产物将明确失败，不自动构建引擎、不改用本地引擎 DLL 或旧包。框架与传递运行依赖由 NuGet 解析并复制到测试输出目录。

2026-11-11 上轮历史结果：NuGet 0.2.0.0 的 WatchMod Release 编译与 Obfuscar 通过（0 警告、0 错误）。普通与最终混淆 DLL 的测试运行均在 Program.Main 创建 BitmapFont 前因缺少 `OpenTK, Version=1.1.0.0, PublicKeyToken=bad199fe84eb3df4` 抛出 FileNotFoundException；12 项用例均未开始，运行测试未通过。详见 [运行环境差异与复现](../doc/nuget-020-runtime-dependency.md)。此前本地框架 DLL 的通过结果不代表本轮 NuGet 验证通过。正式游戏原本自带 OpenTK，本结果不能证明游戏无法加载 WatchMod。

测试进程不是 Survivalcraft.exe，不打开游戏窗口，不读写真实世界。退出码 0 表示所有用例通过，非零表示失败。编写完成不等于已经执行或通过，结果以实际运行输出为准。

## 验证内容

- 通过真实 WatchMod.OnLoad/OnUnload 注册和释放，执行真实 ModEventBus、UpdateExtensionRegistry、UpdateExtensionProjectBinding、InvocationRunner、SubsystemUpdate.Update。
- slot 2、数量、contents 与 metadata；游戏时间及 Day 格式、原面板尺寸和字号。
- 两个玩家视图各读自己的合成栏，不共享面板和标签实例；重生后读取新的 ComponentPlayer/ComponentCraftingTable。
- 空视图开始、后加入视图、移除视图、玩家暂时不存在、卸载及重新加载。
- Watch 与独立 peer 同宿主，两种注册顺序都只调用一次真实 Views 宿主；最后一个扩展移除后恢复原生调度。
- 独立 peer 否决真实宿主时，Watch 的 AfterUpdate 仍按已完成扩展轮次执行；下一轮许可不残留。
- Watch 只清理自己拥有的 UI，不删除其他扩展控件；项目绑定销毁以及两个独立世界/Mod 实例的状态隔离。

## 夹具与边界

Source: `tests/SuAPI.UpdateIntegrationTests/Program.cs:Fixture`、`Survivalcraft/Game/SubsystemUpdate.cs:Update`、`SubsystemGameWidgets.cs:Update`、`LabelWidget.cs:LabelWidget`。

夹具以反射建立最小真实 Project、Entity、PlayerData 归属和合成栏数据。SubsystemGameWidgets、SubsystemTimeOfDay、ComponentPlayer、ComponentCraftingTable、原生更新器、控件及列表都来自实际框架 DLL；没有 stub API 或重写框架的调度算法。

GameWidget 跳过含资产/XML 的构造过程，通过正常 CanvasWidget 的基类初始化状态、真实 WidgetsList 及控件建立最小 HUD 树。实际 WatchMod 业务负责创建真实 CanvasWidget/RectangleWidget/LabelWidget、插入和移除。测试从公开控件树读取结果，不直接调用扩展私有方法或手动调用 AfterUpdate。

LabelWidget 默认读取 Fonts/Pericles32。测试用公开 ContentCache.Set 注册真实 BitmapFont，包含内存 ASCII glyph、不含 GPU texture；仅检查控件属性与文字，不调用绘制。CountingCamera 是真实 LoadingCamera 的计数派生类，调用 base.Update；它证明实际 SubsystemGameWidgets.Update 对每个相机的调用次数，宿主本身仍是精确原版类型，未被测试派生子系统替代。

更新器使用真实 SubsystemTime.NextFrame 和空的 SubsystemPlayers；游戏时间格式测试显式设置真实 SubsystemGameInfo 的时间值，未将无玩家时可能为零的模拟 dt 当成游戏时间推进验收。真实 registry 错误队列会被检查，防止原生调度器吞异常导致假通过。

这些测试不覆盖数据库 Load、SuAPICore 自动启动接线、真实玩家创建/重生事件、实际世界文件、图形布局/渲染、触摸/键鼠、字体纹理或 Android。反射重建的重生数据只验证 Watch 从当前 PlayerData 重新解析实体。内存测试通过不能宣称真实游戏端到端验收通过。
