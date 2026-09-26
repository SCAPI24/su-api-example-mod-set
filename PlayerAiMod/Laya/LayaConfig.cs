using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// Laya（System One 判定服务）的配置与会话（plan §5.1/§5.5）。
    ///
    /// **密钥预填链**（照 DSH 那张卡的做法，优先级从高到低）：
    ///   1. 环境变量 `LAYA_API_KEY`（人工/启动脚本给）
    ///   2. `<实例根>/PlayerAi/Laya.local.json`（编辑器「🔑 设密钥」写入；文件名带 `.local.` 是为了让
    ///      `Mod/.gitignore` 与根 `.gitignore` 都能挡住它 —— 铁律：密钥绝不进被跟踪文件）
    ///   3. 空 → 报稳定错误码 `laya_no_key`（不是抛一个泛化的 401）
    ///
    /// 一份配置管全部 Laya 参数（D16）：端点 / 模型 / 密钥 / 超时 / 预算 / 重取。
    /// </summary>
    public sealed class LayaConfig
    {
        public const string ConfigFileName = "Laya.local.json";
        public const string EnvApiKey = "LAYA_API_KEY";

        /// <summary>默认端点（只填到端口，`/v1/systemone` 由客户端补）。</summary>
        public const string DefaultBaseUrl = "http://127.0.0.1:8770";

        public const string DefaultModel = "laya-multilingual-f16.gguf";

        /// <summary>单次请求超时（毫秒）。实测 200 字摘要 3~6 问 ≈ 0.4~1.6 s，留足余量。</summary>
        public const int DefaultTimeoutMs = 1500;

        /// <summary>去重窗口（毫秒）：同一题面在此之内只发一次（选择器每帧重访节点是常态）。</summary>
        public const int DefaultRefreshMs = 1000;

        /// <summary>
        /// 上线状态默认上限。32 是量出来的：决策事实排第一时，
        /// 18 / 29 / 35 字符三种长度都跟着状态走（eat/eat/mine 全对），
        /// 38 字符起就开始乱（fight）——所以取在**明确安全**的那一侧。
        /// </summary>
        public const int DefaultStateChars = 28;

        public string BaseUrl = DefaultBaseUrl;
        public string Model = DefaultModel;
        public int TimeoutMs = DefaultTimeoutMs;
        public int RefreshMs = DefaultRefreshMs;
        public int DigestBudgetChars = StateDigestCompiler.DefaultBudgetChars;

        /// <summary>
        /// **发上线那条状态**的长度上限（与 `DigestBudgetChars` 分开）。
        ///
        /// 为什么要分两份：2026-09-26 实测出这个模型只看摘要开头 —— 决策事实排第一时，
        /// ≤35 字符能跟着状态走，38/51/92 字符就分别给出 fight / sleep / mine（各 3 次一致），
        /// 而出厂摘要当时是 97~120 字符，等于**跑在塌掉那一档**里。
        /// 所以：**发上去的短**（默认 32），**给人看/复盘的那份仍然完整**（`DigestBudgetChars`=200），
        /// 后者走 `state.digest` 与 `ai.laya.ask digest=`，不参与决策。
        /// </summary>
        public int StateChars = DefaultStateChars;
        public int HeadMaxLen = QuestionBankParser.DefaultHeadMaxLen;
        public int MaxLen = QuestionBankParser.DefaultMaxLen;

        /// <summary>密钥（来自环境变量或本地文件；**永不写日志**）。</summary>
        public string ApiKey;

        /// <summary>密钥来源：`env` / `local` / `none`（`ai.action.script.status` 会显示它）。</summary>
        public string ApiKeySource = "none";

        /// <summary>配置文件路径（读过就记下来，便于排障）。</summary>
        public string SourcePath;

        public bool Enabled = true;

        /// <summary>`/v1/systemone` 的完整地址。</summary>
        public string SystemOneUrl
        {
            get { return Trim(BaseUrl) + "/v1/systemone"; }
        }

        public string ModelsUrl
        {
            get { return Trim(BaseUrl) + "/v1/models"; }
        }

        public bool HasKey
        {
            get { return !string.IsNullOrEmpty(ApiKey); }
        }

        /// <summary>掩码后的密钥（日志/HUD 用；服务端 `/api/usage` 自己也是这么显示的）。</summary>
        public string MaskedKey
        {
            get
            {
                if (string.IsNullOrEmpty(ApiKey))
                    return "<none>";
                if (ApiKey.Length <= 12)
                    return "sk-****";
                return ApiKey.Substring(0, 8) + "…" + ApiKey.Substring(ApiKey.Length - 4);
            }
        }

        private static string Trim(string url)
        {
            return string.IsNullOrEmpty(url) ? DefaultBaseUrl : url.TrimEnd('/');
        }

        /// <summary>
        /// 按预填链装载配置。永不抛异常；配置有问题时**保留默认值 + 记下原因**
        /// （调用方通过 <see cref="LoadError"/> 判断"是不是配置本身的问题"）。
        /// </summary>
        public static LayaConfig Load(string instanceRoot)
        {
            var config = new LayaConfig();

            // 1) 本地配置文件（不存在就跳过，不算错误）
            string path = !string.IsNullOrEmpty(instanceRoot)
                ? Path.Combine(instanceRoot, "PlayerAi", ConfigFileName)
                : null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config.SourcePath = path;
                try
                {
                    var report = new PackageReport();
                    PackageValue root;
                    if (PackageJson.TryParse(File.ReadAllText(path), path, report, out root)
                        && root != null && root.IsObject)
                    {
                        config.BaseUrl = root.Get("baseURL").AsString(config.BaseUrl) ?? config.BaseUrl;
                        config.Model = root.Get("model").AsString(config.Model) ?? config.Model;
                        config.TimeoutMs = root.Get("timeoutMs").AsInt(config.TimeoutMs);
                        config.RefreshMs = root.Get("refreshMs").AsInt(config.RefreshMs);
                        config.DigestBudgetChars = root.Get("digestBudgetChars").AsInt(config.DigestBudgetChars);
            config.StateChars = root.Get("stateChars").AsInt(config.StateChars);
                        config.HeadMaxLen = root.Get("headMaxLen").AsInt(config.HeadMaxLen);
                        config.MaxLen = root.Get("maxLen").AsInt(config.MaxLen);
                        config.Enabled = root.Get("enabled").AsBool(true);
                        string key = root.Get("apiKey").AsString(null);
                        if (!string.IsNullOrEmpty(key))
                        {
                            config.ApiKey = key;
                            config.ApiKeySource = "local";
                        }
                    }
                    else
                    {
                        config.LoadError = "config file is not a JSON object: " + path;
                    }
                }
                catch (Exception exception)
                {
                    config.LoadError = exception.GetType().Name + ": " + exception.Message;
                }
            }

            // 2) 环境变量优先（人工/启动脚本覆盖文件）
            try
            {
                string fromEnv = Environment.GetEnvironmentVariable(EnvApiKey);
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    config.ApiKey = fromEnv.Trim();
                    config.ApiKeySource = "env";
                }
            }
            catch (Exception)
            {
                // 环境变量读不到就当没有（不改变别的字段）
            }

            config.Validate();
            return config;
        }

        /// <summary>装载期问题（人类可读；null = 没问题）。</summary>
        public string LoadError;

        public void Validate()
        {
            if (string.IsNullOrEmpty(BaseUrl))
                BaseUrl = DefaultBaseUrl;
            if (string.IsNullOrEmpty(Model))
                Model = DefaultModel;
            if (TimeoutMs < 100)
                TimeoutMs = 100;
            if (TimeoutMs > 30000)
                TimeoutMs = 30000;
            if (RefreshMs < 0)
                RefreshMs = 0;
            if (DigestBudgetChars < 40)
                DigestBudgetChars = 40;
            if (DigestBudgetChars > 400)
                DigestBudgetChars = 400;
            // 上线状态的下限放到 16：实测"只发一个事实"（18 字符）是**最可靠**的那一档，
            // 40 的下限会把它挡在门外 —— 下限卡在 40 正是旧设计的一部分（那时只有一份摘要）。
            if (StateChars < 16)
                StateChars = 16;
            if (StateChars > 400)
                StateChars = 400;
        }

        /// <summary>给人看的现状（**不含密钥本体**）。</summary>
        public Dictionary<string, object> Describe()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = Enabled,
                ["baseUrl"] = BaseUrl,
                ["model"] = Model,
                ["timeoutMs"] = TimeoutMs,
                ["refreshMs"] = RefreshMs,
                ["digestBudgetChars"] = DigestBudgetChars,
                ["stateChars"] = StateChars,
                ["headMaxLen"] = HeadMaxLen,
                ["maxLen"] = MaxLen,
                ["keySource"] = ApiKeySource,
                ["keyMasked"] = MaskedKey,
                ["configPath"] = SourcePath,
                ["loadError"] = LoadError
            };
        }

        public override string ToString()
        {
            return "laya(" + BaseUrl + " model=" + Model + " key=" + ApiKeySource + " timeout="
                + TimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms)";
        }
    }
}
