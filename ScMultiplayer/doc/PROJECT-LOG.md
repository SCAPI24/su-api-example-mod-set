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
远程 192.168.31.25 启动 SC 后日志无 `[ScMP]` 输出，Mod 静默未加载。

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
- 远程 .25 仅开放 TCP 3389 (RDP)，SMB/TCP 445 被防火墙拦截
- 无法从本机直接复制文件到远程
- 需通过 RDP 会话手动拖拽文件

### 网络验证
- 本地防火墙 UDP 51459 + 49152-65535 规则已添加并生效
- ARP 表确认 .25 在线 (MAC b4-69-21)
- 远程需同样执行防火墙规则（待用户在远程桌面内操作）

### 新规则
7. **.scmod 打包后必须验证加载** — 不能打包完就部署，需启动 SC 确认 Console 输出 `Loaded mod: xxx (from scmod)`
8. **ModInfo.xml 必须逐字对照模板** — 不从记忆重建，从已验证的 .scmod 提取或直接复制模板
9. **远程调试端口速查** — 192.168.31.25:8514 (调试HTTP) / :51459 (游戏UDP) / :3389 (RDP)，别忘了
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
# 远程 .25 需要执行的完整防火墙开放（管理员 PowerShell）
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
