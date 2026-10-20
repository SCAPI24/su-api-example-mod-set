using System;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 一次自定义皮肤资源传输的**分片装配器**（纯逻辑，主机与客户端共用）。
    ///
    /// 契约：
    ///   · 第一片建立传输元数据（owner / transfer_id / 名称 / 性别 / 哈希 / 原始长度 /
    ///     压缩方式 / 分片数），后续每一片都必须与它完全一致；
    ///   · 每个分片有硬上限（<see cref="SkinAssetPolicy.ChunkDataBytes"/>），
    ///     总压缩长度有硬上限，解压按声明长度截断 —— 任何一环超限都明确拒绝；
    ///   · 全部到齐后：拼接 → 有界解压 → 长度校验 → SHA-256 校验 → PNG 尺寸校验；
    ///   · 重复分片（内容相同）按幂等处理，内容不同则明确拒绝（不悄悄覆盖）。
    ///
    /// 失败原因同时给出分类（<see cref="SkinAssetRejectReason"/>），调用方据此累加
    /// skinMalformed / skinOversize / skinHashMismatch / rejected(reason)。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:1055-1117
    ///         （TryAssemblePlayerSkinAsset：语义参考；本版按上表逐项校验）
    /// </summary>
    public sealed class SkinAssetAssembler
    {
        private byte[][] m_chunks;

        public bool Started { get; private set; }
        public int OwnerClientId { get; private set; } = -1;
        public int TransferId { get; private set; } = -1;
        public string SkinName { get; private set; } = string.Empty;
        public string Identity { get; private set; } = string.Empty;
        public PlayerSex Sex { get; private set; } = PlayerSex.Male;
        public byte[] Sha256 { get; private set; } = Array.Empty<byte>();
        public int RawLength { get; private set; }
        public int Compression { get; private set; }
        public int ChunkCount { get; private set; }
        public int ReceivedChunks { get; private set; }
        public long LastUpdateMs { get; private set; }

        public bool IsComplete => Started && ReceivedChunks == ChunkCount;

        public string Describe() =>
            "owner=" + OwnerClientId + " transfer=" + TransferId + " name=\"" + SkinName +
            "\" chunks=" + ReceivedChunks + "/" + ChunkCount + " raw=" + RawLength +
            " compression=" + Compression;

        /// <summary>
        /// 接收一片。返回 Ok=true 且 <paramref name="complete"/> 非空 = 资产已完整并通过全部校验。
        /// 失败时调用方必须丢弃本装配器（同一传输不再被信任），并给发送方明确原因。
        /// </summary>
        public SkinAssetCheck TryAccept(int ownerClientId, int transferId, string skinName,
            string identity, PlayerSex sex, byte[] sha256, int rawLength, int compression,
            int chunkIndex, int chunkCount, byte[] data, long nowMs, out byte[] complete)
        {
            complete = null;
            if (ownerClientId < 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "owner clientId 非法");
            if (transferId < 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "transfer_id 非法");
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: false, out string nameReason))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.InvalidName, nameReason);
            if (!SkinAssetPolicy.IsValidHash(sha256))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed,
                    "sha256 不是 32 字节有效哈希（实际 " + (sha256?.Length ?? 0) + " 字节）");
            if (sex != PlayerSex.Male && sex != PlayerSex.Female)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "player_class 非法");
            if (rawLength <= 0 || rawLength > SkinAssetPolicy.MaxAssetBytes)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "raw_length=" + rawLength + " 不在 1.." + SkinAssetPolicy.MaxAssetBytes + " 内");
            if (compression != 0 && compression != 1)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed,
                    "compression=" + compression + " 未定义（只接受 0=无 / 1=DEFLATE）");
            if (chunkCount <= 0 || chunkCount > SkinAssetPolicy.MaxChunkCount)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "chunk_count=" + chunkCount + " 不在 1.." + SkinAssetPolicy.MaxChunkCount + " 内");
            if (chunkIndex < 0 || chunkIndex >= chunkCount)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed,
                    "chunk_index=" + chunkIndex + " 不在 0.." + (chunkCount - 1) + " 内");
            if (data == null || data.Length == 0)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed,
                    "第 " + chunkIndex + " 片为空");
            if (data.Length > SkinAssetPolicy.ChunkDataBytes)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "第 " + chunkIndex + " 片 " + data.Length + " 字节 > 上限 " +
                    SkinAssetPolicy.ChunkDataBytes);
            if (identity != null && identity.Length > 128)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, "identity 过长");

            if (!Started)
            {
                Started = true;
                OwnerClientId = ownerClientId;
                TransferId = transferId;
                SkinName = skinName;
                Identity = identity ?? string.Empty;
                Sex = sex;
                Sha256 = (byte[])sha256.Clone();
                RawLength = rawLength;
                Compression = compression;
                ChunkCount = chunkCount;
                m_chunks = new byte[chunkCount][];
            }
            else
            {
                if (OwnerClientId != ownerClientId || TransferId != transferId)
                    return SkinAssetCheck.Fail(SkinAssetRejectReason.TransferConflict,
                        "传输标识与已登记的不一致（owner/transfer）");
                if (!SkinAssetPolicy.BytesEqual(Sha256, sha256))
                    return SkinAssetCheck.Fail(SkinAssetRejectReason.HashMismatch,
                        "同一传输中途换了哈希（声明 " + SkinAssetPolicy.HashHex(sha256) +
                        " ≠ 已登记 " + SkinAssetPolicy.HashHex(Sha256) + "）");
                if (!string.Equals(SkinName, skinName, StringComparison.Ordinal))
                    return SkinAssetCheck.Fail(SkinAssetRejectReason.TransferConflict,
                        "同一传输中途换了皮肤名（\"" + skinName + "\" ≠ \"" + SkinName + "\"）");
                if (Sex != sex || RawLength != rawLength || Compression != compression ||
                    ChunkCount != chunkCount ||
                    !string.Equals(Identity, identity ?? string.Empty, StringComparison.Ordinal))
                {
                    return SkinAssetCheck.Fail(SkinAssetRejectReason.TransferConflict,
                        "同一传输中途换了元数据（性别/长度/压缩/分片数/身份）");
                }
            }

            byte[] existing = m_chunks[chunkIndex];
            if (existing != null)
            {
                if (!SkinAssetPolicy.BytesEqual(existing, data))
                    return SkinAssetCheck.Fail(SkinAssetRejectReason.TransferConflict,
                        "第 " + chunkIndex + " 片重复但内容不同");
                LastUpdateMs = nowMs;
                return SkinAssetCheck.Pass();   // 幂等重发
            }

            long total = 0;
            for (int i = 0; i < ChunkCount; i++)
            {
                if (m_chunks[i] == null) continue;
                total += m_chunks[i].Length;
            }
            if (total + data.Length > SkinAssetPolicy.MaxCompressedBytes)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Oversize,
                    "分片总长度 " + (total + data.Length) + " > 上限 " +
                    SkinAssetPolicy.MaxCompressedBytes);

            m_chunks[chunkIndex] = (byte[])data.Clone();
            ReceivedChunks++;
            LastUpdateMs = nowMs;
            if (ReceivedChunks < ChunkCount) return SkinAssetCheck.Pass();

            if (!SkinAssetPolicy.TryJoinChunks(m_chunks, ChunkCount, out byte[] payload,
                    out string joinReason))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed, joinReason);

            byte[] raw = payload;
            if (Compression == 1)
            {
                if (!SkinAssetPolicy.TryDecompress(payload, RawLength, out raw, out string zipReason))
                {
                    SkinAssetRejectReason kind = zipReason != null && zipReason.Contains("超过")
                        ? SkinAssetRejectReason.Oversize
                        : SkinAssetRejectReason.Malformed;
                    return SkinAssetCheck.Fail(kind, zipReason);
                }
            }
            if (raw.Length != RawLength)
                return SkinAssetCheck.Fail(SkinAssetRejectReason.Malformed,
                    "资产长度 " + raw.Length + " 与声明 " + RawLength + " 不一致");

            byte[] actual = SkinAssetPolicy.Sha256(raw);
            if (!SkinAssetPolicy.BytesEqual(actual, Sha256))
                return SkinAssetCheck.Fail(SkinAssetRejectReason.HashMismatch,
                    "资产哈希 " + SkinAssetPolicy.HashHex(actual) + " ≠ 声明 " +
                    SkinAssetPolicy.HashHex(Sha256));

            if (!SkinAssetPolicy.TryValidateAssetData(raw, out string dataReason))
            {
                SkinAssetRejectReason kind = dataReason != null && dataReason.Contains("过大")
                    ? SkinAssetRejectReason.Oversize
                    : SkinAssetRejectReason.Malformed;
                return SkinAssetCheck.Fail(kind, dataReason);
            }

            complete = raw;
            return SkinAssetCheck.Pass();
        }

        /// <summary>过期判定（模块 Tick 用它丢弃长期不完整的传输并计数）。</summary>
        public bool IsExpired(long nowMs) =>
            Started && !IsComplete && nowMs - LastUpdateMs >= SkinAssetPolicy.TransferTimeoutMs;
    }
}
