using System;
using System.Security.Cryptography;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机**房间 / 会话标识**（一次建房期间唯一，绝不落盘）。
    ///
    /// 作用（用户口径）：客户端从 Play 屏进入 ⇒ 重新加入房间（整图重下）；
    /// 游玩途中只是网络断了几秒、主机没有重启 ⇒ 同一会话 ⇒ 不下整图，只追帧。
    /// 主机重启后会话标识必然变化 ⇒ 客户端手里的世界过期 ⇒ 整图重下。
    ///
    /// 生成点：<c>TerrainSyncModule.StartHostServer</c>（也就是玩家按 CR 建房、房间会话开始的那一刻）；
    /// 只存在内存里，不写文件、不写 Project.xml、不写世界目录。
    /// 传输：HelloAck（主机 → 客户端）与 WorldBegin 世界清单（append-only 字段），
    /// 客户端把它和自己的下载世界登记在一起（见 <c>EngineWorldTransfer</c>）。
    ///
    /// 这里只放**纯逻辑**：tsverify 用同一份源码做单元断言。
    /// </summary>
    public static class SessionIdentity
    {
        /// <summary>随机字节数（128 位随机 ⇒ 32 位十六进制）。</summary>
        public const int RandomBytes = 16;

        /// <summary>十六进制字符串长度（<see cref="RandomBytes"/> × 2）。</summary>
        public const int HexLength = RandomBytes * 2;

        /// <summary>日志里用的短标识长度（主机 / 客户端会话标识在日志里靠它对照）。</summary>
        public const int ShortLength = 8;

        /// <summary>
        /// 生成一个新的会话标识：128 位密码学随机数的小写十六进制。
        /// **每次房间/会话开始都生成新的**；不读配置、不落盘、不缓存。
        /// </summary>
        public static string NewSessionId()
        {
            Span<byte> bytes = stackalloc byte[RandomBytes];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>日志用短标识：前 8 位 + 省略号；空值明确写 <c>(none)</c>（不猜）。</summary>
        public static string Short(string sessionId)
        {
            string text = (sessionId ?? string.Empty).Trim();
            if (text.Length == 0) return "(none)";
            return text.Length <= ShortLength ? text : text.Substring(0, ShortLength) + "...";
        }

        /// <summary>
        /// 会话标识是否形如本工程生成的 32 位小写十六进制。
        /// 只用于**显式判断**（例如日志 / 测试断言），不把不合法值悄悄替换成默认值：
        /// 不合法就是"没有可用的会话标识"，调用方按首次加入处理并写清原因。
        /// </summary>
        public static bool IsWellFormed(string sessionId)
        {
            string text = (sessionId ?? string.Empty).Trim();
            if (text.Length != HexLength) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }
    }
}
