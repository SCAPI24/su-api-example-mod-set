# MemoryBankDrawMod 当前工作区迁移核对

核对日期：2026-11-11。本记录覆盖当前工作区源码，不表示已经完成编译、游戏或联机验收。

## 接入方式与兼容边界

- 唯一表现增强入口为 `Frame.Update`，调用 `MemoryBankDialogPresenter.EnhanceOpenDialogs`。不再写入数据库 Class，也不再替换 `SubsystemMemoryBankBlockBehavior`。
- 仅识别精确类型 `EditMemoryBankDialog`。从原始对话框读取同一个 `MemoryBankData` 和 `Action`，将它们交给绘图对话框；保留创建对话框的原版或其他 Mod 的保存、物品 ID、电路及网络回调。
- 绘图窗口操作临时副本；确认时写回 Data 并调用原回调，取消或卸载不提交。卸载撤销自己的帧订阅并关闭自己的窗口。
- 不依赖 ConsoleMod、WatchMod、SuAPIShadows 或 SuMultiplayer，不查询它们的类型，不复制它们的保存与网络逻辑。其他 Mod 的自定义 Dialog 子类不属于当前精确匹配契约。
- 此对象是 Dialog，不是 Screen，也不是 IUpdateable；本迁移使用现有帧事件，不宣称已接入 RegisterScreenExtension 或 RegisterUpdateExtension。
- 原生私有字段与 TextBox.Text 的反射契约在加载时校验；契约或增强失败明确报告，不静默回退旧子系统替换路径。

## 版本与构建来源

工程保持 net10.0，通过 PackageReference 严格锁定 Engine、EntitySystem、Survivalcraft 的 `[0.2.0.0]` NuGet 包。使用已有 cnb-nuget 源：`https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json`。不递归构建主框架，也不回退本地 DLL、源码引用或旧包。

ModInfo 的 APIVersion 为 0.2.0.0；Mod 自身 Version 保持 1.1.0，IsMergeLib 保持 true。CopyLocalLockFileAssemblies=true 将依赖复制到构建输出以供混淆解析；Obfuscar 仅处理 MemoryBankDrawMod.dll，核心 DLL 仅通过 AssemblySearchPath 解析，不能混淆或打入 Mod 包。保留入口 SkipType 与构建时复制、修改中间配置的流程。

## 可重复的只读源码检查

从主仓库根目录运行：

```powershell
python Mod/MemoryBankDrawMod/Tools/verify_compatibility_contracts.py
```

脚本仅读取本 Mod 的源码、工程、混淆和清单元数据，检查入口、原始 data/handler 传递、旧替换路径移除、卸载代码、独立依赖及固定 NuGet 契约。不读取主框架源码，不比较主仓库 Version；原生私有字段的实际契约仍需针对新包运行验证。不读取旧 ScMultiplayer，不要求安装任何其他 Mod，不写文件。

这些是源码片段和 XML 结构检查。PASS 只表示被检查的源码契约存在，不证明控制流、回调次数、界面帧时序、保存或网络行为正确；不能替代真实程序集编译或游戏运行验收。未使用源代码替身测试来宣称业务通过。

## 仍需真实构建与游戏验收

已使用正式 MemoryBankDrawMod.csproj 完成 NuGet 0.2.0.0 三包恢复及 Release `build -t:Rebuild`，编译与 Obfuscar 混淆均通过，0 警告、0 错误；只读静态契约检查通过。三包来源为 CNB，包内 repository commit 为 `771afab7707b42156cda08593bba29cee6e84c69`。此次实测使用正式工程与发布包，不以此前本地 DLL 隔离编译替代。

上述结果只证明当前源码可针对这些发布包编译并完成混淆，不证明新包二进制的私有字段反射契约或 UI 行为。尚未部署或启动游戏，未完成 UI、单机保存或联机验收；以下场景仍须实际运行验证。

- 单机物品和方块：打开绘图界面，切换 Linear/Grid/Draw，确认后验证内容保存；物品唯一 ID/数量及方块电路重新模拟仍由原回调负责。
- 同样入口取消：原内容不变，保存回调不执行。
- 联机主机和客户端分别编辑物品/方块：确认仍进入原拥有者网络提交与权限校验，取消不发送提交。
- 重复打开、卸载、退出世界：无重复窗口、遗留 UI 或额外保存；只关闭本 Mod 所拥有窗口。
- 与 WatchMod、ConsoleMod、SuAPIShadows 同时加载：不抢占其子系统或修改其 UI。
- 包发布前核对扁平 Lib/、根 ModInfo.xml、正斜杠 ZIP 条目及 IsMergeLib=true；包不包含引擎核心 DLL。

本次适配仅增量修改 csproj、Obfuscar、ModInfo APIVersion、验证脚本和此说明；保留其他会话已有的 net10.0 与混淆流程，Plug/Func 业务源码保持原字节不变。

## 本轮交付包

已用最终混淆 DLL 生成 bin/Release/net10.0 下带 [SuAPI] 前缀、API0.2.0.0 后缀的 .scmod。根目录 ModInfo.xml、扁平 Lib/、Content/ 和正斜杠条目检查通过，逐条读取与 CRC 校验通过；包内仅含本 Mod DLL，不含核心库或其他 Mod。三份核心输出 DLL 与 CNB 缓存包的 SHA256 一致。本 Mod 程序集未引用其他业务 Mod。旁边 .verification.json 保存包与程序集哈希及依赖来源。尚未部署或进行实机验收。
