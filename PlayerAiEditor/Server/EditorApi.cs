using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 编辑器的数据接口：**它调用的是游戏内那份校验器/编译器**（同一个 C# 源码），
    /// 于是"编辑器说能存"和"游戏说能跑"永远是同一个判断（计划 §5.2）。
    ///
    /// 所有写操作都限制在包目录（<c>&lt;实例根&gt;/PlayerAi/BehaviorTrees</c>）内，且**原子写**。
    /// </summary>
    internal sealed class EditorApi
    {
        private readonly PackageRoots m_roots;
        private readonly PackageLoadOptions m_options;
        private readonly GameBridgeClient m_game;

        public EditorApi(string instanceRoot)
        {
            InstanceRoot = instanceRoot;
            // 唯一的包目录：<实例根>/PlayerAi/BehaviorTrees
            // （以前还有 Mods/PlayerAiMod/PlayerAi/BehaviorTrees 作为第二来源，2026-09-12 按用户要求去掉）
            string packageDirectory = PackageRoots.DirectoryFor(instanceRoot);
            m_roots = new PackageRoots(packageDirectory);
            // G4：保存前/装载时就要能核对问题库引用（库在不在、问题 id、答案类型、分支选项 key）。
            // 库目录与游戏侧同源（`QuestionBankDirectorySource.DirectoriesFor`），
            // 且**按文件戳失效** —— 用户刚改完 .qbank 就要看到新结论，不能吃旧缓存。
            m_options = new PackageLoadOptions
            {
                Roots = m_roots,
                Banks = new QuestionBankDirectorySource(QuestionBankDirectorySource.DirectoriesFor(instanceRoot))
            };

            // 出厂示例：编辑器**自己也要补一遍**（缺什么补什么、绝不覆盖已有文件）。
            //
            // 为什么游戏侧补过还不够：用户完全可能"先开编辑器、后开游戏"（甚至没开过游戏），
            // 那时包目录是空的 —— 界面上就是"一个包都没有"，看着像编辑器坏了；
            // 而出厂示例（demo.greet / su.watch / 公共子树 …）正是他要照着改的样板。
            // 写入失败只记一句，绝不让编辑器起不来（比如目录只读）。
            try
            {
                List<string> installed;
                string error;
                int count = PackageTemplates.Install(m_roots, out installed, out error);
                TemplateInstallCount = count;
                TemplateInstallError = error;
            }
            catch (Exception exception)
            {
                TemplateInstallError = exception.Message;
            }

            // 游戏不在跑也能用编辑器（只是"推送热重载"会失败并如实说明）
            m_game = new GameBridgeClient(instanceRoot);
        }

        public string InstanceRoot { get; }

        /// <summary>启动时补装了几个出厂文件（0 = 都已经在；失败时见 <see cref="TemplateInstallError"/>）。</summary>
        public int TemplateInstallCount { get; private set; }

        public string TemplateInstallError { get; private set; }

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
                ["format"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["scbt"] = ScbtManifest.FormatVersion,
                    ["scat"] = ScatManifest.FormatVersion
                },
                // 启动时补装出厂示例的结果（0 = 都在；>0 = 这次补了几个；错误见 error）
                ["templateInstall"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["installed"] = TemplateInstallCount,
                    ["error"] = TemplateInstallError
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

        /// <summary>`GET /api/packages`：列出包目录里的树包。</summary>
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
                // 前端用它区分"实例目录"和"Mod 分发目录"，后者保存后会提示可能被 Mod 更新覆盖
                result["root"] = owner != null ? owner.Kind : null;
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
        /// 包目录 <c>&lt;实例根&gt;/PlayerAi/BehaviorTrees/</c> 对编辑器**可写**：
        /// 用户要能直接改游戏正在用的那份包，而不是只能"另存为"。
        /// 保存前先跑一遍与游戏同源的校验器；覆盖已有文件需要显式 overwrite。
        /// 游戏侧的写盘另有约束：只允许新建，覆盖必须显式 `overwrite=true`（见 `ai.tree.export`）。
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
                return Error("outside_roots",
                    "这个路径不在包目录里（只允许写 <实例根>/PlayerAi/BehaviorTrees/）。");
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
                ["root"] = owner.Kind,
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

        // ---------------------------------------------------------------- 嵌套包（Task.Subtree）

        /// <summary>
        /// `GET /api/subtree?path=&lt;包&gt;&amp;node=&lt;节点 id&gt;`：把 `Task.Subtree` 引用的那个包
        /// **按游戏内同一套解析规则**找出来，并返回它里面那个入口节点的原文。
        ///
        /// 解析规则与 `TreeCompiler.ResolveSubtree` 完全一致（不自己另写一套）：
        ///   · `properties.package` 写的是**归属包的 `manifest.references` 里的引用 id**，可带 `#节点id`；
        ///   · 引用目标由 `LoadedPackage.FindReference` 给出（包加载时就已经把引用闭包读进来了）。
        /// 所以"编辑器里展开出来的东西"和"游戏里真的会跑的东西"是同一个东西。
        /// </summary>
        public Dictionary<string, object> ReadSubtree(string nameOrPath, string nodeId)
        {
            string path = Resolve(nameOrPath, out string resolveError);
            if (path == null)
                return Error("resolve", resolveError);
            if (string.IsNullOrEmpty(nodeId))
                return Error("invalid_argument", "缺少 node 参数");

            ScbtPackageSet set = PackageLoader.Load(path, m_options);
            if (set.Root == null)
                return Error("unreadable", "包读不出来：" + set.Summarize(8)[0]);

            // 在**所有已加载的包**里找这个节点（节点可能在某个被引用的包里）
            for (int i = 0; i < set.Packages.Count; i++)
            {
                LoadedPackage owner = set.Packages[i];
                ScbtNodeDoc doc = owner.Tree != null ? owner.Tree.Find(nodeId) : null;
                if (doc == null)
                    continue;

                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["ownerFile"] = owner.FileName,
                    ["ownerId"] = owner.PackageId,
                    ["nodeId"] = nodeId,
                    ["nodeType"] = doc.Type
                };

                string reference = ReadProperty(doc, "package");
                result["reference"] = reference;
                if (string.IsNullOrEmpty(reference))
                {
                    result["ok"] = false;
                    result["reason"] = "这个节点没有 properties.package（不是嵌套引用，或者属性还没填）";
                    return result;
                }

                string referenceId = reference;
                string entryId = null;
                int separator = reference.IndexOf('#');
                if (separator >= 0)
                {
                    if (separator + 1 < reference.Length)
                        entryId = reference.Substring(separator + 1);
                    referenceId = reference.Substring(0, separator);
                }

                LoadedPackage referenced = owner.FindReference(referenceId);
                if (referenced == null)
                {
                    result["ok"] = false;
                    result["reason"] = "引用 '" + referenceId + "' 没解析出包（"
                        + owner.FileName + " 的 manifest.references 里可能没写它）";
                    result["references"] = DescribeReferences(owner);
                    return result;
                }

                if (string.IsNullOrEmpty(entryId) && referenced.Manifest != null)
                    entryId = referenced.Manifest.Entry;

                result["resolvedFile"] = referenced.Path;
                result["resolvedId"] = referenced.PackageId;
                result["entry"] = entryId;
                result["references"] = DescribeReferences(owner);
                result["availableEntries"] = DescribeNodeIds(referenced);

                ScbtNodeDoc entry = referenced.Tree != null ? referenced.Tree.ResolveEntry(entryId) : null;
                if (entry == null)
                {
                    result["ok"] = false;
                    result["reason"] = "被引用的包 " + referenced.FileName + " 里没有节点 '" + entryId + "'";
                    return result;
                }

                // 返回**被引用包的整棵树**（前端按 entry 找到那棵子树即可）：节点对象本身没有
                // 序列化入口，而整棵树正是 game 侧编译时用的同一份数据。
                result["tree"] = referenced.Tree.ToValue();
                result["nodes"] = referenced.Tree.NodeCount;
                return result;
            }

            return Error("not_found", "在本包及其被引用的包里都没找到节点：" + nodeId);
        }

        private static string ReadProperty(ScbtNodeDoc doc, string name)
        {
            if (doc == null || doc.Properties == null || !doc.Properties.IsObject)
                return null;
            return doc.Properties.Get(name).AsString(null);
        }

        private static List<string> DescribeReferences(LoadedPackage owner)
        {
            var list = new List<string>();
            if (owner == null || owner.Manifest == null)
                return list;
            for (int i = 0; i < owner.Manifest.References.Count; i++)
            {
                ScbtReference reference = owner.Manifest.References[i];
                bool resolved = i < owner.ReferenceTargets.Count && owner.ReferenceTargets[i] != null;
                list.Add(reference.Id + (resolved ? " → " + owner.ReferenceTargets[i].FileName : " (没解析出来)"));
            }
            return list;
        }

        private static List<string> DescribeNodeIds(LoadedPackage package)
        {
            var list = new List<string>();
            if (package == null || package.Tree == null || package.Tree.Root == null)
                return list;
            CollectNodeIds(package.Tree.Root, list, 32);
            return list;
        }

        private static void CollectNodeIds(ScbtNodeDoc node, List<string> into, int limit)
        {
            if (node == null || into.Count >= limit)
                return;
            into.Add(node.Id + " (" + node.Type + ")");
            for (int i = 0; i < node.Children.Count && into.Count < limit; i++)
                CollectNodeIds(node.Children[i], into, limit);
        }

        // ---------------------------------------------------------------- 实时监视（P3）

        /// <summary>
        /// `GET /api/game/live`：一次把"编辑器要看的活的东西"问全 ——
        /// `ai.status`（模式/活动树/暂停）+ `ai.tree.snapshot`（活动节点路径、tick、上次结果）
        /// + `ai.blackboard`（键值）。
        ///
        /// 为什么合成一个端点：监视面板 600ms 轮询一次，三个命令各开一次 TCP 会让
        /// 游戏侧的命令线程白忙；合起来一次往返就够，而且三份数据是**同一时刻**的。
        /// </summary>
        public Dictionary<string, object> LiveStatus()
        {
            try
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["status"] = m_game.QueryStatus(),
                    ["tree"] = m_game.QueryTreeSnapshot(),
                    ["blackboard"] = m_game.QueryBlackboard()
                };
                return result;
            }
            catch (Exception exception)
            {
                return GameError("读取实时状态", exception);
            }
        }

        /// <summary>`POST /api/game/pause|resume`：从编辑器里暂停/继续行为树。</summary>
        public Dictionary<string, object> SetPaused(bool paused)
        {
            try
            {
                Dictionary<string, object> result = m_game.SetPaused(paused);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["paused"] = paused,
                    ["game"] = result
                };
            }
            catch (Exception exception)
            {
                return GameError(paused ? "暂停行为树" : "继续行为树", exception);
            }
        }

        // ---------------------------------------------------------------- 动作包（P1）

        /// <summary>动作包目录：与树包同一个目录（只有一个包目录）。</summary>
        public List<PackageRoot> ActionRoots()
        {
            var roots = new List<PackageRoot>();
            if (m_roots.InstanceRoot != null)
                roots.Add(m_roots.InstanceRoot);
            return roots;
        }

        /// <summary>
        /// `GET /api/actions`：列出包目录里的 `.scatpak`（时长/帧数/**能否回放**）。
        /// 编辑器把它们当**物料**：拖到画布上就是 `Task.PlayActionPackage` 节点。
        /// </summary>
        public Dictionary<string, object> ListActions()
        {
            var items = new List<Dictionary<string, object>>();
            var folders = new List<Dictionary<string, object>>();

            List<PackageRoot> roots = ActionRoots();
            int replayable = 0;
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
                ["actions"] = items
            };
        }

        // ------------------------------------------------ 动作包编辑（右键菜单：编辑/重命名/删除）

        /// <summary>
        /// `GET /api/action/events`：读出一个动作包的**语义事件轨**（`tracks/events.json`）。
        ///
        /// 用户要求（原话）："动作包我希望可以右键，里面第一个为编辑，可以进入动作包的编辑"。
        /// 事件轨就是最该能编辑的那部分 —— 录下来的是 `click:1010.6,64.83` 这种死像素，
        /// 手工改成 `click:list:WorldsList@世界名` 就再也不会因为窗口尺寸失效
        /// （这一条正是之前"改了窗口就点空"的根治手段）。
        ///
        /// 逐帧输入轨（`tracks/input.bin`）与关键帧**原样保留**：只动事件，不动录制轨道。
        /// </summary>
        public Dictionary<string, object> ReadActionEvents(string nameOrPath)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);

            ScatActionPackage package;
            PackageReport report;
            if (!ScatValidator.TryLoad(path, out package, out report) || package == null)
            {
                return Error("invalid_package",
                    "动作包读不出来：" + report.Summary());
            }

            var events = new List<Dictionary<string, object>>();
            PackageValue list = package.Events != null ? package.Events.Get("events") : null;
            if (list != null && list.IsArray)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    PackageValue entry = list.Item(i);
                    if (entry == null || !entry.IsObject)
                        continue;
                    events.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["index"] = i,
                        ["t"] = entry.Get("t").AsNumber(0.0),
                        ["kind"] = entry.Get("kind").AsString("ui.click"),
                        ["detail"] = entry.Get("detail").AsString(string.Empty)
                    });
                }
            }

            var response = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["file"] = System.IO.Path.GetFileName(path),
                ["path"] = path,
                ["folder"] = folder,
                ["writable"] = IsWritableFolder(folder),
                ["replayable"] = package.CanReplay,
                ["frames"] = package.Frames,
                ["duration"] = package.Duration,
                ["sampleRate"] = package.Manifest != null ? package.Manifest.SampleRate : 60,
                ["events"] = events,
                ["eventCount"] = events.Count,
                ["issues"] = ActionIssuesOf(folder, path)
            };
            if (package.Manifest != null)
            {
                response["id"] = package.Manifest.Id;
                response["name"] = package.Manifest.Name;
                response["recordedUtc"] = package.Manifest.RecordedUtc;
            }
            return response;
        }

        /// <summary>
        /// `POST /api/action/events`：把编辑过的事件轨写回同一个包（**其它轨道一字不动**）。
        ///
        /// 写盘策略与树的保存一致：先写临时文件再替换，中途失败不会留下半个包。
        /// 写完立刻重新校验并把报告带回去（编辑器就能直接显示"这条改成 list: 之后还能不能回放"）。
        /// </summary>
        public Dictionary<string, object> SaveActionEvents(string nameOrPath, PackageValue eventsJson)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);
            if (!IsWritableFolder(folder))
                return Error("read_only", "这个目录不可写（只读包目录）：" + folder);
            if (eventsJson == null)
                return Error("invalid_argument", "缺少 events 正文");

            ScatActionPackage package;
            PackageReport report;
            if (!ScatValidator.TryLoad(path, out package, out report) || package == null)
                return Error("invalid_package", "动作包读不出来：" + report.Summary());

            PackageValue normalized = NormalizeEvents(eventsJson, out string normalizeError);
            if (normalized == null)
                return Error("invalid_argument", normalizeError);

            byte[] bytes = ScatPackage.ToBytes(package.Manifest, package.Track, normalized,
                package.Keyframes);
            string temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception exception)
            {
                TryDelete(temp);
                return Error("write_failed", "写回动作包失败：" + exception.Message);
            }

            Dictionary<string, object> result = ReadActionEvents(path);
            if (Equals(result["ok"], true))
            {
                result["saved"] = true;
                result["bytes"] = bytes.Length;
            }
            return result;
        }

        /// <summary>`POST /api/action/rename`：重命名动作包文件（同时把 manifest 的 name 跟着改）。</summary>
        public Dictionary<string, object> RenameAction(string nameOrPath, string newName)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);
            if (!IsWritableFolder(folder))
                return Error("read_only", "这个目录不可写（只读包目录）：" + folder);
            if (string.IsNullOrWhiteSpace(newName))
                return Error("invalid_argument", "新名字不能为空");

            string target = Sanitize(newName);
            if (!string.IsNullOrEmpty(target))
                target += PackageRoots.ActionExtension;
            if (string.IsNullOrEmpty(target))
                return Error("invalid_argument", "新名字里没有可用字符：" + newName);

            string destination = System.IO.Path.Combine(folder, target);
            string oldFile = System.IO.Path.GetFileName(path);
            if (string.Equals(oldFile, target, StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["file"] = oldFile,
                    ["renamed"] = false,
                    ["reason"] = "名字没变"
                };
            }
            if (File.Exists(destination))
                return Error("exists", "同名动作包已经存在：" + target);

            // manifest 里的 name 跟着改：编辑器列表与树里的引用都按这个名字看。
            ScatActionPackage package;
            PackageReport report;
            if (ScatValidator.TryLoad(path, out package, out report) && package != null
                && package.Manifest != null)
            {
                package.Manifest.Name = System.IO.Path.GetFileNameWithoutExtension(target);
                try
                {
                    File.WriteAllBytes(path, ScatPackage.ToBytes(package.Manifest, package.Track,
                        package.Events, package.Keyframes));
                }
                catch (Exception exception)
                {
                    return Error("write_failed", "改名时写回 manifest 失败：" + exception.Message);
                }
            }

            try
            {
                File.Move(path, destination);
            }
            catch (Exception exception)
            {
                return Error("write_failed", "重命名失败：" + exception.Message);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["renamed"] = true,
                ["from"] = oldFile,
                ["file"] = target,
                ["path"] = destination,
                ["referencedByTrees"] = FindTreesReferencing(folder, oldFile)
            };
        }

        /// <summary>
        /// `POST /api/action/delete`：删除动作包文件。
        /// 只允许删**可写包目录**里的文件（绝不碰只读目录/游戏安装目录）。
        /// </summary>
        public Dictionary<string, object> DeleteAction(string nameOrPath)
        {
            string folder;
            string path = ResolveAction(nameOrPath, out folder, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);
            if (!IsWritableFolder(folder))
                return Error("read_only", "这个目录不可写（只读包目录）：" + folder);

            string file = System.IO.Path.GetFileName(path);
            List<string> referenced = FindTreesReferencing(folder, file);
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                return Error("write_failed", "删除失败：" + exception.Message);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["deleted"] = true,
                ["file"] = file,
                ["referencedByTrees"] = referenced
            };
        }

        /// <summary>把前端发来的事件数组归一成合法的事件轨（时间取非负、按时间排好、字段补全）。</summary>
        private static PackageValue NormalizeEvents(PackageValue eventsJson, out string error)
        {
            error = null;
            PackageValue list = eventsJson;
            if (eventsJson.IsObject)
                list = eventsJson.Get("events");
            if (list == null || !list.IsArray)
            {
                error = "events 必须是数组，或 {\"events\":[...]} 这种对象";
                return null;
            }

            var rows = new List<KeyValuePair<double, PackageValue>>();
            for (int i = 0; i < list.Count; i++)
            {
                PackageValue entry = list.Item(i);
                if (entry == null || !entry.IsObject)
                    continue;
                string kind = entry.Get("kind").AsString("ui.click");
                string detail = entry.Get("detail").AsString(null);
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(detail))
                {
                    error = "第 " + (i + 1) + " 条事件缺 kind 或 detail";
                    return null;
                }
                double time = entry.Get("t").AsNumber(0.0);
                if (time < 0.0)
                    time = 0.0;

                PackageValue normalized = PackageValue.Object();
                normalized.Set("t", PackageValue.Number(time));
                normalized.Set("kind", PackageValue.Str(kind));
                normalized.Set("detail", PackageValue.Str(detail));
                rows.Add(new KeyValuePair<double, PackageValue>(time, normalized));
            }

            // 时间顺序即执行顺序：回放器是"到点就发"，乱序会让同帧的两条事件顺序不确定。
            rows.Sort(delegate (KeyValuePair<double, PackageValue> a,
                KeyValuePair<double, PackageValue> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            PackageValue root = PackageValue.Object();
            root.Set("format", PackageValue.Str("scat-events"));
            root.Set("version", PackageValue.Number(1));
            PackageValue output = PackageValue.Array();
            for (int i = 0; i < rows.Count; i++)
                output.Add(rows[i].Value);
            root.Set("events", output);
            return root;
        }

        /// <summary>校验报告里的 issues（编辑器直接显示给用户）。</summary>
        private static object ActionIssuesOf(string folder, string path)
        {
            try
            {
                Dictionary<string, object> report = ScatLibrary.Validate(folder, path);
                object issues;
                return report.TryGetValue("issues", out issues) ? issues : new List<string>();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>这个目录能不能被编辑器改（包目录 = 实例根，可写）。</summary>
        private bool IsWritableFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return false;
            List<PackageRoot> roots = ActionRoots();
            for (int i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i].Path, folder, StringComparison.OrdinalIgnoreCase))
                    return roots[i].Writable;
            }
            return false;
        }

        /// <summary>哪些树还在引用这个动作包（重命名/删除时给用户一句实话）。</summary>
        private List<string> FindTreesReferencing(string folder, string actionFile)
        {
            var found = new List<string>();
            string bare = System.IO.Path.GetFileNameWithoutExtension(actionFile);
            string[] trees;
            try
            {
                trees = Directory.GetFiles(folder, "*" + PackageRoots.Extension);
            }
            catch (Exception)
            {
                return found;
            }

            for (int i = 0; i < trees.Length; i++)
            {
                string text;
                try
                {
                    text = ReadTreeText(trees[i]);
                }
                catch (Exception)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(text))
                    continue;
                if (text.IndexOf(""" + bare + """, StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf(""" + actionFile + """, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add(System.IO.Path.GetFileName(trees[i]));
                }
            }
            return found;
        }

        /// <summary>取树包的 tree.json 文本（用同一个加载器读，读不出来就返回 null）。</summary>
        private string ReadTreeText(string treePath)
        {
            try
            {
                ScbtPackageSet set = PackageLoader.Load(treePath, m_options);
                if (set == null || set.Root == null || set.Root.Tree == null)
                    return null;
                return set.Root.Tree.ToValue().ToJson(false);
            }
            catch (Exception)
            {
                return null;
            }
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
            }
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
            result["source"] = "instance";
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

        // ---------------------------------------------------------------- UI 定位 / 点击服务

        /// <summary>
        /// `GET /api/game/ui/elements`：当前屏幕上可交互的 UI 元素（**真实坐标** + 可点性）。
        ///
        /// 用户要求（原话）："把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，
        /// 要能够使用来获取坐标或点击对象。避免硬编码由于分辨率变化或窗口尺寸变化导致无法使用"。
        /// 拾取面板点一个元素 → 拿到它的**语义目标**（控件名/路径，列表行给 `list:列表@文字`）
        /// → 直接写进行为树或动作包，而不是把此刻的像素写进去。
        /// </summary>
        public Dictionary<string, object> UiElements(bool includeAll, int maxElements)
        {
            try
            {
                Dictionary<string, object> elements = m_game.QueryUiElements(includeAll, maxElements);
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true
                };
                foreach (KeyValuePair<string, object> pair in elements)
                    response[pair.Key] = pair.Value;
                response["elements"] = SuggestTargets(elements);
                return response;
            }
            catch (Exception exception)
            {
                return GameError("读取界面元素", exception);
            }
        }

        /// <summary>
        /// 给每个元素补一个**推荐的语义目标**（编辑器用它写进树/包）：
        /// 列表行优先给 `list:<列表>@<文字>`（跟着列表内容走），其余给控件路径（最精确）。
        ///
        /// 注意：游戏那边 `obs.ui` 返回的 `elements` 是**一段 JSON 文本**
        /// （`GameBridgeClient.Collect` 把数组按 `ToJson` 收成字符串），这里必须先解析回数组 ——
        /// 否则前端拿到的是字符串，`elements.forEach` 直接炸（实测：元素个数变成 9593 = 字符串长度）。
        /// </summary>
        private static object SuggestTargets(Dictionary<string, object> elements)
        {
            object raw;
            if (elements == null || !elements.TryGetValue("elements", out raw))
                return null;

            List<Dictionary<string, object>> list = ParseElementArray(raw);
            if (list == null)
                return raw;

            foreach (Dictionary<string, object> element in list)
            {
                string name = Value(element, "name");
                string path = Value(element, "path");
                object listInfo;
                if (element.TryGetValue("list", out listInfo))
                {
                    var info = listInfo as Dictionary<string, object>;
                    object selected;
                    if (info != null && info.TryGetValue("selectedIndex", out selected) && selected != null)
                    {
                        element["rowTarget"] = "list:" + (name ?? "?") + "#" + selected;
                    }
                }
                element["target"] = !string.IsNullOrEmpty(path) ? path : name;
                element["shortTarget"] = name;
            }

            elements["elements"] = list;   // 把解析结果放回去：前端拿到的是真数组，不是一段文本
            return list;
        }

        /// <summary>把"可能是一段 JSON 文本、也可能是数组"的 elements 统一成数组。</summary>
        private static List<Dictionary<string, object>> ParseElementArray(object raw)
        {
            var already = raw as List<Dictionary<string, object>>;
            if (already != null)
                return already;

            string text = raw as string;
            if (string.IsNullOrEmpty(text))
                return null;

            var report = new PackageReport();
            PackageValue value;
            if (!PackageJson.TryParse(text, "elements", report, out value) || !value.IsArray)
                return null;

            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < value.Count; i++)
            {
                PackageValue item = value.Item(i);
                if (!item.IsObject)
                    continue;
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string member in item.MemberNames)
                    row[member] = PlainValue(item.Get(member));
                list.Add(row);
            }
            return list;
        }

        /// <summary>
        /// JSON 值 → 前端能**直接用**的 CLR 结构（数组给 List、对象给 Dictionary）。
        ///
        /// 为什么不能像以前那样把数组/对象 `ToJson()` 成字符串：列表控件的每一行
        /// 就在 `list.items` 里，一旦它变成文本，前端 `list.items.forEach(...)` 立刻抛
        /// `is not a function` —— 用户实测：**在地图选择界面点「拾取界面元素」整个面板报错**
        /// （字符串也有 `.length`，所以前端那个 `items.length` 守卫拦不住，只会更隐蔽）。
        /// 实测证据：`/api/game/ui/elements` 里 `list.items` 的类型是 string，
        /// 内容是 `[{"index":0,"text":"Rebritish",...}]`；而游戏侧原始回包里它是真数组。
        /// </summary>
        private static object PlainValue(PackageValue value)
        {
            if (value == null)
                return null;
            if (value.IsBool)
                return value.AsBool();
            if (value.IsNumber)
                return value.AsNumber();
            if (value.IsString)
                return value.AsString();
            if (value.IsArray)
            {
                var items = new List<object>();
                for (int i = 0; i < value.Count; i++)
                    items.Add(PlainValue(value.Item(i)));
                return items;
            }
            if (value.IsObject)
            {
                var members = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string member in value.MemberNames)
                    members[member] = PlainValue(value.Get(member));
                return members;
            }
            return null;
        }

        private static string Value(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw))
                return null;
            return raw as string;
        }

        /// <summary>
        /// `GET /api/game/ui/locate`：解析一个语义目标并返回**当前**坐标与可点性
        /// （走 CmdBridge 的 `ui.locate`，与行为树/回放同一份实现）。
        /// `mark` 打开时游戏里会在那个像素上亮一个红点（默认 2 秒 / 5 像素）——
        /// 用户要求："点定位，在游戏中看不到明显的标记，可以渲染 2s 直接 5 像素的红色圆点，方便定位。"
        /// </summary>
        public Dictionary<string, object> LocateUi(string target, bool mark)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Error("invalid_argument", "缺少目标（target）：例如 Play、list:WorldsList@世界名。");
            try
            {
                Dictionary<string, object> located = m_game.LocateUi(target.Trim(), mark);
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["target"] = target.Trim(),
                    ["mark"] = mark
                };
                foreach (KeyValuePair<string, object> pair in located)
                    response[pair.Key] = pair.Value;
                return response;
            }
            catch (Exception exception)
            {
                return GameError("定位界面元素", exception);
            }
        }

        /// <summary>
        /// `POST /api/game/ui/click`：在游戏里**真的点一下**这个目标（编辑器"试一下"按钮）。
        /// `mode`：`direct`（默认，单帧合成按下）/ `input`（多帧软光标会话）/
        /// `invoke`（直接触发控件自己的按下事件，能触发才有效）。`mark` 打开时亮一下落点。
        /// </summary>
        public Dictionary<string, object> ClickUi(string target, string mode, bool mark)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Error("invalid_argument", "缺少目标（target）：例如 Play、list:WorldsList@世界名。");
            try
            {
                Dictionary<string, object> clicked = m_game.ClickUi(target.Trim(), mode, mark);
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["target"] = target.Trim(),
                    ["mode"] = string.IsNullOrEmpty(mode) ? "direct" : mode,
                    ["mark"] = mark
                };
                foreach (KeyValuePair<string, object> pair in clicked)
                    response[pair.Key] = pair.Value;
                return response;
            }
            catch (Exception exception)
            {
                return GameError("点击界面元素", exception);
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
        /// 控制通道出错时如实分类：**没连上**（游戏没跑/通道没开）、
        /// **连上了但还没准备好**（`not_ready`：没进世界 / AI 没接管）、
        /// **连上了但被拒绝**（例如游戏里那份 Mod 还没有这个命令）—— 是三件不同的事。
        /// 以前后两种都归到"多半是 Mod 太旧"，实测把用户指错了方向。
        /// </summary>
        private static Dictionary<string, object> GameError(string what, Exception exception)
        {
            if (exception is GameCommandException refusal)
            {
                switch (refusal.Code)
                {
                    case "not_ready":
                        return Error("game_not_ready", "游戏在跑，但还没准备好「" + what + "」："
                            + refusal.GameMessage
                            + "　需要：①进入世界 ②游戏里让 AI 接管（sccmd ai enable）。"
                            + "准备好之前这里会一直等，好了会自动开始刷新。");
                    case "unknown_command":
                    case "unknown":
                        // 两种可能，别只说一种：**刚启动那一两秒**通道已经在监听、
                        // 但 PlayerAiMod 的 `ai.*` 还没注册完 —— 这时候报"Mod 太旧"是误导
                        // （实测：游戏刚起来就点实时监视，报的就是这个，等一秒就好了）。
                        return Error("game_refused", "游戏在跑，但拒绝了「" + what + "」："
                            + refusal.GameMessage
                            + " —— 两种可能：①游戏刚启动、Mod 还没注册完命令（等一两秒再试）；"
                            + "②游戏里那份 Mod 确实没有这个命令（重新部署 Mod 并重启游戏）。");
                    default:
                        return Error("game_refused", "游戏在跑，但拒绝了「" + what + "」（"
                            + refusal.Code + "）：" + refusal.GameMessage);
                }
            }

            string message = exception.Message ?? string.Empty;
            // 旧 runtime 文件（游戏上次退出时留下的）会给出裸露的 socket 报错，
            // 很容易被读成"游戏在跑但通道坏了"。这里加一句人话。
            if (message.Contains("refused") || message.Contains("拒绝"))
            {
                return Error("game_unreachable", "游戏没在跑（或控制通道还没开）：" + message
                    + "　提示：如果游戏确实没开，点「启动游戏」；"
                    + GameBridgeClient.RuntimeFileName + " 可能是上次运行留下的旧文件。");
            }
            return Error("game_unreachable", "游戏没在跑（或控制通道没开）：" + message);
        }

        // ---------------------------------------------------------------- 启动 / 结束游戏

        /// <summary>
        /// `GET /api/game/process`：**只看不动**地把"游戏进程 + 控制通道"的状态说清楚 ——
        /// 没启动 / 启动了但通道还没开 / 已连上。前端据此决定按钮可用性与轮询。
        /// </summary>
        public Dictionary<string, object> GameProcessStatus()
        {
            Dictionary<string, object> info = GameLauncher.Describe(InstanceRoot);
            bool connected = false;
            string channelError = null;
            if (info["running"] is bool running && running)
            {
                Dictionary<string, object> ping;
                if (m_game.TryDescribe(out ping, out channelError))
                {
                    connected = true;
                    info["channel"] = ping;
                }
            }
            else
            {
                channelError = info["runtimeStale"] is bool stale && stale
                    ? "游戏没在跑（" + GameBridgeClient.RuntimeFileName + " 是上次运行留下的旧文件）"
                    : "游戏没在跑";
            }
            info["channelConnected"] = connected;
            info["channelError"] = channelError;
            return info;
        }

        /// <summary>
        /// `POST /api/game/launch`：启动游戏（等价于双击 `<实例根>/Survivalcraft.exe`）。
        /// 这是**人用的编辑器**在起进程，不是 AI 在改游戏状态；启动后仍然只走控制通道下命令。
        /// </summary>
        public Dictionary<string, object> LaunchGame()
        {
            return GameLauncher.Launch(InstanceRoot);
        }

        /// <summary>`POST /api/game/quit`：结束游戏（先请它正常退出，超时才强杀）。</summary>
        public Dictionary<string, object> QuitGame()
        {
            return GameLauncher.Quit(InstanceRoot);
        }

        /// <summary>
        /// `GET /api/game/status`：问游戏要 `ai.status`。
        /// 走 <see cref="GameError"/> 分类 —— 以前这个端点在路由里自己 catch，
        /// 报错文案跟别的端点不一致（`not_ready` 被说成裸的"游戏拒绝了命令"）。
        /// </summary>
        public Dictionary<string, object> GameStatus()
        {
            try
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["status"] = m_game.QueryStatus()
                };
            }
            catch (Exception exception)
            {
                return GameError("读取游戏状态", exception);
            }
        }

        /// <summary>
        /// `POST /api/game/tree/stop`：停止并卸下当前树（`ai.tree.stop`）。
        /// 与"暂停"的区别就是用户要的那个：暂停保留运行态（继续 = 从原处接着跑），
        /// 停止回到"什么都没跑"——之后再播放就是干净地从根开始。
        /// </summary>
        public Dictionary<string, object> StopGameTree()
        {
            try
            {
                Dictionary<string, object> result = m_game.StopTree();
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["game"] = result
                };
                foreach (string key in result.Keys)
                    response[key] = result[key];
                return response;
            }
            catch (Exception exception)
            {
                return GameError("停止这棵树", exception);
            }
        }

        /// <summary>
        /// `POST /api/game/tree/switch`：让游戏**切到这个包并开始跑**（`ai.tree.switch`）。
        ///
        /// 这是"播放"这个动作的核心：光保存文件游戏不会换树（活动树还是原来那棵），
        /// 所以编辑器里必须有一个明确的"跑它"入口。切换是毫秒级的（常驻副本优先），
        /// 不会重载世界。
        /// </summary>
        public Dictionary<string, object> SwitchGameTree(string nameOrPath, string entry, bool start)
        {
            string path = Resolve(nameOrPath, out string resolveError);
            if (path == null)
                return Error("not_found", resolveError);

            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // 传绝对路径：游戏与编辑器可能不是同一个实例根，名字解析交给编辑器这边
                ["path"] = path,
                ["start"] = start
            };
            if (!string.IsNullOrEmpty(entry))
                arguments["entry"] = entry;

            try
            {
                Dictionary<string, object> result = m_game.SwitchTree(arguments);
                var response = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ok"] = true,
                    ["path"] = path,
                    ["file"] = System.IO.Path.GetFileName(path),
                    ["game"] = result
                };
                foreach (string key in result.Keys)
                    response[key] = result[key];
                return response;
            }
            catch (Exception exception)
            {
                return GameError("切换到这棵树", exception);
            }
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
                    Roots = m_roots,
                    // 与正常装载共用同一份库来源（同一份缓存）：保存前的这道闸门
                    // 必须和游戏侧给出**同一个结论**，否则"编辑器说没事、进游戏报错"。
                    Banks = m_options.Banks
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

        // ---------------------------------------------------------------- 问题库（P3 第二步）

        /// <summary>
        /// `GET /api/banks`：列出 `<实例根>/PlayerAi/Questions/*.qbank` 及其问题/选项，
        /// 并给出每个问题**建议的 answerKeys 类型**。
        ///
        /// 为什么值得单开一个端点：`Task.LayaAsk` 的两个属性是**裸字符串**——
        /// `questions` 是库名、`answerKeys` 是 `黑板键:问题id:类型` 的逗号串。
        /// 手打的代价很实在：库名打错 → `laya_bank_not_found`；问题 id 或类型打错 →
        /// 校验期报错或运行时整节点 `fail`。把它们变成**下拉 + 勾选**，
        /// 这两个属性就从"要背格式"变成"照着点"。
        ///
        /// 解析用的是**游戏内同一份** `QuestionBankParser`（`State/QuestionBank.cs`），
        /// 于是"编辑器列得出来的"与"游戏装得上的"永远是同一批。
        /// </summary>
        public Dictionary<string, object> ListQuestionBanks()
        {
            string folder = string.IsNullOrEmpty(InstanceRoot) ? null
                : System.IO.Path.Combine(InstanceRoot, "PlayerAi", QuestionBank.FolderName);
            var banks = new List<Dictionary<string, object>>();
            var errors = new List<string>();

            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder, "*" + QuestionBank.Extension,
                        SearchOption.TopDirectoryOnly);
                }
                catch (Exception exception)
                {
                    files = new string[0];
                    errors.Add("读不了 " + folder + "：" + exception.Message);
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < files.Length; i++)
                {
                    string json;
                    try
                    {
                        json = File.ReadAllText(files[i]);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(System.IO.Path.GetFileName(files[i]) + "：" + exception.Message);
                        continue;
                    }

                    string parseError;
                    QuestionBank bank = QuestionBankParser.Parse(json, files[i], out parseError);
                    if (bank == null)
                    {
                        // 坏库**照样列出来**（只是标 red）：它正在磁盘上，用户需要看到"为什么用不了"
                        banks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["file"] = System.IO.Path.GetFileName(files[i]),
                            ["path"] = files[i],
                            ["id"] = null,
                            ["name"] = null,
                            ["questions"] = new List<object>(),
                            ["error"] = parseError
                        });
                        continue;
                    }

                    banks.Add(DescribeBank(bank, files[i]));
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["folder"] = folder,
                ["folderExists"] = !string.IsNullOrEmpty(folder) && Directory.Exists(folder),
                ["banks"] = banks,
                ["errors"] = errors,
                ["bindingKinds"] = new List<string> { "str", "bool", "int", "float" }
            };
        }

        private static Dictionary<string, object> DescribeBank(QuestionBank bank, string path)
        {
            var questions = new List<Dictionary<string, object>>();
            for (int i = 0; i < bank.Questions.Count; i++)
            {
                QuestionTemplate question = bank.Questions[i];
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = question.Id,
                    ["type"] = question.Type,
                    ["suggestedKind"] = SuggestedBindingKind(question),
                    ["instructions"] = question.Instructions,
                    ["options"] = new List<string>(question.OptionKeys())
                };
                questions.Add(entry);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["file"] = System.IO.Path.GetFileName(path),
                ["path"] = path,
                ["id"] = bank.Id,
                ["name"] = bank.Name,
                ["template"] = bank.Template,
                ["description"] = bank.Description,
                ["questions"] = questions,
                ["error"] = null
            };
        }

        /// <summary>
        /// 某个问题**该绑成什么类型**的黑板键：`choice` → str（选项 key）、
        /// `noul` → bool、`score` → float。
        ///
        /// 这与 `BtLayaAskTask` 写入黑板时的类型要求一一对应（写错类型那一步会 fail），
        /// 所以编辑器直接照它预填，用户不用去翻源码。
        /// </summary>
        private static string SuggestedBindingKind(QuestionTemplate question)
        {
            if (question == null)
                return "str";
            if (question.IsChoice)
                return "str";
            if (question.IsNoul)
                return "bool";
            if (question.IsScore)
                return "float";
            return "str";
        }

        // ---------------------------------------------------------------- 资源库 / Laya 配置（P6）

        /// <summary>
        /// 编辑器允许转发给游戏的命令白名单。
        ///
        /// 只放行**资源库那一族**：它们全是"读现状 / 保存 / 丢弃 / 缓存 / 恢复 / 重取 / 冲突处置"，
        /// 没有一个能绕过游戏自己的闸门（保存默认不覆盖、丢弃要回磁盘版、覆盖要先备份）。
        /// **不放行 `ai.edit.*` / `ai.tree.load` 之类** —— 那些是游戏内行为，编辑器不该远程代按。
        /// </summary>
        private static readonly string[] AssetCommands =
        {
            "ai.asset.status",
            "ai.asset.list",
            "ai.asset.load",
            "ai.asset.save",
            "ai.asset.drop",
            "ai.asset.autosave",
            "ai.asset.restore",
            "ai.asset.retry",
            "ai.asset.conflict"
        };

        /// <summary>
        /// `GET /api/assets`：**三态视图**（磁盘版 / 内存版 dirty / 缓存版）+ 待处置冲突 + 重取现状。
        ///
        /// 数据全来自游戏（`ai.asset.list` + `ai.asset.status`）：编辑器**不自己算**——
        /// "哪一版更新"必须与游戏内的判据同源，否则界面说的和实际用的会不一致。
        /// </summary>
        public Dictionary<string, object> Assets()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["instanceRoot"] = InstanceRoot,
                ["packageFolder"] = m_roots.InstanceRoot != null ? m_roots.InstanceRoot.Path : null,
                ["manualSaveFolder"] = PackageRoots.SavesDirectoryFor(InstanceRoot),
                ["cacheFolder"] = AutoSaveCache.ResolveDirectory(InstanceRoot)
            };

            try
            {
                Dictionary<string, object> list = m_game.SendRaw("ai.asset.list", null);
                foreach (KeyValuePair<string, object> pair in list)
                    result[pair.Key] = pair.Value;
                result["ok"] = true;
            }
            catch (GameCommandException exception)
            {
                result["ok"] = false;
                result["code"] = exception.Code;
                result["reason"] = exception.GameMessage;
            }
            catch (Exception exception)
            {
                result["ok"] = false;
                result["reason"] = "the game is not reachable: " + exception.Message;
            }

            // 重取现状（与三态视图并列显示：哪条在等退避、哪条已熔断）
            try
            {
                result["retry"] = m_game.SendRaw("ai.asset.retry", null);
            }
            catch (Exception exception)
            {
                result["retryError"] = exception.Message;
            }

            // G25 的待处置冲突（编辑器要在界面上给"用磁盘覆盖 / 保留内存 / 另存为新包"三个选项）
            try
            {
                result["conflict"] = m_game.SendRaw("ai.asset.conflict", null);
            }
            catch (Exception exception)
            {
                result["conflictError"] = exception.Message;
            }
            return result;
        }

        /// <summary>
        /// `POST /api/asset/{action}`：把动作转发给游戏。`action` 是白名单里的命令后缀
        /// （`save` / `drop` / `autosave` / `restore` / `conflict` / `retry` / `load`），
        /// 参数原样透传（`name` / `dir` / `overwrite` / `mode` / `newName` / …）。
        /// </summary>
        public Dictionary<string, object> AssetCommand(string action, Dictionary<string, object> arguments)
        {
            if (string.IsNullOrEmpty(action))
                return Error("invalid_argument", "缺少 action");

            string command = "ai.asset." + action.Trim().ToLowerInvariant();
            bool allowed = false;
            for (int i = 0; i < AssetCommands.Length; i++)
            {
                if (string.Equals(AssetCommands[i], command, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
                return Error("not_allowed", "编辑器不能转发这条命令：" + command);

            try
            {
                Dictionary<string, object> response = m_game.SendRaw(command,
                    arguments ?? new Dictionary<string, object>(StringComparer.Ordinal));
                response["ok"] = true;
                response["command"] = command;
                return response;
            }
            catch (GameCommandException exception)
            {
                Dictionary<string, object> failed = Error(exception.Code, exception.GameMessage);
                failed["command"] = command;
                return failed;
            }
            catch (Exception exception)
            {
                Dictionary<string, object> failed = Error("game_unreachable", exception.Message);
                failed["command"] = command;
                return failed;
            }
        }

        /// <summary>
        /// `GET /api/laya/review`：把游戏侧的 **`ai.laya.review`**（P4 判定复盘）搬进编辑器。
        ///
        /// 与资源库三态同一个原则：**判据只在游戏侧算一次**（那边才有 `DecisionLog`），
        /// 编辑器只转述。游戏没开着时返回 `{ok:false, code:"game_unreachable"}`，
        /// 界面据此说"先启动游戏"，而不是显示一张空表让人以为"没有判定"。
        ///
        /// `format`：`json`（聚合 + 明细，界面用）/ `md`（可复算的抽样表，界面"复制"按钮用）。
        /// </summary>
        public Dictionary<string, object> LayaReview(int count, string format, bool clear)
        {
            var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["count"] = count < 0 ? 0 : count,
                ["format"] = string.IsNullOrEmpty(format) ? "json" : format
            };
            if (clear)
                arguments["clear"] = true;

            try
            {
                Dictionary<string, object> response = m_game.SendRaw("ai.laya.review", arguments);
                response["ok"] = true;
                response["command"] = "ai.laya.review";
                return response;
            }
            catch (GameCommandException exception)
            {
                Dictionary<string, object> failed = Error(exception.Code, exception.GameMessage);
                failed["command"] = "ai.laya.review";
                return failed;
            }
            catch (Exception exception)
            {
                Dictionary<string, object> failed = Error("game_unreachable", exception.Message);
                failed["command"] = "ai.laya.review";
                return failed;
            }
        }

        /// <summary>
        /// `GET /api/laya/config`：读**一份配置**（D16）—— 端点 / 模型 / 超时 / 预算 / 密钥来源。
        ///
        /// **密钥只回掩码**：界面要显示"设过没有、是哪一条"，但没有任何理由把明文再送回浏览器。
        /// </summary>
        public Dictionary<string, object> ReadLayaConfig()
        {
            LayaConfig config = LayaConfig.Load(InstanceRoot);
            Dictionary<string, object> info = config.Describe();
            info["ok"] = true;
            info["configFile"] = LayaConfigPath();
            info["fileExists"] = File.Exists(LayaConfigPath());
            info["defaults"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["baseURL"] = LayaConfig.DefaultBaseUrl,
                ["model"] = LayaConfig.DefaultModel,
                ["timeoutMs"] = LayaConfig.DefaultTimeoutMs,
                ["refreshMs"] = LayaConfig.DefaultRefreshMs
            };
            // 端点可达性：编辑器顺手替用户探一次（失败不影响读取配置）
            string probeError;
            info["reachable"] = ProbeModels(config, out probeError);
            info["probeError"] = probeError;
            return info;
        }

        /// <summary>
        /// 探一次 `GET /v1/models`：回答"这个端点 + 这把密钥现在能不能用"。
        ///
        /// 为什么值得单做：G6 要求"发请求前就把三种分叉判出来"（端点不通 / 没密钥 / 401）——
        /// 界面上直接显示一句，比让用户去猜"为什么按了没反应"有用得多。
        /// </summary>
        private static bool ProbeModels(LayaConfig config, out string error)
        {
            error = null;
            if (config == null)
            {
                error = "no configuration";
                return false;
            }
            if (!config.HasKey)
            {
                error = "no API key configured";
                return false;
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMilliseconds(Math.Max(1200, config.TimeoutMs + 1200));
                    using (var request = new HttpRequestMessage(HttpMethod.Get, config.ModelsUrl))
                    {
                        request.Headers.Add("Authorization", "Bearer " + config.ApiKey);
                        using (HttpResponseMessage response = client.SendAsync(request)
                            .GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                error = "HTTP " + (int)response.StatusCode;
                                return false;
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// `POST /api/laya/config`：写这份配置（原子写）。
        ///
        /// 三条纪律：
        ///   · **密钥只写不回读**：正文里没给 `apiKey` 就保留文件里原有那把（不会因为"改个超时"把密钥抹掉）；
        ///     给了空串表示**显式清除**；
        ///   · 值先**夹紧**（`LayaConfig.Validate` 的同一套规则），不让界面写进超时 0 ms 这种配置；
        ///   · 文件落在 `<实例根>/PlayerAi/Laya.local.json` —— 与游戏读的是**同一个文件**。
        /// </summary>
        public Dictionary<string, object> SaveLayaConfig(Dictionary<string, object> payload)
        {
            string path = LayaConfigPath();
            if (string.IsNullOrEmpty(path))
                return Error("not_ready", "实例根未知，无法定位 Laya.local.json");

            LayaConfig current = LayaConfig.Load(InstanceRoot);
            var config = new LayaConfig
            {
                BaseUrl = Text(payload, "baseURL", current.BaseUrl),
                Model = Text(payload, "model", current.Model),
                ApiKey = current.ApiKey
            };

            if (payload != null && payload.ContainsKey("timeoutMs"))
                config.TimeoutMs = Number(payload, "timeoutMs", current.TimeoutMs);
            else
                config.TimeoutMs = current.TimeoutMs;

            if (payload != null && payload.ContainsKey("refreshMs"))
                config.RefreshMs = Number(payload, "refreshMs", current.RefreshMs);
            else
                config.RefreshMs = current.RefreshMs;

            if (payload != null && payload.ContainsKey("digestBudgetChars"))
                config.DigestBudgetChars = Number(payload, "digestBudgetChars", current.DigestBudgetChars);
            else
                config.DigestBudgetChars = current.DigestBudgetChars;

            config.HeadMaxLen = current.HeadMaxLen;
            config.MaxLen = current.MaxLen;

            if (payload != null && payload.ContainsKey("enabled"))
            {
                object raw;
                payload.TryGetValue("enabled", out raw);
                config.Enabled = raw is bool ? (bool)raw : current.Enabled;
            }
            else
            {
                config.Enabled = current.Enabled;
            }

            // 密钥：给了才动（空串 = 显式清除）
            if (payload != null && payload.ContainsKey("apiKey"))
            {
                string key = Text(payload, "apiKey", string.Empty);
                config.ApiKey = string.IsNullOrEmpty(key) ? null : key.Trim();
            }
            config.Validate();

            var value = PackageValue.Object();
            value.Set("baseURL", PackageValue.Str(config.BaseUrl));
            value.Set("model", PackageValue.Str(config.Model));
            value.Set("timeoutMs", PackageValue.Number(config.TimeoutMs));
            value.Set("refreshMs", PackageValue.Number(config.RefreshMs));
            value.Set("digestBudgetChars", PackageValue.Number(config.DigestBudgetChars));
            value.Set("headMaxLen", PackageValue.Number(config.HeadMaxLen));
            value.Set("maxLen", PackageValue.Number(config.MaxLen));
            value.Set("enabled", PackageValue.Bool(config.Enabled));
            if (!string.IsNullOrEmpty(config.ApiKey))
                value.Set("apiKey", PackageValue.Str(config.ApiKey));

            string error;
            if (!PackageWriter.TryWriteFile(path,
                new UTF8Encoding(false).GetBytes(value.ToJson(true)), out error))
            {
                return Error("io_error", "写不进 " + path + "：" + error);
            }

            var result = ReadLayaConfig();
            result["saved"] = true;
            result["path"] = path;
            result["keyKept"] = payload == null || !payload.ContainsKey("apiKey")
                ? !string.IsNullOrEmpty(config.ApiKey) : !string.IsNullOrEmpty(config.ApiKey);
            return result;
        }

        /// <summary>`&lt;实例根&gt;/PlayerAi/Laya.local.json`（与游戏侧 `LayaConfig` 同一路径）。</summary>
        public string LayaConfigPath()
        {
            if (string.IsNullOrEmpty(InstanceRoot))
                return null;
            return System.IO.Path.Combine(InstanceRoot, "PlayerAi", LayaConfig.ConfigFileName);
        }

        private static string Text(Dictionary<string, object> payload, string key, string fallback)
        {
            if (payload == null || !payload.ContainsKey(key))
                return fallback;
            object raw;
            payload.TryGetValue(key, out raw);
            string text = raw == null ? null : Convert.ToString(raw);
            return string.IsNullOrEmpty(text) && fallback != null ? fallback : text;
        }

        private static int Number(Dictionary<string, object> payload, string key, int fallback)
        {
            if (payload == null || !payload.ContainsKey(key))
                return fallback;
            object raw;
            payload.TryGetValue(key, out raw);
            if (raw == null)
                return fallback;
            if (raw is int)
                return (int)raw;
            double parsed;
            return double.TryParse(Convert.ToString(raw), out parsed) ? (int)parsed : fallback;
        }

        private string Resolve(string nameOrPath, out string error)        {
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
