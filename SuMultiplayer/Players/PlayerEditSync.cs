using System;
using System.Collections.Generic;
using System.Text;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 编辑玩家屏（引擎 <c>Game.PlayerScreen</c>，入口 ESC → More → Edit Players → 点角色）
    /// 的一次**进入快照**与**退出快照**之间的差异。
    ///
    /// 为什么单独放在 Players/（不引用 Game）：
    ///   · 快照 / 比较 / 校验 / 计数都是纯逻辑，自检可以直接编译同一份源码；
    ///   · 屏幕检测（<c>ScreensManager.CurrentScreen</c> 引用变化）在 Engine/PlayerEditWatcher.cs，
    ///     引擎动作（写 PlayerData / 写角色记录 / 广播）在 Modules/PlayerAvatarModule.cs；
    ///     本类只做值层面的比较，不碰任何静态状态。
    ///
    /// 产品口径（2026-10-20）：
    ///   · **允许重名**：比较 / 校验里没有任何"名字唯一"的判断，重名照常应用并广播；
    ///   · **不轮询玩家资料**：只有屏幕切换时才由调用方抓快照并调用 <see cref="Compare"/>；
    ///   · 名字 / 性别 / 皮肤 / 衣服逐字段比较，皮肤区分内置与自定义（自定义要带 32 字节哈希）。
    ///
    /// Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen（Edit 模式编辑 m_playerData 的
    ///         Name / PlayerClass / CharacterSkinName）
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.VerifyName（名字规则：2..14、
    ///         字母/数字/空格、首尾不能是空格）
    /// </summary>
    [Flags]
    public enum PlayerEditFields
    {
        None = 0,
        Name = 1,
        Sex = 2,
        Skin = 4,
        Clothes = 8
    }

    /// <summary>一个本机槽位在某一刻的资料快照（值；不持有 PlayerData 引用）。</summary>
    public sealed class PlayerEditSlotSnapshot
    {
        /// <summary>本机槽位 0..3（<see cref="PlayerKey.MaxSlot"/>）。</summary>
        public int Slot;

        public string Name = string.Empty;
        public PlayerSex Sex = PlayerSex.Male;
        public string SkinName = string.Empty;
        public byte[] SkinSha256 = Array.Empty<byte>();
        public int[][] Clothes = PlayerAppearance.CreateEmptyClothes();

        public bool HasName => !string.IsNullOrWhiteSpace(Name);
        public bool HasSkin => !string.IsNullOrWhiteSpace(SkinName);
        public bool IsCustomSkin => SkinAssetPolicy.IsCustomSkinName(SkinName);

        public string Signature
        {
            get
            {
                var text = new StringBuilder(96);
                text.Append(Slot).Append('\n')
                    .Append(Name ?? string.Empty).Append('\n')
                    .Append((int)Sex).Append('\n')
                    .Append(SkinName ?? string.Empty).Append('\n')
                    .Append(SkinSha256 != null && SkinSha256.Length > 0
                        ? Convert.ToHexString(SkinSha256)
                        : string.Empty);
                for (int i = 0; i < PlayerAppearance.ClothingSlotCount; i++)
                {
                    text.Append('\n');
                    int[] values = Clothes != null && i < Clothes.Length ? Clothes[i] : null;
                    if (values != null) text.Append(string.Join(",", values));
                }
                return text.ToString();
            }
        }

        public PlayerEditSlotSnapshot Clone() => new PlayerEditSlotSnapshot
        {
            Slot = Slot,
            Name = Name ?? string.Empty,
            Sex = Sex,
            SkinName = SkinName ?? string.Empty,
            SkinSha256 = SkinSha256 != null ? (byte[])SkinSha256.Clone() : Array.Empty<byte>(),
            Clothes = PlayerAppearance.CloneClothes(Clothes)
        };

        /// <summary>
        /// 把引擎资料转成快照（槽位由调用方给出；资料为空时返回 null）。
        /// 名字**原样保留**（不 Trim）：校验 / 比较都看引擎收到的同一个字符串。
        /// </summary>
        public static PlayerEditSlotSnapshot FromProfile(int slot, PlayerProfileSnapshot profile)
        {
            if (profile == null) return null;
            PlayerAppearance appearance = profile.Appearance ?? new PlayerAppearance();
            return new PlayerEditSlotSnapshot
            {
                Slot = slot,
                Name = profile.Name ?? string.Empty,
                Sex = appearance.Sex,
                SkinName = (appearance.SkinName ?? string.Empty).Trim(),
                SkinSha256 = appearance.SkinSha256 != null
                    ? (byte[])appearance.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Clothes = PlayerAppearance.CloneClothes(appearance.Clothes)
            };
        }

        /// <summary>转回资料值（提交 profile 消息 / 写角色记录用；不含名字以外的身份）。</summary>
        public PlayerProfileSnapshot ToProfile() => new PlayerProfileSnapshot
        {
            Name = Name ?? string.Empty,
            Appearance = new PlayerAppearance
            {
                Sex = Sex,
                SkinName = SkinName ?? string.Empty,
                SkinSha256 = SkinSha256 != null ? (byte[])SkinSha256.Clone() : Array.Empty<byte>(),
                Clothes = PlayerAppearance.CloneClothes(Clothes)
            }
        };

        public string Describe() =>
            "slot=" + Slot + " 名字=\"" + (Name ?? string.Empty) + "\" 性别=" + Sex +
            " 皮肤=\"" + (SkinName ?? string.Empty) + "\"" +
            (IsCustomSkin
                ? "（自定义 · " + (SkinSha256 != null ? SkinSha256.Length : 0) + "B 哈希）"
                : "（内置）");
    }

    /// <summary>
    /// 一次屏幕切换前后的本机角色快照集合。身份只用于日志 / 记录键，不参与槽位比较。
    /// </summary>
    public sealed class PlayerEditSnapshot
    {
        public string Identity = string.Empty;
        public string Source = string.Empty;

        /// <summary>抓快照时有没有加载中的世界（false = 主菜单 / 世界还没装好）。</summary>
        public bool HasWorld;

        /// <summary>没有槽位 / 世界不可用时的明确原因（调用方必须写进日志，不静默）。</summary>
        public string Reason = string.Empty;

        public List<PlayerEditSlotSnapshot> Slots = new List<PlayerEditSlotSnapshot>(PlayerKey.SlotCount);

        public bool HasAnySlot => Slots != null && Slots.Count > 0;

        public string Signature
        {
            get
            {
                var text = new StringBuilder(256);
                text.Append(Identity ?? string.Empty).Append('|')
                    .Append(HasWorld ? '1' : '0');
                for (int i = 0; Slots != null && i < Slots.Count; i++)
                    text.Append('\n').Append(Slots[i]?.Signature ?? string.Empty);
                return text.ToString();
            }
        }

        public bool TryGetSlot(int slot, out PlayerEditSlotSnapshot value)
        {
            value = null;
            if (Slots == null) return false;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i] != null && Slots[i].Slot == slot)
                {
                    value = Slots[i];
                    return true;
                }
            }
            return false;
        }

        public PlayerEditSnapshot Clone()
        {
            var clone = new PlayerEditSnapshot
            {
                Identity = Identity ?? string.Empty,
                Source = Source ?? string.Empty,
                HasWorld = HasWorld,
                Reason = Reason ?? string.Empty
            };
            if (Slots != null)
                for (int i = 0; i < Slots.Count; i++)
                    if (Slots[i] != null) clone.Slots.Add(Slots[i].Clone());
            return clone;
        }

        /// <summary>空快照：原因照实写（主菜单 / 没有本机角色 / 抓取失败）。</summary>
        public static PlayerEditSnapshot Empty(string source, bool hasWorld, string reason) =>
            new PlayerEditSnapshot
            {
                Source = source ?? string.Empty,
                HasWorld = hasWorld,
                Reason = reason ?? string.Empty
            };
    }

    public enum PlayerEditSlotChangeKind
    {
        /// <summary>槽位在进入 / 退出时都存在，且有字段变化。</summary>
        Updated = 0,
        /// <summary>退出时新增的本机角色（Add / Play 流程）。</summary>
        Added = 1,
        /// <summary>退出时已经被删除的本机角色（Delete / 放弃创建）。</summary>
        Removed = 2
    }

    /// <summary>一个槽位的变化（逐字段 Flags；新增 / 移除也能表达）。</summary>
    public sealed class PlayerEditSlotChange
    {
        public int Slot;
        public PlayerEditSlotChangeKind Kind;
        public PlayerEditFields Fields;
        public PlayerEditSlotSnapshot Before;
        public PlayerEditSlotSnapshot After;

        public PlayerEditSlotSnapshot Effective => After ?? Before;

        public bool Added => Kind == PlayerEditSlotChangeKind.Added;
        public bool Removed => Kind == PlayerEditSlotChangeKind.Removed;
        public bool NameChanged => (Fields & PlayerEditFields.Name) != 0;
        public bool SexChanged => (Fields & PlayerEditFields.Sex) != 0;
        public bool SkinChanged => (Fields & PlayerEditFields.Skin) != 0;
        public bool ClothesChanged => (Fields & PlayerEditFields.Clothes) != 0;
        public bool AnyFieldChanged => Fields != PlayerEditFields.None;

        /// <summary>变化后的皮肤是不是自定义 `.scskin`（需要走资源上传 / 校验 / 广播链）。</summary>
        public bool IsCustomSkin => Effective != null && Effective.IsCustomSkin;

        public string Describe()
        {
            string fields = DescribeFields(Fields);
            string kind = Kind == PlayerEditSlotChangeKind.Added
                ? "新增"
                : Kind == PlayerEditSlotChangeKind.Removed ? "移除" : "更新";
            return kind + " 槽位 " + Slot + " · " + fields +
                (Before != null ? " · 之前[" + Before.Describe() + "]" : string.Empty) +
                (After != null ? " · 之后[" + After.Describe() + "]" : string.Empty);
        }

        public static string DescribeFields(PlayerEditFields fields)
        {
            if (fields == PlayerEditFields.None) return "无变化";
            var text = new StringBuilder(32);
            if ((fields & PlayerEditFields.Name) != 0) Append(text, "名字");
            if ((fields & PlayerEditFields.Sex) != 0) Append(text, "性别");
            if ((fields & PlayerEditFields.Skin) != 0) Append(text, "皮肤");
            if ((fields & PlayerEditFields.Clothes) != 0) Append(text, "衣服");
            return text.ToString();
        }

        private static void Append(StringBuilder text, string item)
        {
            if (text.Length > 0) text.Append('/');
            text.Append(item);
        }
    }

    /// <summary>一次进入 / 退出快照比较的结果。空 <see cref="Changes"/> = 没有变化。</summary>
    public sealed class PlayerEditDiff
    {
        public List<PlayerEditSlotChange> Changes = new List<PlayerEditSlotChange>(PlayerKey.SlotCount);

        public bool AnyChanged => Changes != null && Changes.Count > 0;

        public int NameChangedCount => Count(change => change.NameChanged);
        public int SkinChangedCount => Count(change => change.SkinChanged);
        public int SexChangedCount => Count(change => change.SexChanged);
        public int ClothesChangedCount => Count(change => change.ClothesChanged);

        public bool HasCustomSkinChange
        {
            get
            {
                if (Changes == null) return false;
                for (int i = 0; i < Changes.Count; i++)
                    if (Changes[i] != null && Changes[i].SkinChanged && Changes[i].IsCustomSkin)
                        return true;
                return false;
            }
        }

        public PlayerEditSlotChange ForSlot(int slot)
        {
            if (Changes == null) return null;
            for (int i = 0; i < Changes.Count; i++)
                if (Changes[i] != null && Changes[i].Slot == slot) return Changes[i];
            return null;
        }

        private int Count(Func<PlayerEditSlotChange, bool> predicate)
        {
            int count = 0;
            if (Changes == null) return count;
            for (int i = 0; i < Changes.Count; i++)
                if (Changes[i] != null && predicate(Changes[i])) count++;
            return count;
        }

        public string Describe()
        {
            if (!AnyChanged) return "没有资料变化";
            var text = new StringBuilder(160);
            for (int i = 0; i < Changes.Count; i++)
            {
                if (Changes[i] == null) continue;
                if (text.Length > 0) text.Append("；");
                text.Append(Changes[i].Describe());
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// 快照 / 比较 / 校验的唯一实现。**不检查名字唯一性**（重名是允许的产品行为）。
    /// </summary>
    public static class PlayerEditSync
    {
        /// <summary>
        /// 抓一份本机角色快照。<paramref name="profiles"/> 的下标就是本机槽位（与
        /// <c>NetworkAvatarSpawner.TryCollectLocalPlayers</c> 的排序口径一致：PlayerIndex 升序）。
        /// </summary>
        public static PlayerEditSnapshot Capture(string identity,
            IList<PlayerProfileSnapshot> profiles, string source, bool hasWorld, string reason)
        {
            var snapshot = new PlayerEditSnapshot
            {
                Identity = (identity ?? string.Empty).Trim(),
                Source = source ?? string.Empty,
                HasWorld = hasWorld,
                Reason = reason ?? string.Empty
            };
            if (profiles == null) return snapshot;
            for (int slot = 0; slot < profiles.Count; slot++)
            {
                PlayerEditSlotSnapshot item = PlayerEditSlotSnapshot.FromProfile(slot, profiles[slot]);
                // 资料不可用的槽位不塞空条目：调用方看到条目数 < 角色数时必须写原因（不伪造）。
                if (item != null && (item.HasName || item.HasSkin)) snapshot.Slots.Add(item);
            }
            return snapshot;
        }

        /// <summary>
        /// 进入 / 退出快照比较。按槽位对齐（不是按名字 / 角色下标猜）：
        ///   · 两边都有 → 逐字段比较，有差异才进 <see cref="PlayerEditDiff.Changes"/>；
        ///   · 只有退出有 → Added；只有进入有 → Removed；
        ///   · 名字相同 / 重名都不会影响比较结果。
        /// </summary>
        public static PlayerEditDiff Compare(PlayerEditSnapshot enter, PlayerEditSnapshot exit)
        {
            var diff = new PlayerEditDiff();
            if (enter == null || exit == null) return diff;
            var slots = new SortedSet<int>();
            for (int i = 0; enter.Slots != null && i < enter.Slots.Count; i++)
                if (enter.Slots[i] != null) slots.Add(enter.Slots[i].Slot);
            for (int i = 0; exit.Slots != null && i < exit.Slots.Count; i++)
                if (exit.Slots[i] != null) slots.Add(exit.Slots[i].Slot);
            foreach (int slot in slots)
            {
                enter.TryGetSlot(slot, out PlayerEditSlotSnapshot before);
                exit.TryGetSlot(slot, out PlayerEditSlotSnapshot after);
                if (before == null && after == null) continue;
                if (before == null)
                {
                    diff.Changes.Add(new PlayerEditSlotChange
                    {
                        Slot = slot,
                        Kind = PlayerEditSlotChangeKind.Added,
                        Fields = PlayerEditFields.Name | PlayerEditFields.Sex |
                            PlayerEditFields.Skin | PlayerEditFields.Clothes,
                        After = after
                    });
                    continue;
                }
                if (after == null)
                {
                    diff.Changes.Add(new PlayerEditSlotChange
                    {
                        Slot = slot,
                        Kind = PlayerEditSlotChangeKind.Removed,
                        Fields = PlayerEditFields.None,
                        Before = before
                    });
                    continue;
                }
                PlayerEditFields fields = PlayerEditFields.None;
                if (!string.Equals(before.Name ?? string.Empty, after.Name ?? string.Empty,
                        StringComparison.Ordinal))
                    fields |= PlayerEditFields.Name;
                if (before.Sex != after.Sex) fields |= PlayerEditFields.Sex;
                if (!string.Equals(before.SkinName ?? string.Empty, after.SkinName ?? string.Empty,
                        StringComparison.Ordinal) ||
                    !SkinAssetPolicy.BytesEqual(before.SkinSha256, after.SkinSha256))
                    fields |= PlayerEditFields.Skin;
                if (!SameClothes(before.Clothes, after.Clothes)) fields |= PlayerEditFields.Clothes;
                if (fields == PlayerEditFields.None) continue;
                diff.Changes.Add(new PlayerEditSlotChange
                {
                    Slot = slot,
                    Kind = PlayerEditSlotChangeKind.Updated,
                    Fields = fields,
                    Before = before,
                    After = after
                });
            }
            return diff;
        }

        /// <summary>
        /// 玩家名字校验的**唯一自写实现**（本文件属于 Players/ 纯逻辑层，不能引用 Game）。
        /// 规则与引擎 <c>PlayerData.VerifyName</c> **逐条相同**；本实现**不 Trim、不改写输入**
        /// （首尾空格由规则本身拒绝，合法性以传入的原始字符串为准）。UI 输入边界是否 Trim
        /// 以引擎 <c>PlayerScreen</c> 自己的约定为准（PlayerData.cs:353-371 只认最终字符串）。
        /// 这里不检查重复 —— 重名是允许的。
        ///
        /// Source: Survivalcraft/Game/PlayerData.cs:353-371 PlayerData.VerifyName
        ///         （长度 2..14；每个字符 char.IsLetterOrDigit 或空格；首尾不能是空格）
        /// </summary>
        public static bool TryValidateName(string name, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(name))
            {
                reason = "名字为空（引擎要求 2..14 个字符）";
                return false;
            }
            if (name.Length < 2 || name.Length > 14)
            {
                reason = "名字长度 " + name.Length + " 非法（允许 2..14）";
                return false;
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c) && c != ' ')
                {
                    reason = "名字包含非法字符 '" + c + "'（只允许字母/数字/空格）";
                    return false;
                }
            }
            if (name[0] == ' ' || name[name.Length - 1] == ' ')
            {
                reason = "名字首尾不能是空格";
                return false;
            }
            return true;
        }

        public static bool TryValidateSlot(int slot, out string reason)
        {
            reason = null;
            if (slot < 0 || slot > PlayerKey.MaxSlot)
            {
                reason = "槽位 " + slot + " 非法（允许 0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 一个槽位快照的**最小校验**（主机 / 客户端共用）：槽位、名字、皮肤名、自定义哈希、
        /// 衣服数组边界。任何失败都给出可直接定位的原因；不夹紧、不换默认皮肤。
        /// </summary>
        public static bool TryValidateSlotSnapshot(PlayerEditSlotSnapshot snapshot, out string reason)
        {
            reason = null;
            if (snapshot == null)
            {
                reason = "槽位快照为空";
                return false;
            }
            if (!TryValidateSlot(snapshot.Slot, out reason)) return false;
            if (!TryValidateName(snapshot.Name, out reason)) return false;
            string skin = (snapshot.SkinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.TryValidateSkinName(skin, allowBuiltIn: true, out reason))
                return false;
            if (SkinAssetPolicy.IsCustomSkinName(skin))
            {
                if (!SkinAssetPolicy.IsValidHash(snapshot.SkinSha256))
                {
                    reason = "自定义皮肤 \"" + skin + "\" 缺少合法的 32 字节 SHA-256";
                    return false;
                }
                if (SkinAssetPolicy.TryGetClassHint(skin, out PlayerSex hint) && hint != snapshot.Sex)
                {
                    reason = "自定义皮肤 \"" + skin + "\" 名称暗示性别 " + hint +
                        "，资料却是 " + snapshot.Sex + "（不静默改性别）";
                    return false;
                }
            }
            return TryValidateClothes(snapshot.Clothes, out reason);
        }

        /// <summary>资料值的最小校验（提交 profile 消息前 / 主机收到后都走这一份）。</summary>
        public static bool TryValidateProfile(PlayerProfileSnapshot profile, int slot, out string reason)
        {
            reason = null;
            if (profile == null)
            {
                reason = "资料为空";
                return false;
            }
            PlayerEditSlotSnapshot snapshot = PlayerEditSlotSnapshot.FromProfile(slot, profile);
            return TryValidateSlotSnapshot(snapshot, out reason);
        }

        /// <summary>
        /// 衣服数组边界：4 个槽位、每个槽位最多 <see cref="MaxClothesPerSlot"/> 个非负物品 id。
        /// 超限显式拒绝（协议/记录里不允许出现会拖垮引擎的巨型数组）。
        /// </summary>
        public const int MaxClothesPerSlot = 64;

        public static bool TryValidateClothes(int[][] clothes, out string reason)
        {
            reason = null;
            if (clothes == null) return true;    // 空 = 没穿衣服
            if (clothes.Length > PlayerAppearance.ClothingSlotCount)
            {
                reason = "衣服槽位 " + clothes.Length + " 超过引擎上限 " +
                    PlayerAppearance.ClothingSlotCount;
                return false;
            }
            for (int slot = 0; slot < clothes.Length; slot++)
            {
                int[] values = clothes[slot];
                if (values == null) continue;
                if (values.Length > MaxClothesPerSlot)
                {
                    reason = "衣服槽位 " + slot + " 有 " + values.Length + " 个 id，超过上限 " +
                        MaxClothesPerSlot;
                    return false;
                }
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] < 0)
                    {
                        reason = "衣服槽位 " + slot + " 第 " + i + " 个 id 为负（" + values[i] + "）";
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool SameClothes(int[][] left, int[][] right)
        {
            for (int slot = 0; slot < PlayerAppearance.ClothingSlotCount; slot++)
            {
                int[] a = left != null && slot < left.Length ? left[slot] : null;
                int[] b = right != null && slot < right.Length ? right[slot] : null;
                int lengthA = a != null ? a.Length : 0;
                int lengthB = b != null ? b.Length : 0;
                if (lengthA != lengthB) return false;
                for (int i = 0; i < lengthA; i++)
                    if (a[i] != b[i]) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 编辑玩家同步的**计数器**（实例字段，无静态状态）。日志按 10 秒限流由
    /// <see cref="ShouldLog"/> 统一决定；计数每次都真实累加。
    /// </summary>
    public sealed class PlayerEditCounters
    {
        /// <summary>10 秒限流（与用户口径一致）。</summary>
        public const long LogIntervalMs = 10000;

        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_rejectedReasons =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public long Entered { get; private set; }
        public long Exited { get; private set; }
        public long ProfileResubmitted { get; private set; }
        public long NameChanged { get; private set; }
        public long SkinChanged { get; private set; }
        public long HostProfileBroadcast { get; private set; }
        public long MissingAsset { get; private set; }
        public long Rejected { get; private set; }
        /// <summary>编辑屏退出时把名字文本框的最终值写回 PlayerData 的次数（Android 键盘不一定触发 FocusLost）。</summary>
        public long UiNameCommitted { get; private set; }
        /// <summary>编辑屏退出时把皮肤预览的最终值写回 PlayerData 的次数。</summary>
        public long UiSkinCommitted { get; private set; }
        /// <summary>想补写 UI 值但拿不到被编辑的 PlayerData / 文本框，导致值只能留在屏幕上的次数。</summary>
        public long UiCommitFailures { get; private set; }

        public void OnEditScreenEntered() => Entered++;
        public void OnEditScreenExited() => Exited++;

        /// <summary>退出时补写名字成功（值已经和 PlayerData 一致时不计数）。</summary>
        public void OnUiNameCommitted(long count = 1)
        {
            if (count > 0) UiNameCommitted += count;
        }

        /// <summary>退出时补写皮肤成功（值已经和 PlayerData 一致时不计数）。</summary>
        public void OnUiSkinCommitted(long count = 1)
        {
            if (count > 0) UiSkinCommitted += count;
        }

        /// <summary>补写失败（被编辑的 PlayerData 取不到 / 文本框读不到）；必须限流写原因，不静默。</summary>
        public void OnUiCommitFailure(long count = 1)
        {
            if (count > 0) UiCommitFailures += count;
        }

        public void OnProfileResubmitted(long count = 1)
        {
            if (count > 0) ProfileResubmitted += count;
        }

        public void OnNameChanged(long count = 1)
        {
            if (count > 0) NameChanged += count;
        }

        public void OnSkinChanged(long count = 1)
        {
            if (count > 0) SkinChanged += count;
        }

        public void OnHostProfileBroadcast(long count = 1)
        {
            if (count > 0) HostProfileBroadcast += count;
        }

        /// <summary>自定义皮肤资源缺失（已显示确定性占位；不是"静默换成内置皮肤"）。</summary>
        public void OnMissingAsset(long count = 1)
        {
            if (count > 0) MissingAsset += count;
        }

        /// <summary>资料更新被拒：原因必须写清楚（长度/字符集/皮肤/槽位/记录写盘失败…）。</summary>
        public void OnProfileUpdateRejected(string reason)
        {
            string key = string.IsNullOrWhiteSpace(reason) ? "(未给出原因)" : reason.Trim();
            Rejected++;
            m_rejectedReasons.TryGetValue(key, out long count);
            m_rejectedReasons[key] = count + 1;
        }

        public IReadOnlyDictionary<string, long> RejectedReasons => m_rejectedReasons;

        /// <summary>10 秒限流：同一个 key 在间隔内只返回一次 true。计数不受影响。</summary>
        public bool ShouldLog(string key, long nowMs, long intervalMs = LogIntervalMs)
        {
            string normalized = string.IsNullOrEmpty(key) ? "(default)" : key;
            if (m_lastLogMs.TryGetValue(normalized, out long last) &&
                nowMs - last < intervalMs)
                return false;
            m_lastLogMs[normalized] = nowMs;
            return true;
        }

        public string Describe()
        {
            var text = new StringBuilder(192);
            text.Append("editScreenEntered=").Append(Entered)
                .Append(" editScreenExited=").Append(Exited)
                .Append(" profileResubmitted=").Append(ProfileResubmitted)
                .Append(" nameChanged=").Append(NameChanged)
                .Append(" skinChanged=").Append(SkinChanged)
                .Append(" hostProfileBroadcast=").Append(HostProfileBroadcast)
                .Append(" missingAsset=").Append(MissingAsset)
                .Append(" profileUpdateRejected=").Append(Rejected)
                .Append(" uiNameCommitted=").Append(UiNameCommitted)
                .Append(" uiSkinCommitted=").Append(UiSkinCommitted)
                .Append(" uiCommitFailures=").Append(UiCommitFailures);
            if (m_rejectedReasons.Count > 0)
            {
                var keys = new List<string>(m_rejectedReasons.Keys);
                keys.Sort(StringComparer.Ordinal);
                text.Append('[');
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(keys[i]).Append('=').Append(m_rejectedReasons[keys[i]]);
                }
                text.Append(']');
            }
            return text.ToString();
        }
    }
}
