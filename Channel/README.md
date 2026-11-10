# 信道（Channel）Mod

复用 button 的 index（**142**）做的一个"可寻址导线"元件：同通道号的导线在**全部已加载方块**范围内视为同一根。

## data 位域（18 位）

| bit | 宽 | 用途 |
|---|---|---|
| 0–2 | 3 | 安装面 face 0..5（与 button 完全一致，vanilla 也读，必须 0..5） |
| 3–10 | 8 | 通道号 0..255（低 4 位 3–6、高 4 位 7–10；默认 0） |
| 11–12 | 2 | 朝向 0..3 |
| 13 | 1 | 空，恒 0，**不参与判断** |
| 14–17 | 4 | 类型码：0=button、1=信道、2..15 预留 |

判定：`(data >> 14) & 0xF`。byte 13 不参与。

**不装 Mod 时**：face 有效 + 通道号默认 0 → vanilla 按 `15 - 低4位` 读到满档 → 就是一个 1.0V 的普通按钮，不崩。

## 端口

| 方向 | 类型 | 作用 |
|---|---|---|
| Left | Input | Y = 通道号**高** 4 位 |
| Right | Input | X = 通道号**低** 4 位 |
| Bottom | Input | 读写使能 |
| Top | `InputOutput` | 导线端 A（与 In 同一根线） |
| In（穿墙/背面） | `InputOutput` | 导线端 B |

量化取电压：`v = Round(邻格输出 × 15)`（与 memory bank 同款）。

### Bottom 四态

| Bottom | 连通道？ | 动作 |
|---|---|---|
| 未接线 | ✅ 默认激活 | 并入同通道网 |
| 高（v ≥ 8） | ✅ 激活 | 并入同通道网 |
| 低（1 ≤ v ≤ 7） | ❌ 当普通导线 | **写入** X/Y → data 3–10（边沿触发） |
| 0V（接了线无信号） | ❌ 当普通导线 | — |

## 电路语义

- **常驻**：Top/In 本地短接 —— 元件内部取两端邻线的**线或（取最大）**后同时输出（与引擎导线域一致）。
- **激活时**：再与同通道号的全部已加载元件并联成一张网（`SubsystemChannelBlockBehavior` 里按通道取最大）。
- 为与"导线"语义一致，本地电压只取**真实驱动源**（跳过信道元件自身与其他信道元件）：否则 Top/In 会把自己的输出回读，悬空网络会锁存住上次电平。
- 状态（输出电压）按 SR latch 方式用 `WritePersistentVoltage` 存；**点击旋转/编辑改 data 时走"状态交接"**，所以旋转只改朝向，电压档与状态不动。

## 玩家行为

| 动作 | 行为 |
|---|---|
| 点击（交互） | 旋转 90°（只改 bit 11–12） |
| 编辑键（手持物品或已放置方块） | 16×16 = 256 格单选对话框，选中格绿色，标题显示 `#N`，底部 OK/Cancel |
| 挖掘 | 掉**信道本体**，通道号归零 → 所有信道物品同一个值，**可堆叠** |
| 放置 | 保留手持物品上编辑过的通道号，只重写 face 与朝向 |
| 合成 | `bbb / bab / "  a"`（2 铜锭 + 5 锗块）→ 4 个信道（图案为 SR latch 上下翻转，已实测与全部原版配方无冲突） |
| 创造栏 | 按钮 + 信道（通道 0） |

## 与其它 Mod 的共存

- **vanilla**：不装本 Mod 时 index 142 仍是原版 ButtonBlock，世界照常打开。
- **ScMultiplayer**：index 142 与 `ButtonBlockBehavior` 子系统由本 Mod 持有；联机部分通过中间件 **SuEventBus** 的事件接管：
  - `CircuitElementRequest` → ScMP 提供网络化按钮元件；
  - `ButtonEditRequest` → ScMP 做按钮电压编辑的权限判定与权威提交；
  - `ChannelEditRequest` / `ChannelEditSubmitRequest` → ScMP 做信道通道号编辑的抑制与提交（服务端只应用 data bit 3–10）。
- 没装 ScMP 时这些事件没有订阅者，本 Mod 全部走原版/本地路径。

### 存档兼容（已核对真实存档）

`Project.xml` **不保存**子系统的 `Class` / `IsOptional`（实测真实存档里 0 个 `<Class>`，只有各子系统的数据节点），加载时 Class 取自**当前 Database**（`EntitySystem/GameEntitySystem/Project.cs` 的 40-47 行读取、267-283 行保存）。因此：

- 带本 Mod 存档 → **卸掉 Mod 读档**：Class 回到原版 `Game.SubsystemButtonBlockBehavior`，按钮完全正常，信道格就是满电压普通按钮 —— 不需要任何额外处理；
- 不带本 Mod 存档 → **装上 Mod 读档**：Class 变成本 Mod 子系统，信道逻辑与编辑对话框生效；
- 信道元件状态存在原版 `SubsystemElectricity` 的 `VoltagesByCell` 里（存档保留，有原版 500 条上限）。

## 依赖

| 包 | 版本 | 说明 |
|---|---|---|
| Engine / EntitySystem / Survivalcraft | `[0.2.1.0]` | cnb-nuget 制品库，net10.0 |
| SuAPICore | `[0.2.1.0]` | ChannelText 使用 SuAPIFonts 中文字体 |

`SuEventBus` 是共享类型协议与同步事件中间件，不是联机业务 Mod。`Middleware/SuEventBus.csproj` 直接链接 `../SuEventBus/Func` 下原有两份协议源码，保持程序集名和请求类型不变，并使用同版精确 NuGet 依赖；不构建或修改原中间件工程，不复制其他 Mod 的业务。它随包放进 `Lib/SuEventBus.dll`，清单和 IMod 保留该依赖。ModLoader 按清单先加载该程序集，无需中间件实现 IMod。不能把它并入 Channel.dll，否则现有订阅者的类型身份和静态事件总线会分裂。

## 构建与打包

```powershell
dotnet restore Mod/Channel/Channel.csproj
dotnet build Mod/Channel/Channel.csproj -c Release --no-restore
# 打包（ZIP，条目用正斜杠，ModInfo.xml 在根，Lib 扁平，只放自己的 DLL + 中间件）
# 产物：Mod/Channel/[SuAPI]Channel-1.0.0.scmod
```


### 0.2.1 挂载边界与本轮验证

- Source: ChannelMod.OnLoad、ButtonBlock.CreateElectricElement、SubsystemChannelBlockBehavior.OnEditBlock/OnEditInventoryItem。
- ButtonBlock 索引142与 ButtonBlockBehavior 数据库 Class 仍由本 Mod 扩展；这些编辑/电路工厂不是 IUpdateable，现有 UpdateExtension 不覆盖它们。Dialog 也不是 Screen，WidgetPresentation 不允许改树，不能用这些新入口冒充业务迁移。
- 四类 SuEventBus 请求的 Handled/Suppressed/Result 协议保留。无人订阅时按既有本地业务执行；有订阅者时遵守原接管标记。这是设计好的可选协作协议，不是异常后的自动兜底。单独改成新的 SuAPI Feature 会绕开现有联机订阅者，因此本轮不迁移或删协议。
- 同一方块索引/行为 Class 仍是独占替换点，不能宣称其他同目标 Mod 自动共存。共享挂载尚缺编辑行为与电路工厂的正式组合契约；若后续需要，由框架单独设计，不能只用帧更新替代这些入口。
- 保存并注销本 Mod 两个 EventSubscriptionToken；卸载不清空共享 SuEventHub，不注销其他订阅者。
- 用 .NET SDK 10 构建；源使用已配置的 cnb-nuget，不改全局 NuGet 配置、不构建 Engine/EntitySystem/Survivalcraft。
- 只取 `Channel/bin/Release/net10.0/Channel.dll` 和同目录 `SuEventBus.dll` 打包，根目录是 `ModInfo.xml`，程序集仅 `Lib/Channel.dll`、`Lib/SuEventBus.dll`。禁止打入核心 DLL、SuAPICore.dll、其他业务 Mod DLL、obj 产物或平台分目录；ZIP 条目必须用 `/`。
- Mod 自身版本保持 `1.0.0`，APIVersion 为 `0.2.1.0`，IsMergeLib=true。本轮正式包位于 `bin/Release/net10.0/[SuAPI]Channel-1.0.0-API0.2.1.0.scmod`，已替换指定游戏目录中的同名包。
- 本轮状态：主任务已实测 Channel 与纯中间件的 Release `-t:Rebuild` 通过，0 警告、0 错误；全部核心依赖来自 NuGet，不使用引擎 ProjectReference。沿用原项目未配置 Obfuscar 的原始 Release DLL，不额外引入混淆。包结构/CRC/哈希校验通过；修复同名服装初始化事件的参数判断后已重新打包部署，真实宿主功能待重启验收。未新增 Tests 或独立运行程序。下节旧离线自检覆盖属于历史记录，不代表已用 0.2.1 重跑。

## 历史离线自检覆盖（非本轮执行）

| 覆盖 | 说明 |
|---|---|
| data 位域 | 6 face × 4 朝向 × 256 通道 = 6144 组编解码往返；face 恒 0–5、bit13 恒 0、类型码恒 1；`SetChannelNumber` 只动 bit 3–10 |
| vanilla 降级 | `data=0` 与 96 种"改过电压的原版按钮"都不被误判为信道；vanilla 读法（`data&7`、`15-((data>>3)&0xF)`）全在合法域 |
| 状态机 | `ChannelLogic.Decide` 960 例真值表（未接线/高/低/0V × 边沿锁存 × X/Y 组合）+ 7/8/0 档边界 + 写入唯一性 |
| 通道总线 | `ChannelBus`：取最大、与上报顺序无关、撤源立即下降（不锁存）、通道互不污染、成员换通道、最后成员退出归零、值不变不发通知 |
| 配方 | 实际发射的配方格喂回引擎同款匹配器：与 255 条原版配方零冲突 |
| 翻译桥 | 加载真实（Obfuscar 后）`TranslationMod.dll`，`TranslationApi.Translate("Channel",…)` 返回「信道」「合成信道」 |
| 中英切换 | 未注册本地化服务 → `Channel`；注册 zh_CN 服务后 → `信道` |

## 实机验证清单

1. **原版回归**：无 Mod 建世界放按钮 → 存档 → 装本 Mod 读档 → 按钮手感/电压编辑不变。
2. **信道功能**：Bottom 未接线=并入通道；高电平=并入；低电平=写入 X/Y（边沿）；0V=只当普通导线。
3. **同号并联**：两个同通道号信道隔远放置，一根上的信号在另一根上出现（范围=已加载方块）；驱动撤掉后应回落到 0（不锁存）。
4. **编辑**：手持/放置两种情况下 16×16 对话框可选通道号，OK 生效、Cancel 不变。
5. **旋转**：点击旋转只改朝向，通道号与输出电压状态保持。
6. **掉落/堆叠**：挖掉得信道本体，多个信道在同一格堆叠；放置后通道号为 0。
7. **降级**：装 Mod 放信道 → 存档 → 卸 Mod 读档 → 显示为满电压普通按钮、不崩 → 装回 Mod 仍是信道。
8. **ScMP 共存**：两者都装，联机按钮按下仍同步；信道通道号编辑经主机权威应用；服务端只改 data bit 3–10。

## 本轮打包、部署与验证边界

已使用本Mod正式Release产物生成带API0.2.1.0后缀的scmod并放入指定游戏Mods目录。包根ModInfo.xml、平坦Lib目录、正斜杠条目、逐条解压/CRC、最终DLL与部署包SHA256一致性均通过；没有打入Engine/EntitySystem/Survivalcraft/SuAPICore等宿主核心DLL。迁移后的对应Mod只保留一份，其他已部署包哈希保持不变。

命令行桥、控制台、玩家AI使用各自最终混淆DLL；信道及地下世界沿用未设置混淆的原项目Release DLL。Channel另带同协议SuEventBus纯库；PlayerAi只声明CmdBridge功能依赖、不复制桥DLL。对外公开的类型/方法名经元数据检查，三个混淆包均未丢失原有公开名称。

五包已交付后保持游戏关闭。地下世界保留原版自动开井及切换真实地形管线的副作用；本轮没有在用户当前存档上自动触发该功能来冒充业务验收。编辑、电路、输入、AI动作及双世界跨层/保存等场景均待实际游戏宿主验收。新共享挂载不等于全部相同目标资源可以任意组合。
