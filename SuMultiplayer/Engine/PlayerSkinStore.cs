using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎侧的皮肤资源动作（由 <see cref="NetworkAvatarSpawner"/> 在每次写外观后调用）。
    /// 只暴露三件事：应用一份皮肤、化身移除时忘掉它、按皮肤名查哈希（抓资料用）。
    /// </summary>
    public interface ISkinMaterializer
    {
        void Apply(PlayerData playerData, string skinName, byte[] sha256);

        void Forget(PlayerData playerData);

        void ForgetAll();

        bool TryGetSkinHash(string skinName, out byte[] sha256);
    }

    /// <summary>
    /// 自定义皮肤资源的**引擎侧实现**（<see cref="IPlayerSkinPort"/> + <see cref="ISkinMaterializer"/>）：
    ///   · 读本机皮肤库只读（`data:/CharacterSkins/&lt;name&gt;`），不写、不导入；
    ///   · 只把收到的资源放在**本次会话内存**里（客户端 replica-scoped cache）；
    ///   · 应用贴图到 <c>ComponentClothing</c> 的 <c>m_skinTexture</c> / <c>m_skinTextureName</c>
    ///     —— 这是引擎每次渲染皮肤时真正读取的两个字段（Source:
    ///     Survivalcraft/Game/ComponentClothing.cs:497-500 UpdateRenderTargets）。
    ///     只写这两个字段的原因：引擎的 <c>CharacterSkinsManager.LoadTexture</c> 对自定义皮肤
    ///     只认全局目录文件；本重写版禁止把别人的皮肤写进全局目录，所以必须把本会话贴图直接
    ///     交给 ComponentClothing（名字同时写回，引擎就不会再走文件加载 / 不静默换成默认皮肤）。
    ///   · 资源缺失时生成**确定性占位贴图**（由哈希/名字派生），并计数 + 限流日志；
    ///     绝不把默认皮肤当成"已应用"。
    ///
    /// 不写全局皮肤库、不写 Project.xml：资源字节只进内存；主机侧的世界作用域持久化由
    /// Modules/PlayerAvatarModule 写 SuMultiplayerPlayers.xml。
    /// </summary>
    public sealed class PlayerSkinStore : IPlayerSkinPort, ISkinMaterializer
    {
        private sealed class CachedAsset
        {
            public PlayerSkinAssetRecord Record;
            public SkinAssetOrigin Origin;
        }

        private sealed class AppliedSkin
        {
            public string SkinName = string.Empty;
            public string HashHex = string.Empty;
            public Texture2D Texture;
            public bool Placeholder;
            public string PlaceholderKey = string.Empty;
        }

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly SkinAssetCounters m_counters;
        private readonly Dictionary<string, CachedAsset> m_cache =
            new Dictionary<string, CachedAsset>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_hashBySkinName =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<PlayerData, AppliedSkin> m_applied =
            new Dictionary<PlayerData, AppliedSkin>();
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public PlayerSkinStore(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics, SkinAssetCounters counters, string ownerId = "player-skin")
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_counters = counters ?? throw new ArgumentNullException(nameof(counters));
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-skin" : ownerId;
        }

        // ------------------------------------------------------------ IPlayerSkinPort

        public bool TryReadLocalSkinAsset(string skinName, out byte[] data, out string reason)
        {
            data = null;
            reason = null;
            if (!SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                reason = "\"" + (skinName ?? string.Empty) + "\" 不是合法的自定义皮肤名";
                return false;
            }
            string fileName = CharacterSkinsManager.GetFileName(skinName);
            if (string.IsNullOrEmpty(fileName))
            {
                reason = "引擎算不出皮肤文件路径（GetFileName 返回空）";
                return false;
            }
            try
            {
                if (!Storage.FileExists(fileName))
                {
                    reason = "本机皮肤库里没有这个文件：" + fileName;
                    return false;
                }
                using Stream stream = Storage.OpenFile(fileName, OpenFileMode.Read);
                data = ReadBounded(stream, SkinAssetPolicy.MaxAssetBytes + 1);
            }
            catch (Exception e)
            {
                reason = "读取本机皮肤文件失败：" + e.GetType().Name + " " + e.Message + "（" + fileName + "）";
                return false;
            }
            if (data == null || data.Length == 0)
            {
                reason = "本机皮肤文件为空：" + fileName;
                return false;
            }
            if (data.Length > SkinAssetPolicy.MaxAssetBytes)
            {
                reason = "本机皮肤文件过大（" + data.Length + " > " + SkinAssetPolicy.MaxAssetBytes + " 字节）";
                return false;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(data, out string validateReason))
            {
                reason = "本机皮肤文件非法：" + validateReason;
                return false;
            }
            return true;
        }

        public bool TryDecodeAssetData(byte[] data, out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;
            if (data == null || data.Length == 0)
            {
                reason = "资产为空";
                return false;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(data, out string policyReason))
            {
                reason = policyReason;
                return false;
            }
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                Image image = Image.Load(stream);
                width = image.Width;
                height = image.Height;
                if (width <= 0 || height <= 0 || width > SkinAssetPolicy.MaxImageDimension ||
                    height > SkinAssetPolicy.MaxImageDimension)
                {
                    reason = "图片尺寸非法（" + width + "x" + height + "）";
                    return false;
                }
                if (!SkinAssetPolicy.IsPowerOfTwo(width) || !SkinAssetPolicy.IsPowerOfTwo(height))
                {
                    reason = "图片尺寸不是 2 的幂（" + width + "x" + height + "）";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                reason = "Image.Load 解码失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        public bool StoreAsset(PlayerSkinAssetRecord record, SkinAssetOrigin origin, out bool duplicate,
            out string reason)
        {
            duplicate = false;
            reason = null;
            if (record == null || !record.HasData)
            {
                reason = "没有资源字节（内置皮肤不走资源缓存）";
                return false;
            }
            if (!record.HasHash)
            {
                reason = "资源没有合法的 32 字节 SHA-256";
                return false;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(record.Data, out string validateReason))
            {
                reason = validateReason;
                return false;
            }
            byte[] actual = SkinAssetPolicy.Sha256(record.Data);
            if (!SkinAssetPolicy.BytesEqual(actual, record.Sha256))
            {
                reason = "资源哈希 " + SkinAssetPolicy.HashHex(actual) + " ≠ 声明 " +
                    SkinAssetPolicy.HashHex(record.Sha256);
                return false;
            }
            string hashKey = record.HashHex;
            if (m_cache.TryGetValue(hashKey, out CachedAsset existing))
            {
                if (!SkinAssetPolicy.BytesEqual(existing.Record.Data, record.Data))
                {
                    reason = "同一哈希对应不同字节（哈希冲突，拒绝覆盖）";
                    return false;
                }
                duplicate = true;
                m_hashBySkinName[record.SkinName] = hashKey;
                return true;
            }
            m_cache[hashKey] = new CachedAsset
            {
                Record = record.Clone(),
                Origin = origin
            };
            if (!string.IsNullOrEmpty(record.SkinName)) m_hashBySkinName[record.SkinName] = hashKey;
            ReapplyCached(record);
            return true;
        }

        public bool TryGetCachedAsset(byte[] sha256, string skinName, out PlayerSkinAssetRecord record)
        {
            record = null;
            if (SkinAssetPolicy.IsValidHash(sha256))
            {
                string hashKey = SkinAssetPolicy.HashHex(sha256);
                if (m_cache.TryGetValue(hashKey, out CachedAsset byHash))
                {
                    record = byHash.Record;
                    return true;
                }
            }
            if (!string.IsNullOrEmpty(skinName) &&
                m_hashBySkinName.TryGetValue(skinName, out string mapped) &&
                m_cache.TryGetValue(mapped, out CachedAsset byName))
            {
                record = byName.Record;
                return true;
            }
            return false;
        }

        public bool TryGetHashForSkin(string skinName, out byte[] sha256)
        {
            sha256 = Array.Empty<byte>();
            if (string.IsNullOrEmpty(skinName)) return false;
            if (!m_hashBySkinName.TryGetValue(skinName, out string hashKey)) return false;
            if (!m_cache.TryGetValue(hashKey, out CachedAsset cached)) return false;
            sha256 = cached.Record.Sha256 != null ? (byte[])cached.Record.Sha256.Clone() : Array.Empty<byte>();
            return sha256.Length > 0;
        }

        public bool TryApplyAvatarSkin(int clientId, PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason)
        {
            placeholder = false;
            reason = null;
            if (m_spawner == null || !m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData == null)
            {
                reason = "本机没有 clientId=" + clientId + " 的联机化身";
                return false;
            }
            return ApplyInternal(playerData, record, allowPlaceholder, out placeholder, out reason);
        }

        /// <summary>按玩家键 (clientId, slot) 应用皮肤（Phase 1 多本机角色）。</summary>
        public bool TryApplyAvatarSkin(PlayerKey key, PlayerSkinAssetRecord record,
            bool allowPlaceholder, out bool placeholder, out string reason)
        {
            placeholder = false;
            reason = null;
            if (m_spawner == null || !m_spawner.TryGet(key, out PlayerData playerData) ||
                playerData == null)
            {
                reason = "本机没有玩家键 " + key + " 的联机化身";
                return false;
            }
            return ApplyInternal(playerData, record, allowPlaceholder, out placeholder, out reason);
        }

        public bool TryApplyLocalSkin(PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason) =>
            TryApplyLocalSkin(0, record, allowPlaceholder, out placeholder, out reason);

        /// <summary>按本机槽位 0..3 应用皮肤（槽位不存在时明确失败，不回退到槽位 0）。</summary>
        public bool TryApplyLocalSkin(int slot, PlayerSkinAssetRecord record, bool allowPlaceholder,
            out bool placeholder, out string reason)
        {
            placeholder = false;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, slot, out PlayerData localPlayer, out reason))
                return false;
            return ApplyInternal(localPlayer, record, allowPlaceholder, out placeholder, out reason);
        }

        public void ForgetAvatar(int clientId)
        {
            if (m_spawner == null || !m_spawner.TryGet(clientId, out PlayerData playerData)) return;
            // 贴图归 ComponentClothing 所有：实体销毁时它会 Dispose；这里只丢映射。
            m_applied.Remove(playerData);
        }

        public void ForgetAll()
        {
            foreach (KeyValuePair<PlayerData, AppliedSkin> item in m_applied)
                DisposeTexture(item.Value.Texture);
            m_applied.Clear();
            // 只保留本机自己的资源（Owned）；主机记录 / 主机广播的副本随会话结束清掉。
            var remove = new List<string>();
            foreach (KeyValuePair<string, CachedAsset> item in m_cache)
                if (item.Value.Origin != SkinAssetOrigin.Owned) remove.Add(item.Key);
            for (int i = 0; i < remove.Count; i++) m_cache.Remove(remove[i]);
            var staleNames = new List<string>();
            foreach (KeyValuePair<string, string> item in m_hashBySkinName)
                if (!m_cache.ContainsKey(item.Value)) staleNames.Add(item.Key);
            for (int i = 0; i < staleNames.Count; i++) m_hashBySkinName.Remove(staleNames[i]);
        }

        public string Describe() =>
            "assets=" + m_cache.Count + " names=" + m_hashBySkinName.Count +
            " applied=" + m_applied.Count;

        // ------------------------------------------------------------ ISkinMaterializer

        public void Apply(PlayerData playerData, string skinName, byte[] sha256)
        {
            if (playerData == null) return;
            var record = new PlayerSkinAssetRecord
            {
                SkinName = skinName ?? string.Empty,
                Sex = NetworkAvatarSpawner.ToPlayerSex(playerData.PlayerClass),
                Sha256 = sha256 != null ? (byte[])sha256.Clone() : Array.Empty<byte>(),
                Data = Array.Empty<byte>()
            };
            if (ApplyInternal(playerData, record, allowPlaceholder: true, out bool placeholder,
                    out string reason))
            {
                if (placeholder) m_counters.OnPlaceholderShown();
                return;
            }
            LogRateLimited("apply:" + reason, "把皮肤 \"" + record.SkinName + "\" 应用到角色失败：" +
                reason + "（不会悄悄换成默认皮肤）", isError: true);
        }

        public void Forget(PlayerData playerData)
        {
            if (playerData == null) return;
            m_applied.Remove(playerData);
        }

        public bool TryGetSkinHash(string skinName, out byte[] sha256) =>
            TryGetHashForSkin(skinName, out sha256);

        // ------------------------------------------------------------ 内部

        private bool ApplyInternal(PlayerData playerData, PlayerSkinAssetRecord record,
            bool allowPlaceholder, out bool placeholder, out string reason)
        {
            placeholder = false;
            reason = null;
            if (playerData == null)
            {
                reason = "PlayerData 为空";
                return false;
            }
            ComponentPlayer componentPlayer = playerData.ComponentPlayer;
            ComponentClothing clothing = componentPlayer?.ComponentClothing;
            if (clothing == null)
            {
                reason = "角色还没有 ComponentClothing（实体未建好）";
                return false;
            }
            string skinName = record?.SkinName;
            if (string.IsNullOrWhiteSpace(skinName)) skinName = playerData.CharacterSkinName;
            if (string.IsNullOrWhiteSpace(skinName))
            {
                reason = "皮肤名为空";
                return false;
            }
            skinName = skinName.Trim();
            // 内置皮肤：交回引擎自己的加载路径；清掉本会话注入的贴图名字以触发重新加载。
            // 既不是内置、也不是合法自定义名 → 明确失败（绝不把非法名字交给引擎的"加载失败就换默认"路径）。
            bool builtIn = SkinAssetPolicy.IsBuiltInSkinName(skinName);
            if (!builtIn && !SkinAssetPolicy.IsCustomSkinName(skinName))
            {
                reason = "皮肤名既不是内置皮肤，也不是合法的 .scskin 自定义皮肤 \"" + skinName + "\"";
                return false;
            }
            if (builtIn)
            {
                if (m_applied.TryGetValue(playerData, out AppliedSkin oldBuiltIn))
                {
                    m_applied.Remove(playerData);
                    DisposeTexture(oldBuiltIn.Texture);
                    SetSkinFields(clothing, null, null);
                }
                playerData.CharacterSkinName = skinName;
                return true;
            }

            playerData.CharacterSkinName = skinName;
            PlayerSkinAssetRecord cached = record;
            if (cached == null || !cached.HasData)
            {
                TryGetCachedAsset(record?.Sha256, skinName, out cached);
            }
            string requestedHashHex = cached?.HashHex ?? SkinAssetPolicy.HashHex(record?.Sha256);
            bool requestedPlaceholder = cached == null || !cached.HasData;
            if (m_applied.TryGetValue(playerData, out AppliedSkin unchanged) &&
                unchanged.Texture != null &&
                string.Equals(unchanged.SkinName, skinName, StringComparison.Ordinal) &&
                string.Equals(unchanged.HashHex, requestedHashHex, StringComparison.Ordinal) &&
                unchanged.Placeholder == requestedPlaceholder)
            {
                // 已经是同一个结果（同一份资源 / 同一张占位）：不重复应用、也不重复计数。
                return true;
            }
            Texture2D texture;
            if (cached != null && cached.HasData)
            {
                if (!TryCreateTexture(cached.Data, out texture, out reason))
                {
                    // 资源在缓存里但无法解码：明确失败（这是资源损坏，不是"没有资源"）。
                    if (allowPlaceholder)
                    {
                        texture = CreatePlaceholderTexture(skinName, cached.Sha256);
                        placeholder = true;
                        LogRateLimited("decode-placeholder:" + cached.HashHex,
                            "皮肤 \"" + skinName + "\" 资源无法解码（" + reason +
                            "）→ 显示确定性占位（已计数）", isError: true);
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            else if (allowPlaceholder)
            {
                texture = CreatePlaceholderTexture(skinName, record?.Sha256);
                placeholder = true;
                LogRateLimited("missing-placeholder:" + (record?.Key ?? skinName),
                    "没有皮肤 \"" + skinName + "\" 的资源 → 显示确定性占位（已计数；" +
                    "资源到达后会自动替换，不静默换成默认皮肤）", isError: true);
            }
            else
            {
                reason = "没有皮肤 \"" + skinName + "\" 的资源（allowPlaceholder=false）";
                return false;
            }

            if (m_applied.TryGetValue(playerData, out AppliedSkin old))
            {
                if (!ReferenceEquals(old.Texture, texture)) DisposeTexture(old.Texture);
            }
            m_applied[playerData] = new AppliedSkin
            {
                SkinName = skinName,
                HashHex = requestedHashHex,
                Texture = texture,
                Placeholder = placeholder,
                PlaceholderKey = SkinAssetPolicy.HashHex(record?.Sha256) + "|" + skinName
            };
            SetSkinFields(clothing, texture, skinName);
            return true;
        }

        private void SetSkinFields(ComponentClothing clothing, Texture2D texture, string name)
        {
            m_parentField.ModifyParentField(clothing, "m_skinTexture", texture, typeof(ComponentClothing));
            m_parentField.ModifyParentField(clothing, "m_skinTextureName", name, typeof(ComponentClothing));
        }

        private bool TryCreateTexture(byte[] data, out Texture2D texture, out string reason)
        {
            texture = null;
            reason = null;
            if (!TryDecodeAssetData(data, out _, out _, out reason)) return false;
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                Image image = Image.Load(stream);
                texture = Texture2D.Load(image);
                return true;
            }
            catch (Exception e)
            {
                reason = "创建贴图失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 确定性占位贴图：同一哈希 / 名字永远得到同一张图（棋盘 + 斜纹），
        /// 让"资源缺失"在画面上可辨认且可复现，而不是随机皮肤或默认皮肤。
        /// </summary>
        private Texture2D CreatePlaceholderTexture(string skinName, byte[] sha256)
        {
            byte[] seed = SkinAssetPolicy.IsValidHash(sha256)
                ? sha256
                : SkinAssetPolicy.Sha256(System.Text.Encoding.UTF8.GetBytes(
                    "sump-placeholder:" + (skinName ?? string.Empty)));
            var image = new Image(64, 64);
            byte r1 = (byte)(48 + seed[0] % 128);
            byte g1 = (byte)(48 + seed[1] % 128);
            byte b1 = (byte)(48 + seed[2] % 128);
            byte r2 = (byte)(24 + seed[3] % 64);
            byte g2 = (byte)(24 + seed[4] % 64);
            byte b2 = (byte)(24 + seed[5] % 64);
            Color first = new Color(r1, g1, b1, (byte)255);
            Color second = new Color(r2, g2, b2, (byte)255);
            Color stripe = new Color((byte)235, (byte)235, (byte)235, (byte)255);
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    bool checker = (((x >> 3) + (y >> 3)) & 1) == 0;
                    bool diagonal = ((x + y) % 32) < 4;
                    image.SetPixel(x, y, diagonal ? stripe : (checker ? first : second));
                }
            }
            return Texture2D.Load(image);
        }

        private void ReapplyCached(PlayerSkinAssetRecord record)
        {
            if (record == null || !record.HasData) return;
            var entries = new List<KeyValuePair<PlayerData, AppliedSkin>>(m_applied);
            for (int i = 0; i < entries.Count; i++)
            {
                AppliedSkin applied = entries[i].Value;
                if (!applied.Placeholder) continue;
                bool matchesHash = string.Equals(applied.HashHex, record.HashHex, StringComparison.Ordinal);
                bool matchesName = string.Equals(applied.SkinName, record.SkinName, StringComparison.Ordinal);
                if (!matchesHash && !matchesName) continue;
                if (ApplyInternal(entries[i].Key, record, allowPlaceholder: false, out _,
                        out string applyReason))
                    m_counters.OnApplied();
                else
                    LogRateLimited("reapply:" + applyReason,
                        "资源到达后替换占位失败：" + applyReason, isError: true);
            }
            // 本机玩家的资料里已经是这个皮肤名、但此前没有应用记录（例如世界刚加载）。
            Project project = GameManager.Project;
            if (project == null) return;
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _)) return;
            if (!string.Equals(local.CharacterSkinName, record.SkinName, StringComparison.Ordinal)) return;
            if (ApplyInternal(local, record, allowPlaceholder: false, out _, out _))
                m_counters.OnApplied();
        }

        private static byte[] ReadBounded(Stream stream, int maxBytes)
        {
            using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
            var buffer = new byte[16 * 1024];
            int total = 0;
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                total += read;
                if (total > maxBytes)
                {
                    memory.Write(buffer, 0, read);
                    return memory.ToArray();
                }
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }

        private static void DisposeTexture(Texture2D texture)
        {
            if (texture == null) return;
            if (ContentManager.IsContent(texture)) return;
            texture.Dispose();
        }

        private void LogRateLimited(string key, string message, bool isError)
        {
            long now = Environment.TickCount64;
            if (m_lastLogMs.TryGetValue(key, out long last) &&
                now - last < SkinAssetPolicy.LogIntervalMs)
                return;
            m_lastLogMs[key] = now;
            if (isError) m_diagnostics?.Error(m_ownerId, message, null);
            else m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}
