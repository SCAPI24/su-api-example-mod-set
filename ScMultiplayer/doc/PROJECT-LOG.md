# ScMultiplayer 项目日志

> 记录所有用户需求、决策和变更
> 项目: ScMultiplayer 联机 Mod
> 最后更新: 2026-05-19

## 2026-05-21 01:56 MemoryBankDrawMod 开发完成

### 需求
为 Memory Bank 方块编辑器添加 Draw 绘图模式：
- Linear/Grid 按钮改为三态循环切换：Linear → Grid → Draw
- Draw 界面左侧 9 个颜色按钮（0=橡皮擦 + 8~F=彩色），点击 toggle 选中/取消
- 右侧 16×16 格子，点击/拖拽填入选中颜色
- 值 0 和 8-F 显示纯色，值 1-7 显示灰色底+白色 SignFont 数字

### 架构
- **EventBus 替换** `SubsystemMemoryBankBlockBehavior`（GUID: `32a2d9ef-b01a-4f80-a6f8-5d2d5e9e9275`）
- **SuEditMemoryBankDialog** 继承 `Dialog`（非 EditMemoryBankDialog），从 `Dialogs/EditMemoryBankDialog` XML 加载布局
- 完全自建 Draw 模式 UI，不依赖原始对话框逻辑

### 踩坑记录

#### 1. TextBoxWidget 是 internal 类
跨程序集无法直接引用，必须用反射 `PropertyInfo.GetValue/SetValue` 访问 Text 属性。

#### 2. ClickableWidget 继承 Widget（非 ContainerWidget）
不能添加 Children。交互元素需 CanvasWidget 容器 + ClickableWidget 叠加层模式：视觉元素设 `IsHitTestVisible=false`，ClickableWidget 放最上层。

#### 3. 构造函数未初始化 TextBox → 数据“丢失”（根因）
XML 加载的 TextBox 内容为空。第一帧 Update 走 SyncTextBoxesToData 从空 TextBox 读取 → m_tmpMemoryBankData 被空数据覆盖 → 原始数据丢失。
修复：构造函数中用 `m_tmpMemoryBankData.SaveString()` 初始化所有 TextBox。

#### 4. 反射设置其他类 private 字段抛 ArgumentException
SuEditMemoryBankDialog 继承 Dialog，`typeof(EditMemoryBankDialog).GetField("m_ignoreTextChanges").SetValue(this, true)` 抛异常——字段不在当前类型上。EventBus 吞异常后对话框不显示。修复：删除 IgnoreTextChanges 逻辑，使用自己的 m_ignoreTextChanges 字段。

#### 5. Widget API 误用
- StackPanelWidget.Direction → `LayoutDirection` 枚举（非 WidgetDirection）
- CanvasWidget.SetWidgetPosition → 实例方法（非静态）
- Widget.Size → 仅 CanvasWidget 有，ClickableWidget 无

#### 6. SignFont FontScale
- 1.0 太小看不清
- 1.5 合适（CELL_SIZE=15px 的格子内）
- 值 1-7 灰色底+白字清晰可辨，保留灰色不改黑

### 文件结构
```
Mod/MemoryBankDrawMod/
├── MemoryBankDrawMod.csproj
├── ModInfo.xml
├── Obfuscar.xml
├── Plug/MemoryBankDrawMod.cs          (IMod 入口，EventBus 替换)
└── Func/
    ├── SuSubsystemMemoryBankBlockBehavior.cs  (重写 OnEditInventoryItem/OnEditBlock)
    └── SuEditMemoryBankDialog.cs             (三态对话框，~530行)
```

### 部署
- .scmod: `[SuAPI]DrawMemoryBank.scmod` (~10KB)
- 位置: `Survivalcraft\bin\Debug\net48\Mods\`

---

### 字体来源
`SurvivalcraftApi-SCAPI1.9_MP\Survivalcraft\Content\Assets\Fonts\`
- `Pericles.webp` → `ChinesePericles.png` (4096×4096 RGBA, 15.7MB)
- `Pericles.lst` → `ChinesePericlesData.txt` (826KB, 10817 glyphs)

### .lst 格式
```
10817                  ← glyph 总数
<char> <texL> <texT> <texR> <texB> <offX> <offY> <advance>  ×10817
51                     ← glyphHeight
2 1                    ← spacing
0.5                    ← scale
?                      ← fallbackCode
7475                   ← kerning 对数
<char> <char> <amount>  ×7475
```
Tex 坐标为归一化 0-1，offset/advance 为像素值。

### 关键发现
- `BitmapFont.Initialize(Texture2D, Stream)` 是 **internal**，Mod 不可调用
- 替代：public constructor `BitmapFont(texture, glyphs, fallbackCode, glyphHeight, spacing, scale)`
- `SetKerning()` 是 public，`m_glyphsByCode` 是 internal
- ModResource 不加载 .lst，需改 .txt 后缀
- ContentCache key: `Content/Fonts/FooData.txt` → `Mod/Fonts/FooData`

---

## 2026-05-20 20:27 StringInterceptor 多尺寸中文字体 v1.5.0

### 需求
原方案从 1 套 ChinesePericles 字体 Clone 出 4 个尺寸，但 Clone 只改 Scale，所有尺寸共享同一纹理和 glyph 坐标——字体在不同尺寸下字形比例不一致。改为分别加载 4 套独立尺寸中文字体。

### 字体文件
每套字体含 .png 纹理 + glyph 数据（原 .lst 重命名为 *data.txt）：

| 字体 | 纹理 | glyph 数据 | GlyphHeight | Scale | 渲染高度 |
|------|------|-----------|-------------|-------|----------|
| chinese12 | chinese12.png | chinese12data.txt | 16 | 0.5 | 8.0 |
| chinese18 | chinese18.png | chinese18data.txt | 24 | 0.5 | 12.0 |
| chinese24 | chinese24.png | chinese24data.txt | 32 | 0.5 | 16.0 |
| chinese32 | chinese32.png | chinese32data.txt | 43 | 0.5 | 21.5 |

underline 变体（chinese12u/18u + *udata.txt）已重命名但未加载（当前 Mod 不涉及 underline 字体）。

### 关键发现: Scale 校准

**问题**: 中文字体 Scale=0.5，Pericles Scale=0.632，渲染高度 = GlyphHeight × Scale。中文字体渲染高度只有 Pericles 的 ~55%，中英文混排时中文明显偏小。

**校准公式**: `校准Scale = PericlesScale × PericlesGH / ChineseGH`

| 字体 | GH | 校准Scale | 渲染高度 ≈ Pericles渲染高度 |
|------|----|-----------|-----------------------------|
| chinese12 | 16 | 0.948 | 15.17 ≈ Pericles12(24×0.632) |
| chinese18 | 24 | 0.895 | 21.49 ≈ Pericles18(34×0.632) |
| chinese24 | 32 | 0.889 | 28.44 ≈ Pericles24(45×0.632) |
| chinese32 | 43 | 0.867 | 37.29 ≈ Pericles32(59×0.632) |

**实现**: `rawFont?.Clone(校准Scale, Vector2.Zero)` — Clone 共享纹理和 glyph 数组，只改 Scale，零额外内存。

### 文件重命名规则
- `.lst` → `*data.txt`: ModResource.LoadResourceFromStream 仅支持 .png/.jpg/.txt/.xml/.dae
- 命名避免与 .png 同名: `chinese12.png` + `chinese12data.txt`（同 key `Mod/Fonts/chinese12` 但类型不同 Texture2D vs string，ContentCache.Set 会覆盖）
- 实际无冲突: `chinese12` → Texture2D, `chinese12data` → string，key 不同

### 映射逻辑
```csharp
// 按 Pericles GlyphHeight 区间选择中文字体
if (glyphHeight <= 24f) return ChineseFont12;  // Pericles12 GH=24
if (glyphHeight <= 34f) return ChineseFont18;  // Pericles18 GH=34
if (glyphHeight <= 45f) return ChineseFont24;  // Pericles24 GH=45
return ChineseFont32;                          // Pericles32 GH=59
```

### DLL 字符串验证技巧
.NET DLL 中字符串为 UTF-16LE 编码，`[Encoding]::Unicode.GetString($bytes).IndexOf()` 搜索。UTF-8 搜索搜不到标识符字符串。const float 被 JIT 内联为浮点指令，字符串搜索搜不到常量名，但日志字符串可搜。

---

## 2026-05-19 03:23 StringInterceptor TranslationProcessor

### 需求
主页 "Play" 按钮文字改为 "开始游戏"

### 实现
`TranslationProcessor : IStringProcessor`，Dictionary 映射 `{"Play", "开始游戏"}`。
注册顺序：TranslationProcessor → DefaultStringProcessor（翻译先于编号）。

---

## 2026-05-17 21:37 (Session Start)

### 用户指令摘要

#### 核心需求
1. 修改 ScMultiplayer 代码前**必须先阅读 Comms 全部代码**, 建立使用规则
2. 在 ScMultiplayer 目录下**创建 doc/ 目录**, 存放:
   - Comms 使用方法、规则、限制
   - ScMultiplayer 使用方法、规则、限制
3. **大项目管理**: 每个功能分部分完成, 严格按规则进行
4. **记录所有用户信息**: 方便后续维护 (本文件)
5. **分析联机同步需求**: 单设备多人 -> 多设备局域网联机
6. **局域网联机**: 确保两台设备效果同步
7. **接口文档**: 记录 ScMultiplayer 接口和未使用但可用的接口
8. **参考项目**: SurvivalcraftNet (https://gitee.com/SC-SPM/SurvivalcraftNet)

#### 最终目标
最简化联机 Mod, 包含:
- 房间创建
- 自动扫描
- 加入房间
- 踢出人员
- 首次连接地图下载
- 延迟补偿功能

"最简化": 不需要多余官网、广告等非游戏内容

#### 技术约束
- 使用 Comms 网络库做网络中间件
- 不修改 Survivalcraft 原始代码 (Mod 开发者模式)
- 以 .scmod 方式实现
- 与 SurvivalcraftNet 技术路径不同 (SuAPI Mod vs 直接改源码)

### 当前代码分析

#### 已读取文件 (全部完成)
- **Comms 核心 (21个文件)**: Comm.cs, Peer.cs, UdpTransmitter.cs, Reader.cs, Writer.cs, Hash.cs, Alarm.cs, Packet.cs, PeerPacket.cs, PeerData.cs, CommSettings.cs, PeerSettings.cs, DeliveryMode.cs, FourCC.cs, KeepAliveTimeoutException.cs, ITransmitter.cs, IWrapperTransmitter.cs, InProcessTransmitter.cs, LimiterTransmitter.cs, NetworkSimulatorTransmitter.cs, NetworkSimulatorStats.cs, TransmitterExtensions.cs, DiagnosticTransmitter.cs, DiagnosticStats.cs
- **Comms.Drt (23个文件)**: Server.cs, ServerGame.cs, Client.cs, Explorer.cs, ServerClient.cs, ServerSettings.cs, ClientSettings.cs, ExplorerSettings.cs, GameDescription.cs, GameStepData.cs, ServerDescription.cs, Message.cs (Drt), MessageSerializer.cs, DesyncDetector.cs, DesyncDetectionMode.cs, MalformedMessageException.cs, 所有 Data/*.cs (11个), 所有 Message/*.cs (15个)
- **ScMultiplayer (10个文件)**: ScMultiplayer.cs, SuComponentInput.cs, SuPlayScreen.cs, SuSubsystemTerrain.cs, Message.cs, SuReader.cs, SuWriter.cs, ChatMessage.cs, GamePlayerPositionMessage.cs, GamePlayerInputMessage.cs, GameModifiedCellsMessage.cs, GameWorldInfoMessage.cs, GameWorldInfoMessage1.cs, GamePakWorldMessage.cs, String2int.cs

#### 现状
- ScMultiplayer 是可运行的, 有基础网络功能
- 中间同步过程需要大量优化
- 基于 Comms (Comms.Drt) Tick 驱动模型
- Server/Client/Explorer 三层完整搭建
- 消息系统完善 (7 种消息类型)
- PlayScreen UI 增强已有基础

### 本次产出

#### 文档文件 (doc/ 目录)
1. `COMMS-ARCHITECTURE.md` -- Comms 网络库完整架构文档 (层次结构/传输模式/Drt框架/接口/规则/数据流)
2. `SCMULTIPLAYER-ARCHITECTURE.md` -- ScMultiplayer Mod 架构文档 (项目结构/核心架构/消息体系/同步状态/连接流程)
3. `SYNC-REQUIREMENTS.md` -- 联机同步需求分析 (同步模型/数据分类/延迟补偿方案/开发路线图)
4. `INTERFACES.md` -- 接口清单 (已用+未用可用, SuAPI接口/Comms接口/游戏Subsystem)
5. `PROJECT-LOG.md` -- 本文件 (项目日志)

#### 分析结论
- **同步模型**: 当前状态同步, 建议过渡到混合模式 (状态+输入)
- **开发优先级**: 生命值 > 物品栏 > 踢人 > UI > 延迟补偿 > 实体
- **延迟补偿**: 方案 B (服务端权威 + 客户端插值) 适合当前阶段
- **关键 Comms 接口**: Peer.DisconnectPeer (踢人), DeliveryMode.Unreliable (优化), client.SendDesyncState (校验)
- **可用 SuAPI 接口**: ModResource KV Store (配置), ModInjector (Block替换)

---

## 2026-05-17 22:23 参考代码分析

### 参考项目: SurvivalcraftApi-SCAPI1.9_MP (本地)
- 路径: D:\Users\Suceru\Desktop\生存战争三件套\Survivalcraft24102mono\联机参考
- 架构: .NET 10 + LiteNetLib + 直接改源码 + HarmonyX注入
- **与 ScMultiplayer 技术路径完全不同**

### 核心发现
- 参考项目使用 **Event-Driven** 模型: SubsystemNetwork 注入 IsNetworkMode 静态字段, 绑定游戏类事件到 Network* 处理函数
- 同步范围比 ScMultiplayer 大得多: 24 个 Network* 处理类, 涵盖 生命/物品栏/交互/电路/实体AI/状态效果
- ScMultiplayer 独有的功能: 聊天/房间列表/局域网发现/世界存档下载

### 产出
- `doc/REFERENCE-COMPARISON.md` - 完整的对比分析 (架构/同步数据/技术路径/实现对应表)

---

## 2026-05-17 22:55 v1.0.2 代码实现

### 变更概要
基于分析结果实施了 ScMultiplayer 联机 Mod 的实质性改进。

### 新增文件
- `Networking/NetworkStateMachine.cs` — 连接状态机 + 世界下载状态机 (基于 Game.StateMachine)
- `Message/GamePlayerHealthMessage.cs` — 生命值同步消息 (Health/MaxHealth/HealthChange/IsDead)
- `Message/GameKickPlayerMessage.cs` — 踢出玩家消息 (TargetClientID/Reason)

### 修改文件
- `Plug/ScMultiplayer.cs` — 重写主类:
  - 集成 NetworkConnectionStateMachine 管理连接生命周期
  - 集成 WorldDownloadStateMachine 管理世界下载流程
  - 新增 30fps 生命值同步 (ComponentHealth.Heal/Injure)
  - 新增踢人功能 (U键, Host Only)
  - 新增 GameKickPlayerMessage / GamePlayerHealthMessage 路由
  - 修复 Server.Dispose() 替代不存在的 Server.Stop()
  - 修复 ReadOnlyList struct 不能使用 ?.FirstOrDefault 问题
  - 统一使用 Message.WriteWithSender 替代 Message.Write
- `Func/Screen/SuPlayScreen.cs` — 修复:
  - 所有 Explorer.DiscoveredServers 访问前加 null 检查
  - 世界数据导出移到 CreateGame 之前 (修复时序)
  - 双击世界列表智能判断创建/加入 (不再依赖 null 判断)
  - 冗余 Task.Run 嵌套简化
- `Func/Subsystem/SuSubsystemTerrain.cs` — 修复:
  - 延迟初始化增加 null 检查, 失败时等待重试
  - ReModifiedCells 操作加 lock 防竞态
  - 整合 base.Update() 调用, 不再手动 ProcessModifiedCells

### 编译 & 打包
- MSBuild Debug 编译通过 (v4.8, net48)
- .scmod 包: SCMultiplayer.scmod (70096 bytes)
  - Lib/X64/ScMultiplayer.dll (54272 bytes)
  - Lib/X64/Comms.dll (112640 bytes)
  - ModInfo.xml (303 bytes)
- 部署到: P:\UGIT\SurvivalcraftMonoWin\Survivalcraft\bin\Debug\net48\Mods\

### 状态机设计
```
NetworkConnection:
  Disconnected -> Discovering -> WaitingForWorld -> WorldDownloading -> Playing

WorldDownload:
  Idle -> Requesting -> Receiving -> Importing -> Complete/Failed
```

### 关键结论修补
- Message pack/unpack: 统一使用 Message.WriteWithSender 确保 IPEndPoint 正确序列化
- 装箱/拆箱: SuReader/SuWriter 已完整支持 Vector2/3/Quaternion/Ray3/Point3 等游戏类型
- StateMachine 来自 Game 命名空间 (Survivalcraft/Game/StateMachine.cs)
- Comms message 注册基于反射按字母序分配 ID, 新增 Message 子类自动注册

---

## 2026-05-17 23:46 v1.0.3 同步修复

### 根因
PlayerIndex 映射冲突。原代码假设本地多人模式 (单设备多 PlayerIndex)，但联机场景下每个设备只有 PlayerIndex 0 一个实体。
- Host 发送: ClientID 0 → PlayerIndex 0 → 发网络 PlayerIndex 0
- Client 接收: ConvertPlayerIndexForClient(0, 1) → 映射回本地 PlayerIndex 0
→ **两边都将远程位置写入自己的本地玩家实体**，互相覆盖，无独立远程实体承载

### 修复

#### 位置同步
- `SendGamePlayerPositionMessage`: 直接发 `client.ClientID` 作为网络标识（不再用 ConvertLocalPlayerIndexToNetwork）
- `HandleGamePlayerPositionMessage`: 写入 `RemotePlayers[remoteClientId]`，不再碰 ComponentPlayers
- 新增 `NetworkPlayerState` 类存放远程玩家状态

#### 生命值同步
- 同位置同步模式: 发 ClientID 而非 PlayerIndex
- 接收写入 RemotePlayers 而非 ComponentPlayers

#### 地形同步
- `SuSubsystemTerrain`: 新增 `m_networkReceivedCells` HashSet 标记远程修改
- 发送时跳过标记过的 Cell（防止回环：A 发→B 应用→B 再发回 A）
- `base.Update(dt)` 调用后清理标记

#### 远程玩家渲染
- 新增 `RenderRemotePlayers()`: 用 `PrimitivesRenderer3D.FlatBatch` 在远程玩家坐标画彩色方块
- 白色方块 0.4×0.4×0.8，跟随远程位置移动
- 超 5 秒无更新自动隐藏

### 产出
- .scmod: [SuAPI]SC联机.scmod (70607 bytes, v1.0.3)

---

## 2026-05-18 02:29 LAN 游戏发现 ChatMessage → GameWorldInfoMessage 修复

### 现象
两台设备 LAN 联机，双方 Explorer 互相发现 Server（`Games=1`），但 SuPlayScreen.Enter() 显示 `Loaded 0 remote games`。

### 直接原因
日志: `Game desc not GameWorldInfoMessage: ChatMessage`

J 键创建房间时，游戏描述使用了 `ChatMessage("Host", "Creating...")`：
```csharp
// ScMultiplayer.cs J 键 handler (旧)
client.CreateGame(sd.Address, Message.WriteWithSender(
    new ChatMessage("Host", "Creating..."), client.Address), ...);
```
远程 Explorer 查询 Server 游戏列表时，Server 返回 ChatMessage 的序列化字节，
SuPlayScreen.Enter() 反序列化为 `GameWorldInfoMessage` 失败 → 跳过 → 最终 0 games。

### 修复方案
**ScMultiplayer.cs J 键**: 替换 ChatMessage 为从运行时状态构建的 GameWorldInfoMessage：
- `SubsystemGameInfo.WorldSettings.Name` → GameName
- `WorldsManager.WorldInfos` (foreach DirectoryName 匹配) → Size, LastSaveTime
- `SubsystemGameInfo.WorldSettings.GameMode` → GameMode
- `SubsystemGameInfo.WorldSettings.EnvironmentBehaviorMode` → EnvironmentBehaviorMode
- `VersionsManager.SerializationVersion` → SerializationVersion
- `client.Address` → HostAddress

缓存 `LastGameDescription` 供 `Client_GameDescriptionRequest` 回调响应。

### 构建失败 (2 次)

#### 失败 1: MSBuild Rebuild → Survivalcraft 依赖锁定
- **错误**: `error MSB3027: 无法将 Engine.dll 复制到 bin\Debug\net48\Engine.dll。文件被 Microsoft Visual Studio (25328) 锁定`
- **根因**: Rebuild 清理 bin 后重建全部依赖项，Survivalcraft.exe 运行时锁定 Engine.dll
- **教训**: **必须先 `taskkill /F /IM Survivalcraft.exe`，不能只杀 VS**（锁 DLL 的是 SC 进程不是 VS）

#### 失败 2: Build 跳过重编译（obj 缓存）
- **现象**: 源文件已修改，但 MSBuild Build 报告 `0 errors` 且 `ScMultiplayer -> ...ScMultiplayer.dll`，DLL 中无新代码
- **根因**: obj 缓存认为输出最新，跳过 CoreCompile
- **解决**: touch 源文件 `(Get-Item ...).LastWriteTime = Get-Date`，确保比 obj 缓存新

### 打包失败 (3 次)

#### 失败 1: Compress-Archive 不支持 .scmod 后缀
- **错误**: `.scmod 不是支持的存档文件格式。只有 .zip 才是支持的存档文件格式。`
- **解决**: 先创建 .zip，再 `Move-Item` 改名为 .scmod

#### 失败 2: Compress-Archive 打包了目录本身
- **解决**: `Push-Location $tmpDir; Compress-Archive -Path *` 保证根目录扁平化

#### 失败 3: 误删 Mods/ 下的 .scmod 且 ModInfo.xml 不在项目源码中
- **错误**: 重打包脚本先 `Remove-Item` 旧 scmod，但 `Copy-Item ModInfo.xml` 失败（文件不存在于项目目录）
- **解决**: 从内存重建 ModInfo.xml（嵌套格式 `<Mod><ModInfo>...</ModInfo></Mod>`）

### 编译成功并部署
- 杀 VS (PID 25328) 释放 Engine.dll 锁 → touch 源文件 → Build → 0 errors
- .scmod: 72574 bytes (Lib/X64/ScMultiplayer.dll 59392 + Comms.dll 113152 + ModInfo.xml 305)
- 本地 (.28) + 远程 (.25) 均已部署，SC 已终止待重启

### 排障铁律 (写入 AGENTS.md / MEMORY.md / SKILL.md)

1. **编译前必须 `taskkill /F /IM Survivalcraft.exe`** — 锁 DLL 的是 SC 进程
2. **修改源码 → touch file → Build** — 防 obj 缓存跳过
3. **验证 DLL**: `[IO.File]::ReadAllBytes` + 搜索特征字符串确认新代码已编译进去
4. **.scmod 打包**: 先 ZIP 再改名，Push-Location + -Path * 扁平化
5. **失败记录**: 所有失败根因和解决方案写入 doc/PROJECT-LOG.md
6. **行动前先全量读**: SOUL.md + MEMORY.md + SKILL.md + today memory 再动手

---

## 2026-05-18 03:14 ModInfo.xml 扁平格式复发 + 远程部署验证

### 现象
远程机启动 SC 后日志无 `[ScMP]` 输出，Mod 静默未加载。

### 根因
**ModInfo.xml 格式错误复发**。02:29 打包时"从内存重建" ModInfo.xml 实际产出了扁平格式：
```xml
<ModInfo>              ← 根元素就是 ModInfo，错了
  <Identifier>ScMultiplayer</Identifier>
  ...
  <Dependencies>
    <Dependency>Comms</Dependency>   ← 格式也错
  </Dependencies>
</ModInfo>
```

ModLoader 解析 `doc.Root.Element("ModInfo")` 在扁平格式下返回 null → modId=null → "Invalid ModID" → .scmod 被跳过。

### 为什么之前没发现
02:29 打包后 SC 进程即被终止，.scmod 从未被真正加载验证过。验证步骤被跳过。

### 修复
重写 ModInfo.xml 为标准嵌套格式：
```xml
<Mod>
  <ModInfo>
    <Identifier>ScMultiplayer</Identifier>
    ...
  </ModInfo>
  <Dependencies>
    <Dependency>
      <ModInfo><Identifier>Comms</Identifier></ModInfo>
    </Dependency>
  </Dependencies>
</Mod>
```

### 附加发现：PowerShell [] 通配符陷阱复发
`Copy-Item $zipPath $scmodPath` 中 `[SuAPI]SC联机.scmod` 的 `[]` 被解析为通配符，导致文件写入 0 bytes。
**修复**: `Copy-Item -LiteralPath $zipPath -Destination $scmodPath`

> **教训**: 文件名含 `[]` 的所有 PowerShell 文件操作（Get-Item/Copy-Item/Move-Item/Remove-Item/Rename-Item）必须用 `-LiteralPath`。

### 远程部署障碍
- 远程机仅开放 TCP 3389 (RDP)，SMB/TCP 445 被防火墙拦截
- 无法从本机直接复制文件到远程
- 需通过 RDP 会话手动拖拽文件

### 网络验证
- 本地防火墙 UDP 51459 + 49152-65535 规则已添加并生效
- ARP 表确认远程机在线（MAC 见 `AGENTS.local.md`）
- 远程需同样执行防火墙规则（待用户在远程桌面内操作）

### 新规则
7. **.scmod 打包后必须验证加载** — 不能打包完就部署，需启动 SC 确认 Console 输出 `Loaded mod: xxx (from scmod)`
8. **ModInfo.xml 必须逐字对照模板** — 不从记忆重建，从已验证的 .scmod 提取或直接复制模板
9. **远程调试端口速查** — 远程机 8514 (调试HTTP) / 51459 (游戏UDP) / 3389 (RDP)，别忘了
10. **远程防火墙按需开放** — 只开 RDP(3389) 不够，游戏需要 UDP 51459+49152-65535，调试需要 TCP 8514

### 防火墙开放端口汇总

> **每个设备 3 个 UDP Socket**：Server(51459 固定) + Explorer(动态) + Client(动态)
> 实际日志：Server=:51459, Explorer=:56367, Client=:56369

| 端口 | 协议 | 用途 | 必须？ |
|------|------|------|--------|
| 3389 | TCP | 远程桌面 (RDP) | ✅ 管理 |
| 8514 | TCP | 远程调试 HTTP | ✅ 调试 |
| 51459 | UDP | ScMultiplayer **Server** (固定) | ✅ 联机 |
| 49152-65535 | UDP | ScMultiplayer **Explorer + Client** (动态，OS分配) | ✅ 联机 |

```powershell
# 远程机需要执行的完整防火墙开放（管理员 PowerShell）
netsh advfirewall firewall add rule name="SC Remote Debug" dir=in action=allow protocol=TCP localport=8514
netsh advfirewall firewall add rule name="ScMultiplayer Server" dir=in action=allow protocol=UDP localport=51459
netsh advfirewall firewall add rule name="ScMultiplayer Dynamic" dir=in action=allow protocol=UDP localport=49152-65535
```

---

## 2026-05-23 Android 触摸输入系统深度修复 (15:00–16:12)

### 需求
Android 端进地图后触摸滑动/Drag 完全无响应，点击 OK 但滑动手势被吞。

### 根因：ProcessTouchMoved 的位置更新条件缺陷

**位置**：`Engine/Platforms/Android/Input/Touch.cs:ProcessTouchMoved()`

原始代码只在 `state == TouchLocationState.Moved` 时更新位置：
```csharp
if (m_touchLocations[num].State == TouchLocationState.Moved)
{
    m_touchLocations[num] = new TouchLocation { Id = id, Position = position, State = TouchLocationState.Moved };
}
```

**时序攻击**：
```
Frame N:
  DispatchTouchEvent → DOWN → adds touch with State=Pressed
  DispatchTouchEvent → MOVE → finds touch, State==Pressed → 不更新位置！  ← 致命
  WidgetInput.UpdateFromTouch() → 读到 Pressed 位置（DOWN坐标），Drag 距离为0
  AfterFrame → Pressed→Moved 转换

Frame N+1:
  DispatchTouchEvent → MOVE → finds touch, State==Moved → 更新位置 ✓
  → 但 N 帧的所有中间 MOVE 事件已丢失
```

AfterFrame 在游戏循环末尾执行，Press→Moved 转换延迟一整帧。在此期间所有 MOVE 事件因状态检查被丢弃。

### 修复

```csharp
// ✅ 无条件更新位置，保持当前 State
m_touchLocations[num] = new TouchLocation
{
    Id = id,
    Position = position,
    State = m_touchLocations[num].State  // Pressed 保持 Pressed，Moved 保持 Moved
};
```

### 排查路线

```
1. [RawTouch] 日志 → 确认 DispatchTouchEvent 收到 DOWN/MOVE/UP (1248 events total)
2. [TouchDiag] 日志 → 确认 Window.IsActive=True, KeyboardVis=False (守卫未拦截)
3. 静态分析 → ProcessTouchMoved 的 State==Moved 条件在 Pressed 阶段为 false
4. 修复 → 构建 → 签名(apksigner) → adb install → 验证
```

### 修改文件
| 文件 | 修改 |
|------|------|
| `Engine/Platforms/Android/Input/Touch.cs` | ProcessTouchMoved 无条件更新位置；移除 RawTouch/TouchDiag 诊断日志；移除 ProcessTouchMoved 内 TouchState 日志 |
| `Engine/Platforms/Android/EngineActivity.cs` | 移除冗余 OnTouchEvent override；移除 DispatchTouchEvent try-catch 诊断 |
| `Survivalcraft/Game/WidgetInput.cs` | 移除 m_debugFrame 诊断字段和 WidgetTouch 日志 |

### 并行发现问题

| 问题 | 根因 | 状态 |
|------|------|------|
| Touch.Pressed/Moved/Released 事件无订阅者 | 数据通路通过 `m_touchLocations` list，事件为辅助；WidgetInput 直接读 `Touch.TouchLocations` | 确认非bug |
| Survivalcraft.dll 早期不在 APK | .NET Android 构建管线问题（类似 Lit shader 卫星程序集），现已确认 APK 含 26MB Survivalcraft.dll.so | 自愈 |
| ProcessTouchMoved 守卫 IsActive 实为 true | 不同问题，但验证排除了守卫假阳性 | 排除 |

### 教训（铁律）

11. **ProcessTouchMoved 必须无条件更新位置** — 状态检查（`State==Moved`）与 AfterFrame 时序冲突，导致帧内 MOVE 事件被吞
12. **TouchLocations list 是唯一数据源** — 事件回调（TouchPressed 等）可以没有订阅者，WidgetInput 直接遍历 list
13. **AfterFrame 在帧末执行 → 状态转换延迟一整帧** — 中间时段的位置更新必须基于当前事件而非缓存状态
14. **Android touch 用 DispatchTouchEvent 路由** — OnTouchEvent 被 SDL2 消费，不可用于自定义处理
15. **逐层步进验证法** — 事件入口 → 中间处理 → 状态更新 → 消费者读取，每层加一个日志验证，不跳跃
16. **ModLoader 只加载声明的依赖 DLL** — .scmod 内 Lib/ 下的 DLL 不会自动加载，只有与 Identifier 同名的 DLL 和 `<Dependencies>` 中声明的 DLL 才会被加载。未声明的 Comms.dll → `ReflectionTypeLoadException: Unable to load one or more of the requested types`
17. **LoadingManager.ReplaceItem 精确匹配 name** — 替换 Screen 加载步骤时，name 必须匹配原始 `QueueItem` 的 name（"Initialize PlayScreen"），不是 Screen 名（"Play"）。用 `QueueItem` 添加同名 `AddScreen` → `ArgumentException: An item with the same key has already been added. Key: Play`
18. **LoadingManager 是 static class** — 不能声明变量，直接 `Game.LoadingManager.QueueItem/ReplaceItem`。`QueueItem(string name, Action action)` 只有 2 个参数
19. **EventBus 静默吞异常** — InvalidCastException 在 EventBus.SubscribeEvent 回调中被静默吞咽，不报错不崩溃，mod 功能直接失效。StringInterceptor 的翻译失效就是这个原因——参数类型从 `List<Action>` 变为 `typeof(LoadingManager)` 但代码没更新

---

## 2026-05-25 ScMultiplayer 迁移至 net10.0 双平台

### 需求
将 ScMultiplayer 及其依赖库 Comms 从 net48 迁移至 net10.0 双平台（Android + Windows）。

### 迁移过程

#### Comms 库迁移
- csproj 从旧格式改为 SDK 样式，双 TFM
- 添加 `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` 避免与 AssemblyInfo.cs 冲突
- 移除显式 Compile 项修复 NETSDK1022
- 双平台编译成功，仅 CS8618 nullable 警告

#### ScMultiplayer 源码修改
1. **HandleLoading 修复**：`LoadingManager` 是 static class，移除变量声明。`QueueItem("Initialize PlayScreen", action)` → `ReplaceItem("Initialize PlayScreen", action)`
2. **UdpTransmitter 构造函数**：旧 `UdpTransmitter(IPAddress, int)` → 新 `UdpTransmitter(int localPort = 0)`，自动检测 LAN 地址
3. **Keyboard 条件编译**：`Key.T/J/K/U` 用 `#if WINDOWS` 包裹
4. **ModInfo.xml 创建**：Identifier=ScMultiplayer，声明 Comms 依赖
5. **Obfuscar.xml 创建**：仅 Windows 端，路径 net10.0-windows10.0.19041.0

#### 运行时加载失败排查
- **错误1**：`Failed to load mod ScMultiplayer: Unable to load one or more of the requested types.`
  - 根因：ModInfo.xml 缺少 `<Dependencies>` 声明，Comms.dll 被跳过
  - 修复：添加 `<Dependencies><Dependency><ModInfo><Identifier>Comms</Identifier></ModInfo></Dependency></Dependencies>`

- **错误2**：`An item with the same key has already been added. Key: Play`
  - 根因：`QueueItem` 添加新加载步骤执行 `AddScreen("Play", ...)`，但原始 "Initialize PlayScreen" 步骤也执行了 `AddScreen("Play", ...)`，字典键冲突
  - 修复：改用 `ReplaceItem("Initialize PlayScreen", ...)` 替换原始步骤

### 最终结果
- Windows: ScMultiplayer 加载成功，Server/Client/Explorer 正常初始化
- Android: adb push 部署成功
- 6个 Mod 全部迁移完成

---

## 2026-05-25 (18:06) SurvivalcraftMiniMap 双平台尺寸/位置调优

### 需求
MiniMap 在 Windows/Android 上显示太小，且位置偏向屏幕中央。

### 排查过程
1. 读取 SuComponentMap.cs，发现 RmapRadius 同时控制视觉大小（screenSize.Y * RmapRadius）和中心定位偏移
2. 第一轮调整: RmapRadius 0.40→0.55，MapScale 翻倍→仍然偏小+偏中央
3. 第二轮调整: RmapRadius 0.55→更大 → 地图大小满意但位置严重偏中央
4. **关键发现**: RmapRadius 增大后 center 偏移量也增大，两者耦合导致地图挤向屏幕中间。拆分为独立参数: visualRadiusPx(mapRadius*MapScale) 控定位，MapScale 控大小
5. **坐标系纠正**: SC 用 OpenGL 坐标系 Y 向上，之前误认为 Y 向下导致多次调整方向错误
6. 定位公式确定为: center = (screenW - visualRadiusPx - marginX, visualRadiusPx + marginY)
7. Windows margin=10% 足够，Android margin=10% 被 UI 挡住→增至 15%
8. Android MapScale 3.6 太大(翻倍了)→降至 1.8
9. 旋转轻微偏心：双缓冲+增量旋转机制固有特征，不影响使用

### 最终参数
| 参数 | Windows | Android |
|------|---------|----------|
| mapRadius | 100 | 100 |
| MapScale | 1.4 | 1.8 |
| marginX/Y | screenSize.Y × 10% | screenSize.Y × 15% |

### 踩坑铁律
1. **RmapRadius 大小/位置耦合铁律**: 不能用同一个系数同时控制视觉半径和定位偏移。增大 RmapRadius→偏移量增大→地图挤向中间。拆分为 visualRadiusPx + marginX/Y
2. **SC 坐标系 Y 向上**: OpenGL 坐标系 Y 从下往上。调 UI 位置前先确认坐标系方向
3. **Android 边距比 Windows 大**: Android UI 元素比例更大，同等百分比边距不够用
4. **MapScale 不能盲目翻倍**: Windows 0.7→1.4 合理不代表 Android 1.8→3.6 也合理。Android 保持 1.8
5. **旋转偏心是机制特征**: 双缓冲增量旋转有微小浮点漂移，可接受

## 2026-05-24 (21:00-22:09) StringInterceptor Release Android AOT/Linker 裁剪问题

### 症状
StringInterceptor Mod 在 Android Release 版加载成功（2505翻译+72 StringsManager条目），但界面上无翻译效果。

### 根因
3个方法被 .NET AOT Linker 裁剪（主程序未使用）:
1. **HashSet<T>.RemoveWhere(Predicate<T>)** — ScanWidgetTree() 中清理已移除 Label，被裁剪→MissingMethodException→整个 Scanner 每帧崩溃→翻译失效
2. **List<T>.Sort(Comparison<T>)** — SaveCollected() 中排序 Entry，被裁剪→排序失败
3. **XDocument.Load(string)** — SaveCollected() 中加载 zh_CN.xml，被裁剪→加载失败

### 修复方案
1. HashSet.RemoveWhere → foreach + 临时列表 + Remove
2. List.Sort(Comparison<T>) → 冒泡排序
3. XDocument.Load(string) → XDocument.Load(Stream) + FileStream

### 关键教训
- **ModEventBus 异常吞咽**: TriggerEvent catch 只写 Console.WriteLine，不记 Game.log。Scanner 每帧 MissingMethodException 被完全吞掉，没有任何可见错误
- **AOT 裁剪通用原则**: Release Android 下，任何主程序未使用的方法/泛型实例都可能被裁剪。Mod 只用最基础集合操作（foreach/Add/Remove/索引器），避免 Linq/委托排序/params 构造函数/高级便利方法
- **诊断方法**: Mod 在 Release 静默失效时，在 handler 外围加 try-catch + Log.Error() 捕获 MissingMethodException

---

## 2026-05-25 (00:00) StringInterceptor 临时诊断日志清理

### 变更
移除 StringInterceptorMod.cs 中所有 `[SuAPI]` 前缀的临时诊断日志和对应计数器字段。

### 移除项
1. `_diagFrameUpdate` 计数器 + `[SuAPI] Frame.Update fired` 日志
2. `_diagRootNull` 计数器 + `[SuAPI] RootWidget is null` 日志
3. `_diagScreenName` 计数器 + `[SuAPI] Screen=...` 日志
4. `_diagUpdateSkips` 计数器 + `[SuAPI] Update skip: not active` 日志
5. 每 label 翻译日志 `[SuAPI] Label '...' -> '...'`

### 保留项
- 所有 Error/Warning 级别日志
- 一次性启动信息（版本、字体加载、翻译加载、scanner 启动）
- 关键生命周期事件（ProcessStrings 统计、SaveCollected 结果、Unloaded）

### 铁律
- 临时调试日志（`[SuAPI]`/`[Window]` 等标记）验证后必须立即移除，不得提交
- Android 上 Engine.Log 通过 GameLogSink 每次 Flush()，频繁日志=频繁磁盘 I/O

---

## 2026-05-25 (00:00) SuComponentMap 临时诊断日志清理 + 双平台定位修复

### 变更
1. 移除 SuComponentMap.Load() 中所有 `Log.Information("SuComponentMap.Load: ...")` 诊断日志
2. 修复 center 定位公式：从 `screenSize.X * (1-RmapRadius/2), screenSize.Y * RmapRadius/2` 改为 `screenSize.X - visualRadiusPx - marginX, visualRadiusPx + marginY`
3. 按平台设置不同 margin 和 MapScale（Windows 10%/1.4，Android 15%/1.8）

### 铁律
- RmapRadius 大小/位置耦合：不能用同一系数同时控制视觉半径和定位偏移
- SC 坐标系 Y 向上：OpenGL Y 从下往上，center.Y 越大越靠上
- Android 边距比例需大于 Windows
## 2026-05-27 编译发布 net8 版本

### 需求
将 Survivalcraft net8 Mono 版编译并发布 Windows 和 Android 版本到 publish 文件夹。

### 项目结构
- 项目根目录: `D:\Users\Suceru\Desktop\jiejian\SurvivalcraftMonoWinAN\`
- Windows csproj: `Survivalcraft\Survivalcraft.csproj` (net8.0, WinExe, AssemblyVersion 2.4.40.8)
- Android csproj: `Survivalcraft\SurvivalcraftAndroid.csproj` (net8.0-android, ApplicationId: com.candyrufusgames.survivalcraft2su, AssemblyVersion 2.4.10.8)
- Android sln: `SurvivalcraftAn.sln`
- global.json: 锁定 SDK 8.0.402, rollForward: disable
- 系统安装 SDK: 8.0.402 + 10.0.300

### 踩坑记录

#### 1. SDK 版本锁定（最关键）
从工作区目录（无 global.json）运行 `dotnet publish` → SDK 10.0 被选中 → 对 net8.0-android 多目标框架报 NETSDK1202 EOL 错误。
**修复**: 必须从项目根目录 `D:\Users\Suceru\Desktop\jiejian\SurvivalcraftMonoWinAN\` 运行所有 dotnet 命令，让 global.json 生效。

#### 2. APK 文件名含方括号
`[SuAPI]Survivalcraft-0.1.2.0.Apk` 文件名中的 `[]` 导致:
- apksigner 的 `--out` 参数解析失败，输出文件不存在
- PowerShell 的 `Get-Item`/`Remove-Item`/`Move-Item` 把 `[]` 当字符集通配符

**修复**:
- apksigner 签名时 `--out` 指向临时文件名 `signed.apk`（无特殊字符）
- 签名完成后再用 `Move-Item -LiteralPath` 重命名为含[]的最终文件名
- PowerShell 操作含[]路径一律用 `-LiteralPath`

#### 3. 版本号管理
项目搜索 `0.1.1.8` 只在 `SurvivalcraftAndroid.csproj` 的 `<ApplicationDisplayVersion>` 一处。
改为 `SuAPI 0.1.2.1`，APK 文件名随之更新为 `[SuAPI]Survivalcraft-0.1.2.1.Apk`。

### 发布命令参考
```powershell
Set-Location "D:\Users\Suceru\Desktop\jiejian\SurvivalcraftMonoWinAN"

# Windows
dotnet publish "Survivalcraft\Survivalcraft.csproj" -c Release -r win-x64 --self-contained true -o "publish\win-x64"

# Android
dotnet publish "Survivalcraft\SurvivalcraftAndroid.csproj" -c Release -f net8.0-android -o "publish\android"

# APK 签名
$buildTools = "C:\Users\Suceru\AppData\Local\Android\Sdk\build-tools\34.0.0"
$keystore = "publish\Suceru.jks"   # 口令见 AGENTS.local.md，禁止写进本文件
& "$buildTools\zipalign.exe" -f 4 "publish\android\com.candyrufusgames.survivalcraft2su.apk" "publish\android\aligned.apk"
& cmd /c "$buildTools\apksigner.bat sign --ks `"$keystore`" --ks-pass pass:<见 AGENTS.local.md> --ks-key-alias suceru --key-pass pass:<见 AGENTS.local.md> --out `"publish\android\signed.apk`" `"publish\android\aligned.apk`""
Move-Item -LiteralPath "publish\android\signed.apk" -Destination "publish\android\[SuAPI]Survivalcraft-0.1.2.1.Apk" -Force
& cmd /c "$buildTools\apksigner.bat verify -v --print-certs `"publish\android\[SuAPI]Survivalcraft-0.1.2.1.Apk`""
```

### 铁律
- **global.json SDK 锁定**: 多 SDK 环境下，必须从含 global.json 的项目根目录运行 dotnet 命令
- **APK 文件名避坑**: 先签名到临时文件再重命名，不要试图让 apksigner 直接输出含[]的文件名
- **PowerShell -LiteralPath**: 操作含 `[]` 的路径时必须用 -LiteralPath，不能用 -Path

### 2026-09-23 待查：联机睡觉醒来后饱食度不减

现象：联机（三端同一 build）中睡一觉醒来后，玩家的饱食度不下降。

已定位的结构（未改代码时先记录）：
- 联机版生命体征是**主机算、客户端播**：`Func/Component/SuComponentVitalStats.cs` 把 `ComponentVitalStats` 替换成
  "主机计算 → 周期下发 → 客户端 `ApplyAuthoritativePlayerStats` 只播放"，本地不做任何预测/模拟。
  因此客户端显示不动，只可能来自主机侧。
- 候选原因 1（主机自己也没扣）：睡眠走"睡眠加速"会话 —— `Modules/Runtime/ScMultiplayerUpdateLoop.cs`
  （`MaintainHostSleepAccelerationSession` / `IsSleepAccelerationActive`）、`Modules/World/ScMultiplayerWorldSync.cs`、
  `Modules/Runtime/ScMultiplayerRuntimePhases.cs`（`CompletePendingClientSleepWakeups`）。加速期间对睡眠玩家
  是零 `GameTimeDelta`（`m_sleepBlackoutFactor/Duration`）。若醒来时该会话没有干净收尾，主机后续仍按"睡眠中"
  处理，`UpdateFood` 不再消耗。
- 候选原因 2（主机在扣但没下发）：`Core/AuthoritativePlayerStateSnapshot.cs` 的发布判据（`|Food - previous.Food|`），
  配合 `Core/ScMultiplayerRuntimeState.cs:ShouldHoldClientSleepTimeline` 与
  `MarkClientSleepWakeBoundaryPending` 的醒来边界；若边界完成早于本次统计值下发，客户端会停在睡前那份。

下一步（用户要求**先不检测**，故暂不加探针、不改代码）：
1. 睡醒后保持不动 60 秒，看主机 `Logs/Server/ScMP-op-*.log` 是否仍有该玩家的生命体征下发；
   无下发 → 候选 1（修睡眠加速会话收尾，醒来强制清 `m_sleepBlackout*`）；
   有下发但数值恒定 → 候选 2（`UpdateFood` 的 `GameTimeDelta` 被压成 0，修加速期间的 delta 门控）。
2. 确认成因后再动代码，按老规矩：先报改动点 → 改 → 三端部署 → 验证。
### 2026-09-23 2.2.x 回退到 2.1.66（两处回归记录）

**1) 主机侧世界加载卡死（已确诊；2.2.1 已注释掉该改动）**
- 现象：服务器进程正常、mod 正常加载（`[ScMP] Wire protocol mod 2.2.0 …`、`Server started OK`、`Database hooks applied`），
  但日志到此为止，无异常、无 `serverError`；`world.join 201ser` 被接受但 `worldLoaded` 永不成立 →
  `world.list` 里 201ser 恒为 `loaded:false`，发现列表里也就没有这台服务器。
- 成因：新增的 7 个 `modInjector.Register("Game.SubsystemXBlockBehavior", "ScMultiplayer.SuSubsystemXBlockBehavior")`
  （仙人掌/腐坏/地毯/落叶/耕地/常春藤/树苗）让**主机初始化在 `Database hooks applied` 之后挂住**。
  去掉这 7 行后立刻恢复（`join 201ser` → `worldLoaded:true`、`currentScreen:Game`），已实验确诊。
- 待办：按 mod 里既有的 `Pak/Database.xml` GUID 补丁机制（植物/草/落叶就是这么做的）重做这 7 个替换，
  再验证：主机不卡死 + 客户端不再凭空长出仙人掌 + 挖掉有正常掉落物。

**2) 点 Play 后 Play 界面自动退回主菜单（2.1.66 无此问题，待查）**
- 现象：主菜单点"游玩"后，CmdBridge 事件环显示 `screen.changed MainMenu→Play`、`ui.click Play`，
  **23 帧（≈0.4 秒）后 `screen.changed Play→MainMenu`**；客户端日志无任何异常；与窗口焦点无关
  （已用 `focusrecover` + `AppActivate` 验证 `realFocus:true / foregroundIsGame:true` 后仍复现）。
- 线索：`Func/Screen/SuPlayScreen.cs`（自 2.1.66 起一行未改）里能离开该界面的只有反钮 `SwitchScreen("MainMenu")`(169)、
  选中世界开玩 `SwitchScreen("GameLoading",…)`(961)，以及 `Enter()`(366-393) → `StartWorldScan`(412-426，
  先弹模态 `BusyDialog("Scanning Worlds")` 再后台扫世界) 这一段抛异常被屏幕管理器兜回主菜单。
  0.4 秒即退出、远早于扫描完成 → 更像 `Enter` 阶段异常，而不是扫描结果触发。

**结论**：2.2.x 相对 2.1.66 的**运行时差异只有版本号**（其余为注释 + 一个未被任何代码引用的新文件），
但为保服务与可比性，**整体回退到 2.1.66**（代码 + 三端部署，并与索引/发行版资产的 2.1.66 口径一致）。
### 2026-09-23 待实现（已选定方案 1）：背包同步加"单调版本号"

**两个可复现场景（同一根因）**
1. 挖 1 个仙人掌 → 底部 + 顶部失去支撑共掉 2 个掉落物 → 两个都捡起，**背包只 +1**（另一个被吞）。
2. 平板站在方块角"边扔边捡"（14 个沙子）→ 一段时间后**净少 1 个**。
共同点：**背包在极短时间内的连续"入包/出包"被这套增量同步合并/覆盖**。mod 的背包是"主机权威 + 稀疏增量"
（`TryBuildInventoryDelta` / `ApplyInventoryDelta`），而"上次已发送的基准"是**一份随操作被重置的共享状态**
（重置点 `MarkHostInventoryAuthoritative`，见 `Modules/Session/ScMultiplayerClientEvents.cs:3283`，注释即写明它会清掉该客户端上次已发送状态）。

**涉及通路（已定位）**
- 入包：`Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs` —— 主机 `HandlePickableAcquireRequest`(243) →
  入包 → `MarkHostInventoryAuthoritative`(308) → 回"变化格"增量(320)；客户端应用 528-565。
- 出包（丢弃/扔出）：`Modules/World/ScMultiplayerWorldSync.cs` —— `TryFindUiDropSource`(约 351) →
  `InventorySlotValues = new[] { itemValue }`(471) / `CaptureInventoryValues`(529)。
- 周期同步：`Modules/Player/ScMultiplayerPlayerSync.cs`(408-430) + `m_forceHostInventorySync`
  （`Core/ScMultiplayerRuntimeState.cs:420`、`Modules/Runtime/ScMultiplayerUpdateLoop.cs:3023`）。

**实现清单（方案 1）**
1. **版本源**：主机侧为每个客户端维护 `m_playerInventoryVersion`（单调递增，仅主机自增；也可用全局计数器）。
   每次**入包/出包**（拾取、UI 丢弃、容器/合成结果、GM 改背包、周期同步的强制全量）后 `++`。
2. **线格式**（build 锁定，三端必须同时升级）：在这些消息里各加 `InventoryVersion`（本次结果对应的版本）与
   `BaseInventoryVersion`（增量相对哪个版本；全量时为 0）：
   `Message/PickableSyncMessage.cs`、`Message/PlayerActionMessage.cs`、`Message/ContainerSyncMessage.cs`、
   `Message/GamePlayerPositionMessage.cs`（周期快照）。
3. **主机**：发增量时填 `BaseInventoryVersion` = 生成该增量所用的基准版本，`InventoryVersion` = 自增后的版本；
   发全量时 `BaseInventoryVersion = 0`。
4. **客户端**：保存 `m_localInventoryVersion`；应用规则：
   - `msg.InventoryVersion <= local` → 丢弃（旧结果，防覆盖）；
   - `msg.InventoryVersion == local + 1 && msg.BaseInventoryVersion == local` → 应用增量并 `local = msg.InventoryVersion`；
   - 其它情况（跳号或基准不符）→ **不发散应用**，改为请求一次**全量背包**，收到全量（`Base=0`）后 `local = InventoryVersion`。
5. **验证**：先用上面两个复现场景（挖 1 掉 2 捡 2；平板边扔边捡 14 个不掉），再做一次"快速连续丢弃 5 个"的压力用例。

---

# 2026-09-24 联机死亡/复活/血量 + "加入房间卡死"一批问题的根因与修复（2.2.0）

> 前提：ScMP 是 build 锁定的（`Message.IsProtocolCompatible` 要求 ModVersion/ProtocolVersion/
> ProtocolHash/BuildFingerprint 全等），所以每个修复都是**三端同包部署**后由用户在 PC + 平板上实测；
> 版本号保持 2.2.0。以下按"现象 → 真因 → 修法 → 验证"记录本次会话的 7 个问题。

## 1. 死亡原因显示 Unknown、游戏统计里没有这次死亡

**现象**：被狼/狮子咬死后面板写着 `Cause of death: Unknown`，统计里也没有这条死亡记录；
"骷髅头"自杀那次却显示 `Choked`。

**真因**（三处叠加）
1. `CauseOfDeath` 与 `PlayerStats.AddDeathRecord()` 在引擎里**只写在 `ComponentHealth.Injure()` 内**
   （`ComponentHealth.cs:91-102`）。联机下客户端血量是"直接写字段跟随主机"的，不经过 `Injure`
   → 客户端这两样天生为空。
2. 死亡那一帧通常带击退，而 **`CaptureHostRemoteKnockbacks` 发的"击退快照"不带死因**
   （原代码传 `null`），它走即时双份 + 可靠一份，**比周期快照先到**：客户端先按"没死因"落地一次
   （`m_localDeathApplied` 锁死），随后带真死因的周期快照被"一次死亡只落地一次"挡掉 → 永远 Unknown。
3. 主机施加"客户端上报的伤害"时死因**写死** `"Client damage request"`，真死因当场丢失。

**修法**
- 击退快照在死亡时带 `health.CauseOfDeath`；周期快照死亡时也带（`SendAuthoritativePlayerHealth`）。
- 客户端 `ApplyLocalAuthoritativeDeath`：按主机给的死因写 `CauseOfDeath` **并**记一条死亡统计；
  拿到真死因时**补写上一条**记录（改写而不是多记一条）；本 Mod 自己的上报标签
  （`Client damage request` 等）**不当作死因显示**，宁可 Unknown 且保留"以后再补"的机会。
- **死因由主机给**：生物攻击（主机端 `Attacked` 确实会触发 —— 既有代码 `EnsureHostSleepWakeHandlers`
  就是靠它叫醒睡着的玩家）、饥饿/溺水/高温都是主机在实时模拟，主机那份角色的 `CauseOfDeath` 就是真死因。

**走过弯路（勿重蹈）**：一开始在客户端用 `Attacked` 自己拼死因（`bitten by a wolf`）再随伤害上报。
这既**多余**（主机本来就有），又会**误判** —— 客户端只记得"最近一次被谁咬"，
于是"刚被咬过一两秒后饿死/摔死"会被标成被动物咬死。该实现已撤回，`SuComponentHealth.cs` 恢复原样。

## 2. 复活链路的三个子问题（同一段代码，先后三次修复）

**2a. "点复活后屏幕全红、又弹一个死亡界面、站着不能动"**
- 真因：客户端本地重生是引擎自己完成的（`PlayerData` 换实体、新实体满血），而**主机那份此时还停在 0**
  （复活请求还没被处理）。若复活窗口的判据里带上"主机没判死"，这个过期的 0 会让窗口失效 →
  本地血量被写回 0 → 屏幕全红 + 再次死亡锁存。
- 修法：**用权威序号区分"过期的死"与"复活后新判的死"** —— 新增 `m_localDeathSequence`（主机广播判死时
  记下该快照序号），与复活那一刻记下的 `m_localRespawnStateSequence` 比较：序号不晚于它的 0 是**过期的**
  （不跟随、继续钉正值、继续撤销本地死亡锁存），序号更大的才是真死。

**2b. "最后半格血点击扣血不死，要等一会再点才死"**
- 真因：上一版为防幽灵死亡，在复活窗口里**把本地扣血整个吞掉**（既不闪红也不上报），窗口最长 5 秒，
  而且只有"有意义变化"的快照才结束窗口（站着不动不产生变化）→ 那段时间点骷髅头完全无效。
- 修法：窗口**只钉血、不吞伤害** —— 照常上报扣血（死不死由主机定）；窗口内唯一地板是
  "主机那份还写着 0"时用引擎重生的满血 1，主机一给正值就照常跟随。

**2c. "复活后立刻又出现死亡界面，且后续几次死亡统计里没有"**
- 真因：窗口不钉血时，复活传送带来的摔落判定/身边动物能在本地把血打到 0 → 本地死亡锁存再次发生
  （幽灵死亡，主机从未判死，所以没有统计）；更糟的是它把"已落地 + 死因已确定"的标记留在 true，
  中间若没有收到主机的**正值**快照，**下一次真死**会被当成"同一场死亡已经记过"而**既不改死因也不记统计**。
- 修法：新增 `UndoLocalDeathLatch()`（复用原兜底逻辑）在窗口内持续撤销本地锁存；
  并在每帧同步里加"**本端观测到血量为正、而主机没判死 ⇒ 复位死亡落地标记**"。

## 3. 复活位置与主机不一致、其他客户端看到角色"朝客户端的位置闪现过去"

- 真因：复活落点**只由主机决定**（`ResetNetworkPlayerAfterRespawn` 用主机锚点，忽略请求里的位置），
  广播 `BroadcastPlayerRespawn` 让**其他客户端**把该角色摆到同一锚点，但**当事人自己被排除在外**
  （`message.PlayerIndex != client.ClientID`）。于是当事人停在自己本地重生搜索出的点
  （引擎 `PlayerData.FindNoIntroSpawnPosition`：锚点附近找"没被挡住"的落点，带随机与地形判定），
  随后它的输入快照（本 Mod 里角色位置以客户端输入为准）就把主机那份、以及其他客户端看到的这个角色
  从复活点一路拽过去。
- 修法：主机接受复活请求、把角色落到锚点之后，立刻用
  `SendPlayerAuthorityState(..., PlayerAuthorityAction.Teleport, ...)` 把最终落点**回传给本人**；
  客户端既有的 `HandlePlayerAuthorityMessage` 会移动本地身体、清输入基准并重置输入序号，
  位置当场从新点重新上报。时序安全：客户端是在本地重生**完成之后**（`ObserveLocalPlayerRespawn`
  观测到新实体）才发 `RespawnRequest` 的，所以回传一定落在本地重生之后。

## 4. 被动物咬时只有血条在动、屏幕不闪红（单机是闪的）

- 真因：单机的红屏累积（`m_redScreenFactor += -4f * HealthChange`）与血条闪烁都在
  `ComponentHealth.Update`（`:256-257`）里由 `HealthChange` 触发；联机下本地血量是被**直接写字段**
  改成主机值的，原生那段永远看不到扣血。
- 修法：在主机快照落地处（`HandleGamePlayerHealthMessage`）按**本端这次实际下降的量**补
  `TriggerLocalDamageFeedback`（红屏 + 血条闪烁）；同时把本地那条路径的闪红**去掉** ——
  本地扣血走的是"上报 → 主机扣 → 快照回来"同一条路，两处都做会一次伤害闪两下。

## 5. "最后一小格血点击扣血不死"（有实测轨迹）

**现象**：血条还剩半格（实际只剩零点几）时点骷髅头不死，有时只是把面板关了；而**刚好一格**时能死。

**实测轨迹**（CmdBridge 每 150ms 采样本地血量，同一位置连点骷髅头）
```
03:47:39.328 hp=0.10450      ← 一格
03:47:39.853 hp=0.00505      ← 掉到余量 0.005
   …43 秒内一直是 0.00505…   ← 再怎么点都不动
03:48:32.007 hp=0            ← 靠攒账磨了 52 秒才判死
```

**真因**：主机那份因自然回血总比血条显示的高一点点，一次 −0.1 打下去**每次都剩 ~0.005 的余量**；
而本地这一下只掉了 `0.005 <` 上报阈值 `0.02`（`LocalDamageReportThreshold`）⇒ 被当成"饥饿/窒息那种
小额"攒账，**永远发不出去**。剩一格时掉落 0.1 ≥ 0.02 过阈值，所以能死。

**修法**：按**伤害来源**区分"命中"（引擎结构天然可分）——
`SuComponentHealth.IUpdateable.Update` 给两次同步传入 `insideNativeUpdate`：
原生伤害（窒息/岩浆/摔落/挤压）都在 `base.Update(dt)` **之内**，UI 骷髅头/尖刺/爆炸在**之外**。判据改为
```csharp
if (lost >= LocalHitDamageThreshold || (!insideNativeUpdate && lost > 0.0001f))
    lost = MathUtils.Max(lost, LocalHitDamage);      // 原生之外的一次性命中：余量再小也按标称 0.1 上报
```
原生之内的小额仍走攒账 —— 否则最后一格血里每帧的窒息伤害都会被按 0.1 上报，主机瞬间被扣光。

## 6. 平板卡在"加入房间"的模态 BusyDialog，只能重启（电路 Recovery 保持态死锁）

**现象**：平板停在 `Joining Room / Connection: Connected`，HUD 在后面照常渲染但完全不能操作，几分钟不动。
**主机侧却认为一切正常**（`connectedClients=2`），但 `playersCount=1`、`activeJoins=0`
—— 主机早已结束/放弃这次加入，客户端毫不知情（半个加入）。

**诊断证据**
- 客户端 `Game.log`：`GameJoined, ClientID=2` → 世界下载完成 → 进入游戏 → `Client project ready`
  → 之后**再无一行**，特别是没有 `Client circuit bootstrap complete` / `Client catch-up complete`。
- `Logs/Client/<日期>.log` 的 `event=join.barrier`（仓库里为这个问题预埋的探针）连续数分钟不变：
  ```
  state=Recovery bootstrap=False recoveryHold=True recoveryRequested=False recoveryAttempts=0
  snapshotApplied=True snapshotBlocksJoin=False snapshotRequested=False rebaseAwaitingFence=False
  fence=True fenceStale=False fenceSerial=453/452
  ```
- 把这些代进 `CircuitSynchronizer.TryCompleteRecoveryHold()`，**所有门槛都过**，只剩
  `SuSubsystemTerrain.LastAppliedTerrainSequence < m_fenceTerrainSequence`。
- 本次加入之后**一条地形消息都没有**（`terrain.recv`/`terrain.apply` 最后一次是一小时前）。

**真因**
1. 加入屏障的"恢复保持"要等地形追到最后一条 fence 要求的主机地形序号，而 fence 带的
   `RequiredTerrainSequence = m_owner.CircuitTerrainSequence` 是主机**当前**值 —— **移动靶**，
   每来一条新 fence 就可能往前推。
2. 地形恢复机制在加入期间被**整体关掉**（`UpdateClientTerrainRecoveryAfterNetworkActions` 里
   `PendingWorldReadyTransferId > 0` 直接 return），所以没人去补这段地形。
3. hold 里也**不会主动请求** snapshot/recovery（那条分支只在 `m_expectedSequence <= m_knownHostSequence`
   时走），更没有超时兜底 ⇒ 死锁，且模态框没有退路。

**修法（用户指定）**
- **fence 到 700ms 就主动要同步**：新增 `FenceRefreshRequestAge = 0.7`（早于 `FenceStaleTime=0.75`），
  `MaintainRecoveryRequests()` 用它触发 `CheckpointRequest`（仍按 1 次/秒节流）。
- **加入阶段就触发地形补推**：新增 `CircuitSynchronizer.IsWaitingForTerrain`；
  `UpdateClientJoinBarrier` 一旦发现"地形落后于 fence 要求"就置 `m_clientTerrainRecoveryActive/Pending`；
  并把上面那条"加入期间整体关掉"的判据改成"世界已导入、且电路正等地形时放行"。
- **补诊断**：客户端 `join.barrier` 增加 `terrainApplied / fenceTerrain / waitingTerrain`；
  主机在 `CircuitTerrainSequence` 变化时记 `event=circuit.terrain hostCircuitTerrain=… hostStep=…`
  → 落 `Logs/Server/ScMP-op-<日期>.log`。

**验证**：新包三端重启后平板正常加入（`state=Ready bootstrap=True`），且
平板 `terrainApplied=176976 fenceTerrain=176976` 与主机 `hostCircuitTerrain=176976` **完全一致**
—— 顺带证实地形门槛比较的确实是同一套编号（此前只能推断）。

## 7. "fence 延迟一直变"（不是故障，但平板通道确实差）

- `fenceAge` = "距上一条**被接受的** fence 的毫秒数"，**天生是锯齿**（0 → 间隔 → 归零），必然一直在变。
- 主机 **16Hz** 发 fence（`TriggerNetworkTick` → `PublishNetworkState(pulse1Hz, pulse16Hz)` → `SendFence(-1)`），
  走的是 **`latest: true` 不可靠通道**（可丢、可被更新的顶掉）。
- 平板实测（卡住那 2 分钟）：fenceAge p50=388ms、max=1850ms，**8.8% 的采样 > 750ms**（= `FenceStaleTime`），
  界面电路状态因此在 `Recovery` ⇄ `Fence` 之间反复跳（5488 : 531 帧）；被接受的 fence 速率只有 ~2Hz
  （主机发 16Hz），到达间隔 p50=803ms。
- PC 对照（同主机、不过代理）：p50=218ms、max=405ms、**超阈值 0%**。
- 平板端是**经本机代理**转发公网流量的（代理类型/端口/设备细节只在 `AGENTS.local.md` 维护），
  这最可能解释"不可靠通道丢得多、延迟忽大忽小"（可靠通道没问题：世界 3.3MB 下载正常）。
- 处置：见问题 6 的"700ms 主动请求同步"。

## 8. 本次会话可复用的观测手段（不加日志也能量化）

- **客户端决策日志**：`Logs/Client/<日期>.log`（平板在 `/sdcard/Download/Survivalcraft/Logs/Client/`）；
  主机的**操作记录**在 `Logs/Server/ScMP-op-<日期>.log`；两者由 `ScMultiplayerOperationLog.Write`
  按角色自动分流。
- **CmdBridge**：`player`（本端血量/Air/位置/背包）、`events`（事件环：`player.damaged/healed/died`，
  掉血量在 `damaged.delta`；还有 `world.loaded`、`screen.changed` 等）、`ui --all`、`dialogs`、`status`、`messages`。
  平板经 `adb forward tcp:<本地> tcp:26751` + `--token`（token 只存在于设备上的 `CmdBridge.json`，不要写进仓库）。
- **实测优于推断**：问题 5 的结论（余量 0.005 卡在 0.02 阈值）与问题 6 的结论（地形移动靶）
  都是靠"连续采样 + 事件环 + 既有诊断"定下来的；只读代码只能得到"应该会死/应该会推进"。
- **PowerShell 坑**：`Start-Process -FilePath` 同样吃 `[ ]` 通配符（本仓库路径含 `[SuAPI]`）→ 用
  `cmd /c start "" /d <目录> <exe>`；远端 PowerShell 的引号容易被 cmd 吃掉 → 优先用
  `cmd /c findstr ...` 这类无引号简单命令，或先 scp 脚本再 `-File` 执行。