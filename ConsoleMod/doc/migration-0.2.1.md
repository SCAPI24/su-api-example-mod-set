# ConsoleMod 0.2.1 增量迁移

以本轮开始时工作区为基准，原文保存在被忽略的 bin/Migration021Baseline/。既有 net10.0、独立 ConsoleInputSubsystem 与共享 RegisterUpdateExtension<SubsystemGameWidgets> 迁移保持，不恢复旧 Views Class 替换。

三核心包严格 [0.2.1.0]，ModInfo APIVersion=0.2.1.0，自身 Version=1.0.0、IsMergeLib=true。Release 使用中间副本动态调整混淆路径，Obfuscar 仅处理 ConsoleMod.dll，核心库只作搜索依赖。

输入保持原 Reset(-100) 早期任务，在原 Input(-10) 前捕获控制台并清除键鼠，Views(200) 扩展只更新显示，不重复执行原 Views。CmdBridge 帧首注入仍经过此输入门；控制台打开时屏蔽游戏输入为既有意图，不修改 PlayerAi 或 CmdBridge 的实现和优先级。不存在跨业务 Mod 类型依赖。

本轮生命周期增量：IsConsoleOpen 从单个全局布尔改为本 Mod 活动控制器集合聚合，释放一个世界不清除另一个实例状态；Android 键盘回调使用 opening session 门闩，拒绝关闭/重开前遗留回调；Windows modal 只走原 DialogsManager.HideDialog，避免退场动画尚持有时提前 Dispose。按钮、命令、Android 原键盘调用时序不变。

静态模块启用状态与 live 输入实例表仅用于加载/卸载桥接：Dispose 移除实例，Enable/Disable 管理每个世界任务，扩展 token 和数据库 token 仍在卸载清理。未新增配置或 Tests，也未使用独立 Mod 进程冒充游戏验收。

本轮已通过正式 Release 构建和 Obfuscar 混淆，0 警告、0 错误；宿主交互尚未验收。需由主任务串行验证 Windows/Android 控制台开关、键盘旧回调、退出世界及与其他输入控制的共存。现有任意替换 Views 的第三方 Mod 仍在原代码中明确报告不兼容；未扩展为可组合的旧式 Views 替换。无需新增主框架接口。

## 本轮打包、部署与验证边界

已使用本Mod正式Release产物生成带API0.2.1.0后缀的scmod并放入指定游戏Mods目录。包根ModInfo.xml、平坦Lib目录、正斜杠条目、逐条解压/CRC、最终DLL与部署包SHA256一致性均通过；没有打入Engine/EntitySystem/Survivalcraft/SuAPICore等宿主核心DLL。迁移后的对应Mod只保留一份，其他已部署包哈希保持不变。

命令行桥、控制台、玩家AI使用各自最终混淆DLL；信道及地下世界沿用未设置混淆的原项目Release DLL。Channel另带同协议SuEventBus纯库；PlayerAi只声明CmdBridge功能依赖、不复制桥DLL。对外公开的类型/方法名经元数据检查，三个混淆包均未丢失原有公开名称。

五包已交付后保持游戏关闭。地下世界保留原版自动开井及切换真实地形管线的副作用；本轮没有在用户当前存档上自动触发该功能来冒充业务验收。编辑、电路、输入、AI动作及双世界跨层/保存等场景均待实际游戏宿主验收。新共享挂载不等于全部相同目标资源可以任意组合。

输入异常路径也会立即Dispose当前控制器，清除活动集合/UI/键盘session；清理错误与原错误聚合报告，GetController仍保留原输入失败原因。此局部修正已重新Release编译/混淆通过。
