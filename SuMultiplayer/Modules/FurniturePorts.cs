using System;
using System.Collections.Generic;
using SuMultiplayer.Furniture;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 引擎外壳（SubsystemFurnitureBlockBehavior / SubsystemHammerBlockBehavior /
    /// SubsystemBucketBlockBehavior / FurnitureBlock）注入的**运行期角色与计数出口**。
    ///
    /// 外壳只读这一个接口：判断"本端是不是联机客户端副本"（决定 Save 是否写空、
    /// Paint / GetAdHocCraftingRecipe 是否允许本地执行），以及把"客户端缺设计格 /
    /// 客户端被拦下的设计新增"如实计数。没有静态状态。
    /// </summary>
    public interface IFurnitureEngineEvents
    {
        /// <summary>本端 = 联机客户端副本（主机 / 单机恒 false）。</summary>
        bool IsMultiplayerClient { get; }

        /// <summary>客户端 Save 把家具设计表写空（明确计数；不是静默丢弃）。</summary>
        void NoteClientSaveSuppressed(int designCount);

        /// <summary>客户端地形里的家具格引用了本地设计表里不存在的设计（明确计数）。</summary>
        void NoteMissingDesignCell(int designIndex);

        /// <summary>客户端直接调到 FurnitureBlock.GetAdHocCraftingRecipe（会新增设计）→ 拦下并计数。</summary>
        void NoteClientCraftBlocked();
    }

    /// <summary>
    /// 客户端地形编辑的家具设计闸门（注入 <c>SuSubsystemTerrain</c>）：
    /// 客户端把一格改成 contents=227 时，若 data 里的设计索引不在"主机已下发"的副本表里，
    /// 这条编辑不得提交给主机（否则主机会按自己的设计槽位解释成另一个设计）。
    /// </summary>
    public interface IClientFurnitureDesignGuard
    {
        bool IsReplicaDesign(int designIndex);

        /// <summary>本会话的设计表是否已经由主机同步过（未同步时不能因为"还不知道"就丢编辑）。</summary>
        bool IsSynced { get; }
    }

    /// <summary>
    /// 引擎外壳 -> 模块的提交入口（实现 = Modules/FurnitureSyncModule.cs）。
    /// 返回 true = 请求已入队发送；调用方**不得**再执行本地设计新增。
    /// </summary>
    public interface IFurnitureDesignRequestSink
    {
        bool TrySubmitDesignRequest(in FurnitureDesignRequestData request, out string reason);

        bool IsReplicaDesign(int designIndex);

        bool IsSynced { get; }
    }

    /// <summary>
    /// 家具同步的引擎适配端口（实现 = Engine/FurnitureReplicaEngine.cs）。
    ///
    /// 模块只做协议 / 序号 / 去重 / 批量 / 超时 / 计数 / 广播；所有 Game 类型操作都在实现里：
    ///   · 主机：抓取权威设计表指纹 / 记录，校验并落地设计请求（安装设计、销毁源方块、
    ///     生成家具物品、上色改格），轮询变化由模块驱动；
    ///   · 客户端：接管已加载设计为副本、按主机索引安装 / 移除副本、扫除本地新增
    ///     （library leak）、刷新家具库存内容、显示明确失败消息。
    ///
    /// 用 <c>in</c> 传值类型；记录是不可变类；**没有静态状态**。
    /// </summary>
    public interface IFurniturePort
    {
        /// <summary>模块在 Attach 时写入自己，Detach 时写 null（外壳 → 请求的唯一入口）。</summary>
        void SetRequestSink(IFurnitureDesignRequestSink sink);

        /// <summary>每帧一次（模块 Tick 调用）：找 Project / 外壳并注入运行期角色。</summary>
        void Pump();

        /// <summary>Block 数量上限（= BlocksManager.Blocks.Length；结构校验用）。</summary>
        int BlockCount { get; }

        /// <summary>本端是不是联机客户端副本（由 <c>SuSubsystemTerrain.ClientReplica</c> 决定）。</summary>
        bool IsClientReplica { get; }

        /// <summary>客户端世界是否已经加载到可以安装设计（Project + 外壳 + 地形就绪）。</summary>
        bool IsWorldReady { get; }

        /// <summary>Project 换代序号（模块据此清空主机的设计基线 / 客户端的副本表）。</summary>
        int SessionSerial { get; }

        // ------------------------------------------------------------------ 主机

        /// <summary>权威设计表的轻量指纹（只读元数据；变化时才抓完整记录）。</summary>
        bool TryCaptureHostFingerprint(out long fingerprint, out int count, out string reason);

        /// <summary>抓取完整权威设计表（最多 <paramref name="max"/> 条；超出说明设计表异常，明确失败）。</summary>
        int CaptureHostDesigns(List<FurnitureDesignRecord> target, int max,
            out long fingerprint, out string reason);

        /// <summary>
        /// 校验并落地一次客户端设计请求。返回 false = 明确拒绝（reason 必有值）。
        /// 成功时 <paramref name="result"/> 带主机索引 / 内容哈希 / 是否重复 / 是否生成物品。
        /// </summary>
        bool TryApplyHostDesignRequest(int clientId, in FurnitureDesignRequestData request,
            out FurnitureDesignResultData result, out FurnitureDesignRecord installed,
            out string reason);

        // ------------------------------------------------------------------ 客户端

        /// <summary>
        /// 把客户端世界里**已经加载**的设计接管为副本（首次识别为联机客户端时调用一次）。
        /// 返回条数；失败时 reason 必有值（不猜空表）。
        /// </summary>
        bool TryAdoptClientLibrary(List<FurnitureDesignRecord> target, out string reason);

        /// <summary>按主机索引把一条记录装进引擎设计表槽位（不调用 TryAddDesign，不写存档）。</summary>
        bool TryInstallReplica(FurnitureDesignRecord record, out string reason);

        /// <summary>从引擎设计表槽位移除一条副本设计（把 Index 置 -1）。</summary>
        bool TryRemoveReplica(int index, out string reason);

        /// <summary>
        /// 副本完整性：槽位被引擎 GC / 别的路径清掉时按记录重装；并把副本设计的
        /// terrainUseCount 保持在 1（否则家具面板 FillSlots 的 GC 会删掉未摆放的设计）。
        /// 返回重装条数。
        /// </summary>
        int EnsureReplicaIntegrity(IReadOnlyList<FurnitureDesignRecord> records,
            List<int> reinstalled);

        /// <summary>
        /// 客户端库泄漏扫除：移除引擎设计表里**不属于主机副本表**的设计。
        /// 返回移除条数，索引写进 <paramref name="removed"/>。
        /// </summary>
        int SweepClientLibraryLeaks(IReadOnlyCollection<int> allowedIndices,
            List<int> removed);

        /// <summary>按当前引擎设计表重新填充本机玩家的家具库存（ComponentFurnitureInventory.FillSlots）。</summary>
        bool TryRefreshFurnitureInventory(out int slotCount, out string reason);

        /// <summary>抓本机家具库存的槽位设计索引（诊断 / 自检口径；去重）。</summary>
        bool TryCaptureFurnitureInventory(List<int> indices, out string reason);

        /// <summary>在客户端玩家 GUI 上显示一条明确的失败 / 状态消息（拿不到玩家时返回 false）。</summary>
        bool TryShowClientMessage(string text);

        /// <summary>模块下线 / 组合根卸载：摘掉外壳注入并清空会话状态（不触碰别的模块）。</summary>
        void Shutdown();

        string Describe();
    }
}
