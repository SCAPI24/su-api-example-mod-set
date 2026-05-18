# ScMultiplayer 项目日志

> 记录所有用户需求、决策和变更
> 项目: ScMultiplayer 联机 Mod
> 最后更新: 2026-05-18

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
