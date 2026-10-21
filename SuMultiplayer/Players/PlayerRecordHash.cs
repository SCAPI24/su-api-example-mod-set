using System;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 一个角色记录的**不常变**内容哈希（每个角色独立，独立于其它角色）。
    ///
    /// 为什么需要它（用户口径 2026-10）：
    ///   · 角色记录（<c>NetworkPlayerRecord</c>）**跟着世界 pak 走**：主机导出世界时整个世界目录
    ///     被复制进 pak，`SuMultiplayerPlayers.xml` 就在里面，加入端下载后本地也有一份；
    ///   · 重连时 pak 不会重发，所以要用"每角色一个 hash"把**变化了的角色记录**挑出来，
    ///     只重发这些角色（自定义皮肤资源这类不常变的大块数据），常变部分继续走各自的增量流；
    ///   · hash **只放进联机角色的 xml**（`SuMultiplayerPlayers.xml`），**绝不写进 `Project.xml`**：
    ///     联机角色实体本来就被从 `Project.m_entities` 摘掉（`NetworkAvatarSpawner` 注释），
    ///     记录也不属于存档，hash 跟着记录走才能随 pak 一起到加入端。
    ///
    /// 常变 / 不常变拆分（用户明确列举）：
    ///   · **不常变**（进 hash）：名字、性别、皮肤名 + 皮肤哈希（含自定义资源哈希）、衣服、
    ///     背包槽位、活跃槽、等级；以及这些字段的"一次性"标志（名字已确认 / 初始物品 / 创造背包）。
    ///   · **常变**（不进 hash）：生命 / 空气 / 食物 / 体力 / 睡眠 / 温度 / 湿度 / 疾病 / 火焰、
    ///     位置 / 朝向、饱食度、统计、成就 —— 这些要么每拍变，要么由别的增量流负责，
    ///     放进 hash 会让"记录没变"永远不成立，diff 就失去意义。
    ///
    /// 算法：FNV-1a 64，按固定字段顺序喂规范化文本（浮点用 invariant "R"，数组用 ";" 连接）。
    /// 同一份记录在任何机器 / 任何会话上算出的值相同；`Version` 前缀保证以后改字段集合时
    /// 旧 hash 不会和新 hash 相撞（改集合 = 改版本 = 所有记录自然视为"变了"）。
    /// </summary>
    public static class PlayerRecordHash
    {
        /// <summary>字段集合版本（改参与 hash 的字段时 +1）。</summary>
        public const string Version = "rec1";

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>算一份记录的 hash。绝不返回 0（0 保留为"未知/缺失"，见 XML 读取）。</summary>
        public static ulong Compute(NetworkPlayerRecord record)
        {
            if (record == null) return 0UL;
            ulong hash = FnvOffset;
            Feed(ref hash, Version);
            Feed(ref hash, record.Name);
            Feed(ref hash, record.NameConfirmed ? "1" : "0");
            Feed(ref hash, ((int)record.PlayerClass).ToString(CultureInfo.InvariantCulture));
            Feed(ref hash, record.SkinName);
            Feed(ref hash, PlayerRecordCodec.FormatSkinHash(record.SkinSha256));
            // 资源本体不进 hash（内容已由 SkinSha256 标识），只把长度带上，防"哈希合法但资源缺失"两种状态同 hash。
            Feed(ref hash, (record.SkinAsset?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
            int[][] clothes = record.Clothes ?? PlayerRecordCodec.CreateEmptyClothes();
            Feed(ref hash, clothes.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < clothes.Length; i++)
                Feed(ref hash, PlayerRecordCodec.FormatIntArray(clothes[i]));
            // 背包：写盘时每个槽位都落（含值 0 / 数量 0），所以这里也逐槽喂，保证 hash 与文件内容一一对应。
            int slots = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            Feed(ref hash, slots.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < slots; i++)
            {
                Feed(ref hash, record.SlotValues[i].ToString(CultureInfo.InvariantCulture));
                Feed(ref hash, record.SlotCounts[i].ToString(CultureInfo.InvariantCulture));
            }
            // 手工制造槽：写盘时只落"真的装着东西"的槽（ShouldPersistItem），hash 也按同一口径。
            int handcraft = Math.Min(record.HandcraftSlotValues?.Length ?? 0,
                record.HandcraftSlotCounts?.Length ?? 0);
            for (int i = 0; i < handcraft; i++)
            {
                if (!PlayerRecordCodec.ShouldPersistItem(record.HandcraftSlotValues[i],
                        record.HandcraftSlotCounts[i]))
                    continue;
                Feed(ref hash, "h" + i.ToString(CultureInfo.InvariantCulture));
                Feed(ref hash, record.HandcraftSlotValues[i].ToString(CultureInfo.InvariantCulture));
                Feed(ref hash, record.HandcraftSlotCounts[i].ToString(CultureInfo.InvariantCulture));
            }
            Feed(ref hash, record.ActiveSlotIndex.ToString(CultureInfo.InvariantCulture));
            Feed(ref hash, PlayerRecordCodec.FormatFloat(record.Level));
            Feed(ref hash, record.HasReceivedInitialItems ? "1" : "0");
            Feed(ref hash, record.InventoryWasCreative ? "1" : "0");
            Feed(ref hash, record.CreativeCategoryIndex.ToString(CultureInfo.InvariantCulture));
            Feed(ref hash, record.CreativePageIndex.ToString(CultureInfo.InvariantCulture));
            return hash == 0UL ? 1UL : hash;
        }

        /// <summary>XML 属性用的十六进制形式（固定 16 位小写）。</summary>
        public static string Format(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>解析 XML 属性；缺失 / 非法 → 0（= 未知，调用方必须重算，不允许当成"没变"）。</summary>
        public static ulong Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0UL;
            return ulong.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out ulong hash) ? hash : 0UL;
        }

        private static void Feed(ref ulong hash, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= FnvPrime;
                }
            }
            // 字段分隔符：避免 "ab"+"c" 与 "a"+"bc" 撞成同一串。
            hash ^= 0x1FUL;
            hash *= FnvPrime;
        }
    }
}
