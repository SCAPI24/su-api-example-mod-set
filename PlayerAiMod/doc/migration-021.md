# 0.2.1.0 迁移

日期：2026-11-11。状态：Release 编译与 Obfuscar 已由主会话串行实际通过，4 条警告、0 错误；主会话报告为 WebRequest 过时、未使用局部变量和两个字段警告，未为消除警告扩大重构。真实游戏宿主/UI/AI 行为尚待验收，不能以编译成功替代。

三核心使用 `[0.2.1.0]` 正式包，删除引擎 ProjectReference。CmdBridgeMod 是真实业务门面依赖，不是宿主挂载冲突；保留 ModInfo/IMod 两处依赖拓扑，引用既有 Release API DLL，Private=false，不将 CmdBridgeMod.dll 放入 AI 包，不递归构建它。可用 CmdBridgeAssemblyPath 指定当前门面 DLL；缺失明确失败。

PlayerAi 原本新增自己的 PlayerAi ComponentTemplate，未替换任何原生 Component、Subsystem 或 Screen；继续独立组件附着真实 Player 实体，不为新接口改换业务入口。新增 OnEntityRemoved/Dispose 注销 actor，释放旧控制器关联，处理重生/离开世界。AI 支持菜单/加载界面工作，仍从 Frame.Update 使用 CmdBridge 的帧首队列；移除门面不可用时直接 Dispatcher.Dispatch 的帧末退化路径，明确报错，不冒充可用输入；卸载后的旧队列回调按 runtime identity 失效。

Release 仅混淆 PlayerAiMod；EntitySystem 不再是 Module，CmdBridge 只作解析依赖。KeepPublicApi 保留公开接口，明确保留由模板 Class 字符串查找的 PlayerAiMod.PlayerAiComponent。

先完成 CmdBridgeMod 的 Release 构建，再执行：

```powershell
dotnet restore Mod/PlayerAiMod/PlayerAiMod.csproj
dotnet build Mod/PlayerAiMod/PlayerAiMod.csproj -c Release --no-restore
```

验收：ai.* 命令/热键、菜单到世界的行为、帧首动作与录制回放、真实玩家重生、世界退出重进、卸载后无残留按键/actor/HUD。包内只放本 Mod 混淆 DLL 和自身 Content，不放 CmdBridge 或核心库。

构建前遵守根 AGENTS.md 的进程清理规则；没有构建核心框架的步骤。原文快照保存在本目录被忽略的 bin/Migration021Baseline/，用于与本轮增量核对，不打包。Release 路径由 MSBuild 配置变量注入 Obfuscar，只有当前 Mod 是混淆 Module；不使用 Debug 产物。

生命周期只读核对：Component.Release 在 Actor==null 时直接返回；OnEntityRemoved 首次注销 actor 后清空属性，随后 Dispose 不重复释放。Runtime.Unregister 仅在 actor 仍在列表时释放输入和解绑控制器；Clear 已清理列表时再次注销也返回 false，不重复接管或创建 actor。

## 本轮打包、部署与验证边界

已使用本Mod正式Release产物生成带API0.2.1.0后缀的scmod并放入指定游戏Mods目录。包根ModInfo.xml、平坦Lib目录、正斜杠条目、逐条解压/CRC、最终DLL与部署包SHA256一致性均通过；没有打入Engine/EntitySystem/Survivalcraft/SuAPICore等宿主核心DLL。迁移后的对应Mod只保留一份，其他已部署包哈希保持不变。

命令行桥、控制台、玩家AI使用各自最终混淆DLL；信道及地下世界沿用未设置混淆的原项目Release DLL。Channel另带同协议SuEventBus纯库；PlayerAi只声明CmdBridge功能依赖、不复制桥DLL。对外公开的类型/方法名经元数据检查，三个混淆包均未丢失原有公开名称。

五包已交付后保持游戏关闭。地下世界保留原版自动开井及切换真实地形管线的副作用；本轮没有在用户当前存档上自动触发该功能来冒充业务验收。编辑、电路、输入、AI动作及双世界跨层/保存等场景均待实际游戏宿主验收。新共享挂载不等于全部相同目标资源可以任意组合。

本轮4条编译警告涉及WebRequest.Create过时、lastResultKnown未使用，以及AssetRecoveryTask.AttemptInterval/LastFailureAt未赋值；已逐项核对这些源码行在迁移前HEAD存在且未改变。未为消警告重构Laya或资产恢复业务。
