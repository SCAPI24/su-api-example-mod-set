# MemoryBankDrawMod 当前工作区迁移核对

核对日期：2026-11-11。本记录区分已完成的正式编译、混淆和宿主加载检查，与尚未逐项完成的界面/保存/联机验收。

## 接入方式与兼容边界

- 唯一表现增强入口为 `Frame.Update`，调用 `MemoryBankDialogPresenter.EnhanceOpenDialogs`。不再写入数据库 Class，也不再替换 `SubsystemMemoryBankBlockBehavior`。
- 仅识别精确类型 `EditMemoryBankDialog`。从原始对话框读取同一个 `MemoryBankData` 和 `Action`，将它们交给绘图对话框；保留创建对话框的原版或其他 Mod 的保存、物品 ID、电路及网络回调。
- 绘图窗口操作临时副本；确认时写回 Data 并调用原回调，取消或卸载不提交。卸载撤销自己的帧订阅并关闭自己的窗口。
- 不依赖 ConsoleMod、WatchMod、SuAPIShadows 或 SuMultiplayer，不查询它们的类型，不复制它们的保存与网络逻辑。其他 Mod 的自定义 Dialog 子类不属于当前精确匹配契约。
- 此对象是 Dialog，不是 Screen，也不是 IUpdateable；本迁移使用现有帧事件，不宣称已接入 RegisterScreenExtension 或 RegisterUpdateExtension。
- 原生私有字段与 TextBox.Text 的反射契约在加载时校验；契约或增强失败明确报告，不静默回退旧子系统替换路径。

## 版本与构建来源

工程保持 net10.0，通过 PackageReference 严格锁定 Engine、EntitySystem、Survivalcraft 的 `[0.2.1.0]` NuGet 包。使用已有 cnb-nuget 源：`https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json`。不递归构建主框架，也不回退本地 DLL、源码引用或旧包。

ModInfo 的 APIVersion 为 0.2.1.0；Mod 自身 Version 保持 1.1.0，IsMergeLib 保持 true。CopyLocalLockFileAssemblies=true 将依赖复制到构建输出以供混淆解析；Obfuscar 仅处理 MemoryBankDrawMod.dll，核心 DLL 仅通过 AssemblySearchPath 解析，不能混淆或打入 Mod 包。保留入口 SkipType 与构建时复制、修改中间配置的流程。

## 可重复的只读源码检查

从主仓库根目录运行：

```powershell
python Mod/MemoryBankDrawMod/Tools/verify_compatibility_contracts.py
```

脚本仅读取本 Mod 的源码、工程、混淆和清单元数据，检查入口、原始 data/handler 传递、旧替换路径移除、卸载代码、独立依赖及固定 NuGet 契约。不读取主框架源码，不比较主仓库 Version；原生私有字段的实际契约仍需针对新包运行验证。不读取旧 ScMultiplayer，不要求安装任何其他 Mod，不写文件。

这些是源码片段和 XML 结构检查。PASS 只表示被检查的源码契约存在，不证明控制流、回调次数、界面帧时序、保存或网络行为正确；不能替代真实程序集编译或游戏运行验收。未使用源代码替身测试来宣称业务通过。

## 本轮构建与游戏验证

本轮已使用正式工程及 NuGet 0.2.1.0 三包完成 Release 重编和 Obfuscar 混淆，0 警告、0 错误；只读静态契约检查通过。最终版本也已按 Android AOT 约束改用复用 List 收集后逐项 Remove，并补齐外部关闭窗口完全脱离后的回调清理；再次正式 Release 重编、混淆和静态检查均通过（0 警告、0 错误）。此前 0.2.0.0 结果不作为本轮通过依据。已与 WatchMod、SuAPIShadows 和现有翻译包一起单实例进入原世界，启动和加载日志没有本轮 MemoryBankDrawMod 错误。确认/取消、物品/方块回写和联机行为仍需完整业务场景验收。

Presenter 改为每个 Mod 实例所有的 IDisposable，只有反射契约元数据保持 static。原生对话框身份在 Hide/Show 前登记所有权。Dispatcher 在当前游戏线程立即执行，因此没有把重复增强认定为已发生问题；身份登记用于实例生命周期保护，不表示允许后台线程直接修改 UI。绘图窗口带一次性终结门闩：确认才写回原 Data 并调用原 Action 一次；取消、失败清理和卸载禁止提交，清理仅作用于本实例拥有的窗口。

0.2.1 的 WidgetPresentation 仅用于布局前文本/字体准备，不允许改树或 Show/Hide；ScreenExtension 仅针对原生 Screen。此功能需要替换 Dialog 的表现，因此保留 Frame.Update 是正确生命周期边界，不强行套用上述 API，不替换原生存储/电路/网络子系统，不反射修改原 Action。

- 单机物品和方块：打开绘图界面，切换 Linear/Grid/Draw，确认后验证内容保存；物品唯一 ID/数量及方块电路重新模拟仍由原回调负责。
- 同样入口取消：原内容不变，保存回调不执行。
- 联机主机和客户端分别编辑物品/方块：确认仍进入原拥有者网络提交与权限校验，取消不发送提交。
- 重复打开、卸载、退出世界：无重复窗口、遗留 UI 或额外保存；只关闭本 Mod 所拥有窗口。
- 与 WatchMod、ConsoleMod、SuAPIShadows 同时加载：不抢占其子系统或修改其 UI。
- 包发布前核对扁平 Lib/、根 ModInfo.xml、正斜杠 ZIP 条目及 IsMergeLib=true；包不包含引擎核心 DLL。

本轮仅修改本 Mod 工程依赖、清单、实例 Presenter、绘图对话框终结保护、入口生命周期、静态脚本及本说明。Mod 自身 Version=1.1.0、net10.0、IsMergeLib=true 与仅本 Mod 混淆保持不变。只读静态检查不能替代真实回调次数、Dispatcher 时序及 UI/单机/联机验收。

## 本轮交付与兼容结果

已生成 `[SuAPI]MemoryBankDrawMod-1.1.0-API0.2.1.0.scmod` 并复制到指定游戏 Mods 目录，逐条ZIP/CRC、根ModInfo.xml、扁平Lib/、正斜杠路径、最终混淆DLL与部署包哈希核对通过。包内仅本Mod DLL与清单/资源目录，不含核心DLL；输出核心程序集与CNB正式0.2.1包内程序集哈希相同。程序集不引用WatchMod、光影、控制台、联机等业务Mod。

本功能使用Dialog，因此保留现有正确的Frame.Update边界；0.2.1新增Screen/WidgetPresentation接口不提供对话框替换契约，不能在布局回调里改树。新实现仅保存自己实例的窗口、传递原生同一个data/Action，外部HideAllDialogs也会在动画结束、ParentWidget=null后取消并释放回调，不保留旧世界引用。
