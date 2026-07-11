using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Engine;
using System.Xml.Linq;
using Engine.Content;
using Engine.Media;
using Game;
using SuAPI;
using SuAPI;

namespace StringInterceptor
{
    /// <summary>
    /// 字符串处理接口 — 外部 Mod 可注册此接口实现自定义文字处理（如翻译）
    /// </summary>
    public interface IStringProcessor
    {
        string Process(string key, string original, int index);
    }

    /// <summary>
    /// 默认实现：在字符串前加顺序编号（模板含 {N} 占位符时跳过，避免破坏 string.Format）
    /// </summary>
    public class DefaultStringProcessor : IStringProcessor
    {
        public string Process(string key, string original, int index)
        {
            // 跳过模板字符串，避免 {N} 被方括号干扰 string.Format
            if (original != null && original.Contains("{"))
                return original;
            return $"[{index}] {original}";
        }
    }

    /// <summary>
    /// 翻译处理器：从 Content/zh_CN.xml 加载翻译（XML 格式），
    /// 运行时按 Screen 分类收集字符串写入 Logs/zh_CN.xml，组内 ABC 排序。
    /// 支持模板占位符：{0}{1}...
    /// </summary>
    public class TranslationProcessor : IStringProcessor
    {
        /// <summary>
        /// 获取日志目录路径（与 GameLogSink 一致）
        /// Android: data: 前缀 -> /sdcard/Download/Survivalcraft/Logs/
        /// Windows: AppDomain.CurrentDomain.BaseDirectory + "Logs"
        /// </summary>
        public static string GetLogsDir()
        {
#if ANDROID
            return Engine.Storage.GetSystemPath("data:Logs");
#else
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
#endif
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
        private Dictionary<string, (Regex regex, string outputTemplate)> _templateRegexes;

        private static readonly object _lock = new object();

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
        /// 将收集到的字符串按 Screen 分类追加写入 Logs/zh_CN.xml（合并已有，去重，组内 ABC 排序）
        /// </summary>
        public static void SaveCollected()
        {
            lock (_lock)
            {
                if (_collected.Count == 0) return;

                try
                {
                    string savePath = System.IO.Path.Combine(GetLogsDir(), "zh_CN.xml");

                    // 确保 Logs 目录存在
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(savePath));

                    // 加载已有条目 → 按 Screen 分组 + 已见 Original
                    var existingScreens = new Dictionary<string, Dictionary<string, string>>();
                    var existingOriginals = new HashSet<string>();
                    if (System.IO.File.Exists(savePath))
                    {
                        try
                        {
                            // AOT-safe: XDocument.Load(string) may be trimmed, use Stream
                            using (var fs = new System.IO.FileStream(savePath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
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
                    doc.Add(new XElement("Translations"));
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

                    { var fs = new System.IO.FileStream(savePath, System.IO.FileMode.Create, System.IO.FileAccess.Write); var sw = new System.IO.StreamWriter(fs, new System.Text.UTF8Encoding(false)); sw.Write(doc.ToString()); sw.Close(); fs.Close(); }
                    Log.Information($"[Translator] Appended {appended} new strings (total: {existingOriginals.Count}) to {savePath}.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Translator] Failed to save: {ex}");
                }
            }
        }

        public string Process(string key, string original, int index)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            // 收集
            CollectForExport(original);

            // 1. 精确匹配（模板层面 + 普通字符串）
            if (Translations.TryGetValue(original, out var translated))
            {
                CollectForExport(original, translated);
                return translated;
            }

            // 2. 反向模板匹配：格式化后的字符串（如 "5 recipes"）匹配模板（如 "{0} recipes"）
            if (_templateRegexes == null)
                BuildTemplateRegexes();

            foreach (var tp in _templateRegexes)
            {
                var match = tp.Value.regex.Match(original);
                if (match.Success)
                {
                    string result = tp.Value.outputTemplate;
                    for (int j = 0; j < match.Groups.Count - 1; j++)
                        result = result.Replace("{" + j + "}", match.Groups[j + 1].Value);
                    CollectForExport(original, result);
                    return result;
                }
            }

            // 未翻译 → 收集为原文=原文
            CollectForExport(original, original);
            return original;
        }

        private static void CollectForExport(string original, string translated = null)
        {
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
            }
        }

        private void BuildTemplateRegexes()
        {
            _templateRegexes = new Dictionary<string, (Regex, string)>();
            foreach (var kv in Translations)
            {
                if (!kv.Key.Contains("{"))
                    continue;

                // "你好{0}位玩家{1}" → 正则 "^你好(.+?)位玩家(.+?)$"
                string pattern = Regex.Escape(kv.Key);
                pattern = Regex.Replace(pattern, @"\\\{(\d+)\\\}", _ => "(.+?)");
                pattern = "^" + pattern + "$";
                try
                {
                    _templateRegexes[kv.Key] = (new Regex(pattern), kv.Value);
                }
                catch { }
            }
        }
    }
    public class StringInterceptorMod : IMod
    {
        public string Name => "String Interceptor";
        public string Version => "1.5.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        private IModParentField _mpf;
        private readonly List<IStringProcessor> _processors = new List<IStringProcessor>();
        // 帧驱动扫描（替代 Timer，避免 Release Android 上 System.Threading.Timer 不可用）
        private int _skipFrames; // 0 = 每帧扫描, N = 跳过 N 帧
        private bool _scannerActive;

        private readonly HashSet<LabelWidget> _processedLabels = new HashSet<LabelWidget>();
        private readonly HashSet<ButtonWidget> _processedButtons = new HashSet<ButtonWidget>();
        private int _globalIndex;
        private long _lastScannedFrame = -1;
        // 自适应扫描频率
        private int _framesSinceNewWidget;
        private const int IDLE_THRESHOLD = 60;
        private bool _collectedSaved;

        public void RegisterProcessor(IStringProcessor processor)
        {
            if (processor != null && !_processors.Contains(processor))
                _processors.Add(processor);
        }

        public void UnregisterProcessor(IStringProcessor processor)
        {
            _processors.Remove(processor);
        }

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            _mpf = Program.ModManager.ModParentField;
            RegisterProcessor(new TranslationProcessor());

            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                return HandleLoadingInitialize((object[])args);
            }, EventPriority.LOWEST);

            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                Update();
                return null;
            }, EventPriority.LOWEST);

            Log.Information("[StringInterceptor] v1.5.0 Loaded. 4-size Chinese fonts + Pericles coexist.");
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
                    catch (Exception ex) { Log.Error($"[StringInterceptor] ChineseFontLoader failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("SeedZhCN", () =>
                {
                    try
                    {
                        string logPath = System.IO.Path.Combine(TranslationProcessor.GetLogsDir(), "zh_CN.xml");
                        if (!System.IO.File.Exists(logPath))
                        {
                            // 确保 Logs 目录存在
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));

                            var root = ContentCache.Get<XElement>("Mod/zh_CN", false);
                            if (root != null)
                            {
                                var doc = new XDocument();
                                doc.Add(root);
                                { var fs = new System.IO.FileStream(logPath, System.IO.FileMode.Create, System.IO.FileAccess.Write); var sw = new System.IO.StreamWriter(fs, new System.Text.UTF8Encoding(false)); sw.Write(doc.ToString()); sw.Close(); fs.Close(); }
                                Log.Information("[StringInterceptor] Seeded Logs/zh_CN.xml from scmod.");
                            }
                        }
                    }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] Seed failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("LoadTranslations", () =>
                {
                    try { TranslationProcessor.LoadTranslations(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] LoadTranslations failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("ProcessStrings", () =>
                {
                    try { ProcessStrings(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] ProcessStrings failed: {ex.Message}"); }
                });

                LoadingManager.QueueItem("StartWidgetScanner", () =>
                {
                    try { StartWidgetScanner(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] StartWidgetScanner failed: {ex.Message}"); }
                });
            }
            else if (args[0] is List<Action> actions)
            {
                // 旧版兼容：List<Action>
                actions.Add(() =>
                {
                    try { ChineseFontLoader.Load(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] ChineseFontLoader failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try
                    {
                        string logPath = System.IO.Path.Combine(TranslationProcessor.GetLogsDir(), "zh_CN.xml");
                        if (!System.IO.File.Exists(logPath))
                        {
                            // 确保 Logs 目录存在
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));

                            var root = ContentCache.Get<XElement>("Mod/zh_CN", false);
                            if (root != null)
                            {
                                var doc = new XDocument();
                                doc.Add(root);
                                { var fs = new System.IO.FileStream(logPath, System.IO.FileMode.Create, System.IO.FileAccess.Write); var sw = new System.IO.StreamWriter(fs, new System.Text.UTF8Encoding(false)); sw.Write(doc.ToString()); sw.Close(); fs.Close(); }
                                Log.Information("[StringInterceptor] Seeded Logs/zh_CN.xml from scmod.");
                            }
                        }
                    }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] Seed failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try { TranslationProcessor.LoadTranslations(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] LoadTranslations failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try { ProcessStrings(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] ProcessStrings failed: {ex.Message}"); }
                });
                actions.Add(() =>
                {
                    try { StartWidgetScanner(); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] StartWidgetScanner failed: {ex.Message}"); }
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
                Log.Warning("[StringInterceptor] m_strings is null or empty.");
                return;
            }

            var keys = new List<string>(strings.Keys);
            int translated = 0;
            foreach (var key in keys)
            {
                string original = strings[key];
                string result = original;
                foreach (var processor in _processors)
                {
                    try { result = processor.Process(key, result, ++_globalIndex); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] Processor failed for key '{key}': {ex.Message}"); }
                }
                if (result != original) translated++;
                strings[key] = result;
            }

            Log.Information($"[StringInterceptor] Processed {strings.Count} StringsManager entries ({translated} translated). Index at {_globalIndex}.");
        }

        private void StartWidgetScanner()
        {
            _scannerActive = true;
            _skipFrames = 0;
            Log.Information("[StringInterceptor] Widget scanner started (frame-driven).");
        }

        private long _scanLogCounter; // [SuAPI] 诊断计数

        /// <summary>
        /// 每帧调用（由 EventBus Update 订阅触发），替代 System.Threading.Timer
        /// </summary>
        public void Update()
        {
            if (!_scannerActive)
            {
                return;
            }
            if (_skipFrames > 0) { _skipFrames--; return; }

            _scanLogCounter++; // [SuAPI]
            if (_scanLogCounter == 1) // [SuAPI] 只输出第1帧
                Log.Information($"[SuAPI] Frame.Update triggered! scanner active, frame={Time.FrameIndex}");

            ScanWidgetTree();
        }

        private void ScanWidgetTree()
        {
            long frame = Time.FrameIndex;
            if (frame == _lastScannedFrame) return;
            _lastScannedFrame = frame;

            var root = ScreensManager.RootWidget;
            if (root == null)
            {
                return;
            }

            // 设置当前 Screen 名称
            TranslationProcessor.CurrentScreen = ScreensManager.CurrentScreen?.GetType().Name ?? "Unknown";

            int labelCount = 0;
            int buttonCount = 0;
            ScanContainer(root, ref labelCount, ref buttonCount);

            bool foundNew = labelCount > 0 || buttonCount > 0;

            if (_scanLogCounter <= 3) // [SuAPI] 诊断前3次扫描
                Log.Information($"[SuAPI] ScanWidgetTree: labels={labelCount}, buttons={buttonCount}, root={root?.GetType().Name}");

            if (foundNew)
            {
                _framesSinceNewWidget = 0;
                _collectedSaved = false; // 新 UI → 允许再次保存
                _skipFrames = 0; // 活跃时每帧扫描
            }
            else
            {
                _framesSinceNewWidget++;
                if (_framesSinceNewWidget >= IDLE_THRESHOLD)
                {
                    _skipFrames = 11; // 空闲时约每 12 帧扫描一次（~200ms @60fps）
                    // UI 稳定 → 保存收集的字符串
                    if (!_collectedSaved)
                    {
                        TranslationProcessor.SaveCollected();
                        _collectedSaved = true;
                    }
                }
            }

            int removedLabels = 0;
            {
                var toRemove = new List<LabelWidget>();
                foreach (var l in _processedLabels)
                    if (l.ParentWidget == null) toRemove.Add(l);
                foreach (var l in toRemove) { _processedLabels.Remove(l); removedLabels++; }
            }
            int removedButtons = 0;
            {
                var toRemove = new List<ButtonWidget>();
                foreach (var b in _processedButtons)
                    if (b.ParentWidget == null) toRemove.Add(b);
                foreach (var b in toRemove) { _processedButtons.Remove(b); removedButtons++; }
            }
            if (removedLabels > 0 || removedButtons > 0)
            {
                _framesSinceNewWidget = 0;
                _skipFrames = 0; // widget 被移除时切换回活跃扫描
            }
        }

        private void ScanContainer(ContainerWidget container, ref int labelCount, ref int buttonCount)
        {
            if (container == null) return;

            foreach (var child in container.Children)
            {
                if (child is LabelWidget label && !_processedLabels.Contains(label))
                {
                    _processedLabels.Add(label);
                    string original = label.Text;
                    if (!string.IsNullOrEmpty(original))
                    {
                        string source = child.GetType().Name;
                        string result = original;
                        foreach (var processor in _processors)
                        {
                            try { result = processor.Process(source, result, ++_globalIndex); }
                            catch { }
                        }
                        if (result != original)
                            label.Text = result;

                        // 如果结果含中文 → 切换为中文字体
                        if (ContainsChinese(result))
                            TrySetChineseFont(label);

                        labelCount++;
                    }
                }
                else if (child is ButtonWidget button && !_processedButtons.Contains(button))
                {
                    _processedButtons.Add(button);
                    string original = button.Text;
                    if (!string.IsNullOrEmpty(original))
                    {
                        string source = child.GetType().Name;
                        string result = original;
                        foreach (var processor in _processors)
                        {
                            try { result = processor.Process(source, result, ++_globalIndex); }
                            catch { }
                        }
                        button.Text = result;

                        if (ContainsChinese(result))
                            TrySetChineseFont(button);

                        buttonCount++;
                    }
                }

                if (child is ContainerWidget childContainer)
                {
                    ScanContainer(childContainer, ref labelCount, ref buttonCount);
                }
            }
        }

        /// <summary>
        /// 检测文本是否含中文字符（CJK Unified Ideographs: U+4E00–U+9FFF）
        /// </summary>
        private static bool ContainsChinese(string text)
        {
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 将 LabelWidget 的 Font 设为中国字体（仅在字体已加载时）
        /// </summary>
        private static void TrySetChineseFont(LabelWidget widget)
        {
            if (widget.Font == null) return;
            float gh = widget.Font.GlyphHeight;
            var cnFont = ChineseFontLoader.GetClosestChineseFont(gh);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        /// <summary>
        /// 将 ButtonWidget 的 Font 设为中国字体
        /// </summary>
        private static void TrySetChineseFont(ButtonWidget widget)
        {
            if (widget.Font == null) return;
            float gh = widget.Font.GlyphHeight;
            var cnFont = ChineseFontLoader.GetClosestChineseFont(gh);
            if (cnFont != null && widget.Font != cnFont)
                widget.Font = cnFont;
        }

        public void OnUnload()
        {
            TranslationProcessor.SaveCollected();
            _scannerActive = false;
            _processedLabels.Clear();
            _processedButtons.Clear();
            _processors.Clear();
            Log.Information("[StringInterceptor] Unloaded.");
        }
    }
}
