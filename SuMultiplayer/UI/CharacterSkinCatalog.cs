using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Engine.Media;
using Game;
using SuMultiplayer.Players;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 角色创建 / 中途换肤两屏共用的**皮肤目录**（只读本机皮肤库）：
    ///   · 内置皮肤：按引擎的 PlayerClass 过滤；
    ///   · 自定义皮肤：列出本机 `data:/CharacterSkins/*.scskin` 里**可读且校验通过**的；
    ///     读不到 / 非法的不列出（选择时也会再校验一次），绝不冒充成别的皮肤；
    ///   · 自定义皮肤资源的读取有硬上限，校验 PNG 头 + 尺寸 + 引擎解码。
    ///
    /// 只读：不 ImportCharacterSkin、不写 data:/CharacterSkins —— 新客户端不会被污染。
    /// Source: Survivalcraft/Game/CharacterSkinsManager.cs:175-193（内置皮肤集合）
    /// Source: Survivalcraft/Game/CharacterSkinsManager.cs:111-138（LoadTexture 的失败回退是引擎行为；
    ///         本屏在列出来源之前就校验，避免把回退结果当成真实皮肤）
    /// </summary>
    public static class CharacterSkinCatalog
    {
        /// <summary>该性别可选的皮肤名（内置排前，自定义排后）。非法的自定义皮肤被过滤并记日志。</summary>
        public static string[] NamesForClass(PlayerClass playerClass, bool includeCustom)
        {
            CharacterSkinsManager.UpdateCharacterSkinsList();
            var names = new List<string>();
            foreach (string name in CharacterSkinsManager.CharacterSkinsNames)
            {
                if (!CharacterSkinsManager.IsBuiltIn(name)) continue;
                if (CharacterSkinsManager.GetPlayerClass(name) != playerClass) continue;
                names.Add(name);
            }
            if (!includeCustom) return names.ToArray();
            foreach (string name in CharacterSkinsManager.CharacterSkinsNames)
            {
                if (CharacterSkinsManager.IsBuiltIn(name)) continue;
                if (!SkinAssetPolicy.IsCustomSkinName(name)) continue;
                PlayerClass? hinted = CharacterSkinsManager.GetPlayerClass(name);
                if (hinted.HasValue && hinted.Value != playerClass) continue;
                if (!TryReadCustomAsset(name, out byte[] data, out string reason))
                {
                    Log.Warning("[SuMP] 皮肤目录：跳过自定义皮肤 \"" + name + "\"：" + reason);
                    continue;
                }
                names.Add(name);
            }
            return names.ToArray();
        }

        /// <summary>
        /// 只读一份本机自定义皮肤资源，并做完整校验（大小 / PNG / 尺寸 / 引擎解码）。
        /// false + reason = 文件不存在 / 过大 / 非法；调用方必须明确提示，不换成别的皮肤。
        /// </summary>
        public static bool TryReadCustomAsset(string skinName, out byte[] data, out string reason)
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
                reason = "引擎算不出皮肤文件路径";
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
                reason = "读取皮肤文件失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (data == null || data.Length == 0)
            {
                reason = "皮肤文件为空";
                return false;
            }
            if (data.Length > SkinAssetPolicy.MaxAssetBytes)
            {
                reason = "皮肤文件过大（" + data.Length + " > " + SkinAssetPolicy.MaxAssetBytes + " 字节）";
                return false;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(data, out reason)) return false;
            try
            {
                using var memory = new MemoryStream(data, writable: false);
                Image image = Image.Load(memory);
                if (image.Width <= 0 || image.Height <= 0 ||
                    image.Width > SkinAssetPolicy.MaxImageDimension ||
                    image.Height > SkinAssetPolicy.MaxImageDimension ||
                    !SkinAssetPolicy.IsPowerOfTwo(image.Width) ||
                    !SkinAssetPolicy.IsPowerOfTwo(image.Height))
                {
                    reason = "图片尺寸非法（" + image.Width + "x" + image.Height + "）";
                    return false;
                }
            }
            catch (Exception e)
            {
                reason = "引擎无法解码皮肤图片：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return true;
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
                memory.Write(buffer, 0, read);
                if (total > maxBytes) break;
            }
            return memory.ToArray();
        }
    }
}
