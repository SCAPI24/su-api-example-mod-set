using SuAPI;
using System;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 把包写成 `.scbtpak` 字节（ZIP 改名，内部一律 `/` 分隔、UTF-8 无 BOM）。
    ///
    /// 用途只有两处，都**不是**运行期行为：
    ///   1. 出厂示例包随 Mod 安装（<see cref="PackageTemplates"/>）；
    ///   2. 编辑器/用户要求的导出（P0-13 `ai.tree.export`）。
    /// AI 在内存里做的改写**永远不走这里**（计划 §4.5：不落盘）。
    ///
    /// 写文件一律**原子写**（先写 `.tmp` 再替换），这样读者（包括正在跑的游戏）绝不会看到半截文件。
    /// </summary>
    public static class PackageWriter
    {
        public const string TemporarySuffix = ".tmp";

        /// <summary>打成 zip 字节。</summary>
        public static byte[] ToBytes(PackageValue manifest, PackageValue tree, string description = null)
        {
            if (manifest == null || tree == null)
                throw new ArgumentNullException(manifest == null ? "manifest" : "tree");

            using (var stream = new MemoryStream())
            {
                using (var archive = ZipArchive.Create(stream, keepStreamOpen: true))
                {
                    var utf8 = new UTF8Encoding(false);
                    AddBytes(archive, ScbtManifest.FileName, utf8.GetBytes(manifest.ToJson(true)));
                    AddBytes(archive, ScbtTree.FileName, utf8.GetBytes(tree.ToJson(true)));
                    if (!string.IsNullOrEmpty(description))
                        AddBytes(archive, "meta/description.md", utf8.GetBytes(description));
                }
                return stream.ToArray();
            }
        }

        private static void AddBytes(ZipArchive archive, string entryName, byte[] data)
        {
            using (var source = new MemoryStream(data))
            {
                archive.AddStream(entryName, source);
            }
        }

        /// <summary>原子写：`path.tmp` → 覆盖替换。失败时清掉临时文件。</summary>
        public static bool TryWriteFile(string path, byte[] bytes, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || bytes == null)
            {
                error = "path and bytes are required";
                return false;
            }

            string temporary = path + TemporarySuffix;
            try
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }

                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                TryDelete(temporary);
                return false;
            }
        }

        public static bool TryWritePackage(string path, PackageValue manifest, PackageValue tree,
            string description, out string error)
        {
            byte[] bytes;
            try
            {
                bytes = ToBytes(manifest, tree, description);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
            return TryWriteFile(path, bytes, out error);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // 临时文件清理失败不影响结果，下次原子写会覆盖它。
            }
        }
    }
}
