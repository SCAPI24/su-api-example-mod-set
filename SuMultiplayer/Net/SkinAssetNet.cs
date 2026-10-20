using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 自定义皮肤资源（<c>.scskin</c>）的**唯一线格式编解码**：
    ///   原始字节 → PNG/尺寸/大小校验 → SHA-256 → DEFLATE → 按帧上限切片 →
    ///   <see cref="Proto.PlayerSkinAsset"/> 列表；反向只在装配器完成时做一次。
    ///
    /// 为什么要切片：帧负载上限是 60 KiB（<c>TerrainFrame.MaxPayloadBytes</c>），
    /// 256x256 的 PNG 可能超过它。每片 ≤ 48 KiB，最多 8 片；装配器（纯逻辑）负责
    /// 元数据一致性 / 总长上限 / 解压上限 / 哈希校验。
    ///
    /// 不在这里写任何文件：资源字节只进内存（主机写世界作用域角色记录是模块/引擎端口的事）。
    /// </summary>
    public static class SkinAssetCodec
    {
        /// <summary>
        /// 把一份资产编码成分片消息。失败时 <paramref name="messages"/> 为空且给出明确原因
        /// （oversize / malformed / invalid-name）。
        /// </summary>
        public static SkinAssetCheck TryEncode(string skinName, PlayerSex sex, byte[] rawAsset,
            int ownerClientId, string identity, int transferId,
            out List<Proto.PlayerSkinAsset> messages, out byte[] sha256)
        {
            messages = null;
            sha256 = Array.Empty<byte>();
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: false, out string nameReason))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.InvalidName, nameReason);
            if (sex != PlayerSex.Male && sex != PlayerSex.Female)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "性别非法");
            if (ownerClientId < 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "owner clientId 非法");
            if (transferId < 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "transfer_id 非法");
            if (identity != null && identity.Length > 128)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "identity 过长");
            if (rawAsset == null || rawAsset.Length == 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "资产为空（0 字节）");
            if (rawAsset.Length > SkinAssetPolicy.MaxAssetBytes)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "资产过大（" + rawAsset.Length + " > " + SkinAssetPolicy.MaxAssetBytes + " 字节）");
            if (!SkinAssetPolicy.TryValidateAssetData(rawAsset, out string dataReason))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, dataReason);

            if (!SkinAssetPolicy.TryCompress(rawAsset, out byte[] compressed, out string compressReason))
            {
                SkinAssetRejectReason kind = compressReason != null && compressReason.Contains("过大")
                    ? SkinAssetRejectReason.Oversize
                    : SkinAssetRejectReason.Malformed;
                return SkinAssetCheck.Fail(kind, compressReason);
            }
            int chunkCount = SkinAssetPolicy.ChunkCountFor(compressed.Length);
            if (chunkCount <= 0 || chunkCount > SkinAssetPolicy.MaxChunkCount)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "压缩后需要 " + chunkCount + " 片 > 上限 " + SkinAssetPolicy.MaxChunkCount);

            sha256 = SkinAssetPolicy.Sha256(rawAsset);
            messages = new List<Proto.PlayerSkinAsset>(chunkCount);
            for (int index = 0; index < chunkCount; index++)
            {
                int offset = index * SkinAssetPolicy.ChunkDataBytes;
                int length = Math.Min(SkinAssetPolicy.ChunkDataBytes, compressed.Length - offset);
                var message = new Proto.PlayerSkinAsset
                {
                    ClientId = ownerClientId,
                    Identity = identity ?? string.Empty,
                    SkinName = skinName,
                    PlayerClass = (uint)sex,
                    Sha256 = ByteString.CopyFrom(sha256),
                    RawLength = (uint)rawAsset.Length,
                    Compression = 1,
                    TransferId = (uint)transferId,
                    ChunkIndex = (uint)index,
                    ChunkCount = (uint)chunkCount,
                    Data = ByteString.CopyFrom(compressed, offset, length)
                };
                messages.Add(message);
            }
            return SkinAssetCheck.Pass();
        }

        /// <summary>读取一条分片消息的全部字段（结构校验由装配器统一做，避免两套口径）。</summary>
        public static SkinAssetCheck TryRead(Proto.PlayerSkinAsset message,
            out int ownerClientId, out int transferId, out string skinName, out string identity,
            out PlayerSex sex, out byte[] sha256, out int rawLength, out int compression,
            out int chunkIndex, out int chunkCount, out byte[] data)
        {
            ownerClientId = -1;
            transferId = -1;
            skinName = string.Empty;
            identity = string.Empty;
            sex = PlayerSex.Male;
            sha256 = Array.Empty<byte>();
            rawLength = 0;
            compression = 0;
            chunkIndex = 0;
            chunkCount = 0;
            data = Array.Empty<byte>();
            if (message == null)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "消息为空");
            ownerClientId = message.ClientId;
            transferId = message.TransferId > int.MaxValue ? -1 : (int)message.TransferId;
            skinName = message.SkinName ?? string.Empty;
            identity = message.Identity ?? string.Empty;
            sex = message.PlayerClass == (uint)PlayerSex.Female ? PlayerSex.Female : PlayerSex.Male;
            sha256 = message.Sha256?.Length > 0 ? message.Sha256.ToByteArray() : Array.Empty<byte>();
            rawLength = message.RawLength > int.MaxValue ? -1 : (int)message.RawLength;
            compression = message.Compression > int.MaxValue ? -1 : (int)message.Compression;
            chunkIndex = message.ChunkIndex > int.MaxValue ? -1 : (int)message.ChunkIndex;
            chunkCount = message.ChunkCount > int.MaxValue ? -1 : (int)message.ChunkCount;
            data = message.Data?.Length > 0 ? message.Data.ToByteArray() : Array.Empty<byte>();
            return SkinAssetCheck.Pass();
        }

        /// <summary>把原始资产写进一个副本（装配器已校验过；这里只复制，不再解释）。</summary>
        public static Stream OpenRead(byte[] data) => new MemoryStream(data, writable: false);
    }
}
