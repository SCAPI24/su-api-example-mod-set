using System;

namespace CmdBridgeMod
{
    /// <summary>
    /// 平台判定：与 SuAPI 用**同一套**标准，不自造第二套。
    ///
    /// Source: EntitySystem/SuAPI/ModLoader.cs:ModLoader.GetCurrentPlatform ——
    /// 它返回 "Android"/"Windows"/"MacOS"/"Linux"/"Unknown"，其中 Android 靠
    /// `PlatformID.Unix` + `ANDROID_ROOT`/`ANDROID_DATA` 环境变量判定；
    /// SuAPI 自己的 `GetPlatformRootDirectory`/`GetPlatformLibDirectory` 也以它为准。
    ///
    /// 兜底：SuAPI 判定失败（例如类型缺失）时再问一次 .NET 自带的
    /// `OperatingSystem.IsAndroid()`（SuAPICore 里也这么用，见 SuAPICoreMod.cs:137）。
    /// </summary>
    internal static class PlatformInfo
    {
        /// <summary>当前是否 Android。Windows 上恒为 false。</summary>
        public static bool IsAndroid
        {
            get
            {
                try
                {
                    if (string.Equals(SuAPI.ModLoader.GetCurrentPlatform(), "Android",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                }
                return OperatingSystem.IsAndroid();
            }
        }
    }
}
