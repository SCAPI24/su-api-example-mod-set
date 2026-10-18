using System;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 身份会话键的派生规则（**唯一一处**；Terrain 层不认识 UserId.dat / Game.UserManager，
    /// 只认 Hello 里带上来的身份字符串）。
    ///
    /// 为什么不是"直接用身份"：身份是 GUID（UserId.dat），**全值属私有信息，禁止进日志 / 被跟踪文件**。
    /// 所以对外（日志、实时地图、页面选角）一律只用这里派生的稳定键：
    ///   · 有身份 → <c>"u" + FNV-1a(64) 的 16 位十六进制</c>（确定性、定长、不可逆）；
    ///   · 没有身份（退化路径）→ <c>"c" + clientId</c>（按连接，**明确**是退化，不与其他端合并）；
    ///   · 主机自己 → <see cref="HostKey"/>（固定字符串）。
    /// 前缀不同 ⇒ 退化键与身份键**永不冲突**。
    /// </summary>
    public static class PlayerSessionKeys
    {
    /// <summary>主机自己的稳定键（它不是网络客户端，没有 UserId 可归档）。</summary>
    public const string HostKey = "host";

    /// <summary>身份稳定键前缀（身份哈希）。</summary>
    public const string UserKeyPrefix = "u";

    /// <summary>退化键前缀（Hello 没带身份：按 clientId 单独一条，明确不合并）。</summary>
    public const string DegradedKeyPrefix = "c";

    /// <summary>日志里只出现身份的前 8 位（GUID 全值禁止进日志）。</summary>
    public const int ShortIdentityLength = 8;

    /// <summary>身份的稳定键：<c>"u" + FNV-1a 64 位十六进制</c>。空身份返回空串（调用方必须走退化路径）。</summary>
    public static string StableKeyOf(string identity)
    {
    string text = (identity ?? string.Empty).Trim();
    if (text.Length == 0) return string.Empty;
    ulong hash = 14695981039346656037UL;             // FNV-1a 64 偏移基数
    for (int i = 0; i < text.Length; i++)
    {
    hash ^= text[i];
    hash *= 1099511628211UL;                     // FNV-1a 64 质数
    }
    return UserKeyPrefix + hash.ToString("x16");
    }

    /// <summary>退化路径的键：<c>"c" + clientId</c>（身份缺失时按连接归档，重连=新键）。</summary>
    public static string DegradedKeyOf(int clientId) => DegradedKeyPrefix + clientId.ToString();

    /// <summary>日志用短身份：只打前 8 位（空 = <c>(empty)</c>）。</summary>
    public static string ShortIdentity(string identity)
    {
    string text = (identity ?? string.Empty).Trim();
    if (text.Length == 0) return "(empty)";
    return text.Length <= ShortIdentityLength
    ? text
    : text.Substring(0, ShortIdentityLength) + "...";
    }
    }
}
