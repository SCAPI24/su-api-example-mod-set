using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScMultiplayer
{
    /// <summary>《玩家领地》P2：领地的拥有者（按 **userId** 跟随，不用 clientId；见设计稿 §1）。</summary>
    public sealed class RegionClaimOwner
    {
        /// <summary>= `UserManager.ActiveUser.UniqueId`（账号 userid）/ 记录键；无身份时为 `name:名字`。</summary>
        public string UserId = string.Empty;
        public string Name = string.Empty;

        public RegionClaimOwner Clone() =>
            new RegionClaimOwner { UserId = UserId ?? string.Empty, Name = Name ?? string.Empty };
    }

    /// <summary>
    /// 《玩家领地》P2：一块领地（轴对齐长方体，整数格**闭区间**；Y 默认整高 0–255）。
    /// 主机持有权威副本；客户端持有只读副本（`RegionClaimMessage` 下发）。
    /// </summary>
    public sealed class RegionClaim
    {
        public int Id;
        public int MinX;
        public int MinY;
        public int MinZ;
        public int MaxX;
        public int MaxY;
        public int MaxZ;
        public string CreatedUtc = string.Empty;
        public string Name = string.Empty;
        public string Flags = string.Empty;
        public readonly List<RegionClaimOwner> Owners = new List<RegionClaimOwner>();

        public int SizeX => MaxX - MinX + 1;
        public int SizeY => MaxY - MinY + 1;
        public int SizeZ => MaxZ - MinZ + 1;

        /// <summary>格子在领地内（闭区间；设计稿 §1 的 `OwnerAt(cell)` 用它）。</summary>
        public bool Contains(int x, int y, int z) =>
            x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ;

        public bool Contains(Point3 cell) => Contains(cell.X, cell.Y, cell.Z);

        /// <summary>拥有者名字（"未指定" 表示还没有赋予任何人）。</summary>
        public string DescribeOwners()
        {
            if (Owners.Count == 0)
                return "未指定";
            var text = new StringBuilder();
            for (int i = 0; i < Owners.Count; i++)
            {
                if (i > 0)
                    text.Append('、');
                string name = Owners[i].Name;
                text.Append(string.IsNullOrWhiteSpace(name) ? Owners[i].UserId : name);
            }
            return text.Length == 0 ? "未指定" : text.ToString();
        }

        public string DescribeBounds() =>
            "X " + MinX + ".." + MaxX + " / Z " + MinZ + ".." + MaxZ + " / Y " + MinY + ".." + MaxY;

        public string Describe() =>
            "#" + Id + "  " + DescribeBounds() + "（" + SizeX + "×" + SizeZ +
            "，拥有者：" + DescribeOwners() + "）";

        public RegionClaim Clone()
        {
            var clone = new RegionClaim
            {
                Id = Id,
                MinX = MinX,
                MinY = MinY,
                MinZ = MinZ,
                MaxX = MaxX,
                MaxY = MaxY,
                MaxZ = MaxZ,
                CreatedUtc = CreatedUtc ?? string.Empty,
                Name = Name ?? string.Empty,
                Flags = Flags ?? string.Empty
            };
            foreach (RegionClaimOwner owner in Owners)
                clone.Owners.Add(owner.Clone());
            return clone;
        }
    }

    /// <summary>
    /// 《玩家领地》P2：领地数据结构 + 主机侧增删（**暂不含执法**，执法是 P5–P7）。
    ///
    /// 锁定口径（设计稿 §1 / §0 第三、四轮）：
    ///   · 编号世界内自增（从 1 开始）、全局唯一；拥有者是 **userId 列表**（可多人）；
    ///   · 单体上限 **64×64×256**（X/Z 各自 ≤64，矩形也行），Y 固定 0–255；
    ///   · 世界上限 **默认 128 块**（不设每人上限）；**允许重叠**（重叠格归编号最小者，P4/P5 用）；
    ///   · 增删只在 **GM/主机**（客户端只能提交请求，P3 起走 DM 审核通道）；
    ///   · 每次改动推进**单调序号**并广播增量（客户端按序号应用）。
    /// </summary>
    public partial class ScMultiplayer
    {
        public const int RegionClaimMaximumSizeXZ = 64;
        public const int RegionClaimMaximumCount = 128;
        public const int RegionClaimMinimumY = 0;
        public const int RegionClaimMaximumY = 255;

        private readonly List<RegionClaim> m_regionClaims = new List<RegionClaim>();
        private int m_regionClaimNextId = 1;
        private long m_regionClaimSequence;
        private string m_lastRegionClaimError = string.Empty;
        // P4：区域展示开关（默认开——"建立领地后默认显示"）；选区预览线框单独开关（默认开）
        private bool m_regionDisplayEnabled = true;
        private bool m_regionSelectionPreviewEnabled = true;

        /// <summary>本端持有的领地数量（主机=权威，客户端=副本）。</summary>
        public int RegionClaimCount => m_regionClaims.Count;

        /// <summary>主机侧的单调序号（客户端副本记录主机最近下发的序号）。</summary>
        public long RegionClaimSequence => m_regionClaimSequence;

        public int RegionClaimNextId => m_regionClaimNextId;

        public string LastRegionClaimError => m_lastRegionClaimError;

        public RegionClaim FindRegionClaim(int id)
        {
            for (int i = 0; i < m_regionClaims.Count; i++)
            {
                if (m_regionClaims[i].Id == id)
                    return m_regionClaims[i];
            }
            return null;
        }

        /// <summary>按显示顺序读取一块领地（面板用：`index` 从 0 开始，按编号升序）。</summary>
        public bool TryGetRegionClaimAt(int index, out int id, out string description)
        {
            id = 0;
            description = string.Empty;
            List<RegionClaim> ordered = GetRegionClaimsOrdered();
            if (index < 0 || index >= ordered.Count)
                return false;
            id = ordered[index].Id;
            description = ordered[index].Describe();
            return true;
        }

        /// <summary>按编号升序的副本（显示/遍历用；**不要**改它）。</summary>
        public List<RegionClaim> GetRegionClaimsOrdered()
        {
            var ordered = new List<RegionClaim>(m_regionClaims);
            ordered.Sort((left, right) => left.Id.CompareTo(right.Id));
            return ordered;
        }

        // ---------------------------------------------------------------- 主机侧增删

        /// <summary>
        /// 建立领地（**仅主机/GM**）。`a`/`b` 是选区两点（X/Z 决定矩形，Y 强制整高）。
        /// 重叠不拒绝（锁定结论 3）；超上限 / 超数量上限 → 返回 false + 原因。
        /// </summary>
        public bool TryCreateRegionClaim(Point3 a, Point3 b, string name,
            string ownerUserId, string ownerName, out int claimId, out string error)
        {
            claimId = 0;
            if (!IsHost)
            {
                error = "只有主机/GM 可以建立领地";
                m_lastRegionClaimError = error;
                return false;
            }
            int minX = Math.Min(a.X, b.X);
            int maxX = Math.Max(a.X, b.X);
            int minZ = Math.Min(a.Z, b.Z);
            int maxZ = Math.Max(a.Z, b.Z);
            // Source: 设计稿 §1「单体上限 64×64×256」：高度固定 0–255，只校验 X/Z。
            int sizeX = maxX - minX + 1;
            int sizeZ = maxZ - minZ + 1;
            if (sizeX <= 0 || sizeZ <= 0)
            {
                error = "选区为空";
                m_lastRegionClaimError = error;
                return false;
            }
            if (sizeX > RegionClaimMaximumSizeXZ || sizeZ > RegionClaimMaximumSizeXZ)
            {
                error = "超出单体上限 " + RegionClaimMaximumSizeXZ + "×" +
                    RegionClaimMaximumSizeXZ + "（当前 " + sizeX + "×" + sizeZ + "）";
                m_lastRegionClaimError = error;
                return false;
            }
            if (m_regionClaims.Count >= RegionClaimMaximumCount)
            {
                error = "已达世界上限 " + RegionClaimMaximumCount + " 块领地";
                m_lastRegionClaimError = error;
                return false;
            }
            var claim = new RegionClaim
            {
                Id = m_regionClaimNextId,
                MinX = minX,
                MinY = RegionClaimMinimumY,
                MinZ = minZ,
                MaxX = maxX,
                MaxY = RegionClaimMaximumY,
                MaxZ = maxZ,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Name = name ?? string.Empty,
                Flags = string.Empty
            };
            if (!string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerName))
            {
                claim.Owners.Add(new RegionClaimOwner
                {
                    UserId = ownerUserId ?? string.Empty,
                    Name = ownerName ?? string.Empty
                });
            }
            m_regionClaims.Add(claim);
            m_regionClaimNextId = claim.Id + 1;
            m_regionClaimSequence++;
            m_lastRegionClaimError = string.Empty;
            MarkHostRegionClaimsDirty();
            BroadcastRegionClaimChange(claim, RegionClaimOperation.Add);
            error = null;
            claimId = claim.Id;
            return true;
        }

        /// <summary>放弃整块领地（**仅主机/GM**）：移除记录，玩家许可随之失效。</summary>
        public bool TryRemoveRegionClaim(int id, out string error)
        {
            if (!IsHost)
            {
                error = "只有主机/GM 可以放弃领地";
                m_lastRegionClaimError = error;
                return false;
            }
            RegionClaim claim = FindRegionClaim(id);
            if (claim == null)
            {
                error = "没有编号为 #" + id + " 的领地";
                m_lastRegionClaimError = error;
                return false;
            }
            m_regionClaims.Remove(claim);
            m_regionClaimSequence++;
            m_lastRegionClaimError = string.Empty;
            MarkHostRegionClaimsDirty();
            BroadcastRegionClaimChange(claim, RegionClaimOperation.Remove);
            error = null;
            return true;
        }

        /// <summary>把改动后的记录替换回副本（P3 的赋予/剥夺用；P2 只用于补发快照前的整表刷新）。</summary>
        internal void ReplaceRegionClaimRecord(RegionClaim claim)
        {
            if (claim == null)
                return;
            for (int i = 0; i < m_regionClaims.Count; i++)
            {
                if (m_regionClaims[i].Id != claim.Id)
                    continue;
                m_regionClaims[i] = claim.Clone();
                return;
            }
            m_regionClaims.Add(claim.Clone());
        }

        internal void ClearRegionClaimRecords()
        {
            m_regionClaims.Clear();
        }

        // ---------------------------------------------------------------- P3：拥有者（赋予 / 剥夺）

        public int RegionClaimOwnerCount(int claimId) => FindRegionClaim(claimId)?.Owners.Count ?? 0;

        /// <summary>读取某块领地的第 `ownerIndex` 个拥有者（面板列拥有者用）。</summary>
        public bool TryGetRegionClaimOwner(int claimId, int ownerIndex, out string userId,
            out string display)
        {
            userId = string.Empty;
            display = string.Empty;
            RegionClaim claim = FindRegionClaim(claimId);
            if (claim == null || ownerIndex < 0 || ownerIndex >= claim.Owners.Count)
                return false;
            RegionClaimOwner owner = claim.Owners[ownerIndex];
            userId = owner.UserId ?? string.Empty;
            display = string.IsNullOrWhiteSpace(owner.Name) ? userId : owner.Name;
            return true;
        }

        /// <summary>
        /// 把领地赋予某个客户端（**仅主机/GM**；设计稿 §2「一个领地可赋予多个玩家」）。
        /// 拥有者按 **userId** 记录（账号 userid），不用 clientId —— 换设备/换 clientId 后归属不变。
        /// </summary>
        public bool TryGrantRegionClaim(int claimId, int targetClientId, out string error)
        {
            if (!IsHost)
            {
                error = "只有主机/GM 可以赋予领地";
                m_lastRegionClaimError = error;
                return false;
            }
            RegionClaim claim = FindRegionClaim(claimId);
            if (claim == null)
            {
                error = "没有编号为 #" + claimId + " 的领地";
                m_lastRegionClaimError = error;
                return false;
            }
            if (!TryResolveRegionClaimOwnerIdentity(targetClientId, out string userId,
                    out string name))
            {
                error = "拿不到该玩家的身份（不在线？）";
                m_lastRegionClaimError = error;
                return false;
            }
            for (int i = 0; i < claim.Owners.Count; i++)
            {
                if (string.Equals(claim.Owners[i].UserId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    error = name + " 已经是这块领地的拥有者";
                    m_lastRegionClaimError = error;
                    return false;
                }
            }
            claim.Owners.Add(new RegionClaimOwner { UserId = userId, Name = name });
            m_regionClaimSequence++;
            m_lastRegionClaimError = string.Empty;
            MarkHostRegionClaimsDirty();
            PublishRegionClaimReplace(claim);
            error = null;
            return true;
        }

        /// <summary>剥夺某个 userId 对该领地的许可（**仅主机/GM**）。</summary>
        public bool TryRevokeRegionClaimOwner(int claimId, string userId, out string error)
        {
            if (!IsHost)
            {
                error = "只有主机/GM 可以剥夺领地";
                m_lastRegionClaimError = error;
                return false;
            }
            RegionClaim claim = FindRegionClaim(claimId);
            if (claim == null)
            {
                error = "没有编号为 #" + claimId + " 的领地";
                m_lastRegionClaimError = error;
                return false;
            }
            int index = -1;
            for (int i = 0; i < claim.Owners.Count; i++)
            {
                if (string.Equals(claim.Owners[i].UserId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                error = "该玩家本来就不是这块领地的拥有者";
                m_lastRegionClaimError = error;
                return false;
            }
            string name = claim.Owners[index].Name;
            claim.Owners.RemoveAt(index);
            m_regionClaimSequence++;
            m_lastRegionClaimError = string.Empty;
            MarkHostRegionClaimsDirty();
            PublishRegionClaimReplace(claim);
            Log.Information("[ScMP] Region claim #" + claim.Id + " owner revoked: " +
                (string.IsNullOrWhiteSpace(name) ? userId : name));
            error = null;
            return true;
        }

        /// <summary>
        /// clientId → 拥有者身份。主机自己（0）用本端账号身份；其余用记录键（= 账号 userid）+
        /// 网络化身名字（Source: ScMultiplayerTrustedDataModificationClients.GetDataModificationClientKey）。
        /// </summary>
        private bool TryResolveRegionClaimOwnerIdentity(int clientId, out string userId,
            out string name)
        {
            userId = string.Empty;
            name = string.Empty;
            if (clientId == 0)
            {
                userId = GetLocalPlayerIdentity();
                name = GetLocalPlayerName();
                return !string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(name);
            }
            if (clientId < 0 || !m_clientRecordKeys.TryGetValue(clientId, out string key) ||
                string.IsNullOrWhiteSpace(key))
                return false;
            userId = key;
            name = m_networkPlayerData.TryGetValue(clientId, out PlayerData data) && data != null
                ? data.Name ?? string.Empty
                : string.Empty;
            return true;
        }

        internal void ApplyRegionClaimSnapshot(IEnumerable<RegionClaim> claims, int nextId,
            long sequence)
        {
            m_regionClaims.Clear();
            if (claims != null)
            {
                foreach (RegionClaim claim in claims)
                {
                    if (claim != null)
                        m_regionClaims.Add(claim.Clone());
                }
            }
            m_regionClaimNextId = Math.Max(nextId, 1);
            m_regionClaimSequence = sequence;
        }

        internal void ApplyRegionClaimDelta(RegionClaim claim, int removeId,
            RegionClaimOperation operation, int nextId, long sequence)
        {
            switch (operation)
            {
                case RegionClaimOperation.Remove:
                case RegionClaimOperation.Replace when claim == null:
                    for (int i = m_regionClaims.Count - 1; i >= 0; i--)
                    {
                        if (m_regionClaims[i].Id == removeId)
                            m_regionClaims.RemoveAt(i);
                    }
                    break;
                default:
                    if (claim != null)
                        ReplaceRegionClaimRecord(claim);
                    break;
            }
            m_regionClaimNextId = Math.Max(nextId, 1);
            m_regionClaimSequence = sequence;
        }

        /// <summary>整表文本摘要（面板/日志用）。</summary>
        public string DescribeRegionClaims()
        {
            if (m_regionClaims.Count == 0)
                return "（本端副本没有领地）";
            var text = new StringBuilder();
            foreach (RegionClaim claim in GetRegionClaimsOrdered())
            {
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(claim.Describe());
            }
            return text.ToString();
        }

        // ================================================================
        // P4：区域展示（归属裁决 + 稳定配色 + 显示开关）
        // ================================================================

        /// <summary>区域展示总开关（设计稿 §2「区域展示（开关）」；建立领地后默认显示）。</summary>
        public bool RegionDisplayEnabled => m_regionDisplayEnabled;

        /// <summary>选区预览线框开关（设计稿 §2「选区预览线框（可选，默认开）」）。</summary>
        public bool RegionSelectionPreviewEnabled => m_regionSelectionPreviewEnabled;

        public bool ToggleRegionDisplay()
        {
            m_regionDisplayEnabled = !m_regionDisplayEnabled;
            return m_regionDisplayEnabled;
        }

        public bool ToggleRegionSelectionPreview()
        {
            m_regionSelectionPreviewEnabled = !m_regionSelectionPreviewEnabled;
            return m_regionSelectionPreviewEnabled;
        }

        /// <summary>
        /// **重叠裁决**（设计稿 §1 锁定结论 3）：覆盖该格的所有领地里取**编号最小**的那块。
        /// 执法（P5–P7）与展示都用它作为唯一入口。
        /// </summary>
        public RegionClaim OwnerClaimAt(int x, int y, int z)
        {
            RegionClaim winner = null;
            for (int i = 0; i < m_regionClaims.Count; i++)
            {
                RegionClaim claim = m_regionClaims[i];
                if (!claim.Contains(x, y, z))
                    continue;
                if (winner == null || claim.Id < winner.Id)
                    winner = claim;
            }
            return winner;
        }

        public RegionClaim OwnerClaimAt(Point3 cell) => OwnerClaimAt(cell.X, cell.Y, cell.Z);

        /// <summary>该格归属的一行描述（面板显示 / 日志用）。</summary>
        public string DescribeOwnerAt(int x, int y, int z)
        {
            RegionClaim claim = OwnerClaimAt(x, y, z);
            if (claim == null)
                return "无主";
            return "#" + claim.Id + "（" + claim.DescribeOwners() + "）";
        }

        /// <summary>
        /// 领地配色索引：**跟随 userId**（设计稿 §3「编号与颜色跟随 userId：换设备/换 clientId 后不变」）。
        /// 用拥有者 userId 的稳定散列（FNV-1a）% 16；没有拥有者时退回用领地编号。
        /// </summary>
        public int RegionClaimPaletteIndex(RegionClaim claim)
        {
            if (claim == null)
                return 0;
            int seed = claim.Owners.Count > 0
                ? StableStringHash(claim.Owners[0].UserId)
                : claim.Id * 2654435761u.GetHashCode();
            return MathUtils.Abs(seed) % 16;
        }

        /// <summary>FNV-1a：跨会话/跨平台稳定（不要用 `string.GetHashCode()`，它每次进程都不同）。</summary>
        private static int StableStringHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (!string.IsNullOrEmpty(value))
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        hash ^= value[i];
                        hash *= 16777619u;
                    }
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        /// <summary>
        /// 区域展示的**分级绘制**（设计稿 §3「性能」）：按到本端玩家的距离排序，
        /// 近处画填充+编号，中距离只画线框，远处不画。
        /// </summary>
        public void CollectRegionClaimsForDisplay(Vector3 position, int maximumFilled,
            int maximumWireframe, List<RegionClaim> filled, List<RegionClaim> wireframe)
        {
            filled?.Clear();
            wireframe?.Clear();
            if (m_regionClaims.Count == 0)
                return;
            var sorted = new List<RegionClaim>(m_regionClaims);
            sorted.Sort((left, right) =>
                DistanceSquaredToClaim(left, position).CompareTo(
                    DistanceSquaredToClaim(right, position)));
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i < maximumFilled)
                    filled?.Add(sorted[i]);
                else if (i < maximumFilled + maximumWireframe)
                    wireframe?.Add(sorted[i]);
                else
                    break;
            }
        }

        private static float DistanceSquaredToClaim(RegionClaim claim, Vector3 position)
        {
            float centerX = (claim.MinX + claim.MaxX + 1) * 0.5f;
            float centerZ = (claim.MinZ + claim.MaxZ + 1) * 0.5f;
            float dx = centerX - position.X;
            float dz = centerZ - position.Z;
            return dx * dx + dz * dz;
        }
    }
}
