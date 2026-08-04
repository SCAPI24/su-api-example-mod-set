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
        private static bool _hasCollectedChanges;
        private static readonly HashSet<string> ExportableBuiltInScreens = new HashSet<string>
        {
            "StringsManager", "MainMenuScreen", "SettingsScreen", "SettingsPerformanceScreen",
            "SettingsAudioScreen", "SettingsGraphicsScreen", "SettingsControlsScreen",
            "SettingsUiScreen", "SettingsCompatibilityScreen", "HelpScreen", "HelpTopicScreen",
            "BestiaryScreen", "BestiaryDescriptionScreen", "RecipaediaScreen",
            "RecipaediaRecipesScreen", "RecipaediaDescriptionScreen", "GameLoadingScreen",
            "ModifyWorldScreen", "NewWorldScreen", "WorldOptionsScreen", "ContentScreen",
            "ManageContentScreen", "SuContentScreen", "SuManageContentScreen",
            "SuCommunityContentScreen"
        };

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
            string path = Storage.CombinePaths(GetLogsDir(), "zh_CN.xml");
            if (Storage.FileExists(path))
                return;

            XElement source = ContentCache.Get<XElement>("Mod/zh_CN", false);
            if (source == null)
                return;

            string text = new XDocument(new XElement(source)).ToString();
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
        /// 清理旧版本导出表中错误收集的游戏内、社区和外置 Mod 文本。仅在内容确实变化时写回，
        /// 并且整个操作放在后台，避免进入主菜单时产生 I/O 停顿。
        /// Source: Survivalcraft/Game/GameScreen.cs:GameScreen
        /// Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.PopulateList
        /// </summary>
        public static void SanitizeExportFileAsync()
        {
            string path = Storage.CombinePaths(GetLogsDir(), "zh_CN.xml");
            _ = Task.Run(delegate
            {
                try
                {
                    lock (_lock)
                    {
                        if (!Storage.FileExists(path))
                            return;
                        XDocument document;
                        using (var stream = Storage.OpenFile(path, OpenFileMode.Read))
                            document = XDocument.Load(stream);
                        if (document.Root == null)
                            return;

                        bool changed = false;
                        var remove = new List<XElement>();
                        foreach (XElement screen in document.Root.Elements("Screen"))
                        {
                            string name = (string)screen.Attribute("Name") ?? string.Empty;
                            if (!IsExportableBuiltInScreen(name))
                                remove.Add(screen);
                        }
                        foreach (XElement screen in remove)
                        {
                            screen.Remove();
                            changed = true;
                        }
                        if (!changed)
                            return;

                        using (var stream = Storage.OpenFile(path, OpenFileMode.Create))
                        using (var writer = new System.IO.StreamWriter(stream,
                            new System.Text.UTF8Encoding(false)))
                            writer.Write(document.ToString());
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[TranslationMod] Failed to sanitize export file: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 将收集到的字符串按 Screen 分类追加写入 Logs/zh_CN.xml（合并已有，去重，组内 ABC 排序）
        /// </summary>
        public static void SaveCollected()
        {
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
                                    if (!IsExportableBuiltInScreen(sn))
                                        continue;
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
                        if (!IsExportableBuiltInScreen(screen))
                            continue;
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
                    return result;
                }
            }

            // 未翻译的动态内容、社区内容和外置 Mod 文本不进入导出表。
            if (collectForExport)
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
                _hasCollectedChanges = true;
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

        private static bool IsExportableBuiltInScreen(string screenName)
        {
            return ExportableBuiltInScreens.Contains(screenName);
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
        public string Version => "1.6.3";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }

        public bool IsMergeLib => true;

        private IModParentField _mpf;
        private readonly TranslationProcessor _translationProcessor = new TranslationProcessor();
        // 帧驱动扫描（替代 Timer，避免 Release Android 上 System.Threading.Timer 不可用）
        private int _skipFrames; // 0 = 每帧扫描, N = 跳过 N 帧
        private bool _scannerActive;

        private readonly HashSet<LabelWidget> _processedLabels = new HashSet<LabelWidget>();
        private readonly HashSet<ButtonWidget> _processedButtons = new HashSet<ButtonWidget>();
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

            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                return HandleLoadingInitialize((object[])args);
            }, EventPriority.LOWEST);

            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                Update();
                return null;
            }, EventPriority.LOWEST);

            Log.Information("[TranslationMod] v1.6.3 Loaded. Shared SuAPI font profiles are lazy.");
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
                        TranslationProcessor.SeedExportFileAsync();
                        TranslationProcessor.SanitizeExportFileAsync();
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
                        TranslationProcessor.SeedExportFileAsync();
                        TranslationProcessor.SanitizeExportFileAsync();
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
                _skipFrames = 2;
            }
        }

        /// <summary>
        /// 只接受游戏原版及明确列出的 SuAPI 内置 Mod Screen。外置 Mod 的 Screen 不参与扫描。
        /// 游戏内 HUD 也跳过，避免日志、聊天和运行时状态进入翻译表。
        /// Source: EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:SuAPICoreMod.LoadBuiltInMod
        /// </summary>
        private static bool IsSupportedScreen(Screen screen)
        {
            if (screen == null || screen is GameScreen)
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
            return name == "SuAPICore" ||
                name == "SuAPIModDownload" ||
                name == "SuAPIExternalContentImport";
        }

        /// <summary>
        /// 动态列表、日志对话框以及外置 Mod 根控件没有稳定原文，不能写入翻译导出表。
        /// Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
        /// Source: Survivalcraft/Game/ViewGameLogDialog.cs:ViewGameLogDialog.ViewGameLogDialog
        /// </summary>
        private static bool IsSupportedWidget(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (IsRegisteredExternalWidget(current))
                    return false;
                if (!IsSupportedUiAssembly(current.GetType().Assembly))
                    return false;
                // 原版 Dialog 可由任意外置 Mod 直接创建，无法可靠判断来源；
                // 仅处理 SuAPI 内置模块自定义的 Dialog 类型。
                if ((current is Dialog && current.GetType().Assembly == typeof(Screen).Assembly) ||
                    (current is ListPanelWidget listPanel && IsDynamicContentList(listPanel)))
                    return false;

                object tag = current.Tag;
                if (tag is WorldInfo || tag is PlayerInfo || tag is PlayerData ||
                    tag is CommunityContentEntry)
                    return false;
            }
            return true;
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
                        if (ContainsChinese(result))
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
        /// 社区下载项和 Mod 管理项来自网络或磁盘，不能视为固定翻译文本。其他原版静态列表
        /// 仍允许扫描，确保 SuAPI 的固定选择项可进入翻译表。
        /// Source: Survivalcraft/Game/CommunityContentScreen.cs:CommunityContentScreen.PopulateList
        /// Source: EntitySystem/SuAPICore/Plug/SuManageContentScreen.cs:SuManageContentScreen.PopulateModList
        /// </summary>
        private static bool IsDynamicContentList(ListPanelWidget listPanel)
        {
            if (listPanel.Name == "CommunitySelection.List")
                return false;

            Screen screen = ScreensManager.CurrentScreen;
            return screen is CommunityContentScreen || screen is ManageContentScreen;
        }

        private void ScanContainer(ContainerWidget container, ref int labelCount, ref int buttonCount)
        {
            if (container == null) return;

            foreach (var child in container.Children)
            {
                if (!IsSupportedWidget(child))
                    continue;

                if (child is LabelWidget label && !_processedLabels.Contains(label))
                {
                    string original = label.Text;
                    if (!IsPlaceholderText(original))
                    {
                        _processedLabels.Add(label);
                        string result = ProcessWidgetText(child.GetType().Name, original);
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
                    string original = button.Text;
                    if (!IsPlaceholderText(original))
                    {
                        _processedButtons.Add(button);
                        string result = ProcessWidgetText(child.GetType().Name, original);
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

        // Source: TranslationMod/Plug/TranslationMod.cs:TranslationProcessor.Process
        private string ProcessWidgetText(string source, string original)
        {
            return _translationProcessor.Process(source, original, ++_globalIndex,
                collectForExport: true);
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
        /// 保留调用方已明确选择的共享 Chinese profile，仅将原版字体映射到最近的 profile。
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
            _processedLabels.Clear();
            _processedButtons.Clear();
            lock (s_externalWidgetsLock)
            {
                s_externalWidgets.Clear();
                s_externalCatalogs.Clear();
            }
            Log.Information("[TranslationMod] Unloaded.");
        }
    }
}
