using System;
using System.Reflection;
using System.Xml.Linq;
using Engine;
using Engine.Media;
using Game;

namespace Channel
{
    /// <summary>
    /// 文案：默认英文；装了翻译 Mod（会注册 SuAPICore 的本地化服务）时切中文。
    /// 中文词条由本 Mod 自带，并通过反射交给翻译 Mod（AddTranslations / RegisterWidget），
    /// 不修改翻译 Mod 的任何文件。
    /// Source: EntitySystem/SuAPICore/Plug/SuAPIModLocalizationApi.cs:SuAPIModLocalizations
    /// Source: Mod/TranslationMod/Plug/TranslationMod.cs:TranslationApi
    /// </summary>
    internal static class ChannelText
    {
        public const string ModIdentifier = "Channel";
        public const string TranslationLanguage = "zh_CN";

        public const string ChannelEn = "Channel";
        public const string ChannelZh = "信道";

        public const string ChannelDescriptionEn =
            "A channel wire. Wires with the same channel number are connected together. " +
            "Right and Left inputs set the channel number (low/high 4 bits), a low level on the Bottom input writes it, " +
            "a high level (or no connection) joins the channel.";

        public const string ChannelDescriptionZh =
            "信道导线。通道号相同的导线视为同一根。Right/Left 输入分别给通道号的低/高 4 位，" +
            "Bottom 低电平时写入通道号，高电平（或不接线）时接入通道。";

        public const string RecipeDescriptionEn = "Make channel wires";
        public const string RecipeDescriptionZh = "合成信道";

        private static bool s_translationHooked;

        public static bool IsChinese
        {
            get
            {
                string language = ReadCurrentLanguage();
                return !string.IsNullOrEmpty(language)
                    && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Channel => IsChinese ? ChannelZh : ChannelEn;

        public static string ChannelDescription => IsChinese ? ChannelDescriptionZh : ChannelDescriptionEn;

        public static string RecipeDescription => IsChinese ? RecipeDescriptionZh : RecipeDescriptionEn;

        /// <summary>
        /// 中文环境下返回与原字体**视觉等高**的中文/Pericles 兼容字体（本仓库规范做法）；
        /// 英文环境或加载失败返回 null，调用方保持原字体。
        /// Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetMatchingProfile
        /// Source: Mod/TranslationMod/Plug/ChineseFontLoader.cs
        /// </summary>
        public static BitmapFont GetChineseFont(BitmapFont source)
        {
            if (!IsChinese)
                return null;
            try
            {
                return SuAPICore.SuAPIFonts.GetMatchingProfile(source);
            }
            catch (Exception ex)
            {
                Log.Warning("[Channel] chinese font load failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>把本 Mod 的词条与 UI 注册给翻译 Mod（未安装则静默跳过）。</summary>
        public static void RegisterWithTranslationMod(Widget dialogRoot = null)
        {
            try
            {
                Type api = FindType("TranslationMod.TranslationApi");
                if (api == null)
                    return;

                if (!s_translationHooked)
                {
                    s_translationHooked = true;
                    Invoke(api, "Register", new object[] { ModIdentifier, TranslationLanguage });
                    Invoke(api, "AddTranslations", new object[] { ModIdentifier, TranslationLanguage, BuildTranslations() });
                }

                if (dialogRoot != null)
                    Invoke(api, "RegisterWidget", new object[] { dialogRoot, ModIdentifier, TranslationLanguage });
            }
            catch (Exception ex)
            {
                Log.Warning("[Channel] translation hook failed: " + ex.Message);
            }
        }

        private static void Invoke(Type api, string methodName, object[] args)
        {
            MethodInfo[] methods = api.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                    continue;
                bool match = true;
                for (int j = 0; j < args.Length; j++)
                {
                    if (args[j] == null)
                        continue;
                    if (!parameters[j].ParameterType.IsInstanceOfType(args[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    method.Invoke(null, args);
                    return;
                }
            }
        }

        private static XElement BuildTranslations()
        {
            XElement root = new XElement("Translations");
            AddEntry(root, ChannelEn, ChannelZh);
            AddEntry(root, RecipeDescriptionEn, RecipeDescriptionZh);
            AddEntry(root, ChannelDescriptionEn, ChannelDescriptionZh);
            return root;
        }

        private static void AddEntry(XElement root, string original, string translation)
        {
            root.Add(new XElement("Entry",
                new XAttribute("Original", original),
                new XAttribute("Translation", translation)));
        }

        private static string ReadCurrentLanguage()
        {
            try
            {
                Type type = FindType("SuAPICore.SuAPIModLocalizations");
                PropertyInfo property = type?.GetProperty("CurrentLanguage", BindingFlags.Public | BindingFlags.Static);
                return property?.GetValue(null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Type FindType(string fullName)
        {
            Type type = Type.GetType(fullName, throwOnError: false);
            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }
            return null;
        }
    }
}
