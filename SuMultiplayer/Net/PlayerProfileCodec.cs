using System;
using System.Linq;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 联机资料（显示名 / 性别 / 皮肤 / 衣服）的**唯一编解码**：纯值 ↔ protobuf。
    ///
    /// Source: Mod/ScMultiplayer/Message/PlayerProfileMessage.cs:PlayerProfileMessage.Read/Write
    ///         （ClientId / Name / PlayerClass / SkinName / Clothes / SkinSha256 逐字段对应）
    ///
    /// 放在 Net/ 而不是模块里：握手（Terrain）与资料广播（联机角色模块）用的是同一份转换，
    /// 不允许两边各写一套导致"握手里的皮肤"和"广播里的皮肤"不一致。
    /// </summary>
    public static class PlayerProfileCodec
    {
        /// <summary>引擎 ClothingSlot 数量；协议里多出来的槽位直接丢弃（不越界写）。</summary>
        public const int ClothingSlotCount = PlayerAppearance.ClothingSlotCount;

        public static Proto.PlayerAppearance ToAppearanceMessage(PlayerAppearance appearance)
        {
            var message = new Proto.PlayerAppearance
            {
                Sex = (uint)(appearance?.Sex ?? PlayerSex.Male),
                SkinName = appearance?.SkinName ?? string.Empty,
                SkinSha256 = Google.Protobuf.ByteString.CopyFrom(
                    appearance?.SkinSha256 ?? Array.Empty<byte>())
            };
            int[][] clothes = appearance?.Clothes ?? PlayerAppearance.CreateEmptyClothes();
            for (int slot = 0; slot < ClothingSlotCount; slot++)
            {
                var clothing = new Proto.ClothingSlot();
                int[] values = slot < clothes.Length ? clothes[slot] : null;
                if (values != null) clothing.Values.Add(values);
                message.Clothes.Add(clothing);
            }
            return message;
        }

        public static PlayerAppearance ToAppearance(Proto.PlayerAppearance message)
        {
            var appearance = new PlayerAppearance();
            if (message == null) return appearance;
            appearance.Sex = message.Sex == (uint)PlayerSex.Female
                ? PlayerSex.Female
                : PlayerSex.Male;
            appearance.SkinName = message.SkinName ?? string.Empty;
            appearance.SkinSha256 = message.SkinSha256?.Length > 0
                ? message.SkinSha256.ToByteArray()
                : Array.Empty<byte>();
            for (int slot = 0; slot < ClothingSlotCount; slot++)
            {
                if (slot >= message.Clothes.Count) continue;
                Proto.ClothingSlot clothing = message.Clothes[slot];
                if (clothing == null || clothing.Values.Count == 0) continue;
                appearance.Clothes[slot] = clothing.Values.ToArray();
            }
            return appearance;
        }

        public static Proto.PlayerProfile ToMessage(int clientId, string identity,
            PlayerProfileSnapshot profile, bool isHost, bool hasCharacter)
        {
            var message = new Proto.PlayerProfile
            {
                ClientId = clientId,
                Identity = identity ?? string.Empty,
                Name = profile?.Name ?? string.Empty,
                IsHost = isHost,
                HasCharacter = hasCharacter
            };
            if (profile?.Appearance != null)
                message.Appearance = ToAppearanceMessage(profile.Appearance);
            return message;
        }

        /// <summary>解析一条资料消息；消息为空时返回 null（调用方必须明确处理"没有资料"）。</summary>
        public static PlayerProfileSnapshot FromMessage(Proto.PlayerProfile message,
            out int clientId, out string identity, out bool isHost, out bool hasCharacter)
        {
            clientId = message?.ClientId ?? -1;
            identity = (message?.Identity ?? string.Empty).Trim();
            isHost = message?.IsHost ?? false;
            hasCharacter = message?.HasCharacter ?? false;
            if (message == null) return null;
            return new PlayerProfileSnapshot
            {
                Name = (message.Name ?? string.Empty).Trim(),
                Appearance = ToAppearance(message.Appearance)
            };
        }

        /// <summary>协议里是否真的带了资料（旧构建 / 首次加入 = 全是空字段）。</summary>
        public static bool HasProfile(Proto.PlayerProfile message)
        {
            if (message == null) return false;
            return !string.IsNullOrWhiteSpace(message.Name) ||
                (message.Appearance != null &&
                 (!string.IsNullOrWhiteSpace(message.Appearance.SkinName) ||
                  message.Appearance.Sex != 0 ||
                  message.Appearance.Clothes.Count > 0));
        }
    }
}
