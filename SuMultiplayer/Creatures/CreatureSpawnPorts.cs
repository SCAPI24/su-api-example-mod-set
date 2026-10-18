using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Creatures
{
    /// <summary>
    /// 生物世界的引擎端口（由 <c>Engine/CreatureWorldAdapter</c> 实现；自检工程用假世界实现）。
    ///
    /// 设计原则：模块只做"哪些生物、什么时候、发给谁"；**创建/删除/施加状态这些引擎动作
    /// 全部在这个端口后面**。端口拿不到 / 返回 false 时必须写清原因并计数，绝不静默成功。
    /// </summary>
    public interface ICreatureWorld
    {
        /// <summary>
        /// 每帧开始时调用：绑定引擎里的两个生成子系统、设置"客户端只做复制"的抑制开关，
        /// 并在 Project 换新时丢弃旧的复制体引用。返回 false = 当前没有可操作的世界（原因写清）。
        /// </summary>
        bool BeginTick(bool isHost, bool clientReplicaMode, double now, out string reason);

        /// <summary>
        /// 当前是否真的有一个可操作的 Project（世界已加载且没有被换新/卸载）。
        /// 客户端在**世界传输/导入期间**会先收到主机的生物全量快照——这时不能逐条去创建
        /// 复制体（旧实现在这里逐条失败，实测 createFail=50 / orphanUpdate=550）；
        /// 模块用它把整批记录延后，等世界就绪后再请求一次全量快照。
        /// </summary>
        bool HasWorld { get; }

        /// <summary>
        /// 告知端口"主机分配给本端的区域半径"（米）。适配器用它做**可见性对账**：
        /// 复制体是否真的落在本机玩家周围这个半径内（典型故障：主机侧联机角色位置过期，
        /// 复制体全都落在 150 米外 → 计数器看着正常、屏幕上一只也没有）。
        /// 只影响诊断计数/日志，不改变分配规则（分配仍由主机按 68m/26 计算）。
        /// </summary>
        void ConfigureClientRegion(float radiusMeters);

        /// <summary>
        /// 主机：枚举世界里所有**非玩家生物**（ComponentCreature 且没有 ComponentPlayer），
        /// 写进 <paramref name="target"/>（Token/TemplateName/位置/姿态/速度/生命）。
        /// 返回写入条数；-1 = 失败（原因写清）。
        /// </summary>
        int CaptureHostCreatures(List<CreatureStateRecord> target, out string reason);

        /// <summary>
        /// 主机：按旧实现的远端维持逻辑，在客户端角色周围补足生物（每客户端上限 26，半径 68 米）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:869-971
        /// </summary>
        void MaintainHostPopulation(IReadOnlyList<CreatureRegion> regions, double now, out string reason);

        /// <summary>
        /// 客户端：按主机记录创建复制体（同 key 已存在时等价于施加状态，天然幂等）。
        /// 创建时必须摘掉本地 AI/移动/生命/变形/生成状态机的 Updateable（旧实现同一处理）。
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:EnsureRemoteAnimal
        /// </summary>
        bool TryCreateReplica(CreatureStateRecord record, out string reason);

        /// <summary>
        /// 客户端：把主机记录里的**非表现层**状态写到已有复制体上（生命值、模板变化 → 换实体）。
        /// 位置/姿态/速度**不在这里写**：它们由 <see cref="TryApplyReplicaPresentation"/> 每帧
        /// 用插值/外推结果驱动（收到记录就硬写位置 = 10 Hz 抖动，实测问题正是这个）。
        /// </summary>
        bool TryApplyReplicaState(CreatureStateRecord record, out string reason);

        /// <summary>
        /// 客户端：每帧把插值/外推得到的表现位置、姿态、速度写进复制体。
        /// body 不存在 / 已随 Project 销毁 → false + 原因（模块丢掉引用并请求全量）。
        /// </summary>
        bool TryApplyReplicaPresentation(int key, Vector3 position, Quaternion rotation,
            Vector3 velocity, out string reason);

        /// <summary>客户端：删除指定 key 的复制体。</summary>
        bool TryRemoveReplica(int key, out string reason);

        /// <summary>
        /// 客户端：保留 <paramref name="keepKeys"/> 里的复制体，删除其余生物实体
        /// （= 引擎自己随机生成出来的、或主机已经不再分配给本端的）。
        /// 返回删除条数。
        /// </summary>
        int TrimReplicasTo(ICollection<int> keepKeys, out string reason);
    }

    /// <summary>
    /// 主机侧"客户端区域"来源：clientId → 该客户端的联机角色位置。
    /// 由联机角色模块的 NetworkAvatarSpawner 提供（不是按 index 或区块中心猜）。
    /// </summary>
    public interface ICreatureRegionSource
    {
        /// <summary>当前所有玩家端（Debug 只读端不在其中）。</summary>
        IReadOnlyList<int> ClientIds { get; }

        /// <summary>
        /// 该玩家端联机角色在世界里的真实位置。角色还没建好 / 没有 ComponentBody → false + 原因；
        /// **绝不返回一个猜的位置**。
        /// </summary>
        bool TryGetClientPosition(int clientId, out Vector3 position, out string reason);
    }

    /// <summary>主机发送出口（模块把传输端口包成它；自检工程可直接记录/断言）。</summary>
    public interface ICreatureSender
    {
        /// <summary>
        /// 只发给指定玩家端。返回 false = 这一帧发不进去（背压 / 连接不可用）：
        /// 调用方必须保留"未确认发送"的集合，下一帧重试，绝不假装已发送。
        /// </summary>
        bool TrySendTo(int clientId, byte[] payload);
    }
}
