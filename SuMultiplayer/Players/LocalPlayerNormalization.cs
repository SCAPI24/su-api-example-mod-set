using System;
using System.Collections.Generic;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// Phase 2 **客户端本机角色归一化**（纯逻辑）：决定下载世界里哪些本机 <c>PlayerData</c>
    /// 属于本设备、哪些是**主机自己的角色副本**（它们已经 / 将要由远端化身表示）。
    ///
    /// 为什么需要这一步（Phase 1 的明确边界）：
    ///   客户端用 <c>WorldsManager.ImportWorld</c> 整包接收主机世界，Project.xml 的 Players 里是
    ///   主机自己的 1..N 个本机角色。Phase 1 的 <c>TryCollectLocalPlayers</c> 把"非联机化身"的
    ///   PlayerData 一律当本机角色枚举 → 客户端把主机角色当成自己的槽位公告（<c>LOCAL_PLAYER_SLOTS</c>），
    ///   主机再为这些幽灵槽位建化身：重复 / 幽灵化身、ESC 统计错人、相机与输入争用，
    ///   并且很容易撞到 <c>SubsystemPlayers.MaxPlayers = 4</c> /
    ///   <c>SubsystemGameWidgets.MaxGameWidgets = 4</c>。
    ///
    /// 判定口径（不猜 index、不按"第一个有 GameWidget"猜）：
    ///   · **本机自己那条** = 引擎枚举顺序里的第一条（<c>TryCollectLocalPlayers</c> 按 PlayerIndex
    ///     升序，<c>TryGetLocalPlayer</c> 取第一个；本机资料应用也落在它上面）→ 无条件保留；
    ///   · 其余本机角色与主机名册里的主机本机槽位 <c>(0, slot)</c> 名字 / 性别 / 皮肤一致
    ///     → 判为**下载来的主机角色副本**（远端化身才是它的表示），移除；
    ///   · 与本机自己的槽位（名册里 clientId = 本机通道编号）/ 主机分配资料一致、且不与主机槽位
    ///     重合 → 本机自己的分屏角色，保留（最多到本机已知槽位数）；
    ///   · 既不属于主机槽位、也没有本机元数据的其余角色：在**下载世界**里不可能属于本机
    ///     （唯一的来源是主机档案）→ 移除，并写明原因（不静默留着）。
    ///
    /// 名字 / 性别 / 皮肤**同时**匹配主机槽位与本机资料时（任务要求的同名边界）：按
    /// "下载世界只包含主机角色"这一事实判为主机副本，并计入 <see cref="LocalPlayerNormalizePlan.AmbiguousMatches"/>，
    /// 绝不静默二选一；本机自己那条按引擎顺序保留，所以不会把自己删掉。
    ///
    /// 引擎上限：<see cref="MaxLocalCharacters"/>（SubsystemPlayers.MaxPlayers /
    /// SubsystemGameWidgets.MaxGameWidgets）。保留的本机角色 + 名册里的远端角色超过上限时
    /// 产出 <see cref="LocalPlayerNormalizePlan.CapacityLimited"/> + 明确原因（limited roster），
    /// 由调用方显式拒绝超出的远端化身并写日志，绝不静默超限。
    ///
    /// 本类**不含静态可变状态**，也不引用 Game：无设备自检直接编译这一份源码。
    /// </summary>
    public enum LocalPlayerNormalizeSkip
    {
        /// <summary>不跳过（本次要执行归一化）。</summary>
        None = 0,
        /// <summary>本端是主机（主机自己的本机角色就是世界的角色，不做下载世界归一化）。</summary>
        NotClient = 1,
        /// <summary>Project / 世界目录还没就绪（世界仍在加载）。</summary>
        NoWorld = 2,
        /// <summary>当前世界不是下载来的联机世界（单机 / 主机自己的世界）。</summary>
        NotDownloadedWorld = 3,
        /// <summary>这个 Project 已经归一化过（幂等：每个世界安装只做一次）。</summary>
        AlreadyNormalized = 4
    }

    /// <summary>一个待判定的本机 PlayerData（只带判定需要的字段；由引擎适配层填充）。</summary>
    public sealed class LocalPlayerCandidate
    {
        /// <summary>引擎玩家槽位（<c>PlayerData.PlayerIndex</c>；只用于本机索引，不是身份）。</summary>
        public int PlayerIndex;
        /// <summary>角色名（<c>PlayerData.Name</c>，已 Trim）。</summary>
        public string Name = string.Empty;
        /// <summary>性别（<c>PlayerData.PlayerClass</c> 的协议镜像）。</summary>
        public PlayerSex Sex;
        /// <summary>皮肤名（<c>PlayerData.CharacterSkinName</c>，已 Trim）。</summary>
        public string SkinName = string.Empty;
        /// <summary>是不是引擎枚举顺序里的第一条（本机实际操作的 PlayerData）。</summary>
        public bool IsAdoptedSelf;

        public string Describe() =>
            "#" + PlayerIndex + " " + (string.IsNullOrEmpty(Name) ? "(no-name)" : Name) +
            " (" + Sex + ", " + (string.IsNullOrEmpty(SkinName) ? "(no-skin)" : SkinName) + ")";
    }

    /// <summary>名册 / 握手资料里的一个本机槽位视图（主机自己的槽位，或本机自己的槽位）。</summary>
    public sealed class LocalPlayerSlotRef
    {
        /// <summary>玩家键 (clientId, slot)；主机自己的槽位 clientId = 0。</summary>
        public PlayerKey Key;
        /// <summary>该槽位的角色名（已 Trim）。</summary>
        public string Name = string.Empty;
        /// <summary>该槽位的性别。</summary>
        public PlayerSex Sex;
        /// <summary>该槽位的皮肤名（已 Trim；为空时匹配只比名字 + 性别）。</summary>
        public string SkinName = string.Empty;
        /// <summary>来源（日志用）：roster:host / roster:self / handshake:host_profile / assigned-profile。</summary>
        public string Source = string.Empty;

        public string Describe() =>
            Key + " " + (string.IsNullOrEmpty(Name) ? "(no-name)" : Name) +
            (string.IsNullOrEmpty(Source) ? string.Empty : " [" + Source + "]");
    }

    /// <summary>一次归一化的判定结果（纯值；调用方据此执行引擎移除并计数 / 写日志）。</summary>
    public sealed class LocalPlayerNormalizePlan
    {
        /// <summary>true = 判定有效，调用方可以执行 <see cref="Remove"/>。</summary>
        public bool Apply;
        /// <summary>跳过原因（<see cref="LocalPlayerNormalizeSkip.None"/> = 没跳过）。</summary>
        public LocalPlayerNormalizeSkip Skip;
        /// <summary>结论 / 跳过原因（可直接写日志）。</summary>
        public string Reason = string.Empty;
        /// <summary>true = 无法归一化（例如世界没有任何本机角色）；失败原因在 <see cref="FailureReason"/>。</summary>
        public bool Failed;
        public string FailureReason = string.Empty;
        /// <summary>保留的本机角色（至少 1 个：引擎顺序里的第一条）。</summary>
        public readonly List<LocalPlayerCandidate> Keep = new List<LocalPlayerCandidate>();
        /// <summary>要移除的本机角色（下载来的主机角色副本 / 无法归属的本地角色）。</summary>
        public readonly List<LocalPlayerCandidate> Remove = new List<LocalPlayerCandidate>();
        /// <summary>与 <see cref="Remove"/> 一一对应的移除原因（明确到匹配到哪个主机槽位）。</summary>
        public readonly List<string> RemoveReasons = new List<string>();
        /// <summary>名字/性别/皮肤同时匹配主机槽位与本机资料的个数（显式边界，不是静默二选一）。</summary>
        public int AmbiguousMatches;
        /// <summary>true = 客户端还没有主机槽位名册（旧主机 / 名册还没到）：其余本地角色只能按下载来的主机副本处理。</summary>
        public bool HostRosterMissing;
        /// <summary>true = 保留的本机角色 + 名册远端角色超过引擎上限（limited roster）。</summary>
        public bool CapacityLimited;
        /// <summary>因为超过引擎上限而必须明确拒绝的远端角色数。</summary>
        public int CapacityRefusedRemotes;
        /// <summary>容量问题的明确原因（可直接写日志）。</summary>
        public string CapacityReason = string.Empty;
        /// <summary>判定时看到的本机角色总数（含保留与移除）。</summary>
        public int LocalPlayerCount;

        public int KeptCount => Keep.Count;
        public int RemovedCount => Remove.Count;
    }

    /// <summary>Phase 2 客户端本机角色归一化的判定逻辑（无静态可变状态）。</summary>
    public static class LocalPlayerNormalization
    {
        /// <summary>引擎硬上限：SubsystemPlayers.MaxPlayers / SubsystemGameWidgets.MaxGameWidgets。</summary>
        public const int MaxLocalCharacters = 4;

        /// <summary>
        /// 生成一次归一化判定。任何输入不满足（不是客户端 / 世界没就绪 / 不是下载世界 / 已经归一化）
        /// 都返回带原因的 <see cref="LocalPlayerNormalizePlan.Skip"/>；没有任何本机角色返回 Failed，
        /// 由调用方重试并明确写日志（不静默跳过）。
        /// </summary>
        /// <param name="isClient">本端是不是客户端（主机不做下载世界归一化）。</param>
        /// <param name="worldReady">Project / 世界目录是否已就绪。</param>
        /// <param name="downloadedWorld">当前世界是不是下载来的联机世界。</param>
        /// <param name="alreadyNormalized">这个 Project 是否已经归一化过（幂等）。</param>
        /// <param name="localPlayers">当前本机角色（非联机化身），顺序不限；<c>IsAdoptedSelf</c> 标出本机自己那条。</param>
        /// <param name="hostSlots">主机名册里的主机本机槽位 (0, slot)。</param>
        /// <param name="ownSlots">本机自己的槽位（名册里 clientId = 本机通道编号 / 主机分配资料）。</param>
        /// <param name="ownLocalCountHint">已公告 / 已知的本机槽位数（0 = 只用 <paramref name="ownSlots"/> 推）。</param>
        /// <param name="remoteCharacterCount">名册里属于其他设备的角色数（容量计算用）。</param>
        public static LocalPlayerNormalizePlan Decide(bool isClient, bool worldReady,
            bool downloadedWorld, bool alreadyNormalized,
            IReadOnlyList<LocalPlayerCandidate> localPlayers,
            IReadOnlyList<LocalPlayerSlotRef> hostSlots,
            IReadOnlyList<LocalPlayerSlotRef> ownSlots,
            int ownLocalCountHint, int remoteCharacterCount)
        {
            var plan = new LocalPlayerNormalizePlan();

            // 判定顺序：已经归一化 → 不是客户端 → 世界没就绪 → 不是下载世界。
            // （调用方在 TickClient 里已经排除了主机，这里仍然显式判定，纯逻辑可独立自检。）
            if (alreadyNormalized)
            {
                plan.Skip = LocalPlayerNormalizeSkip.AlreadyNormalized;
                plan.Reason = "这个 Project 已经归一化过（每个世界安装只做一次；重连 / 重装世界会换 Project 再跑）";
                return plan;
            }
            if (!isClient)
            {
                plan.Skip = LocalPlayerNormalizeSkip.NotClient;
                plan.Reason = "本端不是客户端（主机自己的本机角色就是世界里的角色，不做下载世界归一化）";
                return plan;
            }
            if (!worldReady)
            {
                plan.Skip = LocalPlayerNormalizeSkip.NoWorld;
                plan.Reason = "Project / 世界目录还没就绪（世界仍在加载）→ 等世界就绪后的第一个客户端 tick";
                return plan;
            }
            if (!downloadedWorld)
            {
                plan.Skip = LocalPlayerNormalizeSkip.NotDownloadedWorld;
                plan.Reason = "当前世界不是下载来的联机世界（单机世界 / 主机自己的世界）→ 本机角色全部属于本机";
                return plan;
            }
            if (localPlayers == null || localPlayers.Count == 0)
            {
                plan.Failed = true;
                plan.FailureReason = "下载世界里没有任何本机角色（PlayersData 为空）：" +
                    "主机可能没有角色，引擎会先走角色创建流程 → 暂不归一化，等 PlayerData 建好后重试";
                return plan;
            }

            // 去掉 null 并按 PlayerIndex 稳定排序（与 TryCollectLocalPlayers 的排序口径一致）。
            var ordered = new List<LocalPlayerCandidate>(localPlayers.Count);
            for (int i = 0; i < localPlayers.Count; i++)
            {
                if (localPlayers[i] != null) ordered.Add(localPlayers[i]);
            }
            if (ordered.Count == 0)
            {
                plan.Failed = true;
                plan.FailureReason = "本机角色列表里没有有效条目（全是 null）";
                return plan;
            }
            ordered.Sort((left, right) => left.PlayerIndex.CompareTo(right.PlayerIndex));
            plan.LocalPlayerCount = ordered.Count;

            // 本机自己那条：优先 IsAdoptedSelf（引擎顺序第一条），否则退到 PlayerIndex 最小的一条。
            LocalPlayerCandidate adopted = null;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ordered[i].IsAdoptedSelf) continue;
                adopted = ordered[i];
                break;
            }
            if (adopted == null) adopted = ordered[0];
            plan.Keep.Add(adopted);

            plan.HostRosterMissing = hostSlots == null || hostSlots.Count == 0;
            // 本机已知槽位数：显式提示（已公告数量）与本机槽位视图的最大槽位号 + 1 取大者。
            // 用 maxSlot+1 而不是条数：主机分配资料与名册里的本机槽位 0 是同一个槽位，不能算成两个。
            int ownTarget = Math.Max(1, ownLocalCountHint);
            if (ownSlots != null)
            {
                for (int i = 0; i < ownSlots.Count; i++)
                {
                    LocalPlayerSlotRef slot = ownSlots[i];
                    if (slot == null || !slot.Key.IsValid) continue;
                    ownTarget = Math.Max(ownTarget, slot.Key.Slot + 1);
                }
            }

            // 逐条判定（跳过 adopted 自己）。顺序：同时匹配 → 主机槽位 → 本机槽位 → 无法归属。
            for (int i = 0; i < ordered.Count; i++)
            {
                LocalPlayerCandidate candidate = ordered[i];
                if (ReferenceEquals(candidate, adopted)) continue;
                LocalPlayerSlotRef hostMatch = FindMatch(hostSlots, candidate);
                LocalPlayerSlotRef ownMatch = FindMatch(ownSlots, candidate);
                if (hostMatch != null && ownMatch != null)
                {
                    // 名字 / 性别 / 皮肤与主机槽位、本机资料同时重合：下载世界里的这份只可能是主机角色。
                    plan.AmbiguousMatches++;
                    Remove(plan, candidate,
                        "名字/性别/皮肤同时匹配主机本机槽位 " + hostMatch.Describe() +
                        " 与本机资料 " + ownMatch.Describe() +
                        " → 按\"下载世界里除本机自己那条外的角色都来自主机档案\"判为主机角色副本（显式边界，不静默二选一）");
                    continue;
                }
                if (hostMatch != null)
                {
                    Remove(plan, candidate,
                        "匹配主机本机槽位 " + hostMatch.Describe() +
                        " → 下载来的主机角色副本（它的表示是远端化身，不是本机角色）");
                    continue;
                }
                if (ownMatch != null)
                {
                    if (plan.Keep.Count < ownTarget)
                    {
                        plan.Keep.Add(candidate);
                        continue;
                    }
                    Remove(plan, candidate,
                        "匹配本机槽位 " + ownMatch.Describe() + "，但本机已知槽位数只有 " +
                        ownTarget + " 个 → 这是重复 / 残留的本地角色副本");
                    continue;
                }
                if (plan.Keep.Count < ownTarget)
                {
                    // 没有主机名册匹配、且本机已知槽位还没填满：本机自己的分屏角色（保留）。
                    plan.Keep.Add(candidate);
                    continue;
                }
                Remove(plan, candidate,
                    "既没有匹配主机本机槽位、也没有匹配本机自己的槽位" +
                    (plan.HostRosterMissing ? "（客户端还没收到主机槽位名册）" : string.Empty) +
                    "：下载世界里除本机自己那条以外的角色都来自主机档案 → 按下载来的主机角色副本移除" +
                    "（不静默留下幽灵角色）");
            }

            plan.Apply = true;
            plan.Reason = "本机角色 " + plan.LocalPlayerCount + " 个 → 保留 " + plan.Keep.Count +
                " 个、移除 " + plan.Remove.Count + " 个" +
                (plan.HostRosterMissing
                    ? "（还没有主机槽位名册：除保留项外的本地角色都按下载来的主机副本处理）"
                    : string.Empty);

            // 容量：引擎最多 4 个玩家槽位（本机角色 + 远端化身共用 0..3）。
            int remote = Math.Max(0, remoteCharacterCount);
            int total = plan.Keep.Count + remote;
            if (total > MaxLocalCharacters)
            {
                plan.CapacityLimited = true;
                plan.CapacityRefusedRemotes = total - MaxLocalCharacters;
                plan.CapacityReason =
                    "引擎上限 " + MaxLocalCharacters + " 个玩家槽位（SubsystemPlayers.MaxPlayers / " +
                    "SubsystemGameWidgets.MaxGameWidgets）：本机角色 " + plan.Keep.Count + " 个 + " +
                    "名册里的远端角色 " + remote + " 个 = " + total + " > " + MaxLocalCharacters +
                    " → limited roster：必须明确拒绝 " + plan.CapacityRefusedRemotes +
                    " 个远端化身（本机角色一个都不退让；拒绝的每个键都会写日志并计数）";
            }
            return plan;
        }

        /// <summary>在槽位视图里找与候选角色名字 / 性别 / 皮肤一致的那一条；没有返回 null。</summary>
        public static LocalPlayerSlotRef FindMatch(IReadOnlyList<LocalPlayerSlotRef> slots,
            LocalPlayerCandidate candidate)
        {
            if (slots == null || candidate == null) return null;
            string name = Trim(candidate.Name);
            if (name.Length == 0) return null;
            string skin = Trim(candidate.SkinName);
            for (int i = 0; i < slots.Count; i++)
            {
                LocalPlayerSlotRef slot = slots[i];
                if (slot == null) continue;
                if (!string.Equals(name, Trim(slot.Name), StringComparison.Ordinal)) continue;
                if (slot.Sex != candidate.Sex) continue;
                // 槽位视图没有皮肤（旧主机 / 资料不完整）时只比名字 + 性别；有皮肤就必须一致。
                string slotSkin = Trim(slot.SkinName);
                if (slotSkin.Length > 0 && !string.Equals(skin, slotSkin, StringComparison.Ordinal))
                    continue;
                return slot;
            }
            return null;
        }

        private static void Remove(LocalPlayerNormalizePlan plan, LocalPlayerCandidate candidate,
            string reason)
        {
            plan.Remove.Add(candidate);
            plan.RemoveReasons.Add(reason);
        }

        private static string Trim(string value) => (value ?? string.Empty).Trim();
    }
}
