# 联机加入验证流程（主机 / 平板 / CmdBridge）

本文记录一次实际验证中确认的操作流程，重点解决：

- 列表里同时有本地世界和局域网房间时，如何确认点中的是**服务器房间**；
- 房间行与本地地图同名时，如何用“是否有 Ping / Online 信息”区分；
- 点击 `Play!` 之前如何确认目标地图已经选中；
- 加入后如何确认流程确实进入握手，而不是误开了本地世界。

> 设备地址、ADB 序列号、CmdBridge token 一律以 `AGENTS.local.md` 为准，不写进任何被跟踪文件。

## 1. 前置条件

- 主机 PC 已在游戏世界内；
- 主机已按游戏内 `CR` 建房；
- 平板与主机在同一局域网；
- 平板 CmdBridge 可用（能读 UI、能执行点击）；
- 平板 CmdBridge 端口通过 `adb forward` 映射到本机。

## 2. 主机侧确认房间

1. 主机在游戏内按 `CR`（或按世界设置自动主持）。
2. 确认主机日志出现：

   ```text
   房间已创建：... 地图端口 <port> ...
   ```

3. 确认主机端口在监听：

   ```text
   TCP 0.0.0.0:<port> LISTENING
   UDP 0.0.0.0:<discoveryPort>
   ```

4. 打开主机 `IF`，确认状态行包含：

   ```text
   channel host-listening
   ```

## 3. 平板进入 Play 界面

1. CmdBridge 读取平板当前界面：

   ```text
   status
   ui
   ```

2. 在**主菜单**点击“游玩”（`Play`，元素名是 `Play`，文本随语言变化）：

   ```text
   click Play
   ```

3. 确认进入：

   ```text
   screen: Play
   screenType: SuPlayScreen
   ```

4. **不要在新界面立刻点 `Play!`**：先等局域网探测完成，让服务器房间行进入列表。

## 4. 查找所有地图并识别服务器房间

1. 读取全部列表行（`ui` 现在**默认输出完整 text**；只有显式 `--compact` 才截断，
   且截断文本会带 `[truncated; use maps]` 标记，不能据此判断房间类型）：

   ```text
   ui --all
   # 可选：ui --all --compact 才使用旧的一行截断显示
   ```

2. 推荐直接用语义扫描命令（配对名字/完整详情，按 LAN/Ping/Online 判定 server-room）：

   ```text
   maps
   ```

   示例输出：

   ```text
   maps: count=1 playEnabled=True
   [1] name="Ery Terstates" kind=server-room ping=52ms
       details=3.4MB | 28 Oct 2026 04:45 | 1 player | Creative | Living | LAN (Ping 52ms)
   ```

3. 枚举所有 `WorldItem.Name`，不要只按名字判断。

3. **同名地图的判别规则**：

   - 本地世界行：详情是尺寸、日期、人数、模式、环境、版本；
   - 服务器房间行：详情里带**在线/延迟信息**，例如：

     ```text
     LAN（延迟 52 ms）
     ```

     或 `Service / Online (Ping N ms)`。

4. 客户端判断“这是服务器房间”的依据是**是否有 Ping / Online 信息**，不是地图名字。  
   `Ery Terstates` 可能同时对应本地世界和主机房间，点错就会进入本地世界。

5. 建议同时看 `Play!` 按钮是否可用：
   - 未选中任何行时，按钮不可用；
   - 选中服务器房间后，按钮才可用。

## 5. 选择目标地图

目标地图是 `Ery Terstates` 的服务器房间时：

### 情况 A：目标行未选中

- 点击对应的 `WorldItem.Name = Ery Terstates` 行；
- 再次读取 `ui` / `ui --all`；
- 确认该行仍在、且 `Play!` 变为可用；
- 如果存在两行同名，点击**带 Ping / LAN 的那一行**，不是本地世界行。

### 情况 B：目标行已选中

- 不重复点击地图行；
- 直接点击 `Play!`。

> 当前 CmdBridge 的 `ui` 输出不直接暴露 `ListPanelWidget.SelectedItem`；
> 实际判断只能依赖“选中某行后 `Play!` 是否可用”或日志。
> 这是当前流程的已知盲点，见第 7 节。

## 6. 点击 Play! 并验证加入

1. 点击 `Play!`：

   ```text
   click text:Play!
   ```

2. 确认出现加入对话框：

   ```text
   SuJoinBusyDialog
   ```

3. 确认平板 session 日志进入加入阶段：

   ```text
   session: ... stage=Connecting / AwaitingApproval / Downloading
   ```

4. 主机端会弹出 `JoinRequestDialog`。

5. 主机点 `Allow`：

   — 只有一个请求时：

   ```text
   click JoinRequestDialog.Allow
   ```

   — 同时存在多个请求时，`text:`/名称会命中多个元素。必须：
   - 先 `ui --all` 看完整路径；
   - 按完整路径点击，例如：

     ```text
     click "[GameScreen#0]/[JoinRequestDialog#2]/.../JoinRequestDialog.Allow"
     ```

   — 不要对多个请求盲点 `Allow`，先确认当前要放行的是哪一个客户端。

6. 验证主机已接受：

   - 平板的 `status` 从 `Play` 变为 `Game`，且 `worldLoaded: true`；
   - 主机日志出现客户端角色创建 / 客户端加入相关行；
   - 主机端 `players` / 联机角色状态出现对应客户端。

## 7. 当前已知盲点 / 需要确认的内容

1. **“点击游玩”的层级**
   - 主菜单的“游玩”= 打开 `SuPlayScreen`；
   - Play 屏里的 `Play!` = 进入选中的地图或加入选中的房间。
   需要明确每次指令说的是哪一个。

2. **如何可靠判断列表选中项**
   - `ui` / `maps` 目前不返回 `ListPanelWidget.SelectedItem`；
   - `maps` 会报告 `playEnabled`，但仍不能直接说明哪一行被选中；
   - 建议增加一个 CmdBridge 可读字段（当前选中行的 key / 名称 / 是否是房间），否则自动化容易点错同名世界。

3. **同名地图的最终区分**
   - 当前只能靠详情里的 Ping / LAN / Online 文本；
   - 是否要求 CmdBridge 直接返回“房间行 / 本地世界行”类型，而不是让操作者从文本推断？

4. **多个客户端同时申请加入**
   - 主机同时弹出两个 `JoinRequestDialog` 时，如何知道哪一个对应平板/Windows；
   - 需要按客户端名称/ID/身份显示并选择，而不是按弹窗顺序。

5. **加入失败后的处理**
   - 如果 `Play!` 点完没有进入 `SuJoinBusyDialog`，是继续等待房间发现，还是重新选行？
   - 当前流程建议先读 `session` 日志和房间列表，不自动重试。

6. **是否把本流程写成自动化脚本**
   - 目前每一步通过 CmdBridge 手动读取和点击；
   - 若要自动执行，需要先补上“选中项”和“目标客户端”的可查询状态，否则仍有同名/重复请求误判风险。
