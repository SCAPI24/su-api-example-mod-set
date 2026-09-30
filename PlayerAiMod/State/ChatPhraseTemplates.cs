using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂**聊天短语表**的安装：把内置默认表写成
    /// `&lt;实例根&gt;/PlayerAi/Chat/phrases.json`。
    ///
    /// 与问题库/摘要规格同一套纪律：**缺什么补什么、绝不覆盖**（人/LLM 加过话术的那份就是权威，
    /// Mod 升级不许把它顶掉 —— A36 的老坑）；内容由 <see cref="ChatPhrasesJson.Write"/> 生成，
    /// 所以"文件版 ≡ 内置版"是构造保证的，不靠手抄。
    /// </summary>
    public static class ChatPhraseTemplates
    {
        public const string DefaultFileName = ChatPhraseCatalog.DefaultFileName;

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
                ChatPhraseCatalog.FolderName);
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                error = "cannot create " + directory + ": " + exception.Message;
                return 0;
            }

            int count = 0;
            string writeError;
            count += WriteIfMissing(Path.Combine(directory, DefaultFileName),
                ChatPhrasesJson.Write(ChatPhrases.Default), installed, out writeError);
            if (writeError != null && error == null)
                error = writeError;

            // 物种别名表与短语表同目录、同纪律：缺了补一份，绝不覆盖（用户加过动物就听他的）
            writeError = null;
            count += WriteIfMissing(Path.Combine(directory, ChatAnimalAliases.FileName),
                ChatAnimalAliasesJson.Write(ChatAnimalAliases.Default), installed, out writeError);
            if (writeError != null && error == null)
                error = writeError;

            return count;
        }

        /// <summary>缺文件才写（原子：先写 `.tmp` 再改名，中途断电不会留下半份）。</summary>
        private static int WriteIfMissing(string path, string content, List<string> installed, out string error)
        {
            error = null;
            if (File.Exists(path))
                return 0;
            try
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, content);
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
