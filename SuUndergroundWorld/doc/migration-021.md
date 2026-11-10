# SuUndergroundWorld 0.2.1.0 迁移与协作边界

日期：2026-11-11。状态：正式 NuGet Release 编译通过，0 错误，5 条旧代码告警；未在现有世界触发双世界业务验收。

## 迁移结果

- net10.0，Engine/EntitySystem/Survivalcraft 均严格 `[0.2.1.0]` PackageReference；无引擎 ProjectReference，不递归构建主程序，Mod 版本保持0.1.0，IsMergeLib=true。
- 入口改为 `RegisterUpdateExtension<SubsystemTerrain>`，每个真实 Terrain Host/Project 创建独立 `SuDeepWorldHook` 与 `SuWorldManager`。不覆盖原生 Terrain、Update、Sky 的 Class，不人工添加 scheduler 任务，不通过 Frame.Update 轮询挂接。
- Attached 在 Project 完整加载后的第一次真实 Terrain 更新轮绑定。Update 先运行管理器，再请求 base.Update(dt) 许可；原生 Terrain.Update 由共享执行器统一决定执行次数，不直接调用 Host.Update。
- 旧钩子 order99 的目标是 Sky0/Body2 后、Terrain100 前；现在在真实 Terrain100 的扩展回调阶段满足这一关系。其它扩展可否决原生宿主，本 Mod 不补调绕过否决，不保证同优先级业务间的任意顺序。
- 注册token在卸载Dispose。GameManager.ProjectDisposed提前停止并恢复资源，Detached幂等收尾，避免原Terrain释放前仍指向本Mod地下资源。管理器按Host隔离，不跨世界复用。
- 地形私有字段只绑定已核实的精确backing field和类型；删除按其它同类型字段猜测的路径。首次正常更新时必需子系统与方块表已加载，缺契约明确失败；地下生成器必须使用原设计的另一种子，不再缺字段时静默沿用地表种子。
- 最后玩家位置保存移到恢复地表管线及清理引用之前，避免收尾阶段已失去当前世界身份/玩家引用。

## 原业务与验证范围

双世界仍是旧实现：地表Regions、地下Regions1，按原接缝换算、流体和实体列表交接保存；原版已有的自动开井/穿越和回程规则保持。它会修改世界地形，不能把一次普通启动当成没有世界副作用的测试。

因此包交付后应在确认用于该功能的存档或完整备份上验收：开井、上下穿越、流体撤回、两侧实体/物品、退出重进、死亡复活、帧首/地形更新关系和资源释放。没有为了让流程走通而换生成参数或跳过保存环节，也没有在当前用户世界中自动触发开井来冒充验收通过。

本项目原本没有混淆步骤，本轮保留其正式Release DLL直接打包，不引入新的保护工具。包仅根ModInfo.xml与扁平Lib/SuUndergroundWorld.dll及资源目录，不能打入核心DLL。当前5条告警为原有演示常量形成的不可达分支和未使用字段，不为消警告改写原双世界算法。

## 仍需主框架会话配合的通用能力

新的共享Update入口解决更新接线和宿主调用归属，**不自动仲裁世界管线切换**。原双世界功能仍会更换真实 SubsystemTerrain 的 Terrain/Updater/Renderer/Serializer/Generator/GeoGen 六个成员，暂停对应后台地形线程，并切换实例内的实体、流体及掉落物集合。

这与其它直接持有地形/渲染器引用的Mod有实质边界，例如光影Mod可能给旧Renderer安装了shader，切到新的Renderer后不会自动重新挂载；联机Mod也可能持有旧地形或世界映射。不能把“没有Class替换”描述成任意地形/渲染/联机组合已经兼容。

如需这些组合，应由框架提供统一的世界管线切换协议：明确Project/世界身份、线程安全移交点、资源所有权、切换前后通知及缓存失效；消费者通过令牌注册，并且不自行调用其它Mod的类。没有该契约时，本Mod内不得按其它Mod名字反射调用其私有重挂逻辑，也不能静默丢掉光影/网络功能。

本次未修改主仓库、其它Mod或引入框架私有补丁。此节供用户转交框架会话，不把尚未解决的组合写成通过。

Source: Survivalcraft/SuAPI/UpdateExtension.cs/UpdateExtensionProjectBinding.cs；Game/SubsystemTerrain.cs；Game/GameManager.cs:DisposeProject；原SuWorldManager.Tick/SwitchTo/Shutdown。

## 本轮打包、部署与验证边界

已使用本Mod正式Release产物生成带API0.2.1.0后缀的scmod并放入指定游戏Mods目录。包根ModInfo.xml、平坦Lib目录、正斜杠条目、逐条解压/CRC、最终DLL与部署包SHA256一致性均通过；没有打入Engine/EntitySystem/Survivalcraft/SuAPICore等宿主核心DLL。迁移后的对应Mod只保留一份，其他已部署包哈希保持不变。

命令行桥、控制台、玩家AI使用各自最终混淆DLL；信道及地下世界沿用未设置混淆的原项目Release DLL。Channel另带同协议SuEventBus纯库；PlayerAi只声明CmdBridge功能依赖、不复制桥DLL。对外公开的类型/方法名经元数据检查，三个混淆包均未丢失原有公开名称。

五包已交付后保持游戏关闭。地下世界保留原版自动开井及切换真实地形管线的副作用；本轮没有在用户当前存档上自动触发该功能来冒充业务验收。编辑、电路、输入、AI动作及双世界跨层/保存等场景均待实际游戏宿主验收。新共享挂载不等于全部相同目标资源可以任意组合。
