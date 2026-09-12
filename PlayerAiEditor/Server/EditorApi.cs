using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 编辑器的数据接口：**它调用的是游戏内那份校验器/编译器**（同一个 C# 源码），
    /// 于是"编辑器说能存"和"游戏说能跑"永远是同一个判断（计划 §5.2）。
    ///
    /// 所有写操作都限制在两个白名单目录内（实例可写 / Mod 只读），且**原子写**。
    /// </summary>
    internal sealed class EditorApi
    {
        private readonly PackageRoots m_roots;
        private readonly PackageLoadOptions m_options;
        private readonly GameBridgeClient m_game;

        public EditorApi(string instanceRoot)
        {
            InstanceRoot = instanceRoot;
            string instanceDirectory = System.IO.Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees");
            string modDirectory = System.IO.Path.Combine(instanceRoot, "Mods", "PlayerAiMod",
                "PlayerAi", "BehaviorTrees");
            m_roots = new PackageRoots(instanceDirectory, modDirectory);
            m_options = new PackageLoadOptions { Roots = m_roots };

            // 游戏不在跑也能用编辑器（只是"推送热重载"会失败并如实说明）
            m_game = new GameBridgeClient(instanceRoot);
        }

        public string InstanceRoot { get; }

        public PackageRoots Roots
        {
            get { return m_roots; }
        }

        public GameBridgeClient Game
        {
            get { return m_game; }
        }

        // ---------------------------------------------------------------- 元信息

        /// <summary>`GET /api/meta`：目录、游戏状态、节点 schema 摘要。</summary>
        public Dictionary<string, object> Meta()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["instanceRoot"] = InstanceRoot,
                ["folders"] = m_roots.Describe(),
                ["writableFolder"] = m_roots.InstanceRoot != null ? m_roots.InstanceRoot.Path : null,
                ["modFolder"] = m_roots.ModRoot != null ? m_roots.ModRoot.Path : null,
                ["format"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["scbt"] = ScbtManifest.FormatVersion,
                    ["scat"] = ScatManifest.FormatVersion
                }
            };

            Dictionary<string, object> gameInfo;
            string gameError;
            bool running = m_game.TryDescribe(out gameInfo, out gameError);
            info["gameRunning"] = running;
            info["gameInfo"] = gameInfo;
            info["gameError"] = gameError;
            return info;
        }

        /// <summary>`GET /api/schema`：节点/装饰器/服务的类型与属性表（编辑器物料区直接读它）。</summary>
        public Dictionary<string, object> Schema()
        {
            var nodes = new List<Dictionary<string, object>>();
            List<BtNodeInfo> nodeInfos = BtNodeRegistry.ListNodeInfos();
            for (int i = 0; i < nodeInfos.Count; i++)
            {
                BtNodeInfo info = nodeInfos[i];
                if (!info.PackageSerializable)
                    continue; // 只暴露"能写进包"的类型，避免编辑器造出装不上的树

                nodes.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = info.TypeId,
                    ["shape"] = info.Shape.ToString().ToLowerInvariant(),
                    ["allowsChildren"] = info.AllowsChildren,
                    ["allowsServices"] = info.AllowsServices,
                    ["properties"] = DescribeProperties(info.Properties)
                });
            }

            var decorators = new List<Dictionary<string, object>>();
            List<BtDecoratorInfo> decoratorInfos = BtNodeRegistry.ListDecoratorInfos();
            for (int i = 0; i < decoratorInfos.Count; i++)
            {
                BtDecoratorInfo info = decoratorInfos[i];
                if (!info.PackageSerializable)
                    continue;
                decorators.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = info.TypeId,
                    ["properties"] = DescribeProperties(info.Properties)
                });
            }

            var services = new List<Dictionary<string, object>>();
            List<BtServiceInfo> serviceInfos = BtNodeRegistry.ListServiceInfos();
            for (int i = 0; i < serviceInfos.Count; i++)
            {
                BtServiceInfo info = serviceInfos[i];
                if (!info.PackageSerializable)
                    continue;
                services.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = info.TypeId,
                    ["properties"] = DescribeProperties(info.Properties)
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["nodes"] = nodes,
                ["decorators"] = decorators,
                ["services"] = services,
                ["abortModes"] = new List<string>(BtSchema.AbortModes),
                ["blackboardTypes"] = new List<string>(BtSchema.BlackboardTypes),
                ["valueKinds"] = new List<string>(BtSchema.ValueKinds),
                ["queries"] = new List<string>(BtSchema.BlackboardQueries),
                ["operators"] = new List<string>(BtSchema.CompareOperators),
                ["actionPackageModes"] = new List<string>(BtSchema.ActionPackageModes)
            };
        }

        private static List<Dictionary<string, object>> DescribeProperties(
            IReadOnlyList<BtPropertySpec> specs)
        {
            var list = new List<Dictionary<string, object>>();
            if (specs == null)
                return list;

            for (int i = 0; i < specs.Count; i++)
            {
                BtPropertySpec spec = specs[i];
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = spec.Name,
                    ["kind"] = spec.Kind.ToString().ToLowerInvariant(),
                    ["required"] = spec.Required,
                    ["default"] = spec.DefaultValue,
                    ["description"] = spec.Description
                };
                if (spec.AllowedValues != null && spec.AllowedValues.Length > 0)
                    entry["allowed"] = new List<string>(spec.AllowedValues);
                list.Add(entry);
            }
            return list;
        }

        // ---------------------------------------------------------------- 包

        /// <summary>`GET /api/packages`：列出两个目录里的树包（实例目录优先）。</summary>
        public Dictionary<string, object> ListPackages()
        {
            var items = new List<Dictionary<string, object>>();
            List<string> files = m_roots.ListFiles();
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                PackageRoot owner = m_roots.OwnerOf(file);
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["file"] = System.IO.Path.GetFileName(file),
                    ["path"] = file,
                    ["source"] = owner != null ? owner.Kind : "unknown",
                    ["writable"] = owner != null && owner.Writable
                };
                try
                {
                    var info = new FileInfo(file);
                    entry["bytes"] = info.Length;
                    entry["modifiedUtc"] = info.LastWriteTimeUtc.ToString("o");
                }
                catch (Exception)
                {
                }

                // 顺手给出 id/入口/节点数：列表里就能看出是哪棵树
                ScbtPackageSet set = PackageLoader.Load(file, m_options);
                if (set.Root != null)
                {
                    entry["id"] = set.Root.Manifest != null ? set.Root.Manifest.Id : null;
                    entry["entry"] = set.Root.Manifest != null ? set.Root.Manifest.Entry : null;
                    entry["nodes"] = set.Root.Tree != null ? set.Root.Tree.NodeCount : 0;
                    entry["references"] = set.Root.Manifest != null ? set.Root.Manifest.References.Count : 0;
                }
                entry["errors"] = set.ErrorCount;
                entry["warnings"] = set.WarningCount;
                items.Add(entry);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["folders"] = m_roots.Describe(),
                ["count"] = items.Count,
                ["packages"] = items
            };
        }

        /// <summary>`GET /api/package?path=…`：读一个树包（原文 + 校验报告）。</summary>
        public Dictionary<string, object> ReadPackage(string nameOrPath)
        {
            string path = Resolve(nameOrPath, out string resolveError);
            if (path == null)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = false,
                    ["reason"] = resolveError
                };
            }

            ScbtPackageSet set = PackageLoader.Load(path, m_options);
            var issues = new List<string>();
            issues.AddRange(set.Summarize(64));

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = set.Root != null && !set.HasErrors,
                ["path"] = path,
                ["file"] = System.IO.Path.GetFileName(path),
                ["errors"] = set.ErrorCount,
                ["warnings"] = set.WarningCount,
                ["issues"] = issues,
                ["packages"] = DescribeLoaded(set)
            };

            if (set.Root != null)
            {
                PackageRoot owner = m_roots.OwnerOf(path);
                result["writable"] = owner != null && owner.Writable;
                result["manifest"] = set.Root.Manifest != null
                    ? set.Root.Manifest.ToValue()
                    : PackageValue.Object();
                result["tree"] = set.Root.Tree != null ? set.Root.Tree.ToValue() : PackageValue.Object();
                result["hash"] = set.Root.Hash;
            }
            else
            {
                result["writable"] = false;
            }
            return result;
        }

        private static List<Dictionary<string, object>> DescribeLoaded(ScbtPackageSet set)
        {
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < set.Packages.Count; i++)
            {
                LoadedPackage package = set.Packages[i];
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["file"] = package.FileName,
                    ["id"] = package.PackageId,
                    ["hash"] = package.Hash,
                    ["nodes"] = package.Tree != null ? package.Tree.NodeCount : 0,
                    ["errors"] = package.Report.ErrorCount,
                    ["warnings"] = package.Report.WarningCount,
                    ["milliseconds"] = Math.Round(package.LoadMilliseconds, 2)
                });
            }
            return list;
        }

        /// <summary>
        /// `POST /api/package?path=…`：校验并保存（原子写）。
        /// **只允许写实例目录**（Mod 分发目录只读），且保存前先跑一遍与游戏同源的校验器。
        /// </summary>
        public Dictionary<string, object> SavePackage(string nameOrPath, string manifestJson,
            string treeJson, bool overwrite)
        {
            string path = Resolve(nameOrPath, out string resolveError);
            if (path == null)
            {
                return Error("resolve", resolveError);
            }

            PackageRoot owner = m_roots.OwnerOf(path);
            if (owner == null || !owner.Writable)
            {
                return Error("readonly", "该包在只读目录（Mods/PlayerAiMod/…）。"
                    + "想改就把它复制到实例目录：" + (m_roots.InstanceRoot != null
                        ? m_roots.InstanceRoot.Path : "<no instance folder>"));
            }

            if (File.Exists(path) && !overwrite)
                return Error("exists", "文件已存在（需要 overwrite=true 才覆盖）");

            var report = new PackageReport();
            PackageValue manifestValue;
            if (!PackageJson.TryParse(manifestJson, "manifest.json", report, out manifestValue))
                return Report(report, "manifest.json 不是合法 JSON");

            PackageValue treeValue;
            if (!PackageJson.TryParse(treeJson, "tree.json", report, out treeValue))
                return Report(report, "tree.json 不是合法 JSON");

            ScbtManifest manifest = ScbtManifest.Parse(manifestValue, "manifest.json", report);
            if (manifest == null)
                return Report(report, "manifest.json 读不出来");

            ScbtTree tree = ScbtTree.Parse(treeValue, "tree.json", report);
            if (tree == null)
                return Report(report, "tree.json 读不出来");

            // 与游戏**同一份**校验器
            manifest.Validate("manifest.json", report);
            PackageValidator.ValidateTree(tree, manifest, "tree.json", report);
            if (report.HasErrors)
                return Report(report, "校验未通过（游戏也会拒绝装载）");

            // 引用的包必须能被解析出来（否则存下去游戏会装不上）
            ScbtPackageSet validation = ValidateBytes(manifestValue, treeValue);
            if (validation != null && validation.HasErrors)
            {
                var issues = new List<string>(validation.Summarize(32));
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = false,
                    ["reason"] = "引用的包解析失败（references 指向的 .scbtpak 找不到或有问题）",
                    ["issues"] = issues
                };
            }

            string error;
            byte[] bytes = PackageWriter.ToBytes(manifestValue, treeValue);
            if (!PackageWriter.TryWriteFile(path, bytes, out error))
                return Error("io", "写入失败：" + error);

            string hash = PackageLoader.ComputeHash(bytes);
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["path"] = path,
                ["bytes"] = bytes.Length,
                ["hash"] = hash,
                ["writable"] = true,
                ["validation"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["errors"] = report.ErrorCount,
                    ["warnings"] = report.WarningCount,
                    ["issues"] = report.Summarize(32)
                }
            };

            // 内存里再装载一次，确认"写出来的东西游戏真能读"
            ScbtPackageSet reloaded = PackageLoader.Load(path, m_options);
            result["reloaded"] = reloaded.Root != null && !reloaded.HasErrors;
            if (!reloaded.HasErrors)
                result["nodes"] = reloaded.Root.Tree != null ? reloaded.Root.Tree.NodeCount : 0;
            return result;
        }

        /// <summary>`POST /api/validate`：只校验不写盘（保存前预检）。</summary>
        public Dictionary<string, object> ValidatePackage(string nameOrPath, string manifestJson,
            string treeJson)
        {
            var report = new PackageReport();

            if (!string.IsNullOrEmpty(manifestJson) && !string.IsNullOrEmpty(treeJson))
            {
                PackageValue manifestValue;
                PackageValue treeValue;
                if (!PackageJson.TryParse(manifestJson, "manifest.json", report, out manifestValue)
                    || !PackageJson.TryParse(treeJson, "tree.json", report, out treeValue))
                {
                    return Report(report, "JSON 解析失败");
                }

                ScbtManifest manifest = ScbtManifest.Parse(manifestValue, "manifest.json", report);
                ScbtTree tree = ScbtTree.Parse(treeValue, "tree.json", report);
                if (manifest != null && tree != null)
                {
                    manifest.Validate("manifest.json", report);
                    PackageValidator.ValidateTree(tree, manifest, "tree.json", report);
                }
            }
            else
            {
                string path = Resolve(nameOrPath, out string resolveError);
                if (path == null)
                    return Error("resolve", resolveError);
                ScbtPackageSet set = PackageLoader.Load(path, m_options);
                report.AddRange(set.Report);
                for (int i = 0; i < set.Packages.Count; i++)
                    report.AddRange(set.Packages[i].Report);
                if (set.Root != null)
                    TreeCompiler.Compile(set, set.Root, null, report);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = !report.HasErrors,
                ["errors"] = report.ErrorCount,
                ["warnings"] = report.WarningCount,
                ["issues"] = report.Summarize(64)
            };
        }

        /// <summary>`POST /api/notify`：让游戏热重载刚保存的包（编辑器保存后的最后一跳）。</summary>
        public Dictionary<string, object> NotifyGame(string pathOrName)
        {
            string path = Resolve(pathOrName, out string resolveError);
            if (path == null)
                return Error("resolve", resolveError);

            string hash;
            try
            {
                hash = PackageLoader.ComputeHash(File.ReadAllBytes(path));
            }
            catch (Exception exception)
            {
                return Error("io", "读不出文件：" + exception.Message);
            }

            try
            {
                Dictionary<string, object> result = m_game.NotifyTree(path, "sha256:" + hash);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["path"] = path,
                    ["hash"] = hash,
                    ["game"] = result
                };
            }
            catch (Exception exception)
            {
                return GameError("推送热重载", exception);
            }
        }

        // ---------------------------------------------------------------- 动作包（P1）

        /// <summary>动作包的查找链：实例包目录在前，Mod 只读分发目录在后（与游戏内同一套约定）。</summary>
        public List<PackageRoot> ActionRoots()
        {
            var roots = new List<PackageRoot>();
            if (m_roots.InstanceRoot != null)
                roots.Add(m_roots.InstanceRoot);
            if (m_roots.ModRoot != null)
                roots.Add(m_roots.ModRoot);
            return roots;
        }

        /// <summary>
        /// `GET /api/actions`：列出两个目录里的 `.scatpak`（时长/帧数/**能否回放**）。
        /// 编辑器把它们当**物料**：拖到画布上就是 `Task.PlayActionPackage` 节点。
        /// </summary>
        public Dictionary<string, object> ListActions()
        {
            var items = new List<Dictionary<string, object>>();
            var folders = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<PackageRoot> roots = ActionRoots();
            int replayable = 0;
            int shadowed = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                PackageRoot root = roots[i];
                folders.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["kind"] = root.Kind,
                    ["path"] = root.Path,
                    ["writable"] = root.Writable
                });

                List<Dictionary<string, object>> found = ScatLibrary.Describe(root.Path);
                for (int j = 0; j < found.Count; j++)
                {
                    Dictionary<string, object> entry = found[j];
                    entry["source"] = root.Kind;
                    entry["writable"] = root.Writable;
                    entry["folder"] = root.Path;

                    string file = entry["file"] as string;
                    if (file != null && !seen.Add(file))
                    {
                        entry["shadowed"] = true; // 被实例目录里的同名包遮住
                        shadowed++;
                        items.Add(entry);
                        continue;
                    }
                    if (Equals(entry["replayable"], true))
                        replayable++;
                    items.Add(entry);
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["folders"] = folders,
                ["count"] = items.Count,
                ["replayable"] = replayable,
                ["shadowed"] = shadowed,
                ["actions"] = items
            };
        }

        /// <summary>`POST /api/action/validate`：校验一个动作包（结构 + 能不能回放）。</summary>
        public Dictionary<string, object> ValidateAction(string nameOrPath)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);

            Dictionary<string, object> result = ScatLibrary.Validate(folder, path);
            result["folder"] = folder;
            result["source"] = folder != null && m_roots.InstanceRoot != null
                && string.Equals(folder, m_roots.InstanceRoot.Path, StringComparison.OrdinalIgnoreCase)
                ? "instance" : "mod";
            return result;
        }

        /// <summary>`POST /api/action/play`：让游戏直接回放（不经行为树）——给人测试包用。</summary>
        public Dictionary<string, object> PlayAction(string nameOrPath, int repeat)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);

            try
            {
                // 传绝对路径：编辑器与游戏可能不是同一个实例根，名字解析交给编辑器这边
                Dictionary<string, object> result = m_game.PlayAction(path, repeat <= 0 ? 1 : repeat);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["path"] = path,
                    ["folder"] = folder,
                    ["repeat"] = repeat <= 0 ? 1 : repeat,
                    ["game"] = result
                };
            }
            catch (Exception exception)
            {
                return GameError("回放动作包", exception);
            }
        }

        /// <summary>`POST /api/action/stop`：停止回放并释放输入。</summary>
        public Dictionary<string, object> StopAction()
        {
            try
            {
                Dictionary<string, object> result = m_game.StopAction();
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["game"] = result
                };
            }
            catch (Exception exception)
            {
                return GameError("停止回放", exception);
            }
        }

        /// <summary>
        /// 控制通道出错时如实分类：**没连上**（游戏没跑/通道没开）和
        /// **连上了但被拒绝**（例如游戏里那份 Mod 还没有这个命令 —— 需要重新部署并重启）
        /// 是两件事，别混成一句"游戏没在跑"。
        /// </summary>
        private static Dictionary<string, object> GameError(string what, Exception exception)
        {
            string message = exception.Message ?? string.Empty;
            if (message.StartsWith("game refused the command", StringComparison.OrdinalIgnoreCase))
            {
                return Error("game_refused", "游戏在跑，但拒绝了「" + what + "」：" + message
                    + " —— 多半是游戏里那份 Mod 还没有这个命令（重新部署 Mod 并重启游戏后可解）。");
            }
            return Error("game_unreachable", "游戏没在跑（或控制通道没开）：" + message);
        }

        /// <summary>
        /// `POST /api/action/create`：写一个**确定内容的示例动作包**到实例目录（自造动作包，
        /// 不依赖真机录制）。已存在时拒绝覆盖（除非 overwrite=true）。
        /// </summary>
        public Dictionary<string, object> CreateSampleAction(string name, bool overwrite)
        {
            PackageRoot instance = m_roots.InstanceRoot;
            if (instance == null)
                return Error("readonly", "没有可写的实例包目录");

            string id = Sanitize(name);
            if (string.IsNullOrEmpty(id))
                id = PlayerAiPackages.SampleActionName;

            string path = System.IO.Path.Combine(instance.Path, id + PackageRoots.ActionExtension);
            if (File.Exists(path) && !overwrite)
                return Error("exists", "动作包已存在（需要 overwrite=true 才覆盖）：" + path);

            byte[] bytes = PlayerAiPackages.BuildSampleActionPackage(id);
            string writeError;
            if (!PackageWriter.TryWriteFile(path, bytes, out writeError))
                return Error("io", "写入失败：" + writeError);

            Dictionary<string, object> result = ScatLibrary.Validate(instance.Path, path);
            result["created"] = true;
            result["bytes"] = bytes.Length;
            result["source"] = "instance";
            result["writable"] = true;
            result["folder"] = instance.Path;
            return result;
        }

        /// <summary>名字清洗：只留文件名安全字符（防止路径穿越写出去）。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string trimmed = System.IO.Path.GetFileName(name.Trim());
            if (trimmed.EndsWith(PackageRoots.ActionExtension, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - PackageRoots.ActionExtension.Length);

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    builder.Append(c);
            }
            return builder.ToString();
        }

        private string ResolveAction(string nameOrPath, out string folder, out string error)
        {
            folder = null;
            error = null;
            if (string.IsNullOrEmpty(nameOrPath))
            {
                error = "缺少 path/name 参数";
                return null;
            }

            var directories = new List<string>();
            List<PackageRoot> roots = ActionRoots();
            for (int i = 0; i < roots.Count; i++)
                directories.Add(roots[i].Path);

            string path = ScatLibrary.ResolveIn(directories, nameOrPath, out folder);
            if (path == null)
            {
                error = "找不到动作包：" + nameOrPath + "（目录：" + string.Join("；", directories.ToArray())
                    + "）";
                return null;
            }
            if (!File.Exists(path))
            {
                error = "动作包不存在：" + path;
                return null;
            }
            return path;
        }

        // ---------------------------------------------------------------- 工具

        private ScbtPackageSet ValidateBytes(PackageValue manifest, PackageValue tree)
        {
            try
            {
                var memory = new MemoryPackageSource();
                string probe = System.IO.Path.Combine(m_roots.InstanceRoot != null
                    ? m_roots.InstanceRoot.Path : System.IO.Path.GetTempPath(),
                    "__validate__.scbtpak");
                memory.Add(probe, PackageWriter.ToBytes(manifest, tree));

                var options = new PackageLoadOptions
                {
                    Source = new OverlaySource(memory, m_options.Source),
                    Roots = m_roots
                };
                return PackageLoader.Load(probe, options);
            }
            catch (Exception)
            {
                return null; // 探针失败不影响保存（真正的问题会在游戏侧暴露）
            }
        }

        /// <summary>内存优先、磁盘兜底的数据源（保存前的引用解析探针用）。</summary>
        private sealed class OverlaySource : IPackageSource
        {
            private readonly MemoryPackageSource m_primary;
            private readonly IPackageSource m_fallback;

            public OverlaySource(MemoryPackageSource primary, IPackageSource fallback)
            {
                m_primary = primary;
                m_fallback = fallback;
            }

            public bool Exists(string path)
            {
                return m_primary.Exists(path) || (m_fallback != null && m_fallback.Exists(path));
            }

            public bool TryReadAllBytes(string path, out byte[] bytes, out string error)
            {
                if (m_primary.TryReadAllBytes(path, out bytes, out error))
                    return true;
                if (m_fallback != null)
                    return m_fallback.TryReadAllBytes(path, out bytes, out error);
                return false;
            }

            public bool TryGetLastWriteTimeUtc(string path, out DateTime utc)
            {
                if (m_primary.TryGetLastWriteTimeUtc(path, out utc))
                    return true;
                if (m_fallback != null)
                    return m_fallback.TryGetLastWriteTimeUtc(path, out utc);
                utc = default(DateTime);
                return false;
            }
        }

        private string Resolve(string nameOrPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(nameOrPath))
            {
                error = "缺少 path/name 参数";
                return null;
            }

            string path = m_roots.Resolve(nameOrPath, m_options.Source.Exists);
            if (path == null)
            {
                error = "找不到包：" + nameOrPath + "（目录：" + m_roots.Describe() + "）";
                return null;
            }
            return path;
        }

        private static Dictionary<string, object> Error(string code, string message)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = false,
                ["code"] = code,
                ["reason"] = message
            };
        }

        private static Dictionary<string, object> Report(PackageReport report, string reason)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = false,
                ["reason"] = reason,
                ["errors"] = report.ErrorCount,
                ["warnings"] = report.WarningCount,
                ["issues"] = report.Summarize(32)
            };
        }
    }
}
