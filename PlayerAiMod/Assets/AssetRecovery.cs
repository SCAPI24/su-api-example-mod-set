using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 启动恢复的一个决议：某一份资源**该用哪一版**、**为什么**、以及要装进内存的字节。
    ///
    /// `Explanation` 不是装饰：计划 §4.11 R1 要求恢复**绝不静默** ——
    /// "用了缓存"这件事必须能被说出来（日志 / 编辑器 / `ai.asset.status`），
    /// 否则用户看到的就是"我的包怎么自己变了"。
    /// </summary>
    public sealed class AssetRecoveryPlan
    {
        public string Name;

        public string SourcePath;

        /// <summary>磁盘版 / 缓存版 / 磁盘版更新（要盖掉过期缓存）。</summary>
        public AutoSaveCache.RecoveryChoice Choice;

        /// <summary>人话解释（"缓存比存档新 X 分钟"）。</summary>
        public string Explanation;

        /// <summary>要装进内存的字节（取不到就是 null —— 计划里如实标"没得恢复"）。</summary>
        public byte[] Payload;

        /// <summary>这份字节是从缓存来的。</summary>
        public bool FromCache;

        public string CachePayload;

        /// <summary>缓存时间戳（没有缓存时为 MinValue）。</summary>
        public DateTime CacheSavedUtc;

        public DateTime DiskModifiedUtc;

        public bool Recoverable
        {
            get { return Payload != null && Payload.Length > 0; }
        }

        public string Describe()
        {
            return Name + " -> " + Choice + (FromCache ? " (from cache)" : " (from disk)")
                + " bytes=" + (Payload != null ? Payload.Length : 0)
                + " : " + Explanation;
        }

        public override string ToString()
        {
            return "AssetRecoveryPlan(" + Describe() + ")";
        }
    }

    /// <summary>
    /// **启动恢复流程**（计划 §4.11 的状态机右侧）：
    /// `读发行/存档 → 有 .autosave？ → 比时间戳 + 内容哈希 → 决定用缓存还是存档`。
    ///
    /// 它**只做决议**，不偷偷改运行态：决议由调用方（游戏启动 / `ai.asset.restore`）应用，
    /// 并把 <see cref="AssetRecoveryPlan.Explanation"/> 写进日志与状态命令。
    /// 这样"断电后 AI 用了哪一版"永远有据可查 —— 这是 R1 落地的关键。
    /// </summary>
    public static class AssetRecovery
    {
        /// <summary>
        /// 单份资源的决议（**纯函数**，不碰磁盘；自检直接喂字节）。
        ///
        /// 判据顺序见 <see cref="AutoSaveCache.ChooseRecovery"/>：
        ///   缓存不存在 / 与磁盘版内容相同 → 用磁盘版；
        ///   磁盘版在缓存之后被人改过（哈希对不上） → 用磁盘版（**人的改动优先**）；
        ///   否则按时间戳比谁新。
        /// </summary>
        public static AssetRecoveryPlan Decide(string name, string path, byte[] diskBytes,
            DateTime diskModifiedUtc, AutoSaveEntry cached, byte[] cacheBytes)
        {
            var plan = new AssetRecoveryPlan
            {
                Name = name,
                SourcePath = path,
                DiskModifiedUtc = diskModifiedUtc,
                CacheSavedUtc = cached != null ? cached.SavedTimeUtc : DateTime.MinValue,
                CachePayload = cached != null ? cached.PayloadPath : null
            };

            string diskHash = diskBytes != null && diskBytes.Length > 0
                ? PackageLoader.ComputeHash(diskBytes) : null;

            AutoSaveCache.RecoveryChoice choice =
                AutoSaveCache.ChooseRecovery(cached, diskHash, diskModifiedUtc);
            plan.Choice = choice;

            if (choice == AutoSaveCache.RecoveryChoice.Cache)
            {
                if (cacheBytes != null && cacheBytes.Length > 0)
                {
                    plan.Payload = cacheBytes;
                    plan.FromCache = true;
                    plan.Explanation = AutoSaveCache.Explain(choice, cached, diskModifiedUtc);
                    return plan;
                }

                // 缓存记录在、字节读不回来 → **不猜**，退回磁盘版并说明（R2/R3 的同一精神）
                plan.Choice = AutoSaveCache.RecoveryChoice.Disk;
                plan.Explanation = "the autosave was newer but its payload could not be read;"
                    + " falling back to the package on disk";
                plan.Payload = diskBytes;
                plan.FromCache = false;
                return plan;
            }

            plan.Payload = diskBytes;
            plan.FromCache = false;
            plan.Explanation = AutoSaveCache.Explain(choice, cached, diskModifiedUtc);
            return plan;
        }

        /// <summary>
        /// 对包目录里的每个包做一次决议（读磁盘 + 读缓存索引）。
        /// 读不出来的包**照样出现在结果里**（`Recoverable=false`），因为"缺文件"本身就是要报告的事实。
        /// </summary>
        public static List<AssetRecoveryPlan> Plan(PackageRoots roots, AutoSaveCache cache)
        {
            var plans = new List<AssetRecoveryPlan>();
            if (roots == null)
                return plans;

            List<AutoSaveEntry> entries = cache != null && cache.Available
                ? cache.ReadEntries() : new List<AutoSaveEntry>();

            List<string> files = roots.ListFiles();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                string name = MemoryAssetStore.NameOf(path);
                covered.Add(name);

                byte[] diskBytes = null;
                DateTime modified = DateTime.MinValue;
                try
                {
                    if (File.Exists(path))
                    {
                        diskBytes = File.ReadAllBytes(path);
                        modified = File.GetLastWriteTimeUtc(path);
                    }
                }
                catch (Exception)
                {
                    // 读不到就是"没有磁盘版"，决议会如实反映
                }

                AutoSaveEntry entry = FindEntry(entries, name);
                byte[] cacheBytes = entry != null && cache != null ? cache.ReadPayload(entry) : null;
                plans.Add(Decide(name, path, diskBytes, modified, entry, cacheBytes));
            }

            // **只有缓存、盘上没有**的那些才是最该恢复的（内存里改完还没保存就断电）。
            // 它们不在 ListFiles() 的结果里，所以必须单独补上 —— 否则"最值钱的恢复"恰好被漏掉。
            for (int i = 0; i < entries.Count; i++)
            {
                AutoSaveEntry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Name) || covered.Contains(entry.Name))
                    continue;

                byte[] cacheBytes = cache != null ? cache.ReadPayload(entry) : null;
                plans.Add(Decide(entry.Name, entry.SourcePath, null, DateTime.MinValue, entry, cacheBytes));
            }
            return plans;
        }

        private static AutoSaveEntry FindEntry(List<AutoSaveEntry> entries, string name)
        {
            if (entries == null || string.IsNullOrEmpty(name))
                return null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return entries[i];
            }
            return null;
        }

        /// <summary>
        /// 对**内存库里的一条记录**做决议：磁盘版取"**此刻**盘上的那一份"（不是载入时的那一份），
        /// 缓存版取 `.autosave/` 里的影子副本。`ai.asset.list` 的三态视图与单包恢复都用它。
        /// </summary>
        public static AssetRecoveryPlan PlanFor(AssetRecord record, AutoSaveCache cache)
        {
            if (record == null)
                return null;

            byte[] diskBytes = null;
            DateTime modified = DateTime.MinValue;
            if (!string.IsNullOrEmpty(record.SourcePath))
            {
                try
                {
                    if (File.Exists(record.SourcePath))
                    {
                        diskBytes = File.ReadAllBytes(record.SourcePath);
                        modified = File.GetLastWriteTimeUtc(record.SourcePath);
                    }
                }
                catch (Exception)
                {
                    // 读不到就是"此刻没有磁盘版"，决议照实说
                }
            }

            AutoSaveEntry entry = null;
            byte[] cacheBytes = null;
            if (cache != null && cache.Available)
            {
                entry = FindEntry(cache.ReadEntries(), record.Name);
                if (entry != null)
                    cacheBytes = cache.ReadPayload(entry);
            }

            return Decide(record.Name, record.SourcePath, diskBytes, modified, entry, cacheBytes);
        }

        /// <summary>
        /// 把决议装进内存库。返回一份逐条说明（调用方写日志 / 回给命令）。
        /// **只走 <see cref="MemoryAssetStore.Adopt"/>**，绝不写盘 —— 恢复不等于保存。
        /// </summary>
        public static List<string> Apply(MemoryAssetStore store, IReadOnlyList<AssetRecoveryPlan> plans)
        {
            var lines = new List<string>();
            if (store == null || plans == null)
                return lines;

            for (int i = 0; i < plans.Count; i++)
            {
                AssetRecoveryPlan plan = plans[i];
                if (!plan.Recoverable)
                {
                    lines.Add(plan.Name + ": nothing to recover (" + plan.Explanation + ")");
                    continue;
                }

                string error;
                AssetRecord record = store.Adopt(plan.Name, plan.Payload,
                    plan.FromCache ? AssetOrigin.Cache : AssetOrigin.Disk, out error);
                if (record == null)
                {
                    lines.Add(plan.Name + ": recovery failed - " + error);
                    continue;
                }
                lines.Add(plan.Name + ": " + (plan.FromCache ? "restored from autosave" : "took the disk version")
                    + " (" + plan.Explanation + ", gen=" + record.Generation + ")");
            }
            return lines;
        }

        /// <summary>一段可写进日志的汇总（**必须能被看懂**）。</summary>
        public static string Summarize(IReadOnlyList<AssetRecoveryPlan> plans)
        {
            if (plans == null || plans.Count == 0)
                return "no packages to recover";

            int fromCache = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].FromCache)
                    fromCache++;
            }
            return "recovery plan: " + plans.Count + " package(s), " + fromCache + " from the autosave";
        }
    }
}
