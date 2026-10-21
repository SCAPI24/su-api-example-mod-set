using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 自定义皮肤资源在**内存里**的一份记录（名称 / 性别 / 哈希 / 原始字节）。
    ///
    /// 语义边界（用户口径）：
    ///   · 主机把这一份写进**世界作用域**的角色记录（SuMultiplayerPlayers.xml），
    ///     重连时从记录恢复，不需要客户端重新上传；
    ///   · 客户端只把它放进**本次会话的副本缓存**，绝不写进全局皮肤库
    ///     （data:/CharacterSkins），所以新客户端不会因为进过一次房间就多出别人的皮肤。
    ///
    /// 本类是纯值：不引用 Game / Engine，自检可直接构造断言。
    /// Source: Mod/ScMultiplayer/Core/ScMultiplayerSupportTypes.cs:658-666（SkinSessionAsset）
    /// </summary>
    public sealed class PlayerSkinAssetRecord
    {
        public string SkinName = string.Empty;
        public PlayerSex Sex = PlayerSex.Male;
        public byte[] Sha256 = Array.Empty<byte>();
        public byte[] Data = Array.Empty<byte>();
        /// <summary>
        /// 这份皮肤所属**角色记录**的不常变内容 hash（<see cref="PlayerRecordHash"/>；0 = 未知）。
        /// 主机补发皮肤资源前用它和该连接申报的已知 hash 比对：相等 = 对端本地已有这条记录，不必重发。
        /// </summary>
        public ulong RecordHash;

        public bool HasData => Data != null && Data.Length > 0;

        public bool HasHash => SkinAssetPolicy.IsValidHash(Sha256);

        /// <summary>去重键：优先用 SHA-256；没有哈希时退回名称（旧记录 / 损坏记录）。</summary>
        public string HashHex => SkinAssetPolicy.HashHex(Sha256);

        public string Key => HasHash
            ? SkinAssetPolicy.HashHex(Sha256)
            : "name:" + (SkinName ?? string.Empty).ToLowerInvariant();

        public PlayerSkinAssetRecord Clone() => new PlayerSkinAssetRecord
        {
            SkinName = SkinName ?? string.Empty,
            Sex = Sex,
            Sha256 = Sha256 != null ? (byte[])Sha256.Clone() : Array.Empty<byte>(),
            Data = Data != null ? (byte[])Data.Clone() : Array.Empty<byte>(),
            RecordHash = RecordHash
        };

        public string Describe() =>
            "\"" + (SkinName ?? string.Empty) + "\" · " + (HasHash ? SkinAssetPolicy.HashHex(Sha256) : "(no hash)") +
            " · " + (HasData ? Data.Length + "B" : "(no asset)");

        public bool HasSameBytes(PlayerSkinAssetRecord other) =>
            other != null && HasHash && other.HasHash &&
            SkinAssetPolicy.BytesEqual(Sha256, other.Sha256) &&
            SkinAssetPolicy.BytesEqual(Data, other.Data);
    }

    /// <summary>拒绝原因分类（计数与显式日志都用它，避免"失败就当成功"）。</summary>
    public enum SkinAssetRejectReason
    {
        None = 0,
        /// <summary>资产 / 压缩 / 分片超过上限。</summary>
        Oversize = 1,
        /// <summary>字段缺失、分片冲突、PNG 不合法、名称非法以外的结构问题。</summary>
        Malformed = 2,
        /// <summary>声明哈希与实际字节不一致（或同名不同内容）。</summary>
        HashMismatch = 3,
        /// <summary>皮肤名不合法（路径分隔符 / 扩展名 / 字符集 / 长度）。</summary>
        InvalidName = 4,
        /// <summary>主机不认识这个发送方（未加入的玩家端 / 调试端 / 已离开）。</summary>
        UnknownClient = 5,
        /// <summary>主机把这个身份的角色记录写入失败（没有记录 / 文件损坏等）。</summary>
        StoreFailed = 6,
        /// <summary>发送方无权做这件事（冒领别的 client_id / 方向错误）。</summary>
        NotAllowed = 7,
        /// <summary>同一 transfer_id 的元数据前后不一致，或重复分片内容不同。</summary>
        TransferConflict = 8,
        /// <summary>资源发送失败（通道不可用 / 背压 / 对端不可达）。</summary>
        SendFailed = 9
    }

    /// <summary>一次校验 / 接收的结果：Ok=false 时 Kind + Reason 都是可直接定位的原因。</summary>
    public sealed class SkinAssetCheck
    {
        public bool Ok;
        public SkinAssetRejectReason Kind;
        public string Reason;

        public static SkinAssetCheck Pass() => new SkinAssetCheck
        {
            Ok = true,
            Kind = SkinAssetRejectReason.None,
            Reason = null
        };

        public static SkinAssetCheck Fail(SkinAssetRejectReason kind, string reason) => new SkinAssetCheck
        {
            Ok = false,
            Kind = kind,
            Reason = reason ?? kind.ToString()
        };

        public override string ToString() => Ok ? "ok" : Kind + ": " + Reason;
    }

    /// <summary>
    /// 自定义皮肤资源的**纯策略**：大小上限、名称规则、PNG 尺寸校验、SHA-256、DEFLATE、
    /// 分片上限。所有拒绝都有明确原因（调用方据此计数并写 10 秒限流日志）。
    ///
    /// 引擎事实（只读参考，不在本类里调用）：
    ///   Source: Survivalcraft/Game/CharacterSkinsManager.cs:225-236（ValidateCharacterSkin：
    ///           ≤256x256 且宽高为 2 的幂）
    ///   Source: Survivalcraft/Game/CharacterSkinsManager.cs:49-56（GetFileName：自定义皮肤路径）
    ///   Source: Survivalcraft/Game/ExternalContentManager.cs:117-139（名称字符集规则）
    /// </summary>
    public static class SkinAssetPolicy
    {
        /// <summary>原始资产上限（256 KiB）；引擎皮肤最大 256x256，正常远小于它。</summary>
        public const int MaxAssetBytes = 256 * 1024;

        /// <summary>每个分片的负载上限；帧上限 60 KiB，留出 protobuf / 名称 / 身份的余量。</summary>
        public const int ChunkDataBytes = 48 * 1024;

        /// <summary>分片数上限（48 KiB * 8 > 256 KiB）。</summary>
        public const int MaxChunkCount = 8;

        /// <summary>压缩后字节上限（防止压缩炸弹无限膨胀）。</summary>
        public const int MaxCompressedBytes = MaxAssetBytes + 64 * 1024;

        /// <summary>皮肤名长度上限（与引擎 ExternalContentManager 一致）。</summary>
        public const int MaxSkinNameLength = 50;

        public const int Sha256Length = 32;
        public const string SkinExtension = ".scskin";
        public const int MaxImageDimension = 256;

        /// <summary>不完整传输的过期时间（毫秒）；过期后明确丢弃并计数，不静默卡住。</summary>
        public const long TransferTimeoutMs = 15000;

        /// <summary>同一发送方的并发传输上限（防御内存滥用）。</summary>
        public const int MaxConcurrentTransfers = 16;

        /// <summary>计数器摘要日志的最小间隔（用户口径 10 秒）。</summary>
        public const long LogIntervalMs = 10000;

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static bool IsBuiltInSkinName(string name) =>
            !string.IsNullOrEmpty(name) && name[0] == '$';

        /// <summary>自定义皮肤名 = 非内置 + 合法 `.scskin` 文件名。</summary>
        public static bool IsCustomSkinName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name[0] == '$') return false;
            return TryValidateSkinName(name, allowBuiltIn: false, out _);
        }

        /// <summary>
        /// 名称校验。规则与引擎导入外部内容一致（拉丁字母 / 数字 / - . _ 空格，≤50 字符），
        /// 额外要求自定义皮肤必须以 `.scskin` 结尾且不能含路径分隔符（防止路径穿越）。
        /// </summary>
        public static bool TryValidateSkinName(string name, bool allowBuiltIn, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                reason = "皮肤名为空";
                return false;
            }
            if (name.Length != name.Trim().Length)
            {
                reason = "皮肤名不能以空格开头或结尾";
                return false;
            }
            if (name.Length > MaxSkinNameLength)
            {
                reason = "皮肤名过长（" + name.Length + " > " + MaxSkinNameLength + " 字符）";
                return false;
            }
            bool builtIn = name[0] == '$';
            if (builtIn)
            {
                if (!allowBuiltIn)
                {
                    reason = "\"" + name + "\" 是内置皮肤名，这里只接受自定义皮肤";
                    return false;
                }
                if (name.Length < 2)
                {
                    reason = "内置皮肤名缺少内容：" + name;
                    return false;
                }
            }
            else if (!name.EndsWith(SkinExtension, StringComparison.OrdinalIgnoreCase) ||
                     name.Length <= SkinExtension.Length)
            {
                reason = "自定义皮肤必须以 " + SkinExtension + " 结尾：\"" + name + "\"";
                return false;
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == ' ' ||
                    (c == '$' && i == 0 && builtIn);
                if (allowed) continue;
                reason = "皮肤名含非法字符 U+" + ((int)c).ToString("X4") + "：\"" + name + "\"";
                return false;
            }
            return true;
        }

        public static bool IsValidHash(byte[] hash)
        {
            if (hash == null || hash.Length != Sha256Length) return false;
            for (int i = 0; i < hash.Length; i++)
                if (hash[i] != 0) return true;
            return false;
        }

        public static string HashHex(byte[] hash) =>
            hash == null || hash.Length == 0 ? string.Empty : Convert.ToHexString(hash).ToLowerInvariant();

        public static byte[] ParseHashHex(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
            string text = value.Trim();
            if (text.Length != Sha256Length * 2) return Array.Empty<byte>();
            try
            {
                byte[] hash = Convert.FromHexString(text);
                return IsValidHash(hash) ? hash : Array.Empty<byte>();
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }

        public static byte[] Sha256(byte[] data)
        {
            if (data == null) return Array.Empty<byte>();
            return System.Security.Cryptography.SHA256.HashData(data);
        }

        public static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        /// <summary>
        /// 资产格式校验：大小 + PNG 头 + 尺寸（≤256、宽高为 2 的幂）。
        /// 真正的解码校验由引擎侧端口（Image.Load）再做一次；这里失败一定是明确原因。
        /// </summary>
        public static bool TryValidateAssetData(byte[] data, out string reason)
        {
            reason = null;
            if (data == null || data.Length == 0)
            {
                reason = "资产为空（0 字节）";
                return false;
            }
            if (data.Length > MaxAssetBytes)
            {
                reason = "资产过大（" + data.Length + " > " + MaxAssetBytes + " 字节）";
                return false;
            }
            if (!TryValidateImage(data, out int width, out int height, out reason)) return false;
            return true;
        }

        /// <summary>
        /// 引擎的 <c>Image.Load</c> 同时支持 PNG 与 JPEG（Source: Engine/Media/Image.cs:216-235），
        /// 所以自定义皮肤两种都接受；尺寸规则与引擎 <c>ValidateCharacterSkin</c> 一致。
        /// </summary>
        public static bool TryValidateImage(byte[] data, out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;
            if (data == null || data.Length < 4)
            {
                reason = "图片数据过短（" + (data?.Length ?? 0) + " 字节）";
                return false;
            }
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return TryValidatePng(data, out width, out height, out reason);
            if (data[0] == 0xFF && data[1] == 0xD8)
                return TryValidateJpeg(data, out width, out height, out reason);
            reason = "不是 PNG / JPEG 图片（签名不匹配）";
            return false;
        }

        private static bool TryValidateJpeg(byte[] data, out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;
            int offset = 2;
            while (offset + 3 < data.Length)
            {
                if (data[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }
                byte marker = data[offset + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    offset += 2;
                    continue;
                }
                if (marker == 0xD9) break;
                if (offset + 4 > data.Length) break;
                int length = (data[offset + 2] << 8) | data[offset + 3];
                if (length < 2 || offset + 2 + length > data.Length) break;
                bool sof = (marker >= 0xC0 && marker <= 0xC3) ||
                    (marker >= 0xC5 && marker <= 0xC7) ||
                    (marker >= 0xC9 && marker <= 0xCB) ||
                    (marker >= 0xCD && marker <= 0xCF);
                if (sof)
                {
                    if (length < 7 || offset + 9 > data.Length)
                    {
                        reason = "JPEG SOF 段过短（读不到尺寸）";
                        return false;
                    }
                    height = (data[offset + 5] << 8) | data[offset + 6];
                    width = (data[offset + 7] << 8) | data[offset + 8];
                    return ValidateDimensions(width, height, out reason);
                }
                offset += 2 + length;
            }
            reason = "JPEG 缺少 SOF 尺寸段（不是完整图片）";
            return false;
        }

        private static bool ValidateDimensions(int width, int height, out string reason)
        {
            reason = null;
            if (width <= 0 || height <= 0)
            {
                reason = "图片尺寸非法（" + width + "x" + height + "）";
                return false;
            }
            if (width > MaxImageDimension || height > MaxImageDimension)
            {
                reason = "图片尺寸 " + width + "x" + height + " 超过 " + MaxImageDimension + "x" +
                    MaxImageDimension;
                return false;
            }
            if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
            {
                reason = "图片尺寸 " + width + "x" + height + " 不是 2 的幂（引擎要求）";
                return false;
            }
            return true;
        }

        public static bool TryValidatePng(byte[] data, out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;
            if (data == null || data.Length < 33)
            {
                reason = "PNG 数据过短（" + (data?.Length ?? 0) + " 字节）";
                return false;
            }
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (data[i] == PngSignature[i]) continue;
                reason = "不是 PNG（签名不匹配）";
                return false;
            }
            // 第一个 chunk 必须是 IHDR，长度 13。
            if (ReadInt32BigEndian(data, 8) != 13 ||
                data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R')
            {
                reason = "PNG 缺少 IHDR（不是可用的皮肤图片）";
                return false;
            }
            width = ReadInt32BigEndian(data, 16);
            height = ReadInt32BigEndian(data, 20);
            return ValidateDimensions(width, height, out reason);
        }

        public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

        /// <summary>把皮肤名里能看出的性别提示解析出来（NameHint）；没有提示返回 false。</summary>
        public static bool TryGetClassHint(string skinName, out PlayerSex sex)
        {
            sex = PlayerSex.Male;
            if (string.IsNullOrWhiteSpace(skinName)) return false;
            string lower = skinName.ToLowerInvariant();
            if (lower.Contains("female") || lower.Contains("girl") || lower.Contains("woman"))
            {
                sex = PlayerSex.Female;
                return true;
            }
            if (lower.Contains("male") || lower.Contains("boy") || lower.Contains("man"))
            {
                sex = PlayerSex.Male;
                return true;
            }
            return false;
        }

        public static int ChunkCountFor(int payloadLength) =>
            payloadLength <= 0 ? 0 : (payloadLength + ChunkDataBytes - 1) / ChunkDataBytes;

        public static bool TryCompress(byte[] raw, out byte[] compressed, out string reason)
        {
            compressed = null;
            reason = null;
            if (raw == null || raw.Length == 0)
            {
                reason = "没有可压缩的资产字节";
                return false;
            }
            try
            {
                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(raw, 0, raw.Length);
                compressed = output.ToArray();
            }
            catch (Exception e)
            {
                reason = "压缩失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (compressed.Length == 0)
            {
                reason = "压缩结果为空";
                return false;
            }
            if (compressed.Length > MaxCompressedBytes)
            {
                reason = "压缩后仍然过大（" + compressed.Length + " > " + MaxCompressedBytes + " 字节）";
                return false;
            }
            return true;
        }

        /// <summary>有界解压：解压结果绝不允许超过声明的 rawLength（防压缩炸弹）。</summary>
        public static bool TryDecompress(byte[] compressed, int rawLength, out byte[] raw, out string reason)
        {
            raw = null;
            reason = null;
            if (compressed == null || compressed.Length == 0)
            {
                reason = "压缩数据为空";
                return false;
            }
            if (rawLength <= 0 || rawLength > MaxAssetBytes)
            {
                reason = "声明的原始长度非法（" + rawLength + "）";
                return false;
            }
            try
            {
                using var input = new MemoryStream(compressed, writable: false);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(Math.Min(rawLength, 64 * 1024));
                var buffer = new byte[16 * 1024];
                int total = 0;
                while (true)
                {
                    int read = deflate.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    total += read;
                    if (total > rawLength)
                    {
                        reason = "解压后超过声明的原始长度（截断到 " + rawLength + " 字节）";
                        return false;
                    }
                    output.Write(buffer, 0, read);
                }
                if (total != rawLength)
                {
                    reason = "解压后长度 " + total + " 与声明的 " + rawLength + " 不一致";
                    return false;
                }
                raw = output.ToArray();
                return true;
            }
            catch (Exception e)
            {
                reason = "解压失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>把分片拼起来（调用方已保证每片长度有界）。</summary>
        public static bool TryJoinChunks(byte[][] chunks, int chunkCount, out byte[] joined, out string reason)
        {
            joined = null;
            reason = null;
            if (chunks == null || chunks.Length != chunkCount)
            {
                reason = "分片数组长度与 chunk_count 不一致";
                return false;
            }
            long total = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                if (chunks[i] == null)
                {
                    reason = "第 " + i + " 片缺失";
                    return false;
                }
                total += chunks[i].Length;
                if (total > MaxCompressedBytes)
                {
                    reason = "分片总长度超过上限（" + total + " > " + MaxCompressedBytes + " 字节）";
                    return false;
                }
            }
            var result = new byte[total];
            int offset = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                Buffer.BlockCopy(chunks[i], 0, result, offset, chunks[i].Length);
                offset += chunks[i].Length;
            }
            joined = result;
            return true;
        }

        private static int ReadInt32BigEndian(byte[] data, int offset) =>
            (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

        public static string DescribeLimit() =>
            "asset<=" + MaxAssetBytes + "B chunk<=" + ChunkDataBytes + "B chunks<=" + MaxChunkCount;
    }
}
