using System;
using System.Collections.Generic;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

using SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 引擎侧"客户端副本 / 主机权威"端口（实现方 = Engine/ClientReplicaEngine.cs，组合根注入）。
    ///
    /// 为什么做成端口而不是让模块直接引用引擎：模块层（Modules/）保持不依赖 Game，
    /// 这样端口契约、编解码与策略仍能被 P:\tmp\tsverify 编译/自检；引擎能力只在组合根装配。
    /// </summary>
    public interface IClientEditPort
    {
        /// <summary>
        /// 主线程上捕获到的**客户端本地地形写入**（引擎 `m_modifiedCells` 里的每一格）。
        /// 引擎侧在 <c>SuSubsystemTerrain.Update</c> 的 ProcessModifiedCells 之前抛；
        /// 模块在同一个线程里把它变成 CellEditRequest 发出去（不跨线程、不排队到下一帧）。
        /// </summary>
        event Action<ClientCellEdit> LocalEditCaptured;

        /// <summary>读主机侧当前格值（校验 / already 判定用）。</summary>
        bool TryReadCell(int x, int y, int z, out int contents, out int data);

        /// <summary>主机侧：区块内容是否可用（ChangeCell 的落地前提）。</summary>
        bool IsChunkLoaded(int chunkX, int chunkZ);

        /// <summary>
        /// 主机侧：按**权威路径**落地（引擎里 = SubsystemTerrain.ChangeCell，会跑方块行为并进改动表，
        /// 因此下一步的既有 CellChange 广播会把结果发给所有客户端）。
        /// 方块被销毁时同时补生成掉落（口径见 <see cref="EditApplyOutcome"/> / <see cref="EditDropLedger"/>）。
        /// </summary>
        bool TryApplyHostEdit(int clientId, int x, int y, int z, int contents, int data,
            int toolLevel, bool hasToolLevel,
            out EditApplyOutcome outcome, out string reason);

        /// <summary>客户端侧：把一格恢复成主机权威值（拒绝 / 超时回滚；不触发行为、不进改动表）。</summary>
        bool TryApplyReplicaValue(int x, int y, int z, int contents, int data);

        /// <summary>主机侧：该客户端当前兴趣窗口（区块中心 + 半径）；false = 还没上报中心。</summary>
        bool TryGetClientRange(int clientId, out int centerChunkX, out int centerChunkZ, out int radiusChunks);

        /// <summary>
        /// 主机侧：处理一条原始编辑请求（解析 → 每连接去重 → 校验 → 权威落地）。
        /// 返回裁决；调用方据它编码 CellEditResult（重复序号返回上一次裁决，幂等）。
        /// </summary>
        ClientEditDecision HandleEditRequest(int clientId, byte[] payload, bool isPlayerPeer,
            bool hasRange, int centerChunkX, int centerChunkZ, int radiusChunks);

        /// <summary>强类型入口（消息层已经解析好，避免重复解析）。</summary>
        ClientEditDecision HandleEditRequest(int clientId, SuMultiplayer.Protocol.Authority.CellEditRequest request,
            bool isPlayerPeer, bool hasRange, int centerChunkX, int centerChunkZ, int radiusChunks);

        /// <summary>编辑裁决统计（requests/accepted/rejected/...；限流日志用）。</summary>
        string EditStats { get; }

        /// <summary>
        /// 客户端捕获统计（staged / delivered / deferredFrames / pending / 每种丢弃原因）。
        /// 捕获阶段在引擎侧，模块只消费事件；把这份计数拼进状态行，丢格时能直接定位是哪一级。
        /// </summary>
        string CaptureStats { get; }

        /// <summary>连接建立 / 断开：清掉该连接的序号去重状态（重连 = 新 clientId，序号从 1 开始）。</summary>
        void ResetEditClient(int clientId);

        /// <summary>本端是否是联机客户端副本（供模块更新闸门 / 诊断；引擎侧同一事实）。</summary>
        bool IsClientReplica { get; }

        /// <summary>
        /// 客户端本机**当前有效交互距离**（引擎 ComponentMiner.Raycast 的 reach：
        /// 创造 = SettingsManager.CreativeReach，生存 = 5；已夹到可发送的合理范围）。
        /// 每次编辑请求都带上它，主机按 min(reach + 容差, 主机上限) 校验。
        /// </summary>
        float LocalReach { get; }
    }

    /// <summary>
    /// 客户端"强制核对某块"端口（编辑请求超时后的回滚通道）：
    /// 客户端把该块重新算摘要上报，主机比对不一致就回传整块 —— 于是这一格回到主机最后的值。
    /// 与"静默等下一次周期校验"的区别：这是**明确触发**的回滚，并且会计数 / 写一行原因。
    /// </summary>
    public interface IChunkReverifyPort
    {
        /// <summary>false = 这块不在客户端内存集合（没什么可回滚的）或还没有同步客户端。</summary>
        bool RequestReverification(int chunkX, int chunkZ);
    }

    /// <summary>引擎侧世界信息端口：主机抓快照 / 客户端应用快照。</summary>
    public interface IWorldInfoPort
    {
        /// <summary>主机：从引擎当前状态抓一份快照（时间 / 天气 / 雾 / 闪电 / WorldSettings）。</summary>
        bool TryCaptureHost(out WorldInfoSnapshot snapshot);

        /// <summary>
        /// 客户端：把主机快照应用到引擎（GameInfo 时钟 + TimeOfDayOffset + WorldSettings + Weather 字段 +
        /// 闪电表现）。硬套时钟只在漂移超过阈值时做，避免 2 Hz 覆盖造成抖动。
        /// </summary>
        void ApplyClient(WorldInfoSnapshot snapshot);

        long AppliedCount { get; }
    }

    /// <summary>引擎侧随机数状态端口（随世界 pak 下发；客户端每个 Project 只应用一次）。</summary>
    public interface IRandomStatePort
    {
        /// <summary>主机会话种子（非 0；建房时生成，只在内存里）。</summary>
        int SessionSeed { get; }

        /// <summary>主机：抓所有 Subsystems 上的 Engine.Random 状态（键 = 类型全名|字段名）。</summary>
        Dictionary<string, long> CaptureHostStates();

        /// <summary>客户端：登记主机下发的种子 + 状态表；由 <see cref="PumpApply"/> 在 Project 就绪后应用一次。</summary>
        void ApplyClientStates(int seed, IReadOnlyDictionary<string, long> states);

        /// <summary>每个 tick 调用：Project 换新时应用一次（同一 Project 绝不重复应用）。</summary>
        void PumpApply();

        bool AppliedForCurrentProject { get; }
        long AppliedFields { get; }
        long MissingFields { get; }
    }

    /// <summary>
    /// 一条在线玩家端 + 它的**稳定用户键**。
    ///
    /// 用户键 = <see cref="SuMultiplayer.Terrain.PlayerSessionKeys"/> 派生的 userid 键
    /// （有身份 = <c>u</c>+FNV-1a；没有身份 = <c>c</c>+clientId 的明确退化），**不是传输编号**：
    /// clientId 每次连接都会重分配（重连即变），拿它当位序会让上一轮的数据投给下一个人。
    ///
    /// mask 位序 = 调用方（实体复制模块）按用户键分配的**槽位**，槽位在一次主机会话内
    /// 不复用（见 <c>Net/EntityInterestNet.cs</c>），所以一个 bit 永远只代表同一个用户。
    /// </summary>
    public readonly struct PlayerPeerUser
    {
        public PlayerPeerUser(int clientId, string userKey)
        {
            ClientId = clientId;
            UserKey = userKey ?? string.Empty;
        }

        /// <summary>当前这次连接的传输句柄（重连会变）。</summary>
        public int ClientId { get; }

        /// <summary>稳定用户键（mask 的正确位序依据）。</summary>
        public string UserKey { get; }
    }

    /// <summary>
    /// 主机侧向**全部玩家端**广播（世界信息 2 Hz）。与 ITransportPort.TrySend 的区别：
    /// 后者只发给"最后加入的那个"，多客户端时另一个永远收不到。
    /// </summary>
    public interface IPeerBroadcastPort
    {
        /// <summary>发给所有在线玩家端（调试端不算）；返回成功入队的端数。</summary>
        int BroadcastToPlayers(MessageType type, byte[] payload);

        /// <summary>
        /// 发给所有在线玩家端，但**跳过 <paramref name="excludeClientId"/>**；返回成功入队的端数。
        /// 语义与 <see cref="BroadcastToPlayers"/> 相同，只多一个排除项 —— 皮肤资源转发用它保证
        /// "发起者不收资源、只收回执"（资源绝不能发回换皮肤的那个客户端）。
        /// excludeClientId &lt; 0 = 不排除任何人（等价于全广播）。
        ///
        /// 默认实现只支持 excludeClientId &lt; 0；需要真正排除时必须由实现方覆盖
        /// （**不做"忽略排除项照发"的静默兜底** —— 那会把皮肤资源发回发起者）。
        /// </summary>
        int BroadcastToPlayersExcept(int excludeClientId, MessageType type, byte[] payload) =>
            excludeClientId < 0
                ? BroadcastToPlayers(type, payload)
                : throw new NotSupportedException(
                    "IPeerBroadcastPort 的实现没有提供 BroadcastToPlayersExcept：不能安全地排除发起者");

        /// <summary>
        /// 按 clientId 升序抓取当前在线玩家端与它们的稳定用户键（mask 位序的依据，见
        /// <see cref="PlayerPeerUser"/>）。窗口/身份都还没就绪的连接不会出现在结果里
        /// （调用方计数，不退化成按 clientId 投递）。
        /// </summary>
        int CapturePlayerUsers(List<PlayerPeerUser> target) =>
            throw new NotSupportedException(
                "IPeerBroadcastPort 的实现没有提供 CapturePlayerUsers：不能按稳定用户键投递");

        /// <summary>
        /// 按 mask 发给对应玩家端（位序 = <paramref name="roster"/> 下标，同一份 payload，一次编码）。
        /// <paramref name="deliveredMask"/> 回填**实际入队**的位 —— 没入队的位由调用方保留重试，
        /// 绝不"当成功"地把它们从待发里删掉。返回成功入队端数。
        /// </summary>
        int BroadcastToPlayersMasked(MessageType type, byte[] payload,
            IReadOnlyList<PlayerPeerUser> roster, ulong mask, out ulong deliveredMask) =>
            throw new NotSupportedException(
                "IPeerBroadcastPort 的实现没有提供 BroadcastToPlayersMasked：不能按用户掩码投递");

        /// <summary>当前在线玩家端数量（诊断 / 世界信息按需发送用）。</summary>
        int PlayerPeerCount { get; }

        /// <summary>这个 clientId 是不是玩家端（调试端只读，不参与编辑裁决 / 世界信息）。</summary>
        bool IsPlayerClient(int clientId);

        bool IsHosting { get; }
        bool IsConnected { get; }
    }
}
