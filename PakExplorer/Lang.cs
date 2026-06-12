using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PakExplorer;

/// <summary>
/// 多语言翻译管理
/// </summary>
public static class Lang
{
    private static Dictionary<string, string> _strings = new();
    private static string _current = "en";
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PakExplorer", "lang.cfg");

    public static string Current => _current;

    /// <summary>
    /// 语言切换事件
    /// </summary>
    public static event Action LanguageChanged;

    /// <summary>
    /// 初始化：加载保存的语言设置
    /// </summary>
    public static void Init()
    {
        string lang = "en";
        if (File.Exists(ConfigPath))
        {
            var saved = File.ReadAllText(ConfigPath).Trim();
            if (saved == "en" || saved == "zh")
            {
                lang = saved;
            }
        }
        LoadLanguage(lang);
    }

    /// <summary>
    /// 切换语言
    /// </summary>
    public static void LoadLanguage(string lang)
    {
        _current = lang;
        _strings.Clear();

        string fileName = lang == "zh" ? "lang_zh.json" : "lang_en.json";

        // 优先从文件系统加载
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        else
        {
            // 回退到嵌入资源
            var assembly = typeof(Lang).Assembly;
            var resourceName = $"PakExplorer.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
            }
        }

        // 保存设置
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(ConfigPath, lang);
        }
        catch { }

        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// 获取翻译文本，key不存在时返回key本身
    /// </summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value))
        {
            return value;
        }
        return key;
    }

    /// <summary>
    /// 带格式化参数的翻译获取
    /// </summary>
    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
