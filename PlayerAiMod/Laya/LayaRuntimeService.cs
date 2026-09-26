using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// Laya 在游戏里的运行服务（plan §5/§6）：配置 + 客户端 + 问题库 + 摘要编译。
    ///
    /// 与 <see cref="GameActionServices"/> 同样的取舍：**由运行时持有当唯一权威**
    /// （实机踩过"跨表面静态读到 null"，见 plan A10）。
    /// </summary>
    public sealed class LayaRuntimeService : ILayaRuntime
    {
        private readonly PlayerAiRuntime m_runtime;
        private readonly Dictionary<string, QuestionBank> m_banks =
            new Dictionary<string, QuestionBank>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> m_bankDirectories = new List<string>();

        /// <summary>缓存里每一份库**读进来时**的文件戳（时间戳+长度），用来发现"磁盘上被改过"。</summary>
        private readonly Dictionary<string, string> m_loadedStamps =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经报过"取不到"的库名（去重：同一个库连续失败只报一次）。</summary>
        private readonly HashSet<string> m_reportedBanks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private LayaClient m_client;
        private string m_lastBankError;

        public LayaRuntimeService(PlayerAiRuntime runtime, LayaConfig config, IEnumerable<string> bankDirectories)
            : this(runtime, config, bankDirectories, true)
        {
        }

        /// <summary>
        /// `publish=false`：只建一个**离线用**的实例，**不动静态发布点**（自检 / 离线工具用）。
        ///
        /// 为什么必须留这个口子：构造函数会写 `LayaRuntimeHost.Current`，而**树里的 `Task.LayaAsk`
        /// 就是靠它找服务**的 —— 于是"自检里 new 一个服务来测缓存"会**把游戏正在用的那一个顶掉**，
        /// 之后所有判定都去自检的临时目录找问题库，全局报 `laya_no_bank`（实机踩过，
        /// 平板上一跑 `bt.selftest` 之后所有判定就全废，PC 上因为顺序不同侥幸没暴露）。
        /// 依赖静态发布的东西，构造函数就**不该**有这种副作用 —— 这里给测试留一条干净的路。
        /// </summary>
        public LayaRuntimeService(PlayerAiRuntime runtime, LayaConfig config,
            IEnumerable<string> bankDirectories, bool publish)
        {
            m_runtime = runtime;
            Config = config ?? new LayaConfig();
            m_client = new LayaClient(Config);
            if (publish)
                LayaRuntimeHost.Current = this;
            if (bankDirectories != null)
            {
                foreach (string directory in bankDirectories)
                {
                    if (!string.IsNullOrEmpty(directory))
                        m_bankDirectories.Add(directory);
                }
            }
        }

        /// <summary>当前服务（由运行时在访问时保证指向自己那份）。</summary>
        public static LayaRuntimeService Current { get; set; }

        public LayaConfig Config { get; }

        public LayaClient Client
        {
            get { return m_client; }
        }

        /// <summary>判定记录（P4 复盘）：`ai.laya.review` 与编辑器面板都读它。</summary>
        public DecisionLog Decisions
        {
            get { return m_client != null ? m_client.Decisions : null; }
        }

        public IReadOnlyList<string> BankDirectories
        {
            get { return m_bankDirectories; }
        }

        /// <summary>换配置（编辑器改完热生效）。</summary>
        public void ReloadConfig(LayaConfig config)
        {
            if (config == null)
                return;
            m_client.Reload(config);
        }

        /// <summary>
        /// 问题库**取不到**时的回调（`name`, `error`）。为 null 时什么都不做。
        ///
        /// 运行时用它把失败送进 §4.12 的分流/退避/熔断那条链（plan §4.12 的"问题库"那一格）。
        /// 为什么用回调而不是让服务自己去拿运行时：Laya 服务在自检与编辑器里也会被单独构造，
        /// 那时根本没有运行时（同一个取舍见 `PackageReloader.IMemoryVersionSource`）。
        /// </summary>
        public Action<string, string> BankFailure { get; set; }

        /// <summary>问题库**取到了**时的回调（用来清掉失败标记与熔断计数）。</summary>
        public Action<string> BankSuccess { get; set; }

        /// <summary>
        /// 磁盘上的库**变了、正在重读**时的回调（`name`）。
        ///
        /// 为什么值得单独报一次：这是"编辑器改完就能生效"的关键一步，而它**默认是静默的** ——
        /// 出了问题时（比如人以为改生效了、实际读的是另一份）没有这条线就查不出来。
        /// 与 `BankFailure` 同一个理由用回调而不是直接写事件日志：本类在自检/编辑器里也会被单独构造。
        /// </summary>
        public Action<string> BankReloaded { get; set; }

        /// <summary>
        /// 解析问题库：先查缓存，再按目录顺序找文件；`only` 是逗号分隔的问题 id 子集。
        /// </summary>
        public bool TryResolveBank(string nameOrPath, string only, out QuestionBank bank, out string error)
        {
            bank = null;
            error = null;
            if (string.IsNullOrEmpty(nameOrPath))
            {
                error = "no question bank name given";
                return false;
            }

            QuestionBank loaded;
            if (m_banks.TryGetValue(nameOrPath, out loaded))
            {
                // **文件被改过就重读**（plan G18 的姊妹问题）：
                // 以前这里一旦缓存就**永不失效**，于是"编辑器/人改完 .qbank，游戏里还在用旧的"，
                // 只能重启游戏才生效 —— 而复盘表还会显示旧的内容哈希，两边看起来都对。
                // 判据用 时间戳 + 长度（一次 stat，比一次 150~500ms 的判定便宜得多）。
                if (!HasBankFileChanged(loaded))
                {
                    // 缓存命中：**不能再直接 return**（A54）。
                    // 旧写法在这里 `bank = loaded; return true;`，而"按 `only` 取子集"在下面读盘路径之后
                    // —— 于是 `only` **只在第一次（未命中）生效**，之后每个 tick 都拿到**整库**：
                    // 实机后果 = 出厂 demo 声明 `only=goal` 却一直在问 3 个问题
                    // （178 vs 78 input tokens、每个判定白花 ~100 token + ~70ms），
                    // 而且答案里会带回它**根本没问**的问题。缓存存的是**整库**，
                    // 子集只在出口处切 —— 两条路径都必须经过这里。
                    if (!string.IsNullOrEmpty(only))
                        loaded = loaded.Select(SplitIds(only));
                    bank = loaded;
                    return true;
                }

                ReportBankReloaded(nameOrPath);
                m_banks.Remove(nameOrPath);
                m_loadedStamps.Remove(nameOrPath);
            }

            {
                // 读盘路径（缓存里没有，或缓存的那一份已经被改过）
                string path = FindBankFile(nameOrPath);
                if (path == null)
                {
                    error = "question bank not found: '" + nameOrPath + "' (searched: "
                        + string.Join(", ", m_bankDirectories.ToArray()) + ")";
                    m_lastBankError = error;
                    ReportBankFailure(nameOrPath, error);
                    return false;
                }

                try
                {
                    string json = File.ReadAllText(path);   // 读一次就关（不锁文件）
                    string parseError;
                    loaded = QuestionBankParser.Parse(json, path, out parseError);
                    if (loaded == null)
                    {
                        error = "question bank is invalid: " + parseError;
                        m_lastBankError = error;
                        ReportBankFailure(nameOrPath, error);
                        return false;
                    }
                    loaded.SourcePath = path;
                    loaded.SourceHash = PackageLoader.ComputeHash(
                        new System.Text.UTF8Encoding(false).GetBytes(json));
                    m_banks[nameOrPath] = loaded;
                    // 键用**文件路径**：HasBankFileChanged 按库的 SourcePath 查；
                    // 用请求名当键会永远查不到 → 每次都当成"变了" → 每帧重读一遍磁盘。
                    m_loadedStamps[path] = StampOf(path);
                    ReportBankSuccess(nameOrPath);
                }
                catch (Exception exception)
                {
                    error = exception.GetType().Name + ": " + exception.Message;
                    m_lastBankError = error;
                    ReportBankFailure(nameOrPath, error);
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(only))
            {
                loaded = loaded.Select(SplitIds(only));
            }

            bank = loaded;
            return true;
        }

        /// <summary>`"goal,threat"` → `["goal","threat"]`（空项丢掉）。两条路径共用。</summary>
        private static List<string> SplitIds(string only)
        {
            var ids = new List<string>();
            string[] parts = only.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (id.Length > 0)
                    ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// 把"取不到"报给回调 —— **只在状态变化时报一次**（同一个库连续失败不刷屏）。
        ///
        /// 为什么必须做这个去重：`Task.LayaAsk` 每个 tick 都会来解析一次，
        /// 不去重的话事件日志会被同一个错误刷满、还会把熔断窗口算得飞快（明明只坏了一次）。
        /// </summary>
        private void ReportBankFailure(string nameOrPath, string error)
        {
            if (BankFailure == null || string.IsNullOrEmpty(nameOrPath))
                return;
            if (!m_reportedBanks.Add(nameOrPath))
                return;
            BankFailure(nameOrPath, error);
        }

        /// <summary>把"取到了"报给回调（只有"之前报过失败"才回调，避免每次成功都去清别人的失败标记）。</summary>
        private void ReportBankSuccess(string nameOrPath)
        {
            if (BankSuccess == null || string.IsNullOrEmpty(nameOrPath))
                return;
            if (m_reportedBanks.Remove(nameOrPath))
                BankSuccess(nameOrPath);
        }

        /// <summary>把"磁盘上变了、正在重读"报给回调（回调为 null 时什么都不做）。</summary>
        private void ReportBankReloaded(string nameOrPath)
        {
            if (BankReloaded == null || string.IsNullOrEmpty(nameOrPath))
                return;
            try
            {
                BankReloaded(nameOrPath);
            }
            catch (Exception)
            {
                // 记账是旁路：回调抛异常不能把判定链打断
            }
        }

        /// <summary>
        /// **忘掉"已经报过失败"** —— 人在外部处置过（`ai.asset.retry reset=`）或运行时把状态清了之后必须调。
        ///
        /// 不调会怎样：去重集合还记着这个名字，于是**同一个库的下一次失败再也不会上报**，
        /// 追踪器看起来"什么都没发生"（实机踩过：reset 完之后再失败，日志里一条都没有）。
        /// 去重是为了不刷屏，不是为了永远闭嘴 —— 状态被清掉，去重也必须跟着清。
        /// </summary>
        public void ForgetReportedBank(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return;
            m_reportedBanks.Remove(nameOrPath);
        }

        /// <summary>
        /// **忘掉全部**已经报过失败的库 —— 相位交接（§4.13）时调。
        ///
        /// 跨相位时"这个库取不到"是**另一件事**：世界外缺 `front_goal` 与世界内缺 `world_goal`
        /// 是两个独立的故障，去重集合如果跨相位留着，第二个相位的首次失败就会被当成"报过了"而静默。
        /// </summary>
        public void ForgetAllReportedBanks()
        {
            if (m_reportedBanks.Count == 0)
                return;
            m_reportedBanks.Clear();
        }

        private string FindBankFile(string nameOrPath)
        {            string trimmed = nameOrPath.Trim();
            bool hasPath = trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0
                || trimmed.EndsWith(QuestionBank.Extension, StringComparison.OrdinalIgnoreCase);

            var candidates = new List<string>();
            if (hasPath)
            {
                candidates.Add(trimmed);
                if (!trimmed.EndsWith(QuestionBank.Extension, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(trimmed + QuestionBank.Extension);
            }
            else
            {
                candidates.Add(trimmed + QuestionBank.Extension);
            }

            for (int c = 0; c < candidates.Count; c++)
            {
                for (int d = 0; d < m_bankDirectories.Count; d++)
                {
                    string combined = Path.Combine(m_bankDirectories[d], candidates[c]);
                    if (File.Exists(combined))
                        return combined;
                }
            }
            return null;
        }

        /// <summary>
        /// 编译**发上线**的那条状态（走 CmdBridge 的只读观察门面）。
        /// 拿不到观察就返回 null + 原因（节点据此失败，**不发请求白等**）。
        ///
        /// 用 `StateChars`（默认 32）而不是 `DigestBudgetChars`：实测这个模型只看开头，
        /// 长摘要把答案推去默认值（详见 `StateDigestCompiler.Fields()` 顶上那段实测）。
        /// 人要看完整的那份用 <see cref="CompileRichDigest"/>。
        /// </summary>
        public string CompileDigest(BtContext context, out string error)
        {
            StateInputs inputs;
            if (!TryObserveInputs(out inputs, out error))
                return null;
            // **上线状态一律"短 + wire 模式"**（2026-09-26，plan §12.4 / §12.4.1 之后）。
            //
            // 原来按相位分两种：世界里 28 字符 + 良性值不发，世界外用宽预算（111 字符，
            // 理由是"G16 的候选行天生比 28 长"）。但世界外那条链已经被量穿：
            //   · `front_goal` **常答**（4 行候选 / 1 行 / 空列表 / 主菜单 → 一律 `open_ui`）；
            //   · 给它补 `row0..row3` 选项**也没用** —— 这个模型不会"从状态里的列表挑第 N 项"。
            // ⇒ 候选行发上去**没有任何人能用**，它在 111 字符里就是 40+ 字符的噪声，
            //   而 §12.1 已证明"长状态会把答案推向默认值"。所以世界外也走 wire：
            //   候选行只留在人工/复盘那份（`state.digest`、`ai.laya.ask digest=`）。
            // 世界外那条链因此保持**确定性**：树里写死 `list:WorldsList#0`，翻页/滚动由 C# 做。
            return StateDigestCompiler.Compile(inputs, Config.StateChars, wire: true);
        }

        /// <summary>
        /// 编译**给人看/复盘**的完整摘要（`state.digest`、调参对照用）。
        /// **绝不参与决策** —— 它和上线那条共用同一张字段表，只是预算不同。
        /// </summary>
        public string CompileRichDigest(out string error)
        {
            StateInputs inputs;
            if (!TryObserveInputs(out inputs, out error))
                return null;
            return StateDigestCompiler.Compile(inputs, Config.DigestBudgetChars);
        }

        /// <summary>
        /// 结构化状态字段（供 `Service.ObserveState` 写黑板）。与摘要共用同一张字段表 + 同一次观察读取。
        /// </summary>
        public bool TryObserveFields(out List<KeyValuePair<string, string>> fields, out string error)
        {
            fields = null;
            StateInputs inputs;
            if (!TryObserveInputs(out inputs, out error))
                return false;

            fields = StateDigestCompiler.Evaluate(inputs);
            return true;
        }

        /// <summary>读一次原始观察（走 CmdBridge 的只读门面）。摘要与黑板字段都必须走它，口径才一致。</summary>
        private bool TryObserveInputs(out StateInputs inputs, out string error)
        {
            inputs = null;
            error = null;
            CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade == null)
            {
                error = "CmdBridgeMod facade is not available";
                return false;
            }

            try
            {
                Dictionary<string, object> raw = facade.DescribeStateInputs();
                inputs = StateInputs.FromObservation(raw);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// **丢掉问题库缓存**（下一次解析重新读盘）。
        ///
        /// 平时用不上（解析时会自己看文件有没有变），但它给"人明确知道该重读"的场合留了口子：
        /// 编辑器保存完问题库、或者排查"到底读的是哪一份"时，一条命令就能强制重读。
        /// </summary>
        public void ClearBankCache(out int dropped)
        {
            dropped = m_banks.Count;
            m_banks.Clear();
            m_loadedStamps.Clear();
            m_reportedBanks.Clear();   // 状态清了，去重也必须跟着清（否则下次失败不上报）
        }

        /// <summary>缓存里的这一份和磁盘上的一致吗（时间戳 + 长度；取不到就是"变了"，逼着重读）。</summary>
        /// <summary>
        /// **磁盘上的库是不是比缓存里的新**：`ai.laya.status` 用它提示
        /// "你改了库，游戏还没重读 （下一次判定就会自动重读）。与解析时的失效判据是同一个函数，
        /// 于是"提示"与"实际行为"不可能不一致。
        /// </summary>
        public bool IsBankStale(QuestionBank bank)
        {
            return bank != null && HasBankFileChanged(bank);
        }
        private bool HasBankFileChanged(QuestionBank bank)
        {
            if (bank == null || string.IsNullOrEmpty(bank.SourcePath))
                return true;

            string stamp;
            if (!m_loadedStamps.TryGetValue(BankStampKey(bank), out stamp))
                return true;
            return !string.Equals(stamp, StampOf(bank.SourcePath), StringComparison.Ordinal);
        }

        private string BankStampKey(QuestionBank bank)
        {
            return bank.SourcePath ?? string.Empty;
        }

        /// <summary>`时间戳ticks:长度`。取不到文件（被删/被锁）返回 "<missing>"，于是判成"变了"。</summary>
        private static string StampOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return "<missing>";
                var info = new FileInfo(path);
                return info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length.ToString();
            }
            catch (Exception)
            {
                return "<missing>";
            }
        }

        /// <summary>已缓存的问题库对象（`ai.laya.status` 的版本对账用；顺序稳定）。</summary>
        public List<QuestionBank> CachedBanks()        {
            var banks = new List<QuestionBank>();
            foreach (QuestionBank bank in m_banks.Values)
                banks.Add(bank);
            banks.Sort(delegate(QuestionBank a, QuestionBank b)
            {
                return string.Compare(a != null ? a.Id : null, b != null ? b.Id : null,
                    StringComparison.OrdinalIgnoreCase);
            });
            return banks;
        }

        /// <summary>已缓存的问题库（`ai.laya.status` 用）。</summary>
        public List<string> CachedBankNames()        {
            var names = new List<string>(m_banks.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>现状（`ai.laya.status`；**不含密钥本体**）。</summary>
        public Dictionary<string, object> Describe()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["config"] = Config.Describe(),
                ["bankDirectories"] = new List<string>(m_bankDirectories),
                ["cachedBanks"] = CachedBankNames(),
                ["lastBankError"] = m_lastBankError,
                ["client"] = m_client.Describe()
            };
            return info;
        }

        /// <summary>由实例根算出问题库目录（与包/脚本目录同源）。定义在纯逻辑层，这里只转调。</summary>
        public static List<string> BankDirectoriesFor(string instanceRoot)
        {
            return QuestionBankDirectorySource.DirectoriesFor(instanceRoot);
        }

        /// <summary>安装出厂问题库（缺什么补什么，绝不覆盖）。</summary>
        public static int InstallTemplates(string instanceRoot, out string error)
        {
            List<string> installed;
            return QuestionBankTemplates.Install(instanceRoot, out installed, out error);
        }
    }
}
