using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂**摘要规格**的安装：把内置默认规格写成
    /// `&lt;实例根&gt;/PlayerAi/Digest/world.digest.json`。
    ///
    /// 两条纪律（与问题库/动作脚本模板一致）：
    ///   · **缺什么补什么、绝不覆盖** —— 人（或 LLM）改过的那份就是权威，
    ///     Mod 升级不许把它顶掉（A36 的老坑：改模板不等于改实例）；
    ///   · 文件内容**由 <see cref="DigestSpecJson.Write"/> 从内置规格生成**，
    ///     所以"文件版 ≡ 硬编码版"是构造保证的，不靠手抄两份 JSON。
    /// </summary>
    public static class DigestSpecTemplates
    {
        /// <summary>出厂文件名（= `<see cref="DigestSpec.DefaultId"/>.digest.json`）。</summary>
        public const string DefaultFileName = DigestSpec.DefaultId + DigestCatalog.Extension;

        public static int Install(string instanceRoot, out List<string> installed, out string error)
        {
            installed = new List<string>();
            error = null;
            if (string.IsNullOrEmpty(instanceRoot))
            {
                error = "no instance root";
                return 0;
            }

            string directory = Path.Combine(instanceRoot, DigestCatalog.PlayerAiFolderName,
                DigestCatalog.FolderName);
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                error = "cannot create " + directory + ": " + exception.Message;
                return 0;
            }

            string path = Path.Combine(directory, DefaultFileName);
            if (File.Exists(path))
                return 0;

            try
            {
                // 原子写（`.tmp` → 替换）：与包写入同一条纪律，绝不留下半截文件。
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, DigestSpecJson.Write(DigestSpec.Default));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporary, path);
                installed.Add(path);
                return 1;
            }
            catch (Exception exception)
            {
                error = "cannot write " + path + ": " + exception.Message;
                return 0;
            }
        }
    }
}
