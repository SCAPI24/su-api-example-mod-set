using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作脚本（`.aeact` / 内嵌 JSON）：**"基础动作包混搭"的载体**（plan §3.2）。
    ///
    /// 与 `.scatpak`（录制回放）的关系：录制是"人教的一条",脚本是"可参数化、可组合的积木序列"。
    /// 两者最终都喂给同一个执行器（§3.3）。
    ///
    /// ```json
    /// { "format": "aea", "version": 1, "id": "mine_stone_once",
    ///   "guards": ["alive", "world.loaded", "modal.none"],
    ///   "onFail": "abort",
    ///   "steps": [ { "verb": "hotbar", "slot": 1 }, { "verb": "dig", "ms": 900 } ] }
    /// ```
    /// </summary>
    public sealed class ActionScript
    {
        public const string FormatName = "aea";
        public const int FormatVersion = 1;
        public const string Extension = ".aeact";

        public string Id;
        public string Name;
        public string Description;

        /// <summary>前置守卫（词汇与 `obs.waitFor` 的条件一致，见 <see cref="ActionGuards"/>）。</summary>
        public readonly List<string> Guards = new List<string>();

        /// <summary>`abort` / `retry:N` / `next`。</summary>
        public string OnFail = "abort";

        public readonly List<ActionStep> Steps = new List<ActionStep>();

        /// <summary>来源信息（日志/复盘用；不参与执行）。</summary>
        public string SourcePath;
        public string SourceHash;

        public double DeclaredDurationMs
        {
            get
            {
                double total = 0.0;
                for (int i = 0; i < Steps.Count; i++)
                    total += Steps[i].Repeat * Steps[i].TimeoutMs;
                return total;
            }
        }

        public string Describe()
        {
            return "script " + (Id ?? "?") + " steps=" + Steps.Count
                + " guards=" + Guards.Count + " onFail=" + OnFail;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>脚本里的一步：一个 verb + 参数 + 重复/超时。</summary>
    public sealed class ActionStep
    {
        public string Verb;
        public readonly ActionArgs Args = new ActionArgs();

        /// <summary>重复次数（≥1）。</summary>
        public int Repeat = 1;

        /// <summary>本步超时（毫秒）；0 = 用编译时长 × 系数自动算。</summary>
        public int TimeoutMs;

        /// <summary>本步失败后是否继续（默认 false = 整个脚本按 onFail 处理）。</summary>
        public bool ContinueOnFail;

        /// <summary>原始 JSON（导出/编辑器回写用，保证不丢字段）。</summary>
        public PackageValue Raw;

        public string Describe()
        {
            return Verb + "(" + Args + ")" + (Repeat > 1 ? " x" + Repeat : string.Empty)
                + (TimeoutMs > 0 ? " timeout=" + TimeoutMs + "ms" : string.Empty);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>脚本解析结果（成功带脚本，失败带错误码）。</summary>
    public sealed class ActionScriptParseResult
    {
        public ActionScript Script;
        public string Error;

        public bool Ok
        {
            get { return Script != null && string.IsNullOrEmpty(Error); }
        }
    }

    /// <summary>
    /// 动作脚本的解析与校验。**规则只有一份**：解析器与 `ai.action.script.validate`、
    /// 编辑器保存前预检都走这里（对齐 plan §7.3「前端不自己实现规则」）。
    /// </summary>
    public static class ActionScriptParser
    {
        public static ActionScriptParseResult Parse(PackageValue root, string sourcePath = null)
        {
            var result = new ActionScriptParseResult();
            if (root == null || !root.IsObject)
            {
                result.Error = ActionErrorCodes.InvalidArgument + ": script must be a JSON object";
                return result;
            }

            string format = root.Get("format").AsString(null);
            if (!string.IsNullOrEmpty(format)
                && !string.Equals(format, ActionScript.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = ActionErrorCodes.InvalidArgument + ": unsupported format '" + format
                    + "' (expected '" + ActionScript.FormatName + "')";
                return result;
            }

            int version = root.Get("version").AsInt(ActionScript.FormatVersion);
            if (version != ActionScript.FormatVersion)
            {
                result.Error = ActionErrorCodes.InvalidArgument + ": unsupported script version " + version
                    + " (this build reads " + ActionScript.FormatVersion + ")";
                return result;
            }

            var script = new ActionScript
            {
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                Description = root.Get("description").AsString(null),
                SourcePath = sourcePath
            };

            PackageValue guards = root.Get("guards");
            if (guards.IsArray)
            {
                for (int i = 0; i < guards.Count; i++)
                {
                    string guard = guards.Item(i).AsString(null);
                    if (string.IsNullOrEmpty(guard))
                        continue;
                    if (!ActionGuards.IsKnown(guard))
                    {
                        result.Error = ActionErrorCodes.InvalidArgument + ": unknown guard '" + guard
                            + "' (known: " + string.Join(", ", ActionGuards.Names) + ")";
                        return result;
                    }
                    script.Guards.Add(guard);
                }
            }

            string onFail = root.Get("onFail").AsString(null);
            if (!string.IsNullOrEmpty(onFail))
            {
                if (!IsKnownOnFail(onFail))
                {
                    result.Error = ActionErrorCodes.InvalidArgument + ": unknown onFail '" + onFail
                        + "' (expected abort / retry:N / next)";
                    return result;
                }
                script.OnFail = onFail.Trim().ToLowerInvariant();
            }

            PackageValue steps = root.Get("steps");
            if (!steps.IsArray || steps.Count == 0)
            {
                result.Error = ActionErrorCodes.InvalidArgument + ": script needs a non-empty 'steps' array";
                return result;
            }
            if (steps.Count > ActionCompileDefaults.MaxSteps)
            {
                result.Error = ActionErrorCodes.InvalidArgument + ": too many steps (" + steps.Count
                    + ", limit " + ActionCompileDefaults.MaxSteps + ")";
                return result;
            }

            for (int i = 0; i < steps.Count; i++)
            {
                PackageValue item = steps.Item(i);
                if (!item.IsObject)
                {
                    result.Error = ActionErrorCodes.InvalidArgument + ": steps[" + i + "] must be an object";
                    return result;
                }

                string verb = item.Get("verb").AsString(null);
                if (string.IsNullOrEmpty(verb))
                {
                    result.Error = ActionErrorCodes.InvalidArgument + ": steps[" + i + "] has no 'verb'";
                    return result;
                }

                ActionVerbSpec spec;
                if (!ScriptCompiler.TryGetVerb(verb, out spec))
                {
                    result.Error = ActionErrorCodes.VerbUnknown + ": steps[" + i + "] unknown verb '"
                        + verb + "' (known: " + string.Join(", ", ScriptCompiler.VerbNames) + ")";
                    return result;
                }

                var step = new ActionStep
                {
                    Verb = spec.Name,
                    Repeat = Math.Max(1, item.Get("repeat").AsInt(1)),
                    TimeoutMs = Math.Max(0, item.Get("timeoutMs").AsInt(0)),
                    ContinueOnFail = item.Get("continueOnFail").AsBool(false),
                    Raw = item
                };

                // verb 参数 = steps[i] 里除控制字段外的全部成员（于是参数表是唯一权威）
                foreach (string member in item.MemberNames)
                {
                    if (IsControlField(member))
                        continue;
                    MergeArg(step.Args, member, item.Get(member));
                }

                List<string> unknown = step.Args.UnknownNames(spec.Parameters);
                if (unknown.Count > 0)
                {
                    result.Error = ActionErrorCodes.InvalidArgument + ": steps[" + i + "] unknown parameter(s) for '"
                        + spec.Name + "': " + string.Join(", ", unknown);
                    return result;
                }

                for (int p = 0; p < spec.Parameters.Count; p++)
                {
                    ActionParamSpec param = spec.Parameters[p];
                    if (param.Required && !step.Args.Has(param.Name))
                    {
                        result.Error = ActionErrorCodes.InvalidArgument + ": steps[" + i + "] '" + param.Name
                            + "' is required for " + spec.Name;
                        return result;
                    }
                }

                script.Steps.Add(step);
            }

            result.Script = script;
            return result;
        }

        /// <summary>便捷解析：直接吃 JSON 文本（复用包格式的严格 JSON 解析器，规则只有一份）。</summary>
        public static ActionScriptParseResult ParseJson(string json, string sourcePath = null)
        {
            var result = new ActionScriptParseResult();
            var report = new PackageReport();
            PackageValue value;
            if (!PackageJson.TryParse(json ?? string.Empty, sourcePath ?? "script", report, out value))
            {
                PackageIssue first = report.FirstError;
                result.Error = ActionErrorCodes.InvalidArgument + ": "
                    + (first != null ? first.Describe() : "invalid JSON");
                return result;
            }
            return Parse(value, sourcePath);
        }

        private static readonly string[] ControlFields =
        {
            "verb", "repeat", "timeoutMs", "continueOnFail", "when", "note"
        };

        private static bool IsControlField(string name)
        {
            for (int i = 0; i < ControlFields.Length; i++)
            {
                if (string.Equals(ControlFields[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static ActionArgs MergeArg(ActionArgs args, string name, PackageValue value)
        {
            args.Set(name, value);
            return args;
        }

        private static bool IsKnownOnFail(string value)
        {
            string text = value.Trim().ToLowerInvariant();
            if (text == "abort" || text == "next")
                return true;
            if (text.StartsWith("retry:", StringComparison.Ordinal))
            {
                int times;
                return int.TryParse(text.Substring(6), out times) && times >= 0;
            }
            return false;
        }
    }

    /// <summary>
    /// 脚本守卫的词汇表 —— **刻意与 `obs.waitFor` 的条件词汇一致**（不新造一套条件语言，plan §3.2）。
    /// 真正的判定在游戏侧（CmdBridge 的观察层）；这里只维护"合法名字"与说明，供解析器与编辑器共用。
    /// </summary>
    public static class ActionGuards
    {
        public const string Alive = "player.alive";
        public const string Dead = "player.dead";
        public const string WorldLoaded = "world.loaded";
        public const string WorldUnloaded = "world.unloaded";
        public const string NoModal = "modal.none";
        public const string NoDialog = "dialog.none";
        public const string Awake = "player.awake";
        public const string Sleeping = "player.sleeping";

        /// <summary>`aim.block` / `aim.entity` / `aim.none` —— 准星此刻指着什么。</summary>
        public const string AimBlock = "aim.block";
        public const string AimEntity = "aim.entity";
        public const string AimNone = "aim.none";

        public static readonly string[] Names =
        {
            Alive, Dead, WorldLoaded, WorldUnloaded, NoModal, NoDialog, Awake, Sleeping,
            AimBlock, AimEntity, AimNone
        };

        /// <summary>
        /// 带参数的条件前缀（`modal.is:X` / `screen.is:X` / `element.clickable:X`）。
        ///
        /// 与 <see cref="ActionGuardRules.Wired"/> 必须一致：**判定实现**在那边，
        /// 这里只是给解析器/编辑器用的"合法名字表"。加了新前缀却没实现，
        /// 运行时会以 `target_unavailable` 失败（这是有意的：判不了绝不等于通过）。
        /// </summary>
        public static readonly string[] Prefixes =
        {
            ActionGuardRules.ModalPrefix, ActionGuardRules.ScreenPrefix,
            ActionGuardRules.ElementPresentPrefix, ActionGuardRules.ElementHittablePrefix,
            ActionGuardRules.ElementClickablePrefix, ActionGuardRules.EventsSincePrefix
        };

        public static bool IsKnown(string guard)
        {
            if (string.IsNullOrEmpty(guard))
                return false;
            string text = guard.Trim();
            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            for (int i = 0; i < Prefixes.Length; i++)
            {
                if (text.StartsWith(Prefixes[i], StringComparison.OrdinalIgnoreCase)
                    && text.Length > Prefixes[i].Length)
                {
                    return true;
                }
            }
            return false;
        }

        public static string Describe()
        {
            return string.Join(", ", Names) + " | "
                + string.Join(", ", Prefixes) + "<值>";
        }
    }
}
