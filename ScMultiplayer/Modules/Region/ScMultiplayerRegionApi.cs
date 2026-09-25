using Engine;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P2：领地数据的**跨程序集门面**（独立 mod / GmMod 只通过它读写）。
    ///
    /// ⚠️ 与 <see cref="RegionSelectionApi"/> 同理：跨程序集引用的是**名字**，
    /// 主类 `ScMultiplayer` 的成员会被 Obfuscar 改名，第三方 mod 直接引用会
    /// `MissingFieldException / MissingMethodException`。所以这里只暴露**静态、无虚方法**的门面，
    /// 在 `Obfuscar.xml` 里整类跳过（与 `DataModificationTool` 同一套路）。
    ///
    /// 口径（设计稿 §0 第三/四轮）：建立 / 放弃**只有主机/GM**可用；客户端调用只会拿到失败原因。
    /// </summary>
    public static class RegionClaimApi
    {
        public static bool IsAvailable => ScMultiplayer.currentInstance != null;

        /// <summary>本端是不是主机（只有主机能建立/放弃领地）。</summary>
        public static bool IsHostAuthority => ScMultiplayer.IsHost;

        public static int MaximumSizeXZ => ScMultiplayer.RegionClaimMaximumSizeXZ;

        public static int MaximumCount => ScMultiplayer.RegionClaimMaximumCount;

        /// <summary>本端持有的领地数量（主机=权威；客户端=主机下发的副本）。</summary>
        public static int Count
        {
            get
            {
                ScMultiplayer mod = ScMultiplayer.currentInstance;
                return mod != null ? mod.RegionClaimCount : 0;
            }
        }

        /// <summary>主机下发的单调序号（客户端副本用；面板/日志可显示）。</summary>
        public static long Sequence
        {
            get
            {
                ScMultiplayer mod = ScMultiplayer.currentInstance;
                return mod != null ? mod.RegionClaimSequence : 0L;
            }
        }

        /// <summary>按编号升序读取第 `index` 块领地的编号与一行描述（面板列表用）。</summary>
        public static bool TryDescribe(int index, out int id, out string text)
        {
            id = 0;
            text = string.Empty;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            return mod != null && mod.TryGetRegionClaimAt(index, out id, out text);
        }

        /// <summary>整表文本摘要（多行）。</summary>
        public static string DescribeAll()
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            return mod != null ? mod.DescribeRegionClaims() : "联机 Mod 未加载";
        }

        /// <summary>
        /// 把**当前选区**登记为领地（仅主机/GM）。失败时 `error` 是给玩家看的一行原因。
        /// </summary>
        public static bool TryCreateFromSelection(string name, out int id, out string error)
        {
            id = 0;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载，无法建立领地";
                return false;
            }
            if (!ScMultiplayer.IsHost)
            {
                error = "只有主机/GM 可以建立领地";
                return false;
            }
            if (!mod.TryGetRegionSelectionBox(out Point3 min, out Point3 max))
            {
                error = "选区未完成：点1/点2 未齐备";
                return false;
            }
            return mod.TryCreateRegionClaim(min, max, name,
                ScMultiplayer.GetLocalPlayerIdentity(), ScMultiplayer.GetLocalPlayerName(),
                out id, out error);
        }

        /// <summary>某块领地的拥有者数量（面板列拥有者用）。</summary>
        public static int OwnerCount(int claimId)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            return mod != null ? mod.RegionClaimOwnerCount(claimId) : 0;
        }

        /// <summary>读取某块领地第 `ownerIndex` 个拥有者：`userId` 用于剥夺，`display` 用于显示。</summary>
        public static bool TryDescribeOwner(int claimId, int ownerIndex, out string userId,
            out string display)
        {
            userId = string.Empty;
            display = string.Empty;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            return mod != null && mod.TryGetRegionClaimOwner(claimId, ownerIndex, out userId,
                out display);
        }

        /// <summary>把领地赋予某个在线客户端（仅主机/GM；可多人共享）。</summary>
        public static bool TryGrant(int claimId, int targetClientId, out string error)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载，无法赋予领地";
                return false;
            }
            return mod.TryGrantRegionClaim(claimId, targetClientId, out error);
        }

        /// <summary>剥夺某个 userId 对该领地的许可（仅主机/GM）。</summary>
        public static bool TryRevokeOwner(int claimId, string userId, out string error)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载，无法剥夺领地";
                return false;
            }
            return mod.TryRevokeRegionClaimOwner(claimId, userId, out error);
        }

        /// <summary>放弃整块领地（仅主机/GM）。</summary>
        public static bool TryRemove(int id, out string error)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载，无法放弃领地";
                return false;
            }
            return mod.TryRemoveRegionClaim(id, out error);
        }

        // ------------------------------------------------- P3b：客户端提交（走主机 DM 审核）

        /// <summary>
        /// 客户端把**当前选区**作为 `ScMP.Region.Claim` 提交给主机（主机审批后落地）。
        /// 主机端不要走这条（主机直接 `TryCreateFromSelection` 落地即可，设计稿 §4）。
        /// </summary>
        public static bool RequestCreateFromSelection(string name, out string error)
        {
            error = null;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载，无法提交建立请求";
                return false;
            }
            if (!mod.TryGetRegionSelectionBox(out Point3 min, out Point3 max))
            {
                error = "选区未完成：点1/点2 未齐备";
                return false;
            }
            return SubmitRegionOperation(RegionDataOperation.Claim, new RegionDataModificationRequest
            {
                MinX = min.X,
                MinY = ScMultiplayer.RegionClaimMinimumY,
                MinZ = min.Z,
                MaxX = max.X,
                MaxY = ScMultiplayer.RegionClaimMaximumY,
                MaxZ = max.Z,
                Name = name ?? string.Empty
            }, out error);
        }

        /// <summary>客户端请求放弃某块领地（主机审批后落地）。</summary>
        public static bool RequestRemove(int id, out string error) =>
            SubmitRegionOperation(RegionDataOperation.Drop,
                new RegionDataModificationRequest { RegionId = id }, out error);

        /// <summary>客户端请求把某块领地赋予某个在线客户端（主机审批后落地）。</summary>
        public static bool RequestGrant(int claimId, int targetClientId, out string error) =>
            SubmitRegionOperation(RegionDataOperation.Grant,
                new RegionDataModificationRequest { RegionId = claimId, TargetClientId = targetClientId },
                out error);

        /// <summary>客户端请求剥夺某个 userId 对某块领地的许可（主机审批后落地）。</summary>
        public static bool RequestRevoke(int claimId, string userId, out string error) =>
            SubmitRegionOperation(RegionDataOperation.Revoke,
                new RegionDataModificationRequest
                {
                    RegionId = claimId,
                    TargetUserId = userId ?? string.Empty
                }, out error);

        /// <summary>客户端查询领地表（调试/刷新用）。</summary>
        public static bool RequestList(out string error) =>
            SubmitRegionOperation(RegionDataOperation.List, new RegionDataModificationRequest(),
                out error);

        private static bool SubmitRegionOperation(string operation,
            RegionDataModificationRequest request, out string error)
        {
            error = null;
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                error = "联机 Mod 未加载";
                return false;
            }
            DataModificationSubmitResult result = mod.RequestRegionModification(operation, request);
            if (result != null && result.IsAccepted)
            {
                error = null;
                return true;
            }
            error = result == null ? "提交失败" : result.Details;
            return false;
        }

        // ---------------------------------------------------------------- P4：区域展示

        /// <summary>区域展示总开关（建立领地后默认显示）。</summary>
        public static bool IsDisplayEnabled =>
            ScMultiplayer.currentInstance?.RegionDisplayEnabled == true;

        /// <summary>选区预览线框开关（默认开）。</summary>
        public static bool IsSelectionPreviewEnabled =>
            ScMultiplayer.currentInstance?.RegionSelectionPreviewEnabled == true;

        public static bool ToggleDisplay() =>
            ScMultiplayer.currentInstance?.ToggleRegionDisplay() == true;

        public static bool ToggleSelectionPreview() =>
            ScMultiplayer.currentInstance?.ToggleRegionSelectionPreview() == true;

        /// <summary>
        /// 该格归属描述（重叠格归编号最小者）：`#3（Basil）` / `无主`。
        /// 面板的"当前所在地块归属"用它，也是 P5 执法的同一套裁决。
        /// </summary>
        public static string DescribeOwnerAt(int x, int y, int z)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            return mod != null ? mod.DescribeOwnerAt(x, y, z) : "（联机 Mod 未加载）";
        }
    }
}
