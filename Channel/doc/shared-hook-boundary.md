# Channel 0.2.1.0 的非Update接入边界

日期：2026-11-11。Channel已升级正式0.2.1.0包，仍保留ButtonBlock(142)及ButtonBlockBehavior的专用替换；此节描述剩余组合限制，不声称已改成通用共享宿主。

真实业务入口是 OnEditBlock、OnEditInventoryItem 和 CreateElectricElement，它们不是IUpdateable.Update，也不是Screen生命周期。当前UpdateExtension/ScreenExtension/WidgetPresentation不能代替这些入口；用更新轮询或布局回调替代会改变编辑和电路创建时机。

SuEventBus的typed请求仍是与既有订阅方共享的协议，独立打成纯库Lib/SuEventBus.dll。该依赖是中间件，不是把其它业务Mod合并进包。Channel内部世界总线保持实例所有，卸载只注销自己的事件token，不清空共享Hub的其它订阅。

若要求多个完全独立Mod同时扩展同一142/同一编辑与电路创建入口，需框架另提供可组合的块行为分发：准确宿主/方块实例、每方法独立调用上下文、原方法许可/替换结果的明确归属、data/handler原拥有者、确定顺序、冲突与失败状态。任何“有人返回true就随便停”或复制另一Mod编辑回写都不能当组合协议。

本轮保留原业务完整流程，不擅自改变四类事件或订阅方；未改主框架。当前迁移可与不争夺该方块/行为入口的Mod共存，同目标替换仍需明确冲突协调和实机验收。

## 同名初始化事件的参数边界修复

Source: BlocksManager.cs:BlocksManager.Initialize、ClothingBlock.cs:ClothingBlock.Initialize、ModEventBus.cs:ModEventBus.TriggerEvent、ChannelMod.cs:ChannelMod.OnLoad。

原版方块初始化传入 `Block[]`，服装初始化传入 `ClothingData[]`，两者都触发 `BlocksManager.Initialize`。本轮迁移曾将合法的服装参数判成错误；事件总线把订阅异常包装成字符串结果，服装初始化读取结果的布尔标记时便出现 `System.String` 转 `System.Boolean` 的加载错误。这是 Channel 的参数判断回归，不是已证实的两个 Mod 抢占。

Channel 现在明确区分两种参数：仅对 `Block[]` 执行自己的方块注册，收到 `ClothingData[]` 时返回布尔值 `false` 表示本订阅不修改服装数据。未知参数仍明确报错，不把异常当作正常结果。修复只涉及本 Mod，未修改主框架或其他 Mod。

修复后 Release 重编通过，0 警告、0 错误；已重新生成并部署正式 scmod，ZIP 条目、逐条解压/CRC、包内 DLL 与 Release DLL、生成包与部署包的 SHA256 均一致，其他已部署文件哈希不变。未在修复后自动启动游戏，加载与业务验收仍需在真实游戏宿主中完成。
