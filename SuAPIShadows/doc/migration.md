# SuAPIShadows 新版本移植

日期：2026-11-11。

- 保留独立子系统与阴影算法；已在数据库注册中补充 Terrain 加载依赖，确保地形渲染器先完成初始化。
- 目标框架为 `net10.0`，ModInfo 的 `APIVersion` 对齐 `0.2.0.0`；Mod 版本保持 `1.0.1`，`IsMergeLib=true`。
- Engine、EntitySystem、Survivalcraft 三个依赖统一使用 NuGet `PackageReference`，版本严格锁定 `[0.2.0.0]`，从已有 `cnb-nuget` 源 `https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json` 还原。不使用本地 DLL、HintPath 或 ProjectReference，不构建引擎，不修改全局 NuGet 配置。
- 启用 `CopyLocalLockFileAssemblies`，将 NuGet 运行时依赖复制到输出目录供 Obfuscar 解析。Release 构建执行 Obfuscar，输入 `bin/Release/net10.0`，输出其 `Obfuscated/` 子目录。只有 SuAPIShadows 为混淆 Module；依赖 DLL 仅用于解析。保留 Mod 与子系统反射入口名。
- 构建流程：先结束所有 `Survivalcraft.exe` 并确认已退出，更新本 Mod 源文件时间戳，再依次执行 `dotnet restore Mod/SuAPIShadows/SuAPIShadows.csproj` 与 `dotnet build Mod/SuAPIShadows/SuAPIShadows.csproj -c Release --no-restore`。桌面与 Android 构建必须串行。
- 打包仅使用本 Mod 的最终混淆 DLL、扁平 `Lib/` 与根目录 ModInfo.xml；不得打包复制到输出目录的引擎或其他依赖 DLL，ZIP 条目用 `/`，文件名前缀 `[SuAPI]`。
- 验证状态：本轮已完成 NuGet `0.2.0.0` 还原，Release `-t:Rebuild` 与 Obfuscar 均通过，0 警告、0 错误。三个核心包均来自 CNB，包源码提交为 `771afab7707b42156cda08593bba29cee6e84c69`。已补充数据库加载依赖并部署修复包；未启动游戏，实际阴影绘制待重新进入世界验收。

## 本轮交付包

已用最终混淆 DLL 生成 bin/Release/net10.0 下带 [SuAPI] 前缀、API0.2.0.0 后缀的 .scmod。根目录 ModInfo.xml、扁平 Lib/、Content/ 和正斜杠条目检查通过，逐条读取与 CRC 校验通过；包内仅含本 Mod DLL，不含核心库或其他 Mod。三份核心输出 DLL 与 CNB 缓存包的 SHA256 一致。本 Mod 程序集未引用其他业务 Mod。旁边 .verification.json 保存包与程序集哈希及依赖来源。修复包已复制到指定游戏 Mods 目录并比对哈希，尚待游戏内视觉验收。

## 2026-11-11 地形阴影加载顺序修复

游戏日志在加载世界时记录 `Terrain shadows disabled: Value cannot be null. (Parameter 'target')`。DynamicShadows.Load 立即读取 TerrainRenderer，而原注册没有加载依赖；FindSubsystem 只保证实例存在，TerrainRenderer 要到 Terrain.Load 中才创建。初始化异常随后关闭地形路径，生物投影仍走独立绘制，因此出现生物有影而地形无影。

修复仅在 Plug/ShadowsMod.cs 的 DynamicShadows 模板上添加字符串参数 `Dependencies="Terrain"`。Member 继承此参数，原生 Project.LoadSubsystem 按 DynamicShadows → Terrain → GameInfo 顺序完成加载，无循环。它是引擎子系统的加载依赖，不是其他 Mod 的依赖；不改变阴影算法，不增加逐帧重试。

重新使用 NuGet 0.2.0.0 执行 Release Rebuild 与 Obfuscar，通过（0 警告、0 错误）。重新打包并替换 `publish/[SuAPI]Survivalcraft/Mods/[SuAPI]SuAPIShadows-1.0.1-API0.2.0.0.scmod`，ZIP 逐条读取、CRC、最终 DLL 与部署包哈希核对通过。旧包保存在本 Mod 被忽略的 bin/Release/net10.0/PreviousDeployment/ 下，其他 Mod 包未变化。

游戏按编译规则已退出；由用户重新启动并进入世界验收地形太阳阴影。此次未声明游戏内视觉验证通过。

Source: EntitySystem/GameEntitySystem/Project.cs:Project.LoadSubsystem；Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.Load；Subsystem/SubsystemDynamicShadows.cs:Load/InitializeTerrainShadows。

## 2026-11-11 角色阴影、缓存及发光面修正

已使用纯 Mod 方案完成角色 GPU 深度投影、512静态/256动态太阳层、逐灯静态/动态 atlas 缓存及原生发光三角形补绘。具体实现和边界见 shadow-performance-design.md。主框架没有修改，也没有替换原版方块。

Release Rebuild 与 Obfuscar通过（0警告、0错误）；最终混淆 DLL 已重新打入同名 scmod 并替换指定游戏 Mods 包，其他 Mod 包哈希保持不变。之前正常包备份在本 Mod bin/Release/net10.0/PreviousDeployment/。本次游戏运行验证确认仅一进程、进入真实世界，未出现 SuAPIShadows 初始化/着色器/绘制错误；尚未完成视觉场景验收及帧时间对照。

## 2026-11-11 角色轮廓、矮动物及太阳草影修正

用户实机发现人物/动物影子只剩少量方格，野猪无影，太阳草影缺失。源码确认宽范围角色层原256/80约31.25厘米每像素；与地形共用0.0018深度偏移又相当于599格深度跨度中的约1.078格，足以吞掉0.85格高野猪及低草。野猪原模板允许CastsShadow，草的AlphaTest subset5也已在mask63中，不以新增动物白名单或切换双面剔除处理。

- 保留远处角色256宽范围图，新增24格覆盖的512近处图，约4.69厘米每像素，边缘渐变到远图以保留远处覆盖。
- 角色使用约3厘米的独立太阳深度偏移；近、远角色图均采用比较深度后插值的四tap PCF，避免硬比较和对打包深度直接插值造成的块状轮廓。
- 地形太阳图保持512和原覆盖，只将接收偏移降到约18厘米；强度、地形采样数量和草subset不变。需在实机复核大平面/斜面的自阴影及草影。
- 火把基础亮度、GetEmittedLightAmount、amount/15强度、光源位置及范围均未改变；不额外提升亮度。此前去除近光源半径豁免后，非发光杆和墙面正常受阴影的视觉变化不等于降低基础发光量。
- 人物姿态准备仍限制12Hz，静态地形缓存不因近图重画而增加刷新；近图增加一份512深度目标和近范围模型绘制。未测量本轮帧时间收益或代价，不据此承诺FPS数值。

Release Rebuild与Obfuscar通过（0警告、0错误），同名scmod已替换指定Mods目录；ZIP/CRC、DLL与部署包哈希一致，其他Mod包未变化。实际游戏单实例已进入原世界，无本轮SuAPIShadows错误；角色轮廓、野猪和太阳草影最终观感仍由实机场景验收。

## 2026-11-11 手持物投影

角色模型之外的手持物由原生 ComponentHumanModel.DrawExtras 单独调用 Block.DrawBlock，原角色深度流程未收集这部分。现新增 HeldItemShadowRenderer 与 HeldItemDepthShader，按原版手骨、物品完整 value、InHandScale/Rotation/Offset、真实环境与第三人称绘制模式采集物品的真实批次几何。第一人称世界阴影使用身体手骨，不采用HUD持物变形。

- 人物准备批次内最多12Hz采集，按texture、UV、alpha语义、顶点/索引变化决定GPU上传；各太阳层和点光六面复用缓存。物品矩阵/完整value/几何revision纳入已有模型revision，物品世界包围盒并入剔除范围。
- 支持Textured、Flat三角形/线、Font批次，保留原texture/sampler/透明轮廓；空DrawBlock保持无几何，不以盒子替代。半透明仍属于当前二值深度阴影系统，不新增透光积分。
- 空手、死亡、角色移除及换物使缓存更新或释放；设备恢复后从保留的顶点/索引重新上传。没有新增Entity、Block替换、阴影贴图或跨Mod依赖。
- Release Rebuild与Obfuscar通过（0警告、0错误），包结构/CRC/最终DLL与部署哈希通过，同名scmod已替换指定目录，其他Mod包未变化。
- 实际游戏单实例进入原世界，角色持有物品且处于白天，运行日志无本轮SuAPIShadows错误；地面轮廓、各类物品及切换观感仍需场景验收。

Source: ComponentHumanModel.cs:DrawExtras；BlocksManager.cs:DrawMeshBlock/DrawCubeBlock/DrawFlatBlock；BasePrimitivesRenderer.cs:FlatBatches/TexturedBatches/FontBatches；VertexBuffer.cs:HandleDeviceReset。

## 2026-11-11 植被风动与0.2.1适配

最新CNB三核心包精确升级为0.2.1.0；主框架未修改。新增18类草/叶/花/作物/灌木/水草包装、共享GPU位移、稳定季节风、有限生物移动扰动、水下专用波动及公开内存开关。具体参数、边界和实测记录见 foliage-wind-implementation-plan.md 文末。

纯Mod接线识别原生BlocksManager.Initialize事件的两种负载：Block[]执行植被注册，ClothingData[]明确不参与。共享injector注册在取得原生Block数组后进行，避免其他Mod较早ApplyBlocks提前消费本方记录。冲突或注册错误明确保存并报告，不猜类型或静默覆盖。

Release编译与Obfuscar通过（0警告、0错误）；最终包已替换指定Mods目录，真实原世界加载及开关关闭/恢复检查通过；诊断代码已移除再重编部署。最新文件为[SuAPI]SuAPIShadows-1.0.1-API0.2.1.0.scmod。当前部署目录仅本包，完整多Mod组合、水下与季节场景、帧时间对照未作通过声明。
