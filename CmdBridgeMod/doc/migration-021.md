# 0.2.1.0 迁移

日期：2026-11-11。状态：Release 编译与 Obfuscar 已由主会话串行实际通过，0 警告、0 错误。真实游戏宿主/UI/AI 行为尚待验收，不能以编译成功替代。

保持现有 Frame.Update 观察/帧末录制、FrameStartPump 下一帧输入和 CursorSoftGuard order -11，不改换 ScreenExtension 或共享 Views。它们有不同的实际阶段要求；独立更新器不替换宿主。

本轮仅增量更新四包 Engine/EntitySystem/Survivalcraft/SuAPICore 到 `[0.2.1.0]`、清单、Obfuscar 显式 KeepPublicApi 和本文；原工作区已有的生命周期迁移未回退。CmdBridgeMod/CmdBridgeInput 公共门面须在混淆后继续供 PlayerAiMod 使用。

```powershell
dotnet restore Mod/CmdBridgeMod/CmdBridgeMod.csproj
dotnet build Mod/CmdBridgeMod/CmdBridgeMod.csproj -c Release --no-restore
```

验收：TCP 开关两种状态的公开输入门面、按键脉冲、失焦鼠标、order -11 guard、UI 点击、关闭/重开世界、卸载后泵和 guard 清理，以及 PlayerAi 命令/热键。

构建前遵守根 AGENTS.md 的进程清理规则；没有构建核心框架的步骤。原文快照保存在本目录被忽略的 bin/Migration021Baseline/，用于与本轮增量核对，不打包。Release 路径由 MSBuild 配置变量注入 Obfuscar，只有当前 Mod 是混淆 Module；不使用 Debug 产物。

## 本轮打包、部署与验证边界

已使用本Mod正式Release产物生成带API0.2.1.0后缀的scmod并放入指定游戏Mods目录。包根ModInfo.xml、平坦Lib目录、正斜杠条目、逐条解压/CRC、最终DLL与部署包SHA256一致性均通过；没有打入Engine/EntitySystem/Survivalcraft/SuAPICore等宿主核心DLL。迁移后的对应Mod只保留一份，其他已部署包哈希保持不变。

命令行桥、控制台、玩家AI使用各自最终混淆DLL；信道及地下世界沿用未设置混淆的原项目Release DLL。Channel另带同协议SuEventBus纯库；PlayerAi只声明CmdBridge功能依赖、不复制桥DLL。对外公开的类型/方法名经元数据检查，三个混淆包均未丢失原有公开名称。

五包已交付后保持游戏关闭。地下世界保留原版自动开井及切换真实地形管线的副作用；本轮没有在用户当前存档上自动触发该功能来冒充业务验收。编辑、电路、输入、AI动作及双世界跨层/保存等场景均待实际游戏宿主验收。新共享挂载不等于全部相同目标资源可以任意组合。
