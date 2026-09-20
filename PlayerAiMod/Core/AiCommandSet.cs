using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// `ai.*` 命令集（P0-7）：让脚本/AI/编辑器能操作 AI 本身。
    ///
    /// 设计原则：
    ///   · **纯语义**：处理器只依赖 <see cref="IAiCommandContext"/> / <see cref="IAiTreeHost"/> /
    ///     <see cref="PackageReloader"/>，不碰游戏类型 —— 于是能在临时工程里逐条自检（见 AiCommandSelfTest）；
    ///   · **通道无关**：绑定到 CmdBridgeMod 的代码单独放在 `Core/AiCommandBridge.cs`，
    ///     换成别的通道（HTTP/本地 CLI）只要再写一个适配器；
    ///   · 命令名稳定、返回 JSON 友好（字典/列表/字符串/数字/布尔）。
    ///
    /// 命令表见 <see cref="CommandNames"/>（计划 §8.2 的 P0 子集）。
    /// </summary>
    public static class AiCommandSet
    {
        /// <summary>命令名（注册与文档共用一份清单）。</summary>
        public static readonly string[] CommandNames =
        {
            "ai.status",
            "ai.pause",
            "ai.resume",
            "ai.enable",
            "ai.disable",
            "ai.blackboard",
            "ai.tree.list",
            "ai.tree.load",
            "ai.tree.stop",
            "ai.tree.reload",
            "ai.tree.notify",
            "ai.tree.validate",
            "ai.tree.prepare",
            "ai.tree.switch",
            "ai.input.release",
            "ai.record.start",
            "ai.record.pause",
            "ai.record.stop",
            "ai.record.save",
            "ai.record.discard",
            "ai.edit.set",
            "ai.edit.insert",
            "ai.edit.remove",
            "ai.edit.move",
            "ai.tree.export",
            "ai.action.list",
            "ai.action.validate",
            "ai.action.play",
            "ai.action.stop",
            "ai.tree.snapshot",
            "ai.logs",
            "bt.selftest"
        };

        /// <summary>命令说明（`cmd.list` 与文档用）。</summary>
        public static readonly Dictionary<string, string> CommandDescriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ai.status"] = "AI 总览：模式、暂停、行为树来源与哈希、活动节点路径、黑板、重载统计",
                ["ai.pause"] = "暂停行为树（保留运行态、释放 AI 输入）",
                ["ai.resume"] = "继续行为树（不重置运行态）",
                ["ai.enable"] = "接管本端角色（允许 AI 产生动作）",
                ["ai.disable"] = "放弃接管（释放全部 AI 输入）",
                ["ai.blackboard"] = "读/写黑板：key=... [value=... type=bool|int|float|string]；all=true 列全部",
                ["ai.tree.list"] = "列出包目录里的行为树包（<实例根>/PlayerAi/BehaviorTrees）",
                ["ai.tree.load"] = "装载/切换活动树的包（name=...或 path=...）",
                ["ai.tree.stop"] = "停止并卸下当前行为树（释放输入；再 switch 就是从头开始）",
                ["ai.tree.reload"] = "强制重载（排队，tick 边界生效；可带 name=...）",
                ["ai.tree.notify"] = "编辑器推送重载通知（path=... hash=...）",
                ["ai.tree.validate"] = "只校验不装载（编辑器保存前预检）",
                ["ai.tree.prepare"] = "预编译进常驻树库（name=... 或 all=true）；切换前先把成本付掉",
                ["ai.tree.switch"] = "毫秒级切换活动树（name=... [entry=...]；优先用常驻副本）",
                ["ai.input.release"] = "立刻释放 AI 注入的所有输入",
                ["ai.record.start"] = "开始录制动作包（录制中行为树停手、人类操作被记录）",
                ["ai.record.pause"] = "录制暂停 ⇄ 继续",
                ["ai.record.stop"] = "结束录制（进入待命名状态，用 ai.record.save 落盘）",
                ["ai.record.save"] = "把录好的动作包写成 <名字>.scatpak（name=... [overwrite=true]）",
                ["ai.record.discard"] = "丢弃待保存的录制",
                ["ai.edit.set"] = "改活树里一个节点的参数（id=… name=… value=… [type=]）——只改内存",
                ["ai.edit.insert"] = "在活树里插入节点（parent=… json={…节点定义} [index=] [id=]）——只改内存",
                ["ai.edit.remove"] = "从活树里摘掉节点（id=… [force=true]）——只改内存",
                ["ai.edit.move"] = "搬移节点（id=… parent=… [index=]）——只改内存",
                ["ai.tree.export"] = "把含运行时改动的活树导出成**新包**（name=…），供编辑器另存",
                ["ai.action.list"] = "列出动作包（时长/帧数/是否可回放）",
                ["ai.action.validate"] = "校验一个动作包（结构 + 能否回放；name=…）",
                ["ai.action.play"] = "直接回放动作包（name=… [repeat=]）—— 给人测试包用",
                ["ai.action.stop"] = "停止回放并释放输入",
                ["ai.tree.snapshot"] = "活动节点快照（路径 + 每个节点状态；P3 编辑器实时监视复用）",
                ["ai.logs"] = "事件日志：count=… 看最近若干条，clear=true 清空",
                ["bt.selftest"] = "跑内核 + 包格式 + 命令层的自检，返回逐条结果"
            };

        /// <summary>命令表构建（注册与自检共用）。</summary>
        public static Dictionary<string, Func<AiCommandRequest, IAiCommandContext, object>> BuildHandlers()
        {
            var handlers = new Dictionary<string, Func<AiCommandRequest, IAiCommandContext, object>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["ai.status"] = Status,
                ["ai.pause"] = Pause,
                ["ai.resume"] = Resume,
                ["ai.enable"] = Enable,
                ["ai.disable"] = Disable,
                ["ai.blackboard"] = Blackboard,
                ["ai.tree.list"] = TreeList,
                ["ai.tree.load"] = TreeLoad,
                ["ai.tree.stop"] = TreeStop,
                ["ai.tree.reload"] = TreeReload,
                ["ai.tree.notify"] = TreeNotify,
                ["ai.tree.validate"] = TreeValidate,
                ["ai.tree.prepare"] = TreePrepare,
                ["ai.tree.switch"] = TreeSwitch,
                ["ai.input.release"] = ReleaseInput,
                ["ai.record.start"] = RecordStart,
                ["ai.record.pause"] = RecordPause,
                ["ai.record.stop"] = RecordStop,
                ["ai.record.save"] = RecordSave,
                ["ai.record.discard"] = RecordDiscard,
                ["ai.edit.set"] = EditSet,
                ["ai.edit.insert"] = EditInsert,
                ["ai.edit.remove"] = EditRemove,
                ["ai.edit.move"] = EditMove,
                ["ai.tree.export"] = TreeExport,
                ["ai.action.list"] = ActionList,
                ["ai.action.validate"] = ActionValidate,
                ["ai.action.play"] = ActionPlay,
                ["ai.action.stop"] = ActionStop,
                ["ai.tree.snapshot"] = TreeSnapshot,
                ["ai.logs"] = Logs,
                ["bt.selftest"] = SelfTest
            };

            // 清单与实现必须一一对应：漏一个就是"命令表里有、实际没人接"
            for (int i = 0; i < CommandNames.Length; i++)
            {
                if (!handlers.ContainsKey(CommandNames[i]))
                    throw new InvalidOperationException("Missing handler for " + CommandNames[i]);
            }
            return handlers;
        }

        public static object Execute(AiCommandRequest request, IAiCommandContext context)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            Func<AiCommandRequest, IAiCommandContext, object> handler;
            if (!BuildHandlers().TryGetValue(request.Command ?? string.Empty, out handler))
            {
                throw new AiCommandException("unknown_command",
                    "Unknown command '" + request.Command + "'.");
            }
            return handler(request, context);
        }

        // ---------------------------------------------------------------- 状态与开关

        private static object Status(AiCommandRequest request, IAiCommandContext context)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["paused"] = context.Paused;
            result["pauseReason"] = context.PauseReason;
            result["inputAvailable"] = context.InputAvailable;

            IAiTreeHost host = context.Host;
            var hostInfo = new Dictionary<string, object>(StringComparer.Ordinal);
            if (host == null)
            {
                hostInfo["name"] = null;
                hostInfo["note"] = "no controllable local player yet (world not loaded or AI disabled)";
                result["host"] = hostInfo;
                result["mode"] = AiMode.Inactive.ToWireName();
            }
            else
            {
                hostInfo["name"] = host.HostName;
                hostInfo["kind"] = host.HostKind;
                hostInfo["enabled"] = host.Enabled;
                hostInfo["ready"] = host.IsReady;
                hostInfo["hasTree"] = host.HasTree;
                // 控制器宿主额外说明"现在在哪一层操作"：世界里 = 玩家输入，主菜单 = 只点 UI
                if (host is ControllerTreeHost controller)
                {
                    hostInfo["situation"] = controller.Situation;
                    hostInfo["player"] = controller.PlayerName;
                }
                result["host"] = hostInfo;
                result["mode"] = host.Mode.ToWireName();

                BtRuntime tree = host.Tree;
                if (tree != null)
                {
                    BtSnapshot snapshot = tree.Snapshot();
                    result["tree"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["id"] = tree.TreeId,
                        ["source"] = tree.SourcePackage,
                        ["hash"] = tree.SourceHash,
                        ["dirty"] = tree.IsDirty,
                        ["liveNodes"] = TreeMutation.Count(tree.Root),
                        ["running"] = tree.IsRunning,
                        ["ticks"] = tree.TickCount,
                        ["time"] = Math.Round(tree.Time, 3),
                        ["lastResult"] = tree.LastResult.ToString(),
                        ["lastError"] = tree.LastError,
                        ["migrations"] = tree.MigrationCount,
                        ["lastMigration"] = tree.LastMigration != null ? tree.LastMigration.Describe() : null,
                        ["activePath"] = snapshot.DescribeActivePath(),
                        ["nodesVisited"] = tree.LastNodesVisited,
                        ["aborts"] = tree.AbortCount,
                        ["loops"] = tree.CompletedLoops
                    };
                }

                var blackboard = new Dictionary<string, object>(StringComparer.Ordinal);
                AiBlackboard board = host.Blackboard;
                if (board != null)
                {
                    blackboard["count"] = board.Count;
                    blackboard["revision"] = board.Revision;
                    blackboard["keys"] = new List<string>(board.Keys);
                }
                result["blackboard"] = blackboard;
            }

            result["machine"] = MachineSummary(host);
            result["recording"] = RecordingSummary(context);
            result["packages"] = PackageSummary(context.Reloader);
            result["library"] = LibrarySummary(context.Library);
            result["logs"] = LogSummary(context.EventLog, 0);
            result["action"] = context.ActionPlayer != null
                ? context.ActionPlayer.Status()
                : new Dictionary<string, object>(StringComparer.Ordinal) { ["available"] = false };
            result["reloads"] = ReloadSummary(context.Reloader);
            return result;
        }

        private static Dictionary<string, object> MachineSummary(IAiTreeHost host)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (host == null)
                return info;

            info["id"] = host.MachineStateId;
            info["previous"] = host.MachinePreviousStateId;
            info["timeInState"] = Math.Round(host.MachineTimeInState, 3);
            info["transitions"] = host.MachineTransitionCount;
            info["faulted"] = host.MachineFaulted;
            return info;
        }

        private static Dictionary<string, object> RecordingSummary(IAiCommandContext context)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            IAiRecordingControl recording = context.Recording;
            if (recording == null)
            {
                info["available"] = false;
                return info;
            }

            AiRecordingStatus status = recording.Status;
            info["available"] = true;
            info["phase"] = status.ToWireName();
            info["duration"] = Math.Round(status.Duration, 3);
            info["frames"] = status.Frames;
            info["name"] = status.Name;
            info["suggestedName"] = status.SuggestedName;
            info["lastSavedPath"] = status.LastSavedPath;
            info["message"] = status.Message;
            return info;
        }

        // ---------------------------------------------------------------- 录制命令

        private static object RecordStart(AiCommandRequest request, IAiCommandContext context)
        {
            IAiRecordingControl recording = RequireRecording(context, request.Command);
            string result = recording.Start(request.GetString("name", null));
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["recording"] = DescribeRecording(recording)
            };
        }

        private static object RecordPause(AiCommandRequest request, IAiCommandContext context)
        {
            IAiRecordingControl recording = RequireRecording(context, request.Command);
            string result = recording.TogglePause();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["recording"] = DescribeRecording(recording)
            };
        }

        private static object RecordStop(AiCommandRequest request, IAiCommandContext context)
        {
            IAiRecordingControl recording = RequireRecording(context, request.Command);
            string name = request.GetString("name", null);
            string result = recording.Stop();

            var response = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["recording"] = DescribeRecording(recording)
            };

            // 带名字就顺手存了（脚本/AI 一次调用完成；不带名字则留在待命名，由人来命名）
            if (!string.IsNullOrEmpty(name))
            {
                response["saved"] = true;
                response["path"] = recording.Save(name, request.GetBoolean("overwrite", false));
            }
            return response;
        }

        private static object RecordSave(AiCommandRequest request, IAiCommandContext context)
        {
            IAiRecordingControl recording = RequireRecording(context, request.Command);
            string name = request.RequireString("name");
            string path = recording.Save(name, request.GetBoolean("overwrite", false));
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["recording"] = DescribeRecording(recording)
            };
        }

        private static object RecordDiscard(AiCommandRequest request, IAiCommandContext context)
        {
            IAiRecordingControl recording = RequireRecording(context, request.Command);
            string result = recording.Discard();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["recording"] = DescribeRecording(recording)
            };
        }

        private static Dictionary<string, object> DescribeRecording(IAiRecordingControl recording)
        {
            AiRecordingStatus status = recording.Status;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["phase"] = status.ToWireName(),
                ["duration"] = Math.Round(status.Duration, 3),
                ["frames"] = status.Frames,
                ["name"] = status.Name,
                ["suggestedName"] = status.SuggestedName,
                ["lastSavedPath"] = status.LastSavedPath,
                ["message"] = status.Message
            };
        }

        private static IAiRecordingControl RequireRecording(IAiCommandContext context, string command)
        {
            if (context.Recording == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs the recording session (PlayerAiMod runtime not ready)");
            }
            return context.Recording;
        }

        private static Dictionary<string, object> PackageSummary(PackageReloader reloader)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (reloader == null)
            {
                info["available"] = false;
                return info;
            }

            info["available"] = true;
            info["folders"] = reloader.Roots != null ? reloader.Roots.Describe() : "<none>";
            info["active"] = reloader.ActivePath;
            info["activeHash"] = reloader.ActiveHash;
            info["isLoaded"] = reloader.IsLoaded;
            info["loadedPackages"] = reloader.LoadedPackageCount;
            if (reloader.ActiveSet != null && reloader.ActiveSet.Root != null)
            {
                var packages = new List<Dictionary<string, object>>();
                for (int i = 0; i < reloader.ActiveSet.Packages.Count; i++)
                {
                    LoadedPackage package = reloader.ActiveSet.Packages[i];
                    packages.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["file"] = package.FileName,
                        ["id"] = package.PackageId,
                        ["hash"] = ShortHash(package.Hash),
                        ["nodes"] = package.Tree != null ? package.Tree.NodeCount : 0,
                        ["errors"] = package.Report.ErrorCount,
                        ["warnings"] = package.Report.WarningCount
                    });
                }
                info["packages"] = packages;
            }
            return info;
        }

        private static Dictionary<string, object> ReloadSummary(PackageReloader reloader)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (reloader == null)
                return info;

            info["count"] = reloader.ReloadCount;
            info["rejected"] = reloader.RejectedCount;
            info["ignored"] = reloader.IgnoredCount;
            info["pending"] = reloader.PendingCount;
            info["watchSeconds"] = reloader.PackageWatchSeconds;
            TreeReloadResult last = reloader.LastResult;
            if (last != null)
            {
                info["lastResult"] = last.Describe();
                info["lastIssues"] = new List<string>(last.Issues);
            }
            return info;
        }

        private static object Pause(AiCommandRequest request, IAiCommandContext context)
        {
            context.Pause("command:" + request.Command);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["paused"] = context.Paused,
                ["mode"] = context.Host != null ? context.Host.Mode.ToWireName() : "inactive"
            };
        }

        private static object Resume(AiCommandRequest request, IAiCommandContext context)
        {
            context.Resume("command:" + request.Command);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["paused"] = context.Paused,
                ["mode"] = context.Host != null ? context.Host.Mode.ToWireName() : "inactive"
            };
        }

        private static object Enable(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            host.Enabled = true;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = host.Enabled,
                ["mode"] = host.Mode.ToWireName()
            };
        }

        private static object Disable(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            host.Enabled = false;
            host.ReleaseInput();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = host.Enabled,
                ["mode"] = host.Mode.ToWireName()
            };
        }

        private static object ReleaseInput(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            host.ReleaseInput();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["released"] = true,
                ["mode"] = host.Mode.ToWireName()
            };
        }

        // ---------------------------------------------------------------- 黑板

        private static object Blackboard(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            AiBlackboard board = host.Blackboard;
            if (board == null)
                throw new AiCommandException("not_ready", "The host has no blackboard.");

            string key = request.GetString("key", null);
            if (request.GetBoolean("all", false) || string.IsNullOrEmpty(key))
            {
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string name in board.Keys)
                {
                    object value;
                    values[name] = TryReadValue(board, name, out value) ? Describe(value) : "<unknown type>";
                }
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["count"] = board.Count,
                    ["revision"] = board.Revision,
                    ["values"] = values
                };
            }

            if (!request.Has("value"))
            {
                object value;
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["key"] = key,
                    ["found"] = TryReadValue(board, key, out value),
                    ["value"] = TryReadValue(board, key, out value) ? Describe(value) : null
                };
            }

            string kind = request.GetString("type", "float").Trim().ToLowerInvariant();
            switch (kind)
            {
                case "bool":
                    board.Set(new AiBlackboardKey<bool>(key), request.GetBoolean("value", false));
                    break;
                case "int":
                    board.Set(new AiBlackboardKey<int>(key), request.GetInteger("value", 0));
                    break;
                case "string":
                    board.Set(new AiBlackboardKey<string>(key), request.GetString("value", string.Empty));
                    break;
                case "float":
                    board.Set(new AiBlackboardKey<float>(key), request.GetFloat("value", 0f));
                    break;
                default:
                    throw new AiCommandException("invalid_argument",
                        "type must be bool|int|float|string, found '" + kind + "'.");
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["key"] = key,
                ["type"] = kind,
                ["written"] = true,
                ["revision"] = board.Revision
            };
        }

        private static bool TryReadValue(AiBlackboard board, string key, out object value)
        {
            bool boolValue;
            if (board.TryGet(key, out boolValue))
            {
                value = boolValue;
                return true;
            }
            int intValue;
            if (board.TryGet(new AiBlackboardKey<int>(key), out intValue))
            {
                value = intValue;
                return true;
            }
            float floatValue;
            if (board.TryGet(key, out floatValue))
            {
                value = floatValue;
                return true;
            }
            string stringValue;
            if (board.TryGet(new AiBlackboardKey<string>(key), out stringValue))
            {
                value = stringValue;
                return true;
            }
            AiActorView actorView;
            if (board.TryGet(key, out actorView))
            {
                value = actorView;
                return true;
            }
            value = null;
            return false;
        }

        private static object Describe(object value)
        {
            if (value == null)
                return null;
            if (value is AiActorView)
                return value.ToString();
            return value;
        }

        // ---------------------------------------------------------------- 行为树包

        private static Dictionary<string, object> LibrarySummary(TreeLibrary library)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (library == null)
            {
                info["available"] = false;
                return info;
            }

            info["available"] = true;
            info["capacity"] = library.Capacity;
            info["prepares"] = library.PrepareCount;
            info["switches"] = library.SwitchCount;
            info["staleRecompiles"] = library.StaleRecompiles;
            info["active"] = library.Active != null ? library.Active.Key : null;
            info["prepared"] = library.DescribePrepared();
            TreeSwitchResult last = library.LastSwitch;
            if (last != null)
                info["lastSwitch"] = last.Describe();
            return info;
        }

        /// <summary>预编译：把"读包 + 校验 + 编译"的成本从切换时刻挪到这里。</summary>
        private static object TreePrepare(AiCommandRequest request, IAiCommandContext context)
        {
            TreeLibrary library = RequireLibrary(context, request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);

            if (request.GetBoolean("all", false))
            {
                var report = new PackageReport();
                List<PreparedTree> prepared = library.PrepareAll(
                    request.GetInteger("maximum", 0), report);
                var entries = new List<Dictionary<string, object>>();
                for (int i = 0; i < prepared.Count; i++)
                {
                    entries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = prepared[i].Key,
                        ["ready"] = prepared[i].Ready,
                        ["nodes"] = prepared[i].NodeCount,
                        ["compileMs"] = Math.Round(prepared[i].CompileMilliseconds, 2)
                    });
                }
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["prepared"] = entries,
                    ["count"] = entries.Count,
                    ["errors"] = report.ErrorCount,
                    ["issues"] = report.Summarize(8),
                    ["library"] = LibrarySummary(library)
                };
            }

            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' or all=true is required.");

            var single = new PackageReport();
            PreparedTree tree = library.Prepare(name, request.GetString("entry", null), single);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = tree.Key,
                ["path"] = tree.Path,
                ["ready"] = tree.Ready,
                ["nodes"] = tree.NodeCount,
                ["hash"] = ShortHash(tree.Hash),
                ["compileMs"] = Math.Round(tree.CompileMilliseconds, 2),
                ["errors"] = single.ErrorCount,
                ["issues"] = single.Summarize(8),
                ["library"] = LibrarySummary(library)
            };
        }

        /// <summary>
        /// 毫秒级切换：优先用常驻副本（`switch=…ms` 就是纯切换耗时，不含编译）。
        /// 副本过期（文件被改过）才重新编译 —— 那部分耗时单独报在 `compileMs` 里。
        /// </summary>
        private static object TreeSwitch(AiCommandRequest request, IAiCommandContext context)
        {
            TreeLibrary library = RequireLibrary(context, request.Command);
            IAiTreeHost host = RequireHost(context, request.Command);

            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' or 'path' is required.");

            TreeSwitchResult result = library.Switch(name, host, request.GetString("entry", null),
                request.GetBoolean("start", true));
            // 切换成功 = 接管（与 `ai.tree.load` 一致）：编辑器点"播放"的语义就是"跑起来"。
            if (result.Switched)
                host.Enabled = true;

            var response = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["switched"] = result.Switched,
                ["usedPrepared"] = result.UsedPrepared,
                ["recompiled"] = result.Recompiled,
                ["reason"] = result.Reason,
                ["path"] = result.Path,
                ["hash"] = ShortHash(result.Hash),
                ["nodes"] = result.NodeCount,
                ["switchMs"] = Math.Round(result.SwitchMilliseconds, 3),
                ["totalMs"] = Math.Round(result.TotalMilliseconds, 3),
                ["compileMs"] = Math.Round(result.CompileMilliseconds, 3),
                ["migration"] = result.Migration != null ? result.Migration.Describe() : null,
                ["mode"] = host.Mode.ToWireName(),
                ["issues"] = result.Issues
            };
            return response;
        }

        // ---------------------------------------------------------------- 内存态改写（P0-12）

        /// <summary>改一个节点的参数。**只改内存**：原包字节不变（验收标准 7）。</summary>
        private static object EditSet(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            string id = request.RequireString("id");
            string name = request.RequireString("name");

            PackageValue value = ParseTypedValue(request, out string kind);
            TreeEditResult result = TreeMutation.SetProperty(host.Tree, id, name, value);

            return EditResponse(result, context);
        }

        /// <summary>插入节点：`json={…}` 给完整定义（与包格式同一套校验），或 `type=…` 给最小定义。</summary>
        private static object EditInsert(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            string parentId = request.RequireString("parent");
            int index = request.GetInteger("index", -1);

            var report = new PackageReport();
            ScbtPackageSet set = context.Reloader != null ? context.Reloader.ActiveSet : null;
            LoadedPackage owner = set != null ? set.Root : null;

            ScbtTree tree = null;
            string json = request.GetString("json", null);
            if (!string.IsNullOrEmpty(json))
            {
                PackageValue value;
                if (!PackageJson.TryParse(json, "edit.insert", report, out value))
                    throw new AiCommandException("invalid_argument", "json= is not valid JSON");

                tree = ScbtTree.Parse(value, "edit.insert", report);
            }
            else
            {
                string type = request.GetString("type", null);
                if (string.IsNullOrEmpty(type))
                {
                    throw new AiCommandException("invalid_argument",
                        "either json= or type= is required for ai.edit.insert");
                }

                PackageValue node = PackageValue.Object();
                node.Set("type", PackageValue.Str(type));
                string nodeId = request.GetString("id", null);
                if (!string.IsNullOrEmpty(nodeId))
                    node.Set("id", PackageValue.Str(nodeId));
                PackageValue properties;
                if (request.Arguments.TryGetValue("properties", out object raw) && raw is string text
                    && !string.IsNullOrEmpty(text))
                {
                    if (!PackageJson.TryParse(text, "edit.insert", report, out properties))
                        throw new AiCommandException("invalid_argument", "properties= is not valid JSON");
                    node.Set("properties", properties);
                }
                tree = ScbtTree.Parse(node, "edit.insert", report);
            }

            if (tree == null || tree.Root == null || report.HasErrors)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["applied"] = false,
                    ["reason"] = "node definition is not valid",
                    ["issues"] = report.Summarize(8)
                };
            }

            TreeEditResult result = TreeMutation.Insert(host.Tree, parentId, index, tree.Root, owner,
                report);
            return EditResponse(result, context);
        }

        /// <summary>摘掉节点（先给子树正常收尾）。</summary>
        private static object EditRemove(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            string id = request.RequireString("id");
            TreeEditResult result = TreeMutation.Remove(host.Tree, id,
                request.GetBoolean("force", false));
            return EditResponse(result, context);
        }

        /// <summary>搬移/换分支。</summary>
        private static object EditMove(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            string id = request.RequireString("id");
            string parentId = request.RequireString("parent");
            TreeEditResult result = TreeMutation.Move(host.Tree, id, parentId,
                request.GetInteger("index", -1));
            return EditResponse(result, context);
        }

        private static Dictionary<string, object> EditResponse(TreeEditResult result,
            IAiCommandContext context)
        {
            if (context.EventLog != null)
                context.EventLog.Write(result.Applied ? "edit" : "edit-reject", result.Describe());

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["applied"] = result.Applied,
                ["kind"] = result.Kind.ToString().ToLowerInvariant(),
                ["node"] = result.NodeId,
                ["reason"] = result.Reason,
                ["detail"] = result.Detail,
                ["nodes"] = result.NodeCount,
                ["dirty"] = context.Host != null && context.Host.Tree != null
                    && context.Host.Tree.IsDirty,
                ["issues"] = result.Issues,
                ["note"] = "in-memory change only; the original .scbtpak is untouched "
                    + "(use ai.tree.reload to discard, ai.tree.export to save as a new package)"
            };
        }

        /// <summary>把 `value=` 按 `type=` 解析成带类型的 JSON 值（默认 float）。</summary>
        private static PackageValue ParseTypedValue(AiCommandRequest request, out string kind)
        {
            kind = (request.GetString("type", null) ?? request.GetString("valueKind", null)
                ?? "float").Trim().ToLowerInvariant();
            string text = request.GetString("value", null);

            switch (kind)
            {
                case "bool":
                    return PackageValue.Bool(request.GetBoolean("value", false));
                case "int":
                    return PackageValue.Number(request.GetInteger("value", 0));
                case "string":
                case "enum":
                    kind = "string";
                    return PackageValue.Str(text ?? string.Empty);
                case "float":
                    return PackageValue.Number(request.GetFloat("value", 0f));
                default:
                    throw new AiCommandException("invalid_argument",
                        "type must be float|int|bool|string, found '" + kind + "'");
            }
        }

        // ---------------------------------------------------------------- 导出（P0-13）

        /// <summary>
        /// 把**内存里的活树**（含 ai.edit.* 的改动）导出成新包。
        /// 只写新文件到实例包目录，绝不覆盖来源包 —— 这是"AI 不落盘"与"人要保存"的交界处。
        /// </summary>
        private static object TreeExport(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            IAiTreeHost host = RequireHost(context, request.Command);

            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("file", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required (the new package)");

            string directory = reloader.Roots != null && reloader.Roots.InstanceRoot != null
                ? reloader.Roots.InstanceRoot.Path : null;
            if (string.IsNullOrEmpty(directory))
            {
                throw new AiCommandException("not_ready",
                    "no writable package folder (PlayerAi/BehaviorTrees)");
            }

            try
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                throw new AiCommandException("io_error", "cannot create " + directory + ": "
                    + exception.Message);
            }

            string safeName = AiRecordingSession.SanitizeName(name);
            string expected = System.IO.Path.Combine(directory,
                safeName + PackageRoots.Extension);
            if (System.IO.File.Exists(expected) && !request.GetBoolean("overwrite", false))
            {
                throw new AiCommandException("already_exists",
                    "'" + safeName + PackageRoots.Extension
                    + "' already exists; pass overwrite=true or choose another name");
            }

            ScbtManifest source = reloader.ActiveSet != null && reloader.ActiveSet.Root != null
                ? reloader.ActiveSet.Root.Manifest : null;

            string path;
            string error;
            int nodes;
            bool ok = TreeWriter.TryExportPackage(host.Tree, source, name, directory, out path,
                out error, out nodes);
            if (!ok)
                throw new AiCommandException("io_error", error ?? "export failed");

            if (context.EventLog != null)
            {
                context.EventLog.Write("export", "wrote " + path + " (" + nodes + " nodes) from "
                    + (path != null ? reloader.ActivePath : "<none>"));
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["exported"] = true,
                ["path"] = path,
                ["nodes"] = nodes,
                ["entry"] = host.Tree.Root != null ? host.Tree.Root.Id : null,
                ["dirty"] = host.Tree.IsDirty,
                ["source"] = reloader.ActivePath,
                ["note"] = "the original package was not modified; the export is a new file"
            };
        }

        // ---------------------------------------------------------------- 观测（P0-10）

        /// <summary>
        /// 活动节点快照：活动路径上每个节点的 id/类型/名字/结果/驻留时间/激活次数。
        /// P3 的编辑器"看着 AI 跑"直接消费这份结构（也是 `ai.status.tree.activePath` 的详细版）。
        /// </summary>
        private static object TreeSnapshot(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            BtRuntime tree = host.Tree;
            if (tree == null)
                throw new AiCommandException("not_ready", "the host has no behaviour tree runtime");

            BtSnapshot snapshot = tree.Snapshot();
            var active = new List<Dictionary<string, object>>();
            for (int i = 0; i < snapshot.ActivePath.Count; i++)
            {
                BtNodeSnapshot node = snapshot.ActivePath[i];
                active.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = node.Id,
                    ["type"] = node.NodeType,
                    ["name"] = node.Name,
                    ["active"] = node.IsActive,
                    ["result"] = node.LastResult.ToString(),
                    ["activeTime"] = Math.Round(node.ActiveTime, 3),
                    ["activations"] = node.ActivationCount
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["treeId"] = snapshot.TreeId,
                ["source"] = snapshot.SourcePackage,
                ["hash"] = ShortHash(snapshot.SourceHash),
                ["running"] = snapshot.IsRunning,
                ["dirty"] = snapshot.IsDirty,
                ["lastResult"] = snapshot.LastResult.ToString(),
                ["ticks"] = snapshot.TickCount,
                ["time"] = Math.Round(snapshot.Time, 3),
                ["nodesVisited"] = snapshot.LastNodesVisited,
                ["aborts"] = snapshot.AbortCount,
                ["loops"] = snapshot.CompletedLoops,
                ["lastError"] = snapshot.LastError,
                ["activePath"] = snapshot.DescribeActivePath(),
                ["path"] = active,
                ["nodes"] = TreeMutation.Count(tree.Root),
                ["blackboard"] = BlackboardSummary(host)
            };
        }

        private static Dictionary<string, object> BlackboardSummary(IAiTreeHost host)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            AiBlackboard board = host.Blackboard;
            if (board == null)
                return info;

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in board.Keys)
            {
                object value;
                values[name] = TryReadValue(board, name, out value) ? Describe(value) : "<unknown type>";
            }
            info["count"] = board.Count;
            info["revision"] = board.Revision;
            info["values"] = values;
            return info;
        }

        private static Dictionary<string, object> LogSummary(AiEventLog log, int count)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            if (log == null)
            {
                info["available"] = false;
                return info;
            }

            info["available"] = true;
            info["path"] = log.Path;
            info["enabled"] = log.Enabled;
            info["bytes"] = log.FileBytes;
            info["maxBytes"] = log.MaxBytes;
            info["maxFiles"] = log.MaxFiles;
            info["entries"] = log.WriteCount;
            info["lastError"] = log.LastError;
            if (count > 0)
                info["recent"] = log.Recent(count);
            return info;
        }

        /// <summary>看事件日志（`ai.logs [count=20] [clear=true]`）。</summary>
        private static object Logs(AiCommandRequest request, IAiCommandContext context)
        {
            AiEventLog log = context.EventLog;
            if (log == null)
                throw new AiCommandException("not_ready", "no event log is configured");

            if (request.GetBoolean("clear", false))
            {
                string clearError;
                if (!log.TryClear(out clearError))
                    throw new AiCommandException("io_error", "cannot clear the log: " + clearError);
                log.Write("logs", "cleared by " + request.Command);
            }

            int count = request.GetInteger("count", 20);
            if (count < 0)
                count = 0;

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = log.Path,
                ["enabled"] = log.Enabled,
                ["bytes"] = log.FileBytes,
                ["entries"] = log.WriteCount,
                ["lastError"] = log.LastError,
                ["recent"] = log.Recent(count)
            };
        }

        // ---------------------------------------------------------------- 动作包（P1）

        /// <summary>列出动作包（只有一个包目录：<c>&lt;实例根&gt;/PlayerAi/BehaviorTrees</c>）。</summary>
        private static object ActionList(AiCommandRequest request, IAiCommandContext context)
        {
            IAiActionPlayer player = RequireActionPlayer(context, request.Command);
            List<string> directories = ActionDirectories(player);
            var entries = new List<Dictionary<string, object>>();

            int replayable = 0;
            for (int i = 0; i < directories.Count; i++)
            {
                List<Dictionary<string, object>> found = ScatLibrary.Describe(directories[i]);
                for (int j = 0; j < found.Count; j++)
                {
                    Dictionary<string, object> entry = found[j];
                    entry["folder"] = directories[i];
                    entry["source"] = "instance";
                    entry["writable"] = true;

                    if (Equals(entry["replayable"], true))
                        replayable++;
                    entries.Add(entry);
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["directory"] = player.ActionDirectory,
                ["directories"] = directories,
                ["count"] = entries.Count,
                ["replayable"] = replayable,
                ["actions"] = entries
            };
        }

        /// <summary>校验一个动作包：结构问题逐条报，并明确"能不能回放"。</summary>
        private static object ActionValidate(AiCommandRequest request, IAiCommandContext context)
        {
            IAiActionPlayer player = RequireActionPlayer(context, request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("file", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required.");

            string folder;
            string path = ScatLibrary.ResolveIn(ActionDirectories(player), name, out folder);
            if (path == null)
                return ScatLibrary.Validate(player.ActionDirectory, name); // 统一的"找不到"回包形状

            Dictionary<string, object> result = ScatLibrary.Validate(folder, path);
            result["folder"] = folder;
            result["source"] = "instance";
            return result;
        }

        /// <summary>直接回放（不经行为树）：给人验证"录下来的动作能不能重演"。</summary>
        private static object ActionPlay(AiCommandRequest request, IAiCommandContext context)
        {
            IAiActionPlayer player = RequireActionPlayer(context, request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required.");

            int repeat = request.GetInteger("repeat", 1);
            string result = player.Play(name, repeat);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["status"] = player.Status()
            };
        }

        private static object ActionStop(AiCommandRequest request, IAiCommandContext context)
        {
            IAiActionPlayer player = RequireActionPlayer(context, request.Command);
            string result = player.Stop();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["status"] = player.Status()
            };
        }

        private static IAiActionPlayer RequireActionPlayer(IAiCommandContext context, string command)
        {
            if (context.ActionPlayer == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs the action-package player (runtime not ready)");
            }
            return context.ActionPlayer;
        }

        /// <summary>动作包查找链（主目录在前）；实现没给链时退回主目录，保证老实现照常工作。</summary>
        private static List<string> ActionDirectories(IAiActionPlayer player)
        {
            var directories = new List<string>();
            if (player.ActionSearchDirectories != null)
                directories.AddRange(player.ActionSearchDirectories);
            if (directories.Count == 0 && !string.IsNullOrEmpty(player.ActionDirectory))
                directories.Add(player.ActionDirectory);
            return directories;
        }

        private static TreeLibrary RequireLibrary(IAiCommandContext context, string command)
        {
            if (context.Library == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs the tree library (package folders are not ready)");
            }
            return context.Library;
        }

        private static object TreeList(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            PackageRoots roots = reloader.Roots;
            var files = new List<Dictionary<string, object>>();

            if (request.GetBoolean("refresh", true) && roots != null)
            {
                List<string> found = roots.ListFiles();
                for (int i = 0; i < found.Count; i++)
                {
                    PackageRoot owner = roots.OwnerOf(found[i]);
                    var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = System.IO.Path.GetFileNameWithoutExtension(found[i]),
                        ["file"] = System.IO.Path.GetFileName(found[i]),
                        ["path"] = found[i],
                        ["source"] = owner != null ? owner.Kind : "unknown",
                        ["writable"] = owner != null && owner.Writable
                    };
                    try
                    {
                        var info = new System.IO.FileInfo(found[i]);
                        entry["bytes"] = info.Length;
                        entry["modifiedUtc"] = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                    }
                    catch (Exception)
                    {
                        // 读不到元信息不影响列出清单
                    }
                    entry["active"] = reloader.ActivePath != null
                        && string.Equals(reloader.ActivePath, PackageRoots.NormalizePath(found[i]),
                            PackageRoots.PathComparison);
                    files.Add(entry);
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["folders"] = roots != null ? roots.Describe() : "<none>",
                ["active"] = reloader.ActivePath,
                ["count"] = files.Count,
                ["packages"] = files,
                ["prepared"] = context.Library != null ? context.Library.DescribePrepared() : null
            };
        }

        /// <summary>
        /// 停止并**卸下**当前行为树（`ai.tree.stop`）：释放输入、清空树标识，**并取消全局暂停**。
        ///
        /// 与"暂停"的区别就是用户要的那个区别：暂停保留运行态（继续 = 从原处接着跑），
        /// 停止是**回到什么都没有**的状态 —— 之后再 `ai.tree.switch` 就是干净地从根开始
        /// （树库切换前本来就会 `ResetSubtreeState`）。
        ///
        /// 为什么要顺手取消暂停：暂停是**全局**的（`PlayerAiRuntime.Paused`，帧首先看它），
        /// 停了树却留着暂停，下一次"播放"就只是把树装回去、却永远不会被 tick ——
        /// 用户实测就是"播放→暂停→停止→再播放，显示的还是已暂停、树不执行"。
        /// </summary>
        private static object TreeStop(AiCommandRequest request, IAiCommandContext context)
        {
            IAiTreeHost host = RequireHost(context, request.Command);
            bool hadTree = host.HasTree;
            string reason = request.GetString("reason", null);
            if (hadTree)
                host.StopTree(string.IsNullOrEmpty(reason) ? "stopped by user (ai.tree.stop)" : reason);

            bool wasPaused = context.Paused;
            if (wasPaused)
                context.Resume("command:" + request.Command + " (stop resets everything)");

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["stopped"] = hadTree,
                ["resumed"] = wasPaused,
                ["reason"] = hadTree ? null : "there was no tree to stop",
                ["host"] = host.HostKind,
                ["mode"] = host.Mode.ToWireName(),
                ["hasTree"] = host.HasTree,
                ["paused"] = context.Paused
            };
        }

        private static object TreeLoad(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            IAiTreeHost host = RequireHost(context, request.Command);

            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' or 'path' is required.");

            bool start = request.GetBoolean("start", true);
            TreeReloadResult result = reloader.Load(name, host.Tree, false, ReloadTrigger.Switch);
            if (result.Replaced && start && !host.Tree.IsRunning)
                host.Tree.Start();
            if (result.Replaced)
            {
                // 装载成功 = 接管：AI 现在可以产生动作（HasTree 由运行时的树标识推导）
                host.Enabled = true;
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["replaced"] = result.Replaced,
                ["rejected"] = result.Rejected,
                ["reason"] = result.Reason,
                ["path"] = result.Path,
                ["hash"] = ShortHash(result.Hash),
                ["nodes"] = result.NodeCount,
                ["packages"] = result.PackageCount,
                ["milliseconds"] = Math.Round(result.Milliseconds, 2),
                ["mode"] = host.Mode.ToWireName(),
                ["issues"] = new List<string>(result.Issues)
            };
        }

        private static object TreeReload(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                name = reloader.ActivePath;

            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("not_ready", "No active tree to reload; pass name=...");

            reloader.RequestReload(name);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["queued"] = true,
                ["target"] = name,
                ["pending"] = reloader.PendingCount,
                ["note"] = "applied at the next tick boundary (frame start); check ai.status"
            };
        }

        private static object TreeNotify(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            string path = request.GetString("path", null);
            if (string.IsNullOrEmpty(path))
                path = request.GetString("name", null);
            if (string.IsNullOrEmpty(path))
                throw new AiCommandException("invalid_argument", "'path' is required.");

            string hash = request.GetString("hash", null);
            reloader.Notify(path, hash);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["queued"] = true,
                ["path"] = path,
                ["hash"] = ShortHash(hash),
                ["pending"] = reloader.PendingCount,
                ["note"] = "applied at the next tick boundary; hash mismatch is ignored (writer still writing?)"
            };
        }

        private static object TreeValidate(AiCommandRequest request, IAiCommandContext context)
        {
            PackageReloader reloader = RequireReloader(context, request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                name = reloader.ActivePath;
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' or 'path' is required.");

            PackageReport report = reloader.Validate(name);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = name,
                ["ok"] = !report.HasErrors,
                ["errors"] = report.ErrorCount,
                ["warnings"] = report.WarningCount,
                ["issues"] = report.Summarize(32)
            };
        }

        // ---------------------------------------------------------------- 自检

        /// <summary>防重入：`bt.selftest` 内部若再触发一次会无限递归（栈溢出）。</summary>
        [ThreadStatic]
        private static bool s_selfTestRunning;

        private static object SelfTest(AiCommandRequest request, IAiCommandContext context)
        {
            if (s_selfTestRunning)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["alreadyRunning"] = true,
                    ["note"] = "a self-test is already running on this thread; ignoring the nested call"
                };
            }

            s_selfTestRunning = true;
            try
            {
                Dictionary<string, object> selfTest = RunSelfTests(BtSelfTest.Run(),
                    PackageSelfTest.Run(), AiCommandSelfTest.Run(), AiModeSelfTest.Run(),
                    AiRecordingSelfTest.Run(), TreeEditSelfTest.Run());
                if (context.EventLog != null)
                {
                    context.EventLog.Write("selftest", "passed=" + selfTest["passed"]
                        + " failed=" + selfTest["failed"]);
                }
                return selfTest;
            }
            finally
            {
                s_selfTestRunning = false;
            }
        }

        /// <summary>
        /// 汇总三套自检的结果（抽出来是为了让"汇总逻辑"本身也能被自检：喂三份合成结果即可，
        /// 不必真的再跑一遍套件，也就不会自递归）。
        /// </summary>
        public static Dictionary<string, object> RunSelfTests(params BtSelfTest.TestResult[] suites)
        {
            int passed = 0;
            int failed = 0;
            var failures = new List<string>();
            var tallies = new Dictionary<string, object>(StringComparer.Ordinal);

            if (suites != null)
            {
                for (int i = 0; i < suites.Length; i++)
                {
                    BtSelfTest.TestResult suite = suites[i];
                    if (suite == null)
                        continue;

                    passed += suite.Passed;
                    failed += suite.Failed;
                    Collect(suite, failures);
                    tallies[suite.Label] = suite.Passed + "/" + (suite.Passed + suite.Failed);
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["allPassed"] = failed == 0,
                ["passed"] = passed,
                ["failed"] = failed,
                ["suites"] = tallies,
                ["failures"] = failures
            };
        }

        private static void Collect(BtSelfTest.TestResult result, List<string> into)
        {
            for (int i = 0; i < result.Lines.Count; i++)
            {
                string line = result.Lines[i];
                if (line.StartsWith("FAIL", StringComparison.Ordinal))
                    into.Add(result.Label + ": " + line);
            }
        }

        // ---------------------------------------------------------------- 小工具

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return null;
            return hash.Length > 12 ? hash.Substring(0, 12) : hash;
        }

        private static IAiTreeHost RequireHost(IAiCommandContext context, string command)
        {
            if (context.Host == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs a controllable local player (load a world and make sure AI is enabled).");
            }
            return context.Host;
        }

        private static PackageReloader RequireReloader(IAiCommandContext context, string command)
        {
            if (context.Reloader == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs the package folders (see PlayerAi/BehaviorTrees).");
            }
            return context.Reloader;
        }
    }
}
