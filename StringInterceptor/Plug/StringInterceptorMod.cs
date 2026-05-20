using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Engine;
using System.Xml.Linq;
using Engine.Content;
using Engine.Media;
using Game;
using SuMod;
using SuMod.Tools;

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
                    string savePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "zh_CN.xml");

                    // 加载已有条目 → 按 Screen 分组 + 已见 Original
                    var existingScreens = new Dictionary<string, Dictionary<string, string>>();
                    var existingOriginals = new HashSet<string>();
                    if (System.IO.File.Exists(savePath))
                    {
                        try
                        {
                            var existing = XDocument.Load(savePath);
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
                    var doc = new XDocument(
                        new XDeclaration("1.0", "UTF-8", null),
                        new XElement("Translations")
                    );
                    foreach (var screenKv in existingScreens)
                    {
                        var screenEl = new XElement("Screen", new XAttribute("Name", screenKv.Key));
                        var sorted = new List<KeyValuePair<string, string>>(screenKv.Value);
                        sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                        foreach (var kv in sorted)
                            screenEl.Add(new XElement("Entry", new XAttribute("Original", kv.Key), new XAttribute("Translation", kv.Value)));
                        doc.Root.Add(screenEl);
                    }

                    System.IO.File.WriteAllText(savePath, doc.ToString(), new System.Text.UTF8Encoding(false));
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
        private Timer _widgetScanTimer;
        private readonly HashSet<LabelWidget> _processedLabels = new HashSet<LabelWidget>();
        private readonly HashSet<ButtonWidget> _processedButtons = new HashSet<ButtonWidget>();
        private int _globalIndex;
        private long _lastScannedFrame = -1;

        // 自适应扫描频率
        private int _framesSinceNewWidget;
        private const int IDLE_THRESHOLD = 60;
        private bool _collectedSaved;
        private int _currentIntervalMs = 16;

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
                return HandleLoadingInitialize((List<Action>)args[0]);
            }, EventPriority.LOWEST);

            Log.Information("[StringInterceptor] v1.5.0 Loaded. 4-size Chinese fonts + Pericles coexist.");
        }

        private object[] HandleLoadingInitialize(List<Action> actions)
        {
            // 加载中文字体（不替换 Pericles32） — ContentCache 此时已包含 ModResource 加载的资源
            actions.Add(() =>
            {
                try { ChineseFontLoader.Load(); }
                catch (Exception ex) { Log.Error($"[StringInterceptor] ChineseFontLoader failed: {ex.Message}"); }
            });

            // 首次启动时将 scmod 内 zh_CN.xml 复制到 Logs 作为基线
            actions.Add(() =>
            {
                try
                {
                    string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "zh_CN.xml");
                    if (!System.IO.File.Exists(logPath))
                    {
                        var root = ContentCache.Get<XElement>("Mod/zh_CN", false);
                        if (root != null)
                        {
                            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
                            System.IO.File.WriteAllText(logPath, doc.ToString(), new System.Text.UTF8Encoding(false));
                            Log.Information("[StringInterceptor] Seeded Logs/zh_CN.xml from scmod.");
                        }
                    }
                }
                catch (Exception ex) { Log.Error($"[StringInterceptor] Seed failed: {ex.Message}"); }
            });

            // 加载翻译文件
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

            return new object[] { false, actions };
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
            foreach (var key in keys)
            {
                string original = strings[key];
                string result = original;
                foreach (var processor in _processors)
                {
                    try { result = processor.Process(key, result, ++_globalIndex); }
                    catch (Exception ex) { Log.Error($"[StringInterceptor] Processor failed for key '{key}': {ex.Message}"); }
                }
                strings[key] = result;
            }

            Log.Information($"[StringInterceptor] Processed {strings.Count} StringsManager entries. Index at {_globalIndex}.");
        }

        private void StartWidgetScanner()
        {
            _widgetScanTimer = new Timer(_ =>
            {
                try { Dispatcher.Dispatch(() => ScanWidgetTree()); }
                catch { }
            }, null, _currentIntervalMs, _currentIntervalMs);

            Log.Information("[StringInterceptor] Widget scanner started (adaptive interval).");
        }

        private void ScanWidgetTree()
        {
            long frame = Time.FrameIndex;
            if (frame == _lastScannedFrame) return;
            _lastScannedFrame = frame;

            var root = ScreensManager.RootWidget;
            if (root == null) return;

            // 设置当前 Screen 名称
            var screen = ScreensManager.CurrentScreen;
            TranslationProcessor.CurrentScreen = screen?.GetType().Name ?? "Unknown";

            int labelCount = 0;
            int buttonCount = 0;
            ScanContainer(root, ref labelCount, ref buttonCount);

            bool foundNew = labelCount > 0 || buttonCount > 0;

            if (foundNew)
            {
                _framesSinceNewWidget = 0;
                _collectedSaved = false; // 新 UI → 允许再次保存
                if (_currentIntervalMs != 16)
                {
                    _currentIntervalMs = 16;
                    _widgetScanTimer?.Change(16, 16);
                }
            }
            else
            {
                _framesSinceNewWidget++;
                if (_framesSinceNewWidget >= IDLE_THRESHOLD && _currentIntervalMs != 200)
                {
                    _currentIntervalMs = 200;
                    _widgetScanTimer?.Change(200, 200);
                    // UI 稳定 → 保存收集的字符串
                    if (!_collectedSaved)
                    {
                        TranslationProcessor.SaveCollected();
                        _collectedSaved = true;
                    }
                }
            }

            int removedLabels = _processedLabels.RemoveWhere(l => l.ParentWidget == null);
            int removedButtons = _processedButtons.RemoveWhere(b => b.ParentWidget == null);
            if (removedLabels > 0 || removedButtons > 0)
            {
                _framesSinceNewWidget = 0;
                if (_currentIntervalMs != 16)
                {
                    _currentIntervalMs = 16;
                    _widgetScanTimer?.Change(16, 16);
                }
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
            _widgetScanTimer?.Dispose();
            _widgetScanTimer = null;
            _processedLabels.Clear();
            _processedButtons.Clear();
            _processors.Clear();
            Log.Information("[StringInterceptor] Unloaded.");
        }
    }
}
