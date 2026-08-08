using System;
using System.IO;

namespace ScMultiplayer.Core
{
    // Source: ScMultiplayer.CaptureHostedWorldSnapshot
    // This class is deliberately game-independent. It copies immutable world files only; the
    // caller remains responsible for SaveProject, archive export and temporary-directory cleanup.
    internal static class WorldSnapshotFileCopier
    {
        public static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException("The hosted world directory does not exist.");
            Directory.CreateDirectory(targetDirectory);
            foreach (string sourcePath in Directory.EnumerateFiles(
                sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                string targetPath = Path.Combine(targetDirectory, relativePath);
                string targetParent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetParent))
                    Directory.CreateDirectory(targetParent);
                CopyFile(sourcePath, targetPath);
            }
        }

        private static void CopyFile(string sourcePath, string targetPath)
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.SequentialScan);
            using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            source.CopyTo(target, 64 * 1024);
        }
    }
}
