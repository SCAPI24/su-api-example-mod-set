using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Engine;
using System.Xml.Linq;
using Engine.Content;
using Engine.Media;
using Game;
using SuAPI;

namespace TranslationMod
{
    /// <summary>
    /// 翻译处理器：从 Content/zh_CN.xml 加载翻译（XML 格式），
    /// 运行时按 Screen 分类收集字符串写入 Logs/zh_CN.xml，组内 ABC 排序。
    /// 支持模板占位符：{0}{1}...
    /// </summary>
    internal sealed class TranslationProcessor
    {
        /// <summary>
        /// 获取翻译导出目录（与 GameLogSink 一致）。
        /// 必须保留 data: 前缀并通过 Storage 访问，Android 才会落在公开 Downloads/Survivalcraft/Logs。
        /// Source: Survivalcraft/Game/GameLogSink.cs:GameLogSink.GameLogSink
        /// </summary>
        public static string GetLogsDir()
        {
            return "data:/Logs";
        }

        /// <summary>
        /// Mod 版本号。导出表头写入「版本.条目数.词表哈希」，用来判断导出表是否落后于
        /// 随 Mod 发布的 Content/zh_CN.xml。改版本时同步 ModInfo.xml。
        /// </summary>
        public const string ModVersion = "1.6.49";

        /// <summary>
        /// 当前词表指纹「Mod版本.条目数.内容哈希」。导出表头存这个值，
        /// 下次启动比对即可判断导出表是否需要刷新。
        /// </summary>
        public static string ComputeTableVersion()
        {
            try
            {
                XElement source = ContentCache.Get<XElement>("Mod/zh_CN", false);
                if (source == null)
                    return ModVersion + ".0.none";
                int count = 0;
                foreach (XElement entry in source.Descendants("Entry"))
                    count++;
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(source.ToString());
                uint hash = 2166136261u;
                for (int i = 0; i < bytes.Length; i++)
                {
                    hash ^= bytes[i];
                    hash *= 16777619u;
                }
                return ModVersion + "." + count.ToString() + "." + hash.ToString("x8");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Translator] Failed to compute table version: {ex.Message}");
                return ModVersion + ".0.error";
            }
        }

        /// <summary>
        /// 导出表版本与当前词表不一致时静默删除该文件，丢弃已累积的内容。
        /// 只比对根节点的 Version 属性：读不到（含 XML 损坏）一律按落后处理。
        /// 删除后由 SeedExportFileAsync 用当前词表重新播种，UI 不做任何提示。
        /// Source: Engine/Engine/Storage.cs:Storage.DeleteFile
        /// </summary>
        public static void DiscardStaleExportFile()
        {
            string path = Storage.CombinePaths(GetLogsDir(), "zh_CN.xml");
            if (!Storage.FileExists(path))
                return;

            string current = ComputeTableVersion();
            string found = null;
            try
            {
                using (var stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    XDocument document = XDocument.Load(stream);
                    found = (string)document.Root?.Attribute("Version");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[Translator] Export table unreadable, discarding: {ex.Message}");
            }

            if (found == current)
                return;

            string shown = string.IsNullOrEmpty(found) ? "(none)" : found;
            try
            {
                Storage.DeleteFile(path);
                Log.Information($"[Translator] Discarded stale export table "
                    + $"(found {shown}, current {current}).");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Translator] Failed to discard stale export table: {ex.Message}");
            }
        }
        // 原文 → 译文（加载自 Content/zh_CN.xml）
        private static readonly Dictionary<string, string> Translations = new Dictionary<string, string>();

        // 运行时收集：Screen名称 → (原文 → 译文)
        private static readonly Dictionary<string, Dictionary<string, string>> _collected = new Dictionary<string, Dictionary<string, string>>();
        private static readonly List<string> _screenOrder = new List<string>();
        // 原文去重（全局）
        private static readonly HashSet<string> _seenOriginals = new HashSet<string>();

        // 当前 Screen 名称（由调用方在处理前设置）
        public static string CurrentScreen { get; set; } = "StringsManager";

        // 模板正则缓存
        // 模板列表，按 key 长度降序。越长的模板越具体，必须先试；Dictionary 的遍历顺序
        // 没有语言保证，若 "{0} ({1})" 抢在 "{0} (recipe #{1})" 前面，配方标题会被拆错。
        private List<(string key, Regex regex, string outputTemplate)> _templateRegexes;

        private static readonly object _lock = new object();
        private static bool _hasCollectedChanges;

        /// <summary>
        /// 「重新记录」总开关。关闭后译文**只**从 .scmod 包内的 `Content/zh_CN.xml` 读取，
        /// 不再采集新字符串、也不再往 `Logs/zh_CN.xml` 播种或落盘。
        /// 对外接口（TranslationApi / Translate / Register / RegisterWidget…）与全部调用点保持不变。
        /// </summary>
        private const bool RecordingEnabled = false;
        /// <summary>
        /// 从 Content/zh_CN.xml 加载翻译（ContentCache key: Mod/zh_CN，.xml 自动加载为 string）
        /// </summary>
        public static void LoadTranslations()
        {
            try
            {
                var root = ContentCache.Get<XElement>("Mod/zh_CN", false);
                if (root == null)
                {
                    Log.Warning("[Translator] Content/zh_CN.xml not found, starting empty.");
                    return;
                }

                foreach (var screenEl in root.Elements("Screen"))
                {
                    foreach (var el in screenEl.Elements("Entry"))
                    {
                        string original = (string)el.Attribute("Original");
                        string translation = (string)el.Attribute("Translation");
                        if (!string.IsNullOrEmpty(original) && translation != null)
                            Translations[original] = translation;
                    }
                }
                // 兼容旧格式：根下直接 <Entry .../>
                foreach (var el in root.Elements("Entry"))
                {
                    string original = (string)el.Attribute("Original");
                    string translation = (string)el.Attribute("Translation");
                    if (!string.IsNullOrEmpty(original) && translation != null)
                        Translations[original] = translation;
                }

                Log.Information($"[Translator] Loaded {Translations.Count} translations from Content/zh_CN.xml.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Translator] Failed to load translations: {ex}");
            }
        }

        /// <summary>
        /// 首次运行时异步导出内置词表，供翻译维护使用。复制 XML 根节点，避免把 ContentCache
        /// 已持有的节点再次挂入 XDocument 而导致 Android 写入失败。
        /// Source: Survivalcraft/Game/GameLogSink.cs:GameLogSink.GameLogSink
        /// </summary>
        public static void SeedExportFileAsync()
        {
            if (!RecordingEnabled) return;
            string path = Storage.CombinePaths(GetLogsDir(), "zh_CN.xml");
            if (Storage.FileExists(path))
                return;

            XElement source = ContentCache.Get<XElement>("Mod/zh_CN", false);
            if (source == null)
                return;

            XElement root = new XElement(source);
            root.SetAttributeValue("Version", ComputeTableVersion());
            string text = new XDocument(root).ToString();
            _ = Task.Run(delegate
            {
                try
                {
                    lock (_lock)
                    {
                        if (Storage.FileExists(path))
                            return;
                        Storage.CreateDirectory(GetLogsDir());
                        using (var stream = Storage.OpenFile(path, OpenFileMode.Create))
                        using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                            writer.Write(text);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[Translator] Failed to seed export file: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 将收集到的字符串按 Screen 分类追加写入 Logs/zh_CN.xml（合并已有，去重，组内 ABC 排序）
        /// </summary>
        public static void SaveCollected()
        {
            if (!RecordingEnabled) return;
            lock (_lock)
            {
                if (!_hasCollectedChanges || _collected.Count == 0)
                    return;

                try
                {
                    string savePath = Storage.CombinePaths(GetLogsDir(), "zh_CN.xml");

                    Storage.CreateDirectory(GetLogsDir());

                    // 加载已有条目 → 按 Screen 分组 + 已见 Original
                    var existingScreens = new Dictionary<string, Dictionary<string, string>>();
                    var existingOriginals = new HashSet<string>();
                    if (Storage.FileExists(savePath))
                    {
                        try
                        {
                            // AOT-safe: XDocument.Load(string) may be trimmed, use Stream
                            using (var fs = Storage.OpenFile(savePath, OpenFileMode.Read))
                            {
                                var existing = XDocument.Load(fs);
                                foreach (var screenEl in existing.Root.Elements("Screen"))
                                {
                                    string sn = (string)screenEl.Attribute("Name") ?? "";
                                    if (!existingScreens.ContainsKey(sn))
                                        existingScreens[sn] = new Dictionary<string, string>();
                                    foreach (var el in screenEl.Elements("Entry"))
                                    {
                                        string orig = (string)el.Attribute("Original");
                                        string trans = (string)el.Attribute("Translation");
                                        if (!string.IsNullOrEmpty(orig) && existingOriginals.Add(orig))
                                            existingScreens[sn][orig] = trans ?? orig;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // 追加新条目
                    int appended = 0;
                    foreach (var screen in _screenOrder)
                    {
                        if (!_collected.TryGetValue(screen, out var entries)) continue;
                        if (!existingScreens.ContainsKey(screen))
                            existingScreens[screen] = new Dictionary<string, string>();
                        var target = existingScreens[screen];
                        foreach (var kv in entries)
                        {
                            if (existingOriginals.Add(kv.Key))
                            {
                                target[kv.Key] = kv.Value;
                                appended++;
                            }
                        }
                    }

                    // 构建输出 XML：Screen 分组 × 组内 ABC 排序
                    var doc = new XDocument();
                    doc.Add(new XElement("Translations",
                        new XAttribute("Version", ComputeTableVersion())));
                    foreach (var screenKv in existingScreens)
                    {
                        var screenEl = new XElement("Screen", new XAttribute("Name", screenKv.Key));
                        var sorted = new List<KeyValuePair<string, string>>(screenKv.Value);
                        // Manual sort (AOT-safe: List.Sort(Comparison) may be trimmed)
                        for (int si = 0; si < sorted.Count - 1; si++)
                            for (int sj = si + 1; sj < sorted.Count; sj++)
                                if (string.CompareOrdinal(sorted[si].Key, sorted[sj].Key) > 0)
                                    { var tmp = sorted[si]; sorted[si] = sorted[sj]; sorted[sj] = tmp; }
                        foreach (var kv in sorted)
                            screenEl.Add(new XElement("Entry", new XAttribute("Original", kv.Key), new XAttribute("Translation", kv.Value)));
                        doc.Root.Add(screenEl);
                    }

                    using (var fs = Storage.OpenFile(savePath, OpenFileMode.Create))
                    using (var sw = new System.IO.StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                        sw.Write(doc.ToString());
                    _hasCollectedChanges = false;
                    _collected.Clear();
                    _screenOrder.Clear();
                    Log.Information($"[Translator] Appended {appended} new strings (total: {existingOriginals.Count}) to {savePath}.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Translator] Failed to save: {ex}");
                }
            }
        }

        /// <summary>
        /// 应用已有译文；只有固定的原版/SuAPI 界面允许导出未知文本。
        /// Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
        /// </summary>
        public string Process(string key, string original, int index, bool collectForExport)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            // 1. 精确匹配（模板层面 + 普通字符串）
            if (Translations.TryGetValue(original, out var translated))
            {
                return translated;
            }

            // 2. 标题尾部冒号。统计面板的行是 AddStat 拼出来的 title + ":"，必须在模板之前处理：
            //    否则模板会把冒号一起吞进捕获组（"Day 41:" 曾变成 "第 41: 天"）。
            //    Source: Survivalcraft/Game/GameMenuDialog.cs:GameMenuDialog.AddStat
            if (TryProcessColonTitle(original, out string titled))
                return titled;

            // 3. 反向模板匹配：格式化后的字符串（如 "5 recipes"）匹配模板（如 "{0} recipes"）
            if (TryMatchTemplate(original, out string templated))
                return templated;

            // 4. 逐行匹配：属性名/属性值是逐行累加出来的复合串，组合数随方块属性变化，
            //    穷举不现实，只能按行翻。
            //    Source: Survivalcraft/Game/RecipaediaDescriptionScreen.cs:RecipaediaDescriptionScreen.UpdateBlockProperties
            //    Source: Survivalcraft/Game/BestiaryDescriptionScreen.cs:BestiaryDescriptionScreen.Update
            if (TryProcessLines(original, out string lineWise))
                return lineWise;

            // 5. 去掉首尾留白再查一次：有些界面把文本右对齐到固定宽度后当成单行显示
            //    （LevelFactorDialog 的 TotalName 是 string.Format("{0,24}", "TOTAL")），
            //    精确匹配和逐行回退都够不着。
            //    Source: Survivalcraft/Game/LevelFactorDialog.cs:LevelFactorDialog.LevelFactorDialog
            if (TrySingleLineLookup(original, out string singleLine))
                return singleLine;

            // 6. 用 " | " 拼出来的信息行。世界列表把大小/时间/人数/模式/环境拼成一行：
            //    "193KB | 15 Sep 2026 21:25 | 1 player | Creative | Living"，
            //    精确匹配和模板都够不着整串，只能拆段逐段翻。
            //    Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
            //    Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs:SuPlayScreen.UpdateWorldItemWidget
            if (TryProcessSegments(original, out string segmented))
                return segmented;

            // 7. "<固定文案> <数据量>"：社区/外部内容列表行的 Details 字段
            //    （"World 1.2MB"）。只在尾部确实像数据量时才处理。
            //    Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.CommunityContentScreen
            if (TryProcessLabelWithSize(original, out string withSize))
                return withSize;

            // 未翻译的动态内容、社区内容和外置 Mod 文本不进入导出表。
            if (collectForExport)
                CollectForExport(original, original);
            return original;
        }

        /// <summary>
        /// 反向模板匹配：把格式化后的字符串（如 "Yes (up to 40)"）匹配到模板条目
        /// （如 "Yes (up to {0})"）。模板条目由 BuildTemplateRegexes 编译成正则。
        /// </summary>
        /// <summary>模板里 {0} 这类占位符最多允许多长的捕获，超过就认为误命中长文本。</summary>
        private const int MaxTemplateCaptureLength = 32;

        private bool TryMatchTemplate(string original, out string result)
        {
            result = null;
            if (_templateRegexes == null)
                BuildTemplateRegexes();

            foreach (var tp in _templateRegexes)
            {
                var match = tp.regex.Match(original);
                if (!match.Success)
                    continue;

                // 护栏：捕获组过长说明是宽模板误命中长文本（如 "{0} days" 命中一整段话），
                // 这种交给后面的逐行匹配，别整段改写
                bool sane = true;
                for (int j = 1; j < match.Groups.Count; j++)
                {
                    if (match.Groups[j].Length > MaxTemplateCaptureLength)
                    {
                        sane = false;
                        break;
                    }
                }
                if (!sane)
                    continue;

                result = tp.outputTemplate;
                for (int j = 0; j < match.Groups.Count - 1; j++)
                {
                    string captured = match.Groups[j + 1].Value;
                    // 捕获组本身也可能是需要翻译的固定文案。例如配方标题是
                    // 方块名 + " (recipe #N)" 拼出来的，模板只会把原文方块名填回去，
                    // 这里再查一次词表，标题才会是「泥土（配方#1）」而不是「Dirt（配方#1）」。
                    // Source: Survivalcraft/Game/CraftingRecipeWidget.cs:CraftingRecipeWidget.UpdateWidgets
                    // 大小写不敏感再兜一次：死亡原因是 "{KillVerb} by a {DisplayName.ToLower()}"，
                    // 例如 "bitten by a gray wolf"，词表里存的是 "Gray Wolf"。
                    // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.MineBlock
                    if (!Translations.TryGetValue(captured, out string capturedTranslation))
                        capturedTranslation = LookupIgnoreCase(captured);
                    if (capturedTranslation != null)
                        captured = capturedTranslation;
                    result = result.Replace("{" + j + "}", captured);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 逐行翻译多行复合串，保留行数、顺序和行尾空行。
        /// 每行依次尝试：整行精确匹配 → 反向模板 → 去掉末尾冒号后按标签匹配（补全角冒号）。
        /// 一行都没命中就返回 false，整串仍走原路径（包括导出收集）。
        /// Source: Survivalcraft/Game/RecipaediaDescriptionScreen.cs:RecipaediaDescriptionScreen.UpdateBlockProperties
        /// Source: Survivalcraft/Game/BestiaryDescriptionScreen.cs:BestiaryDescriptionScreen.Update
        /// </summary>
        private bool TryProcessLines(string original, out string result)
        {
            result = null;
            if (original.IndexOf('\n') < 0)
                return false;

            string[] lines = original.Split('\n');
            if (lines.Length < 2 || lines.Length > 16)
                return false;

            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                    continue;

                // 有些界面（如 LevelFactorDialog）会把每行右对齐到固定宽度再拼接，
                // 行首全是空格。查词表要按去掉首尾空白的文本查，命中后把留白拼回去。
                // Source: Survivalcraft/Game/LevelFactorDialog.cs:LevelFactorDialog.LevelFactorDialog
                int start = 0;
                while (start < line.Length && line[start] == ' ')
                    start++;
                int end = line.Length;
                while (end > start && line[end - 1] == ' ')
                    end--;
                string lead = line.Substring(0, start);
                string trail = line.Substring(end);
                string core = line.Substring(start, end - start);
                if (core.Length == 0)
                    continue;

                if (Translations.TryGetValue(core, out string exact))
                {
                    lines[i] = lead + exact + trail;
                    any = true;
                    continue;
                }
                if (TryMatchTemplate(core, out string templated))
                {
                    lines[i] = lead + templated + trail;
                    any = true;
                    continue;
                }
                if (core[core.Length - 1] == ':' &&
                    Translations.TryGetValue(core.Substring(0, core.Length - 1), out string label))
                {
                    lines[i] = lead + label + "：" + trail;
                    any = true;
                }
            }

            if (!any)
                return false;
            result = string.Join("\n", lines);
            return true;
        }

        /// <summary>
        /// 单行「标题 + 半角冒号」：去掉冒号后按 精确 → 模板 查一次，命中就换成全角冒号拼回去。
        /// 必须排在模板之前 —— 否则 "{0} days" 这种模板会连冒号一起吃进捕获组。
        /// 只处理单行、且长度有限的文本，避免把正文段落当成标题。
        /// Source: Survivalcraft/Game/GameMenuDialog.cs:GameMenuDialog.AddStat
        /// </summary>
        private bool TryProcessColonTitle(string original, out string result)
        {
            result = null;
            if (original.Length < 2 || original.Length > 96)
                return false;
            if (original.IndexOf('\n') >= 0)
                return false;

            string core = original.TrimEnd();
            if (core.Length < 2 || core[core.Length - 1] != ':')
                return false;

            string stem = core.Substring(0, core.Length - 1).TrimEnd();
            if (stem.Length == 0 || !TryLookupText(stem, out string label))
                return false;

            result = label + "：" + original.Substring(core.Length);
            return true;
        }

        /// <summary>
        /// 去掉首尾留白后再查词表 / 模板，命中就把原来的留白拼回去。
        /// 只处理「确实有留白」的串，避免对普通文本多做一次无谓查找。
        /// </summary>
        private bool TrySingleLineLookup(string original, out string result)
        {
            result = null;
            string core = original.Trim();
            if (core.Length == 0 || core == original)
                return false;

            if (!TryLookupText(core, out string whole))
                return false;

            int start = original.Length - original.TrimStart().Length;
            result = original.Substring(0, start) + whole +
                original.Substring(start + core.Length);
            return true;
        }

        /// <summary>词表精确匹配，失败再试反向模板。</summary>
        private bool TryLookupText(string text, out string translated)
        {
            if (Translations.TryGetValue(text, out string exact))
            {
                translated = exact;
                return true;
            }
            return TryMatchTemplate(text, out translated);
        }

        /// <summary>
        /// 用 " | " 拼出来的复合信息行：逐段查词表 / 套模板，再照原样拼回去。
        /// 段长上限防误伤：如果某段超过 64 字符，说明这更像正文而不是信息行，直接放弃。
        /// </summary>
        private bool TryProcessSegments(string original, out string result)
        {
            result = null;
            if (original.IndexOf(" | ", StringComparison.Ordinal) < 0)
                return false;

            string[] parts = original.Split(new[] { " | " }, StringSplitOptions.None);
            if (parts.Length < 2 || parts.Length > 8)
                return false;

            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                    continue;
                if (part.Length > 64)
                    return false;
                if (!TryLookupText(part, out string translated))
                    continue;
                parts[i] = translated;
                any = true;
            }

            if (!any)
                return false;
            result = string.Join(" | ", parts);
            return true;
        }

        /// <summary>
        /// "&lt;词表里的固定文案&gt; &lt;数据量&gt;" —— 内容列表行的 Details 字段。
        /// 只有当尾部真的像 "1.2MB"/"340KB" 时才动，避免把 "World Seed" 这类普通标签拆坏。
        /// </summary>
        private bool TryProcessLabelWithSize(string original, out string result)
        {
            result = null;
            int cut = original.LastIndexOf(' ');
            if (cut <= 0 || cut == original.Length - 1)
                return false;

            string tail = original.Substring(cut + 1);
            if (!LooksLikeDataSize(tail))
                return false;
            if (!Translations.TryGetValue(original.Substring(0, cut), out string label))
                return false;

            result = label + " " + tail;
            return true;
        }

        /// <summary>数据量字面量：1.2MB / 340KB / 4GB / 512B。</summary>
        private static bool LooksLikeDataSize(string text)
        {
            if (text.Length < 2 || text.Length > 16)
                return false;

            int digits = 0;
            int i = 0;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
            {
                if (char.IsDigit(text[i]))
                    digits++;
                i++;
            }
            if (digits == 0)
                return false;

            string unit = text.Substring(i);
            return unit == "B" || unit == "KB" || unit == "MB" || unit == "GB" || unit == "TB";
        }

        /// <summary>
        /// 词表的大小写不敏感查找（懒建索引）。只用于模板捕获组，
        /// 主查找链仍然是精确匹配 —— 界面文案大小写是有意义的。
        /// </summary>
        private static string LookupIgnoreCase(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            if (_translationsIgnoreCase == null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in Translations)
                    map[pair.Key] = pair.Value;
                _translationsIgnoreCase = map;
            }
            return _translationsIgnoreCase.TryGetValue(text, out string result) ? result : null;
        }

        private static Dictionary<string, string> _translationsIgnoreCase;

        private static void CollectForExport(string original, string translated = null)
        {
            if (!RecordingEnabled) return;
            if (string.IsNullOrEmpty(original)) return;
            if (original.Length < 2 && original[0] >= '0' && original[0] <= '9') return;

            lock (_lock)
            {
                if (!_seenOriginals.Add(original)) return;

                string screen = CurrentScreen ?? "Unknown";
                if (!_collected.TryGetValue(screen, out var dict))
                {
                    dict = new Dictionary<string, string>();
                    _collected[screen] = dict;
                    _screenOrder.Add(screen);
                }
                dict[original] = translated ?? original;
                _hasCollectedChanges = true;
            }
        }

        private void BuildTemplateRegexes()
        {
            _templateRegexes = new List<(string, Regex, string)>();
            foreach (var kv in Translations)
            {
                if (!kv.Key.Contains("{"))
                    continue;

                try
                {
                    _templateRegexes.Add((kv.Key, new Regex(BuildTemplatePattern(kv.Key)), kv.Value));
                }
                catch { }
            }

            // 长模板更具体，排在前面先匹配
            _templateRegexes.Sort((a, b) => b.key.Length.CompareTo(a.key.Length));
        }

        /// <summary>
        /// 把 "{0}" 形式的模板键编译成正则："Yes (up to {0})" → "^Yes\ \(up\ to\ (.+?)\)$"。
        /// 原实现是 Regex.Escape 整个键、再用正则把转义出来的 \{0\} 记号换成捕获组；
        /// 那个替换在带空格的模板上并不成立（实测编译出来只匹配字面量 "{0}"），
        /// 模板因此永远匹配不上。改成显式扫描 {n} 记号、其余字符逐个转义。
        /// </summary>
        private static string BuildTemplatePattern(string key)
        {
            var builder = new System.Text.StringBuilder("^");
            for (int i = 0; i < key.Length; i++)
            {
                if (key[i] == '{' && i + 2 < key.Length &&
                    key[i + 1] >= '0' && key[i + 1] <= '9' && key[i + 2] == '}')
                {
                    builder.Append("(.+?)");
                    i += 2;
                    continue;
                }
                builder.Append(Regex.Escape(key[i].ToString()));
            }
            builder.Append("$");
            return builder.ToString();
        }

    }

    /// <summary>
    /// 外置 Mod 显式注册自身 UI 的翻译入口。未注册的外置 Mod 不会被扫描或导出。
    /// Source: Survivalcraft/Game/Widget.cs:Widget.RootWidget
    /// </summary>
    public static class TranslationApi
    {
        /// <summary>
        /// 为一个外置 Mod 创建可复用的翻译上下文。外置 Mod 只需在初始化时声明一次标识和语言，
        /// 后续所有可显示的固定文本均通过该上下文翻译和导出。
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationMod.GetOrCreateExternalCatalog
        /// </summary>
        public static TranslationContext For(string modIdentifier, string language = "zh_CN")
        {
            return new TranslationContext(modIdentifier, language);
        }

        public static void Register(string modIdentifier, string language)
        {
            TranslationMod.RegisterExternalMod(modIdentifier, language);
        }

        public static void RegisterWidget(Widget rootWidget, string modIdentifier, string language)
        {
            TranslationMod.RegisterExternalWidget(rootWidget, modIdentifier, language);
        }

        public static void UnregisterWidget(Widget rootWidget)
        {
            TranslationMod.UnregisterExternalWidget(rootWidget);
        }

        public static string Translate(string modIdentifier, string language, string original,
            string source = "External")
        {
            return TranslationMod.TranslateExternalString(modIdentifier, language, source, original);
        }

        /// <summary>
        /// 合并外置 Mod 随包提供的翻译表。调用方使用唯一的 Content 路径自行加载 XElement，
        /// 本地 Logs/Translations 中已有的维护译文优先级更高。
        /// Source: EntitySystem/SuAPI/ModResource.cs:ModResource.LoadModResources
        /// </summary>
        public static void AddTranslations(string modIdentifier, string language, XElement translations)
        {
            TranslationMod.AddExternalTranslations(modIdentifier, language, translations);
        }

        /// <summary>
        /// 将已翻译文本写入标准 LabelWidget，并在目标文字含中文时匹配中文字体。
        /// Source: Survivalcraft/Game/LabelWidget.cs:LabelWidget.Text
        /// </summary>
        public static void SetText(LabelWidget widget, string modIdentifier, string language,
            string original, string source = "Text")
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));
            widget.Text = Translate(modIdentifier, language, original, source);
            TranslationMod.TrySetChineseFont(widget);
        }

        /// <summary>
        /// 将已翻译文本写入标准按钮，并在目标文字含中文时匹配中文字体。
        /// Source: Survivalcraft/Game/ButtonWidget.cs:ButtonWidget.Text
        /// </summary>
        public static void SetText(ButtonWidget widget, string modIdentifier, string language,
            string original, string source = "Text")
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));
            widget.Text = Translate(modIdentifier, language, original, source);
            TranslationMod.TrySetChineseFont(widget);
        }

        /// <summary>
        /// 将已翻译文本写入标准复选框，并在目标文字含中文时匹配中文字体。
        /// Source: Survivalcraft/Game/CheckboxWidget.cs:CheckboxWidget.Text
        /// </summary>
        public static void SetText(CheckboxWidget widget, string modIdentifier, string language,
            string original, string source = "Text")
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));
            widget.Text = Translate(modIdentifier, language, original, source);
            TranslationMod.TrySetChineseFont(widget);
        }

        /// <summary>
        /// 将已翻译文本写入标准滑条，并在目标文字含中文时匹配中文字体。
        /// Source: Survivalcraft/Game/SliderWidget.cs:SliderWidget.Text
        /// </summary>
        public static void SetText(SliderWidget widget, string modIdentifier, string language,
            string original, string source = "Text")
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));
            widget.Text = Translate(modIdentifier, language, original, source);
            TranslationMod.TrySetChineseFont(widget);
        }

        /// <summary>
        /// 将已翻译文本写入标准链接，并在目标文字含中文时匹配中文字体。
        /// Source: Survivalcraft/Game/LinkWidget.cs:LinkWidget.Text
        /// </summary>
        public static void SetText(LinkWidget widget, string modIdentifier, string language,
            string original, string source = "Text")
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));
            widget.Text = Translate(modIdentifier, language, original, source);
            TranslationMod.TrySetChineseFont(widget);
        }
    }

    /// <summary>
    /// 外置 Mod 的翻译上下文。固定文本使用 Text，含动态参数的文本使用 Format；两者都按 Mod
    /// 标识独立导出，不会进入原版词表。
    /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.Translate
    /// </summary>
    public sealed class TranslationContext
    {
        private readonly string _modIdentifier;
        private readonly string _language;

        internal TranslationContext(string modIdentifier, string language)
        {
            TranslationMod.RegisterExternalMod(modIdentifier, language);
            _modIdentifier = modIdentifier;
            _language = language;
        }

        public string Text(string original, string source = "Text")
        {
            return TranslationApi.Translate(_modIdentifier, _language, original, source);
        }

        public string Format(string format, params object[] arguments)
        {
            return FormatFrom("Text", format, arguments);
        }

        public string FormatFrom(string source, string format, params object[] arguments)
        {
            return string.Format(Text(format, source), arguments ?? Array.Empty<object>());
        }

        public void SetText(LabelWidget widget, string original, string source = "Text")
        {
            TranslationApi.SetText(widget, _modIdentifier, _language, original, source);
        }

        public void SetText(ButtonWidget widget, string original, string source = "Text")
        {
            TranslationApi.SetText(widget, _modIdentifier, _language, original, source);
        }

        public void SetText(CheckboxWidget widget, string original, string source = "Text")
        {
            TranslationApi.SetText(widget, _modIdentifier, _language, original, source);
        }

        public void SetText(SliderWidget widget, string original, string source = "Text")
        {
            TranslationApi.SetText(widget, _modIdentifier, _language, original, source);
        }

        public void SetText(LinkWidget widget, string original, string source = "Text")
        {
            TranslationApi.SetText(widget, _modIdentifier, _language, original, source);
        }

        public void RegisterWidget(Widget rootWidget)
        {
            TranslationApi.RegisterWidget(rootWidget, _modIdentifier, _language);
        }

        public void UnregisterWidget(Widget rootWidget)
        {
            TranslationApi.UnregisterWidget(rootWidget);
        }

        public void AddTranslations(XElement translations)
        {
            TranslationApi.AddTranslations(_modIdentifier, _language, translations);
        }
    }

    internal sealed class ExternalTranslationCatalog
    {
        private readonly string _modIdentifier;
        private readonly string _language;
        private readonly Dictionary<string, string> _translations = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string, string>> _screens =
            new Dictionary<string, Dictionary<string, string>>();
        private readonly List<string> _screenOrder = new List<string>();
        private readonly HashSet<string> _seenOriginals = new HashSet<string>();
        private readonly HashSet<string> _translatedValues = new HashSet<string>();
        private bool _loaded;
        private bool _dirty;

        public ExternalTranslationCatalog(string modIdentifier, string language)
        {
            _modIdentifier = modIdentifier;
            _language = language;
        }

        public string Process(string screen, string original)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            EnsureLoaded();
            if (_translations.TryGetValue(original, out string translated))
                return translated;

            if (_seenOriginals.Add(original))
            {
                if (!_screens.TryGetValue(screen, out Dictionary<string, string> entries))
                {
                    entries = new Dictionary<string, string>();
                    _screens.Add(screen, entries);
                    _screenOrder.Add(screen);
                }
                entries[original] = original;
                _dirty = true;
            }
            return original;
        }

        /// <summary>
        /// 判断控件当前文本是否为此目录已应用的非同文译文，防止 RegisterWidget 在下一轮扫描时
        /// 将 TranslationContext.SetText 的结果再次导出为新的原文。
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.SetText
        /// </summary>
        public bool IsTranslatedValue(string text)
        {
            EnsureLoaded();
            return _translatedValues.Contains(text);
        }

        /// <summary>
        /// 合并 Mod 随包翻译。日志导出的译文已经先被加载，故不会被随包默认值覆盖。
        /// Source: TranslationMod/Plug/TranslationMod.cs:ExternalTranslationCatalog.EnsureLoaded
        /// </summary>
        public void AddTranslations(XElement root)
        {
            if (root == null)
                return;

            EnsureLoaded();
            try
            {
                foreach (XElement screenElement in root.Elements("Screen"))
                {
                    foreach (XElement element in screenElement.Elements("Entry"))
                        AddTranslation(element);
                }
                foreach (XElement element in root.Elements("Entry"))
                    AddTranslation(element);
            }
            catch (Exception ex)
            {
                Log.Warning($"[TranslationMod] Failed to import {_modIdentifier} translations: {ex.Message}");
            }
        }

        private void AddTranslation(XElement element)
        {
            string original = (string)element.Attribute("Original");
            string translation = (string)element.Attribute("Translation");
            if (!string.IsNullOrEmpty(original) && translation != null && !_translations.ContainsKey(original))
            {
                _translations.Add(original, translation);
                if (translation != original)
                    _translatedValues.Add(translation);
            }
        }

        public void Save()
        {
            if (!_dirty)
                return;

            try
            {
                string directory = Storage.CombinePaths(TranslationProcessor.GetLogsDir(), "Translations");
                Storage.CreateDirectory(directory);
                string path = Storage.CombinePaths(directory, _modIdentifier + "." + _language + ".xml");

                var document = new XDocument(new XElement("Translations"));
                foreach (string screen in _screenOrder)
                {
                    if (!_screens.TryGetValue(screen, out Dictionary<string, string> entries))
                        continue;
                    var screenElement = new XElement("Screen", new XAttribute("Name", screen));
                    var sorted = new List<KeyValuePair<string, string>>(entries);
                    // Keep Android AOT compatible without relying on a comparison delegate.
                    for (int left = 0; left < sorted.Count - 1; left++)
                    {
                        for (int right = left + 1; right < sorted.Count; right++)
                        {
                            if (string.CompareOrdinal(sorted[left].Key, sorted[right].Key) > 0)
                            {
                                KeyValuePair<string, string> temporary = sorted[left];
                                sorted[left] = sorted[right];
                                sorted[right] = temporary;
                            }
                        }
                    }
                    foreach (KeyValuePair<string, string> entry in sorted)
                        screenElement.Add(new XElement("Entry", new XAttribute("Original", entry.Key),
                            new XAttribute("Translation", entry.Value)));
                    document.Root.Add(screenElement);
                }

                using (var stream = Storage.OpenFile(path, OpenFileMode.Create))
                using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    writer.Write(document.ToString());
                _dirty = false;
                Log.Information($"[TranslationMod] Saved {_modIdentifier} {_language} translations to {path}.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[TranslationMod] Failed to save {_modIdentifier} translations: {ex.Message}");
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;

            try
            {
                string path = Storage.CombinePaths(Storage.CombinePaths(TranslationProcessor.GetLogsDir(),
                    "Translations"), _modIdentifier + "." + _language + ".xml");
                if (!Storage.FileExists(path))
                    return;

                using (var stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    XElement root = XDocument.Load(stream).Root;
                    if (root == null)
                        return;
                    foreach (XElement screenElement in root.Elements("Screen"))
                    {
                        string screen = (string)screenElement.Attribute("Name") ?? "Unknown";
                        if (!_screens.TryGetValue(screen, out Dictionary<string, string> entries))
                        {
                            entries = new Dictionary<string, string>();
                            _screens.Add(screen, entries);
                            _screenOrder.Add(screen);
                        }
                        foreach (XElement element in screenElement.Elements("Entry"))
                        {
                            string original = (string)element.Attribute("Original");
                            string translation = (string)element.Attribute("Translation");
                            if (string.IsNullOrEmpty(original) || !_seenOriginals.Add(original))
                                continue;
                            entries[original] = translation ?? original;
                            _translations[original] = translation ?? original;
                            if (translation != null && translation != original)
                                _translatedValues.Add(translation);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[TranslationMod] Failed to load {_modIdentifier} translations: {ex.Message}");
            }
        }
    }

    public class TranslationMod : IMod
    {
        private sealed class ExternalWidgetRegistration
        {
            public Widget RootWidget;
            public ExternalTranslationCatalog Catalog;

            public ExternalWidgetRegistration(Widget rootWidget, ExternalTranslationCatalog catalog)
            {
                RootWidget = rootWidget;
                Catalog = catalog;
            }
        }

        private sealed class ExternalLabelState
        {
            public string AppliedText;

            public ExternalLabelState(string appliedText)
            {
                AppliedText = appliedText;
            }
        }

        private static readonly object s_externalWidgetsLock = new object();
        private static readonly Dictionary<Widget, ExternalWidgetRegistration> s_externalWidgets =
            new Dictionary<Widget, ExternalWidgetRegistration>();
        private static readonly Dictionary<string, ExternalTranslationCatalog> s_externalCatalogs =
            new Dictionary<string, ExternalTranslationCatalog>();

        public string Name => "Translation Mod";
        public string Version => "1.6.4";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }

        public bool IsMergeLib => true;

        private IModParentField _mpf;
        private readonly TranslationProcessor _translationProcessor = new TranslationProcessor();
        // 帧驱动扫描（替代 Timer，避免 Release Android 上 System.Threading.Timer 不可用）
        private int _skipFrames; // 0 = 每帧扫描, N = 跳过 N 帧
        private bool _scannerActive;
        // 上一次扫描时的界面 / 弹窗数量。切换时用来立刻取消节流，避免新界面先闪一段英文。
        private Screen _lastScreen;
        private int _lastDialogCount = -1;

        /// <summary>单个控件的翻译状态。</summary>
        private sealed class WidgetTextState
        {
            /// <summary>翻译时所依据的原文。</summary>
            public string SourceText;

            /// <summary>我们写回去的译文。</summary>
            public string AppliedText;
        }

        // 控件 → 翻译状态。不能用「已处理过」的集合：详情页是屏幕单例，Enter() 每次
        // 都会把标签改回英文原文，处理过一次就永久跳过的话，返回再进就永远显示英文。
        // Source: Survivalcraft/Game/HelpTopicScreen.cs:HelpTopicScreen.Enter
        // Source: Survivalcraft/Game/RecipaediaDescriptionScreen.cs:RecipaediaDescriptionScreen.Enter
        // Source: Survivalcraft/Game/BestiaryDescriptionScreen.cs:BestiaryDescriptionScreen.Enter
        private readonly Dictionary<Widget, WidgetTextState> _widgetTextStates =
            new Dictionary<Widget, WidgetTextState>();

        // 「每帧重写自己文本」的控件。这些控件的文本在 ScreensManager.Update() 里被改回
        // 原文，而我们的扫描在 Frame.Update（ScreensManager.Update 之后、Draw 之前），
        // 所以每帧把译文写回去就稳定显示译文。扫描本身有节流，这里单独维护一份热点，
        // 每帧只处理这几个控件，不做全树扫描。
        // Source: Survivalcraft/Game/Program.cs:Program.FrameHandler
        // Source: Survivalcraft/Game/RecipaediaScreen.cs:RecipaediaScreen.Update
        // Source: Survivalcraft/Game/ViewGameLogDialog.cs:ViewGameLogDialog.Update
        private readonly HashSet<Widget> _hotWidgets = new HashSet<Widget>();
        private readonly List<Widget> _hotDead = new List<Widget>();

        // 全树扫描时登记到的 MessageWidget。状态提示每条都是新建的 LabelWidget，
        // 只靠节流扫描会先以原文露面一瞬，所以每帧单独扫这几棵极小的子树。
        // Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.AddMessage
        private readonly HashSet<MessageWidget> _messageWidgets = new HashSet<MessageWidget>();
        private readonly List<MessageWidget> _messageDead = new List<MessageWidget>();
        private readonly Dictionary<LabelWidget, ExternalLabelState> _externalLabelStates =
            new Dictionary<LabelWidget, ExternalLabelState>();
        private int _globalIndex;
        private long _lastScannedFrame = -1;
        // 自适应扫描频率
        private int _framesSinceNewWidget;
        private const int IDLE_THRESHOLD = 60;

        /// <summary>
        /// 外置 Mod 必须主动注册其 UI 根节点和语言，才会被翻译 Mod 处理和独立导出。
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.RegisterWidget
        /// </summary>
        public static void RegisterExternalWidget(Widget rootWidget, string modIdentifier, string language)
        {
            if (rootWidget == null)
                throw new ArgumentNullException(nameof(rootWidget));

            lock (s_externalWidgetsLock)
            {
                s_externalWidgets[rootWidget] = new ExternalWidgetRegistration(rootWidget,
                    GetOrCreateExternalCatalog(modIdentifier, language));
            }
        }

        /// <summary>
        /// 注册没有标准 Widget 的外置 Mod。其自绘文本可通过 TranslationApi.Translate 翻译和导出。
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.Translate
        /// </summary>
        public static void RegisterExternalMod(string modIdentifier, string language)
        {
            lock (s_externalWidgetsLock)
                GetOrCreateExternalCatalog(modIdentifier, language);
        }

        /// <summary>
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.Translate
        /// </summary>
        public static string TranslateExternalString(string modIdentifier, string language,
            string source, string original)
        {
            ExternalTranslationCatalog catalog;
            lock (s_externalWidgetsLock)
                catalog = GetOrCreateExternalCatalog(modIdentifier, language);
            return catalog.Process(string.IsNullOrEmpty(source) ? "External" : source, original);
        }

        /// <summary>
        /// Source: TranslationMod/Plug/TranslationMod.cs:ExternalTranslationCatalog.AddTranslations
        /// </summary>
        public static void AddExternalTranslations(string modIdentifier, string language,
            XElement translations)
        {
            ExternalTranslationCatalog catalog;
            lock (s_externalWidgetsLock)
                catalog = GetOrCreateExternalCatalog(modIdentifier, language);
            catalog.AddTranslations(translations);
        }

        /// <summary>
        /// Source: TranslationMod/Plug/TranslationMod.cs:TranslationApi.UnregisterWidget
        /// </summary>
        public static void UnregisterExternalWidget(Widget rootWidget)
        {
            if (rootWidget == null)
                return;
            lock (s_externalWidgetsLock)
                s_externalWidgets.Remove(rootWidget);
        }

        private static void ValidateExportName(string value, string parameterName)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("A non-empty value is required.", parameterName);
            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                    throw new ArgumentException("Only letters, digits, '-', '_' and '.' are allowed.",
                        parameterName);
            }
        }

        private static ExternalTranslationCatalog GetOrCreateExternalCatalog(string modIdentifier,
            string language)
        {
            ValidateExportName(modIdentifier, nameof(modIdentifier));
            ValidateExportName(language, nameof(language));
            string key = modIdentifier + "\u001f" + language;
            if (!s_externalCatalogs.TryGetValue(key, out ExternalTranslationCatalog catalog))
            {
                catalog = new ExternalTranslationCatalog(modIdentifier, language);
                s_externalCatalogs.Add(key, catalog);
            }
            return catalog;
        }

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            _mpf = Program.ModManager.ModParentField;

            // 把本 Mod 的语言登记给「社区 - 模组」列表：只有本 Mod 在场时，
            // 模组列表才按 zh_CN 显示名称；没装本 Mod 时列表一律显示 en_US。
            // 条目里带 * 强制项的多语言名（如汉化类模组 *zh_CN=…）不受此影响，始终显示它自己的语言。
            // Source: EntitySystem/SuAPICore/Plug/SuAPIModLocalizationApi.cs:SuAPIModLocalizations
            SuAPICore.SuAPIModLocalizations.TryRegisterService(new ModLocalizationService());

            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                return HandleLoadingInitialize((object[])args);
            }, EventPriority.LOWEST);

            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                Update();
                return null;
            }, EventPriority.LOWEST);

            Log.Information($"[TranslationMod] v{TranslationProcessor.ModVersion} Loaded."
                + " Shared SuAPI font profiles are lazy.");
        }

        /// <summary>
        /// 把本 Mod 的语言登记给社区-模组列表。只有本 Mod 在场时列表才用 zh_CN，
        /// 否则一律 en_US —— "多语言显示由翻译 Mod 启用"的落点。
        /// Source: EntitySystem/SuAPICore/Plug/SuAPIModLocalizationApi.cs:ISuAPIModLocalizationService
        /// </summary>
        private sealed class ModLocalizationService : SuAPICore.ISuAPIModLocalizationService
        {
            public bool IsEnabled => true;

            public string Language => "zh_CN";
        }

        private object[] HandleLoadingInitialize(object[] args)
        {
            // MAUI版 Loading.Initialize 传 typeof(LoadingManager)，用 QueueItem 添加加载步骤
            // 旧版传 List<Action>，兼容处理
            if (args[0] is Type type && type.Name == "LoadingManager")
            {
                LoadingManager.QueueItem("ChineseFontLoader", () =>
                {
                    try { ChineseFontLoader.Load(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] ChineseFontLoader failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("LoadTranslations", () =>
                {
                    try
                    {
                        TranslationProcessor.LoadTranslations();
                        // 必须在播种之前同步丢弃：播种跑在后台，会有读写竞争。
                        TranslationProcessor.DiscardStaleExportFile();
                        TranslationProcessor.SeedExportFileAsync();
                    }
                    catch (Exception ex) { Log.Error($"[TranslationMod] LoadTranslations failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("ProcessStrings", () =>
                {
                    try { ProcessStrings(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] ProcessStrings failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("StartWidgetScanner", () =>
                {
                    try { StartWidgetScanner(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] StartWidgetScanner failed: {ex.Message}"); }
                });
            }
            else if (args[0] is List<Action> actions)
            {
                // 旧版兼容：List<Action>
                actions.Add(() =>
                {
                    try { ChineseFontLoader.Load(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] ChineseFontLoader failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try
                    {
                        TranslationProcessor.LoadTranslations();
                        // 必须在播种之前同步丢弃：播种跑在后台，会有读写竞争。
                        TranslationProcessor.DiscardStaleExportFile();
                        TranslationProcessor.SeedExportFileAsync();
                    }
                    catch (Exception ex) { Log.Error($"[TranslationMod] LoadTranslations failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try { ProcessStrings(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] ProcessStrings failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try { StartWidgetScanner(); }
                    catch (Exception ex) { Log.Error($"[TranslationMod] StartWidgetScanner failed: {ex.Message}"); }
                });
            }

            return new object[] { false, args };
        }

        private void ProcessStrings()
        {
            TranslationProcessor.CurrentScreen = "StringsManager";
            var strings = _mpf.GetStaticField<Dictionary<string, string>>(typeof(StringsManager), "m_strings");
            if (strings == null || strings.Count == 0)
            {
                Log.Warning("[TranslationMod] m_strings is null or empty.");
                return;
            }

            var keys = new List<string>(strings.Keys);
            int translated = 0;
            foreach (var key in keys)
            {
                string original = strings[key];
                string result = _translationProcessor.Process(key, original, ++_globalIndex,
                    collectForExport: false);
                if (result != original) translated++;
                strings[key] = result;
            }

            Log.Information($"[TranslationMod] Processed {strings.Count} StringsManager entries ({translated} translated). Index at {_globalIndex}.");
        }

        private void StartWidgetScanner()
        {
            _scannerActive = true;
            _skipFrames = 0;
            Log.Information("[TranslationMod] Widget scanner started (frame-driven).");
        }

        /// <summary>
        /// 帧驱动扫描。只检查当前原版/SuAPI 屏幕，避免外置 Mod 与游戏内 UI 进入翻译导出。
        /// Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
        /// </summary>
        public void Update()
        {
            // 界面切换 / 弹窗增减时立刻取消跳帧节流。空闲节流是 29 帧（60fps 下约 0.5 秒），
            // 不重置的话新界面或新弹窗会先以英文露面一段才被扫到 ——
            // 那正是「进界面先闪一下英文」的来源。
            // 本回调挂在 Frame.Update 的 LOWEST 优先级，即 ScreensManager.Update() 之后、
            // Draw 之前，所以重置后新界面能在同一帧内完成翻译，不再闪。
            // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
            // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.Dialogs
            Screen currentScreen = ScreensManager.CurrentScreen;
            int dialogCount = DialogsManager.Dialogs.Count;
            if (!ReferenceEquals(currentScreen, _lastScreen) || dialogCount != _lastDialogCount)
            {
                _lastScreen = currentScreen;
                _lastDialogCount = dialogCount;
                _skipFrames = 0;
                _framesSinceNewWidget = 0;
            }

            // 每帧回写热点控件的译文：写入时机在 Draw 之前，所以能覆盖游戏每帧的重写
            ApplyHotWidgetTranslations();
            // 状态提示是新建控件，热点机制覆盖不到，单独每帧扫一次这几棵小子树，
            // 让消息出现当帧就是译文、不先闪一下英文
            ScanMessageWidgets();

            if (!_scannerActive)
            {
                return;
            }
            if (_skipFrames > 0) { _skipFrames--; return; }
            ScanWidgetTree();
        }

        private void ScanWidgetTree()
        {
            long frame = Time.FrameIndex;
            if (frame == _lastScannedFrame) return;
            _lastScannedFrame = frame;

            int labelCount = 0;
            int buttonCount = 0;
            Screen screen = ScreensManager.CurrentScreen;
            if (IsSupportedScreen(screen))
            {
                TranslationProcessor.CurrentScreen = screen.GetType().Name;
                ScanContainer(screen, ref labelCount, ref buttonCount);
            }
            ScanRegisteredExternalWidgets(ref labelCount, ref buttonCount);

            bool foundNew = labelCount > 0 || buttonCount > 0;

            if (foundNew)
            {
                _framesSinceNewWidget = 0;
                _skipFrames = 2;
            }
            else
            {
                _framesSinceNewWidget++;
                if (_framesSinceNewWidget >= IDLE_THRESHOLD)
                {
                    _skipFrames = 29;
                }
                else
                    _skipFrames = 5;
            }

            int removedStates = 0;
            {
                var toRemove = new List<Widget>();
                foreach (var kv in _widgetTextStates)
                    if (!IsAttached(kv.Key)) toRemove.Add(kv.Key);
                foreach (var w in toRemove) { _widgetTextStates.Remove(w); removedStates++; }
            }
            if (removedStates > 0)
            {
                _framesSinceNewWidget = 0;
                _skipFrames = 2;
            }
        }

        /// <summary>
        /// 控件是否仍挂在当前 UI 树上。原来只判 ParentWidget == null，而界面里的
        /// Label 其 ParentWidget 是所属界面（非 null），界面切走后条目会一直留着；
        /// 这里改判整棵树的根。
        /// Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.RootWidget
        /// </summary>
        private static bool IsAttached(Widget widget)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            return root != null && widget.RootWidget == root;
        }

        /// <summary>
        /// 控件当前文本是否需要翻译。
        ///   文本 == AppliedText → 我们写的还在，跳过；
        ///   文本 == SourceText  → 游戏把同一个原文写了回来（多数是每帧重写自己文本的
        ///                          界面），加入热点列表交给每帧回写，这里不重复翻译；
        ///   其它                → 换成了新原文，正常翻译。
        /// Source: Survivalcraft/Game/HelpTopicScreen.cs:HelpTopicScreen.Enter
        /// Source: Survivalcraft/Game/Program.cs:Program.FrameHandler
        /// </summary>
        private bool ShouldTranslate(Widget widget, string current, out WidgetTextState state)
        {
            if (!_widgetTextStates.TryGetValue(widget, out state))
            {
                state = new WidgetTextState();
                _widgetTextStates[widget] = state;
                return true;
            }

            if (current == state.AppliedText)
                return false;

            if (current == state.SourceText)
            {
                _hotWidgets.Add(widget);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 每帧扫描已登记的 MessageWidget 子树。这些子树通常只有 1~3 个 Label，
        /// 开销可以忽略，换来的是状态提示在出现的当帧就被翻译。
        /// Source: Survivalcraft/Game/Program.cs:Program.FrameHandler
        /// </summary>
        private void ScanMessageWidgets()
        {
            if (_messageWidgets.Count == 0)
                return;

            _messageDead.Clear();
            int labels = 0;
            int buttons = 0;
            foreach (MessageWidget widget in _messageWidgets)
            {
                if (!IsAttached(widget))
                {
                    _messageDead.Add(widget);
                    continue;
                }
                ScanContainer(widget, ref labels, ref buttons);
            }
            foreach (MessageWidget widget in _messageDead)
                _messageWidgets.Remove(widget);
        }

        /// <summary>
        /// 每帧把热点控件的译文写回去。只比对这几个控件，不做全树扫描。
        /// 我们的写入时机在 ScreensManager.Update 之后、ScreensManager.Draw 之前，
        /// 所以这一写就是本帧最终显示的内容。
        /// Source: Survivalcraft/Game/Program.cs:Program.FrameHandler
        /// </summary>
        private void ApplyHotWidgetTranslations()
        {
            if (_hotWidgets.Count == 0)
                return;

            _hotDead.Clear();
            foreach (Widget widget in _hotWidgets)
            {
                if (!_widgetTextStates.TryGetValue(widget, out WidgetTextState state))
                {
                    _hotDead.Add(widget);
                    continue;
                }
                if (!IsAttached(widget))
                    continue;

                string text = GetWidgetText(widget);
                if (text == null || text == state.AppliedText)
                    continue;
                if (text == state.SourceText)
                    SetWidgetText(widget, state.AppliedText);
                else
                    _hotDead.Add(widget);   // 换成了新原文，交回扫描器翻译
            }
            foreach (Widget widget in _hotDead)
                _hotWidgets.Remove(widget);
        }

        private static string GetWidgetText(Widget widget)
        {
            if (widget is LabelWidget label)
                return label.Text;
            if (widget is ButtonWidget button)
                return button.Text;
            return null;
        }

        private static void SetWidgetText(Widget widget, string text)
        {
            if (widget is LabelWidget label)
                label.Text = text;
            else if (widget is ButtonWidget button)
                button.Text = text;
        }

        /// <summary>
        /// 接受游戏原版及 SuAPI 内置模块的 Screen，外置 Mod 的 Screen 不参与扫描。
        /// GameScreen 也扫：游戏内 HUD 与对话框（GameMenuDialog、EditSignDialog、
        /// EditTruthTableDialog、EditMemoryBankDialog 等）都要翻译，其中玩家可自定义的
        /// 内容由 IsSupportedWidget 逐控件排除。
        /// Source: EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:SuAPICoreMod.LoadBuiltInMod
        /// </summary>
        private static bool IsSupportedScreen(Screen screen)
        {
            if (screen == null)
                return false;
            return IsSupportedUiAssembly(screen.GetType().Assembly);
        }

        /// <summary>
        /// Source: EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:SuAPICoreMod.LoadBuiltInMod
        /// </summary>
        private static bool IsSupportedUiAssembly(System.Reflection.Assembly assembly)
        {
            if (assembly == typeof(Screen).Assembly)
                return true;

            string name = assembly.GetName().Name;
            // 本仓自带 Mod 的界面同样是固定文案，不扫描会导致这些界面永远翻不了：
            // ScMultiplayer 的 SuPlayScreen 继承原版 PlayScreen，用的就是
            // Pak/Screens/PlayScreen.xml 的 "Play!" / "New World"。
            // 第三方 Mod 仍按 README 的约定走 TranslationApi 主动注册。
            return name == "SuAPICore" ||
                name == "SuAPIModDownload" ||
                name == "SuAPIExternalContentImport" ||
                name == "ScMultiplayer";
        }

        /// <summary>
        /// 行内容由运行期数据填充、且无法靠 Tag 识别的列表（按控件名）。
        /// 能靠 Tag 识别的（WorldInfo/PlayerInfo/PlayerData/CommunityContentEntry）
        /// 走 IsSupportedWidget 的 Tag 判定，不在这里重复。
        /// Source: Survivalcraft/Game/ViewGameLogDialog.cs:ViewGameLogDialog.ViewGameLogDialog
        /// </summary>
        private static readonly HashSet<string> DynamicContentLists = new HashSet<string>
        {
            // 设置-兼容性 → 查看日志：行是运行日志原文，随调试输出增长
            "ViewGameLogDialog.ListPanel"
        };

        /// <summary>
        /// 列表行的「标题」控件名 —— 内容是玩家 / 社区 / 服务端给的名字，不翻译也不收集。
        /// 社区、外部内容、内容管理这几个界面不按整张列表排除：一行里除了标题，还有
        /// 游戏自己拼的固定文案（"World 34KB"、"(862K downloads, …)"、
        /// "Furniture Pack | 12 design(s)"），那些要翻。
        /// Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.CommunityContentScreen
        /// Source: Survivalcraft/Game/ExternalContentScreen.cs:ExternalContentScreen.ExternalContentScreen
        /// Source: Survivalcraft/Game/ManageContentScreen.cs:ManageContentScreen.ManageContentScreen
        /// Source: EntitySystem/SuAPICore/Plug/SuManageContentScreen.cs:SuManageContentScreen.CreateItemWidget
        /// </summary>
        private static readonly HashSet<string> PlayerAuthoredRowLabels = new HashSet<string>
        {
            "CommunityContentItem.Text",
            "ExternalContentItem.Text",
            "FurniturePackItem.Text"
        };

        /// <summary>
        /// 唯一的排除准则：玩家能够自定义修改的内容不翻译，其余界面都翻译。
        /// 排除四类载体：
        ///   1. Tag 为 WorldInfo / PlayerInfo / PlayerData / CommunityContentEntry 的子树
        ///      —— 地图名、玩家名、社区下载内容名；ListPanelWidget.ItemWidgetFactory
        ///      会给每一行挂上条目对象作 Tag，所以这一条自动覆盖所有列表行。
        ///   2. DynamicContentLists 点名的列表 —— 行内容随运行期数据变化（游戏日志）。
        ///   3. MessageWidget 子树 —— 聊天与临时提示，文本由玩家或外置 Mod 提供，
        ///      外置 Mod 要翻译这类文本请走 TranslationApi。
        ///   4. 外置 Mod 主动注册的 UI 根控件 —— 同样走 TranslationApi 自行翻译。
        /// Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.ItemWidgetFactory
        /// Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.AddMessage
        /// </summary>
        private static bool IsSupportedWidget(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (IsRegisteredExternalWidget(current))
                    return false;
                if (!IsSupportedUiAssembly(current.GetType().Assembly))
                    return false;
                if (current is ListPanelWidget listPanel && IsDynamicContentList(listPanel))
                    return false;

                // 行标题单独排除：整张列表不再一刀切，行内固定文案才翻得到。
                if (PlayerAuthoredRowLabels.Contains(widget.Name ?? string.Empty))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 这条文本是不是玩家自己写的内容（世界名 / 玩家名 / 社区条目名与链接）。
        /// 列表行整行都挂着条目对象当 Tag，行里混着两类文本：
        ///   玩家写的   —— worldInfo.WorldSettings.Name / entry.Name / entry.ExtraText
        ///   游戏固定   —— "193KB | … | 1 player | Creative | Living" / "World 1.2MB"
        /// 所以只能逐条文本比对，不能像以前那样把整行排除掉，否则固定文案也跟着被挡。
        /// Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
        /// Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.CommunityContentScreen
        /// Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.CreateListWidgets —— value.Tag = obj;
        /// </summary>
        private static bool IsPlayerAuthoredText(Widget widget, string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                switch (current.Tag)
                {
                    case WorldInfo worldInfo:
                        if (text == worldInfo.WorldSettings?.Name || text == worldInfo.DirectoryName)
                            return true;
                        break;
                    // ExternalContentEntry 没有名字字段，行标题是文件名
                    // Source: Survivalcraft/Game/ExternalContentScreen.cs:ExternalContentScreen.ExternalContentScreen
                    case ExternalContentEntry externalEntry:
                        if (text == Storage.GetFileName(externalEntry.Path))
                            return true;
                        break;
                    case CommunityContentEntry entry:
                        // Name / Url 是发布者定义的，ExtraText 不是 —— 它是社区服务端
                        // 下发的固定格式 "(862K downloads, 2016/12/17, v2.0)"，
                        // 属于该翻译的界面文案。
                        // Source: Survivalcraft/Game/CommunityContentManager.cs:CommunityContentManager.Refresh
                        if (text == entry.Name || text == entry.Url)
                            return true;
                        break;
                    // PlayerInfo 只有 CharacterSkinName（皮肤名由玩家选），没有 Name
                    // Source: Survivalcraft/Game/PlayerInfo.cs:PlayerInfo
                    case PlayerInfo playerInfo:
                        if (text == playerInfo.CharacterSkinName)
                            return true;
                        break;
                    case PlayerData playerData:
                        if (text == playerData.Name)
                            return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// 是否位于 MessageWidget（画面中部的临时提示）内部。
        /// 这类控件要翻译，但不进导出表：里面既有游戏固定文案（"First person camera"），
        /// 也有玩家聊天内容，收集上去会把聊天记录灌进导出表。
        /// Source: Survivalcraft/Game/MessageWidget.cs:MessageWidget.AddMessage
        /// Source: Pak/Widgets/GameWidget.xml —— &lt;MessageWidget Name="Message" .../&gt;
        /// </summary>
        private static bool IsInsideMessageWidget(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (current is MessageWidget)
                    return true;
            }
            return false;
        }

        private static bool IsRegisteredExternalWidget(Widget widget)
        {
            lock (s_externalWidgetsLock)
            {
                for (Widget current = widget; current != null; current = current.ParentWidget)
                    if (s_externalWidgets.ContainsKey(current))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// 仅扫描主动注册且已经挂入 ScreensManager.RootWidget 的外置 Mod UI。标准复合控件
        /// 最终都由 LabelWidget 绘制文本，故只处理叶子标签，避免按钮与其内部标签重复导出。
        /// 每次看到 Mod 重新赋值的文本都会重新翻译，已应用的译文不会再作为原文收集。
        /// Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.RootWidget
        /// Source: Survivalcraft/Game/ButtonWidget.cs:ButtonWidget.Text
        /// </summary>
        private void ScanRegisteredExternalWidgets(ref int labelCount, ref int buttonCount)
        {
            ExternalWidgetRegistration[] registrations;
            lock (s_externalWidgetsLock)
                registrations = new List<ExternalWidgetRegistration>(s_externalWidgets.Values).ToArray();

            foreach (ExternalWidgetRegistration registration in registrations)
            {
                Widget root = registration.RootWidget;
                if (root == null || root.RootWidget != ScreensManager.RootWidget)
                    continue;
                ScanExternalWidget(root, registration.Catalog, root.GetType().Name,
                    ref labelCount, ref buttonCount);
            }
        }

        private void ScanExternalWidget(Widget widget, ExternalTranslationCatalog catalog,
            string screen, ref int labelCount, ref int buttonCount)
        {
            if (widget is LabelWidget label)
            {
                string current = label.Text;
                if (!string.IsNullOrEmpty(current) &&
                    (!_externalLabelStates.TryGetValue(label, out ExternalLabelState state) ||
                    current != state.AppliedText))
                {
                    if (catalog.IsTranslatedValue(current))
                    {
                        _externalLabelStates[label] = new ExternalLabelState(current);
                    }
                    else
                    {
                        string result = catalog.Process(screen, current);
                        if (result != current)
                            label.Text = result;
                        if (NeedsExtendedFont(result))
                            TrySetChineseFont(label);
                        _externalLabelStates[label] = new ExternalLabelState(result);
                        labelCount++;
                    }
                }
            }

            if (widget is ContainerWidget container)
            {
                foreach (Widget child in container.Children)
                    ScanExternalWidget(child, catalog, screen, ref labelCount, ref buttonCount);
            }
        }

        /// <summary>
        /// 行内容由运行期数据填充的列表，不能视为固定翻译文本；其余静态列表仍允许扫描，
        /// 这样 SuAPI 的固定选择项照样能进翻译表。
        /// Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.PopulateList
        /// Source: EntitySystem/SuAPICore/Plug/SuManageContentScreen.cs:SuManageContentScreen.PopulateModList
        /// Source: Survivalcraft/Game/ExternalContentScreen.cs:ExternalContentScreen
        /// Source: Survivalcraft/Game/ViewGameLogDialog.cs:ViewGameLogDialog.PopulateList
        /// </summary>
        private static bool IsDynamicContentList(ListPanelWidget listPanel)
        {
            // 内容界面里弹出的固定选择列表：SuCommunityContentScreen 的 CommunitySelectionDialog
            // 只有 "MOD" / "Original Community" 两个固定项，属于该翻译的界面文案
            if (listPanel.Name == "CommunitySelection.List")
                return false;

            if (DynamicContentLists.Contains(listPanel.Name ?? string.Empty))
                return true;

            return false;
        }


        private void ScanContainer(ContainerWidget container, ref int labelCount, ref int buttonCount)
        {
            if (container == null) return;

            foreach (var child in container.Children)
            {
                if (!IsSupportedWidget(child))
                    continue;

                if (child is LabelWidget label)
                {
                    string original = label.Text;
                    if (!IsPlaceholderText(original) &&
                        !IsPlayerAuthoredText(child, original) &&
                        ShouldTranslate(label, original, out WidgetTextState labelState))
                    {
                        string result = ProcessWidgetText(child.GetType().Name, original,
                            !IsInsideMessageWidget(child));
                        labelState.SourceText = original;
                        labelState.AppliedText = result;
                        if (result != original)
                            label.Text = result;

                        // 如果结果含中文 → 切换为中文字体
                        if (NeedsExtendedFont(result))
                            TrySetChineseFont(label);

                        labelCount++;
                    }
                }
                else if (child is ButtonWidget button)
                {
                    string original = button.Text;
                    if (!IsPlaceholderText(original) &&
                        !IsPlayerAuthoredText(child, original) &&
                        ShouldTranslate(button, original, out WidgetTextState buttonState))
                    {
                        string result = ProcessWidgetText(child.GetType().Name, original,
                            !IsInsideMessageWidget(child));
                        buttonState.SourceText = original;
                        buttonState.AppliedText = result;
                        button.Text = result;

                        if (NeedsExtendedFont(result))
                            TrySetChineseFont(button);

                        buttonCount++;
                    }
                }

                if (child is MessageWidget messageWidget)
                    _messageWidgets.Add(messageWidget);

                if (child is ContainerWidget childContainer)
                {
                    ScanContainer(childContainer, ref labelCount, ref buttonCount);
                }
            }
        }

        // Source: TranslationMod/Plug/TranslationMod.cs:TranslationProcessor.Process
        private string ProcessWidgetText(string source, string original,
            bool collectForExport = true)
        {
            return _translationProcessor.Process(source, original, ++_globalIndex,
                collectForExport: collectForExport);
        }

        private static void SaveRegisteredExternalTranslations()
        {
            ExternalTranslationCatalog[] catalogs;
            lock (s_externalWidgetsLock)
                catalogs = new List<ExternalTranslationCatalog>(s_externalCatalogs.Values).ToArray();
            foreach (ExternalTranslationCatalog catalog in catalogs)
                catalog.Save();
        }

        /// <summary>
        /// 检测文本是否含中文字符（CJK Unified Ideographs: U+4E00–U+9FFF）
        /// </summary>
        private static bool IsPlaceholderText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            if (text == "<Plchldr>")
                return true;
            return text.Length > 2 && text[0] == '_' &&
                text[text.Length - 1] == '_';
        }

        /// <summary>
        /// 文本里是否有 ASCII 之外的字符。
        /// 原版拉丁字体（Pericles）只有 ASCII 字形，用它去画 × / 全角标点 / ° 会渲染成
        /// 占位符 —— 实机里 '× 1.30' 显示成 '_ 1.30'，而 '× 无限' 因为含汉字切了字体所以正常。
        /// 所以换字体的判据是"含任何非 ASCII 字符"，不是"含中文"。
        /// Source: Mod/TranslationMod/Plug/ChineseFontLoader.cs:ChineseFontLoader.GetClosestChineseFont
        /// </summary>
        private static bool NeedsExtendedFont(string text)
        {
            foreach (char c in text)
            {
                if (c > 0x7F)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 保留调用方已明确选择的共享 Chinese profile，仅将原版字体映射到最近的 profile。
        /// 英文的尺寸精度由字体生成端保证（浮点边界面积平均重采样），运行时不再做裁边/锐化。
        /// Source: TranslationMod/Plug/ChineseFontLoader.cs:ChineseFontLoader.IsChineseFont
        /// </summary>
        private static BitmapFont GetChineseFont(BitmapFont currentFont)
        {
            if (currentFont == null || ChineseFontLoader.IsChineseFont(currentFont))
                return currentFont;
            return ChineseFontLoader.GetClosestChineseFont(currentFont.GlyphHeight);
        }

        internal static void TrySetChineseFont(LabelWidget widget)
        {
            BitmapFont cnFont = GetChineseFont(widget.Font);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        /// <summary>
        /// 将 ButtonWidget 的 Font 设为中国字体
        /// </summary>
        internal static void TrySetChineseFont(ButtonWidget widget)
        {
            BitmapFont cnFont = GetChineseFont(widget.Font);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        // Source: Survivalcraft/Game/CheckboxWidget.cs:CheckboxWidget.Font
        internal static void TrySetChineseFont(CheckboxWidget widget)
        {
            BitmapFont cnFont = GetChineseFont(widget.Font);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        // Source: Survivalcraft/Game/SliderWidget.cs:SliderWidget.Font
        internal static void TrySetChineseFont(SliderWidget widget)
        {
            BitmapFont cnFont = GetChineseFont(widget.Font);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        // Source: Survivalcraft/Game/LinkWidget.cs:LinkWidget.Font
        internal static void TrySetChineseFont(LinkWidget widget)
        {
            BitmapFont cnFont = GetChineseFont(widget.Font);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        public void OnUnload()
        {
            TranslationProcessor.SaveCollected();
            SaveRegisteredExternalTranslations();
            _scannerActive = false;
            _widgetTextStates.Clear();
            _hotWidgets.Clear();
            _messageWidgets.Clear();
            lock (s_externalWidgetsLock)
            {
                s_externalWidgets.Clear();
                s_externalCatalogs.Clear();
            }
            Log.Information("[TranslationMod] Unloaded.");
        }
    }
}
