using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作包库（P1）：在实例包目录里找/列/校验 `.scatpak`。
    ///
    /// 与树包同一套目录约定（计划 §4.4）：动作包与树包**同路径**，
    /// 也就是 `<实例根>/PlayerAi/BehaviorTrees/*.scatpak`。
    /// </summary>
    public static class ScatLibrary
    {
        /// <summary>列目录里的动作包（按文件名排序；只读，不改文件）。</summary>
        public static List<string> List(string directory)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return result;

            try
            {
                string[] files = Directory.GetFiles(directory, "*" + ScatManifest.Extension,
                    SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                    result.Add(files[i]);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][act] cannot list " + directory + ": "
                    + exception.Message);
            }
            return result;
        }

        /// <summary>把名字/文件名/绝对路径解析成实际文件路径（实例目录优先）。</summary>
        public static string Resolve(string directory, string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return null;

            if (Path.IsPathRooted(nameOrPath))
                return File.Exists(nameOrPath) ? nameOrPath : null;

            string fileName = nameOrPath;
            if (!fileName.EndsWith(ScatManifest.Extension, StringComparison.OrdinalIgnoreCase))
                fileName += ScatManifest.Extension;

            if (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// 在**一串目录**里依次查找（前面的优先）：实例包目录在前，Mod 只读分发目录在后。
        /// 与树包的引用解析同一套约定（计划 §4.4）——出厂示例 `sample_walk.scatpak` 装在
        /// Mod 目录里，也必须能被直接播放，否则"开箱可跑"就是假的。
        /// </summary>
        public static string ResolveIn(List<string> directories, string nameOrPath,
            out string foundDirectory)
        {
            foundDirectory = null;
            if (string.IsNullOrEmpty(nameOrPath))
                return null;

            if (Path.IsPathRooted(nameOrPath))
            {
                if (!File.Exists(nameOrPath))
                    return null;
                foundDirectory = Path.GetDirectoryName(nameOrPath);
                return nameOrPath;
            }

            for (int i = 0; directories != null && i < directories.Count; i++)
            {
                string candidate = Resolve(directories[i], nameOrPath);
                if (candidate == null)
                    continue;
                foundDirectory = directories[i];
                return candidate;
            }
            return null;
        }

        /// <summary>列 + 轻量校验（每个包一行摘要：时长/帧数/能否回放）。</summary>
        public static List<Dictionary<string, object>> Describe(string directory)
        {
            var result = new List<Dictionary<string, object>>();
            List<string> files = List(directory);
            for (int i = 0; i < files.Count; i++)
            {
                ScatActionPackage package;
                PackageReport report;
                ScatValidator.TryLoad(files[i], out package, out report);

                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["file"] = Path.GetFileName(files[i]),
                    ["path"] = files[i],
                    ["bytes"] = new FileInfo(files[i]).Length
                };
                if (package != null)
                {
                    entry["id"] = package.Manifest != null ? package.Manifest.Id : null;
                    entry["duration"] = Math.Round(package.Duration, 3);
                    entry["frames"] = package.Frames;
                    entry["replayable"] = package.CanReplay;
                    entry["keys"] = package.UsedKeyNames();
                }
                entry["errors"] = report.ErrorCount;
                entry["warnings"] = report.WarningCount;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>完整校验（`ai.action.validate`）：返回摘要 + 逐条问题。</summary>
        public static Dictionary<string, object> Validate(string directory, string nameOrPath)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            string path = Resolve(directory, nameOrPath);
            if (path == null)
            {
                result["ok"] = false;
                result["reason"] = "action package not found";
                result["directory"] = directory;
                result["issues"] = new List<string> { "looked for '" + nameOrPath + "' in " + directory };
                return result;
            }

            ScatActionPackage package;
            PackageReport report;
            bool ok = ScatValidator.TryLoad(path, out package, out report);

            result["path"] = path;
            result["file"] = Path.GetFileName(path);
            result["ok"] = ok;
            result["errors"] = report.ErrorCount;
            result["warnings"] = report.WarningCount;
            result["issues"] = report.Summarize(32);
            if (package != null)
            {
                result["replayable"] = package.CanReplay;
                result["id"] = package.Manifest != null ? package.Manifest.Id : null;
                result["duration"] = Math.Round(package.Duration, 3);
                result["frames"] = package.Frames;
                result["keys"] = package.UsedKeyNames();
                result["hash"] = package.Hash;
                result["summary"] = package.Describe();
                if (package.Manifest != null && package.Manifest.Start != null)
                {
                    result["world"] = package.Manifest.Start.WorldName;
                    result["start"] = new List<object>
                    {
                        Math.Round(package.Manifest.Start.X, 2),
                        Math.Round(package.Manifest.Start.Y, 2),
                        Math.Round(package.Manifest.Start.Z, 2)
                    };
                }
                result["note"] = package.Manifest != null ? package.Manifest.Note : null;
            }
            return result;
        }
    }

    /// <summary>动作包播放控制（命令层只认这个接口；真实实现由 PlayerAiRuntime 提供）。</summary>
    public interface IAiActionPlayer
    {
        /// <summary>动作包的**主目录**（录制/可写目录；状态展示用它）。</summary>
        string ActionDirectory { get; }

        /// <summary>
        /// 查找动作包的目录链（主目录在前，只读分发目录在后；前面的优先）。
        /// 至少要含主目录；实现可以返回空列表，此时命令层退回 <see cref="ActionDirectory"/>。
        /// </summary>
        List<string> ActionSearchDirectories { get; }

        /// <summary>直接播放一个动作包（不经行为树；用于人测试包）。</summary>
        string Play(string nameOrPath, int repeat);

        /// <summary>停止播放并释放输入。</summary>
        string Stop();

        /// <summary>播放状态。</summary>
        Dictionary<string, object> Status();
    }
}
