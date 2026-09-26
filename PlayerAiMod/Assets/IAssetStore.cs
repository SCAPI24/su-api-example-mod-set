using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 一次内存改动的**来源**（谁改的：人 / AI / Laya / 恢复流程）。
    ///
    /// 计划 §4.11 要求"每次改动都要留痕（谁改的）"，因为排查"这棵树怎么变成这样了"
    /// 时，**来源**比"内容"更有用：同一份内容由 AI 改出来和由人改出来，处置方式完全不同。
    /// </summary>
    public enum AssetOrigin
    {
        /// <summary>刚从磁盘读进来的原样（也是 drop 的锚点）。</summary>
        Disk,

        /// <summary>编辑器（人在界面里改）。</summary>
        Editor,

        /// <summary>游戏内 AI 的 `ai.edit.*`（内存改写）。</summary>
        Ai,

        /// <summary>Laya 的决策影响了树（池调度 / 树内节点）。</summary>
        Laya,

        /// <summary>来自自动缓存（断电恢复）。</summary>
        Cache,

        /// <summary>来源未知（低频扫描发现 `IsDirty`，但没赶上改动那一拍）。</summary>
        Unknown
    }

    /// <summary>
    /// **内存库里的一条资源**（v1 只做树包）。计划 §4.11："先从存盘读进内存，
    /// **读进来才算加载**；内存里随便改；默认不回写磁盘。"
    ///
    /// 三个哈希/字节对必须分清，它们是"读档-运行-存档"模型的地基：
    ///   · <see cref="DiskBytes"/> / <see cref="DiskHash"/> —— 最近一次**从磁盘载入**的版本（drop 回这里）
    ///   · <see cref="Memory"/> / <see cref="MemoryHash"/> —— 当前**运行态**（权威）
    ///   · <see cref="Dirty"/> —— 两者是否已经不同（不同就要缓存、就要在界面上显形）
    /// </summary>
    public sealed class AssetRecord
    {
        /// <summary>逻辑名（不带扩展名），例如 `demo.greet`。</summary>
        public string Name;

        /// <summary>最近一次载入/保存的绝对路径（缓存版本头里也记它）。</summary>
        public string SourcePath;

        /// <summary>最近一次载入时的磁盘版哈希（没有磁盘版时为 null）。</summary>
        public string DiskHash;

        /// <summary>当前内存版哈希。</summary>
        public string MemoryHash;

        /// <summary>内存版与最近载入的磁盘版是否不同。</summary>
        public bool Dirty;

        /// <summary>内存改写代次（每次 Adopt +1；与"A 暂停代次"同源思路）。</summary>
        public long Generation;

        public string LoadedUtc;

        public string ChangedUtc;

        /// <summary>最近一次改动的来源。</summary>
        public AssetOrigin Origin = AssetOrigin.Unknown;

        /// <summary>当前内存版字节（权威）。</summary>
        public byte[] Memory;

        /// <summary>最近一次载入的磁盘版字节（`drop` 用；从未载入过则为 null）。</summary>
        public byte[] DiskBytes;

        public int MemoryByteCount
        {
            get { return Memory != null ? Memory.Length : 0; }
        }

        public int DiskByteCount
        {
            get { return DiskBytes != null ? DiskBytes.Length : 0; }
        }

        /// <summary>短哈希（日志/界面用）。</summary>
        public static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return "-";
            return hash.Length > 12 ? hash.Substring(0, 12) : hash;
        }

        public string Describe()
        {
            return Name + " mem=" + Short(MemoryHash) + "(" + MemoryByteCount + "B)"
                + " disk=" + Short(DiskHash) + "(" + DiskByteCount + "B)"
                + " gen=" + Generation
                + (Dirty ? " DIRTY" : " clean")
                + " by=" + Origin;
        }

        public override string ToString()
        {
            return "AssetRecord(" + Describe() + ")";
        }
    }

    /// <summary>
    /// 资源库的统一抽象（计划 §4.11 末尾）：`Load / Memory / AutoSave / Save / Drop / Dirty / Hash`。
    ///
    /// **v1 只把树包接进来**（动作包与问题库沿用同一套抽象，晚一步接）——
    /// 这是用户批准的"先做小"：三份读路径一起改会把验证面拉得太长。
    ///
    /// 抽象与实现分离的真正理由：<see cref="MemoryAssetStore"/> 是**可脱离游戏跑的自检对象**
    /// （编辑器里也能编译），而"把活树序列化成字节"那一步必须回到游戏侧（`TreeWriter`），
    /// 所以序列化由**调用方**做，库只管字节。
    /// </summary>
    public interface IAssetStore
    {
        /// <summary>资源种类（v1 = "tree"）。</summary>
        string Kind { get; }

        /// <summary>内存库现有的记录（顺序 = 载入顺序）。</summary>
        IReadOnlyList<AssetRecord> Records { get; }

        /// <summary>按名字/路径取一条记录。</summary>
        bool TryGet(string nameOrPath, out AssetRecord record);

        /// <summary>磁盘 → 内存（clean）。"读进来才算加载"。</summary>
        AssetRecord Load(string nameOrPath, out string error);

        /// <summary>内存改写（`origin` 记来源）；哈希变了就置 <see cref="AssetRecord.Dirty"/>。</summary>
        AssetRecord Adopt(string nameOrPath, byte[] memory, AssetOrigin origin, out string error);

        /// <summary>内存版 → 正式包（落盘）。默认**不覆盖**；`overwrite=true` 时先备份上一代。</summary>
        bool Save(string nameOrPath, string directory, bool overwrite, out string path,
            out string error);

        /// <summary>丢弃内存改动，回到最近一次载入的磁盘版。</summary>
        bool Drop(string nameOrPath, out string error);

        /// <summary>当前内存版 → 自动缓存（影子副本，R1~R4）。</summary>
        bool WriteCache(string nameOrPath, AutoSaveCache cache, out string error);

        /// <summary>自动缓存 → 内存（断电恢复用；`Dirty` 沿用缓存记录里的值）。</summary>
        bool ReadCache(string nameOrPath, AutoSaveCache cache, out string error);
    }
}
