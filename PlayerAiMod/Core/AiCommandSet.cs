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
            "ai.hud",
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
            "ai.action.script.list",
            "ai.action.script.validate",
            "ai.action.script.run",
            "ai.action.script.stop",
            "ai.action.script.status",
            "ai.asset.status",
            "ai.asset.list",
            "ai.asset.load",
            "ai.asset.save",
            "ai.asset.drop",
            "ai.asset.autosave",
            "ai.asset.restore",
            "ai.asset.retry",
            "ai.asset.conflict",
            "ai.pool.on",
            "ai.pool.off",
            "ai.pool.status",
            "ai.pool.bind",
            "ai.laya.status",
            "ai.laya.review",
            "ai.laya.ask",
            "state.digest",
            "ai.action.script.verbs",
            "ai.tree.snapshot",
            "ai.logs",
            "bt.selftest"
        };

        /// <summary>命令说明（`cmd.list` 与文档用）。</summary>
        public static readonly Dictionary<string, string> CommandDescriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ai.status"] = "AI 总览：模式、暂停、行为树来源与哈希、活动节点路径、黑板、重载统计",
            ["ai.hud"] = "屏幕左上角那行状态（on=true|false 开关；不传参数只查询）",
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
                ["ai.action.script.list"] = "列出动作脚本（.aeact：步骤数、verb 序列、解析状态、哈希）",
                ["ai.action.script.validate"] = "只校验一条动作脚本（name=…）",
                ["ai.action.script.run"] = "直接跑一条动作脚本（name=… [repeat=] [totalTimeoutMs=]）",
                ["ai.action.script.stop"] = "停止脚本播放并释放输入",
                ["ai.action.script.status"] = "动作脚本服务的现状（宿主/执行器/脚本目录/正在跑的脚本）",
                ["ai.asset.status"] = "资源库现状（内存库/自动缓存/活动资源）；cacheDrop=true 清掉缓存",
                ["ai.asset.list"] = "三态视图：磁盘版 / 内存版（dirty）/ 缓存版（哪个更新、为什么）",
                ["ai.asset.load"] = "把磁盘上的包读进内存库（name=…）——读进来才算加载",
                ["ai.asset.save"] = "把内存版存成正式包（name=… [dir=… 默认 PlayerAi/Saves] [overwrite=true 会先备份上一代]）",
                ["ai.asset.drop"] = "丢弃内存改动，回到最近载入的磁盘版（活树一起退回）",
                ["ai.asset.autosave"] = "立刻把内存版写进自动缓存（.autosave 影子副本）",
                ["ai.asset.restore"] = "按恢复决议把缓存/磁盘版装回内存库（[name=…] [all=true]）",
                ["ai.asset.retry"] = "拒包/重取现状（四码分流/退避/熔断）；可 enabled= maxAttempts= reset= resetAll=true",
                ["ai.asset.conflict"] = "重载冲突（G25：磁盘版撞上未保存的内存版）；处置 mode=disk|keep|saveAs [newName=]",
                ["ai.pool.on"] = "启用池调度（[pool=a,b,c] [sequence=a,b,c]）——默认关闭",
                ["ai.pool.off"] = "关闭池调度（保留池配置）",
                ["ai.pool.status"] = "行为树池现状与下一次调度决策（只读；[pool=a,b,c] 指定池）",
                ["ai.pool.bind"] = "相位 → 树包绑定（[front=demo.front,world=demo.laya] [clear=true]）：相位一变就让池切过去，两套树包各管一段",
                ["ai.laya.status"] = "Laya 服务现状（配置/密钥来源掩码/在途/缓存/失败计数）",
                ["ai.laya.review"] = "判定复盘（P4）：最近几次问 Laya 的聚合 + 明细 + markdown/digest 抽样表（count/clear/format）",
                ["ai.laya.ask"] = "手动问一次 Laya（questions=库名 [only=id,…] [digest=字面摘要]）——同步，仅调试/调参用",
                ["state.digest"] = "把当前状态编译成喂给 Laya 的一行摘要（budget= 可选）",
                ["ai.action.script.verbs"] = "列出 verb 词表与参数（编辑器物料区、LLM 生成脚本读它）",
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
                ["ai.hud"] = Hud,
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
                ["ai.action.script.list"] = ActionScriptList,
                ["ai.action.script.validate"] = ActionScriptValidate,
                ["ai.action.script.run"] = ActionScriptRun,
                ["ai.action.script.stop"] = ActionScriptStop,
                ["ai.action.script.status"] = ActionScriptStatus,
                ["ai.asset.status"] = AssetStatusCommand,
                ["ai.asset.list"] = AssetListCommand,
                ["ai.asset.load"] = AssetLoadCommand,
                ["ai.asset.save"] = AssetSaveCommand,
                ["ai.asset.drop"] = AssetDropCommand,
                ["ai.asset.autosave"] = AssetAutoSaveCommand,
                ["ai.asset.restore"] = AssetRestoreCommand,
                ["ai.asset.retry"] = AssetRetryCommand,
                ["ai.asset.conflict"] = AssetConflictCommand,
                ["ai.pool.on"] = PoolControlCommand,
                ["ai.pool.off"] = PoolControlCommand,
                ["ai.pool.status"] = PoolStatusCommand,
                ["ai.pool.bind"] = PoolBindCommand,
                ["ai.laya.status"] = LayaStatusCommand,
                ["ai.laya.review"] = LayaReviewCommand,
                ["ai.laya.ask"] = LayaAskCommand,
                ["state.digest"] = StateDigestCommand,
                ["ai.action.script.verbs"] = ActionScriptVerbs,
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
            // §4.13：相位是"树为什么不动"的第一个该看的字段 —— 世界外/过渡态都是正常状态。
            result["phase"] = context.Phase;
            result["phaseChanges"] = context.PhaseChanges;
            // 闸门：`gateActive=true` 时树**一拍都没走**（过渡态），这是正常状态而不是卡死；
            // `gatedFrames` 累计值是用来证明"闸门真的拦过帧"的硬证据。
            result["gateActive"] = context.PhaseGateActive;
            result["gatedFrames"] = context.PhaseGatedFrames;

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

        /// <summary>
        /// `ai.hud [on=true|false]`：屏幕左上角那行 AI 状态（plan G19）。
        ///
        /// 为什么要给命令：① 人能关掉它（不想看就别看）；② **它是可验证的** ——
        /// 自动化能通过 `ui --all` 读到那个控件的文本，于是"AI 现在是什么状态"
        /// 这件事第一次有了机器可读的出口（以前只能读事件日志倒推）。
        /// 只改显示，不碰任何决策状态。
        /// </summary>
        private static object Hud(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            AiHudOverlay hud = runtime.Hud;
            if (request.Has("on"))
                hud.Enabled = request.GetBoolean("on", true);

            Dictionary<string, object> result = hud.Describe();
            result["line"] = hud.LastText;
            return result;
        }

        private static IAiRecordingControl RequireRecording(IAiCommandContext context, string command)        {
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

            // 拒包 / 重取（§4.12）：切不过去要分流（缺文件退避重取 / 校验不过停手）
            AssetFailureDecision decision = null;
            if (PlayerAiRuntime.Instance != null)
            {
                if (result.Switched)
                {
                    PlayerAiRuntime.Instance.NoteAssetSuccess(name);
                }
                else
                {
                    PackageReport report = ValidateQuietly(context.Reloader, name);
                    decision = PlayerAiRuntime.Instance.NoteAssetFailure(name,
                        AssetFailureClassifier.TextOf(report)
                            ?? (result.Issues != null && result.Issues.Count > 0
                                ? string.Join(" | ", result.Issues.ToArray())
                                : (result.Reason ?? "switch failed")),
                        AssetFailureClassifier.DetailOf(report) ?? result.Reason,
                        AssetFailureClassifier.ClassifyReport(report));
                }
            }

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
                ["issues"] = result.Issues,
                ["failure"] = decision != null ? decision.Code : null,
                ["retry"] = decision != null ? decision.StateName() : null,
                ["retryInMs"] = decision != null && decision.Retry
                    ? (int)Math.Round(decision.DelaySeconds * 1000.0) : 0
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

            // 资源库（P6）：内存被改了 → 置 dirty 标记（来源 = AI），自动缓存会按节流把它抓进缓存。
            // 放在这里而不是四个命令各写一遍：四条编辑路径共用同一个出口，漏一条就等于"改了却不缓存"。
            if (result.Applied)
            {
                PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
                if (runtime != null)
                    runtime.NoteTreeEdited(AssetOrigin.Ai);
            }

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
                    + "(use ai.asset.drop to discard, ai.asset.save to write a package)"
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

        private static object ActionStop(AiCommandRequest request, IAiCommandContext context)        {
            IAiActionPlayer player = RequireActionPlayer(context, request.Command);
            string result = player.Stop();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["status"] = player.Status()
            };
        }

        // ---------------------------------------------------------------- 动作脚本（P1）

        /// <summary>
        /// 动作脚本服务（`ai.action.script.*` 的实现）。
        /// 走 <see cref="GameActionServices.Ensure"/>：静态装配点被清掉时**自愈重建**
        /// （实机踩过"同一份 DLL，重启后偶尔全报 not_ready"）；运行时也没了才报 not_ready。
        /// </summary>
        private static GameActionServices RequireScriptServices(string command)
        {
            GameActionServices services = GameActionServices.Ensure();
            if (services == null)
            {
                throw new AiCommandException("not_ready",
                    command + " needs the action script services (PlayerAiMod runtime not installed)");
            }
            return services;
        }

        /// <summary>列出脚本目录里能找到的动作脚本（带解析状态）。</summary>
        private static object ActionScriptList(AiCommandRequest request, IAiCommandContext context)
        {
            GameActionServices services = RequireScriptServices(request.Command);
            List<ActionScriptEntry> entries = services.Library.List();

            var items = new List<Dictionary<string, object>>();
            int valid = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ActionScriptEntry entry = entries[i];
                if (entry.Ok)
                    valid++;
                var item = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = entry.Name,
                    ["path"] = entry.Path,
                    ["valid"] = entry.Ok,
                    ["steps"] = entry.StepCount,
                    ["hash"] = entry.Hash,
                    ["error"] = entry.Error
                };
                if (entry.Script != null)
                {
                    item["id"] = entry.Script.Id;
                    item["guards"] = entry.Script.Guards.Count;
                    item["onFail"] = entry.Script.OnFail;
                    var verbs = new List<string>();
                    for (int s = 0; s < entry.Script.Steps.Count; s++)
                        verbs.Add(entry.Script.Steps[s].Verb);
                    item["verbs"] = verbs;
                }
                items.Add(item);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["directories"] = new List<string>(services.Library.Directories),
                ["count"] = items.Count,
                ["valid"] = valid,
                ["scripts"] = items
            };
        }

        /// <summary>只校验不执行（编辑器保存前预检 / 人排查）。</summary>
        private static object ActionScriptValidate(AiCommandRequest request, IAiCommandContext context)
        {
            GameActionServices services = RequireScriptServices(request.Command);
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required.");
            return services.Library.Validate(name);
        }

        /// <summary>直接跑一条脚本（不经行为树），给人/LLM 验证"这串积木能不能跑通"。</summary>
        private static object ActionScriptRun(AiCommandRequest request, IAiCommandContext context)
        {
            RequireScriptServices(request.Command);
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("path", null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required.");

            int repeat = Math.Max(1, request.GetInteger("repeat", 1));
            int totalTimeoutMs = Math.Max(0, request.GetInteger("totalTimeoutMs", 0));

            string result = runtime.ScriptRuntime.Play(name, repeat, totalTimeoutMs);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = result,
                ["status"] = runtime.ScriptRuntime.Status()
            };
        }

        private static object ActionScriptStop(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");
            bool stopped = runtime.ScriptRuntime.Stop();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["result"] = stopped ? "stopped" : "nothing is playing",
                ["status"] = runtime.ScriptRuntime.Status()
            };
        }

        private static object ActionScriptStatus(AiCommandRequest request, IAiCommandContext context)
        {
            GameActionServices services = RequireScriptServices(request.Command);
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["services"] = services.Describe();
            result["verbs"] = new List<string>(ScriptCompiler.VerbNames);
            if (runtime != null)
            {
                result["player"] = runtime.ScriptRuntime.Status();
                result["root"] = runtime.ResolveActionDirectory();
            }
            return result;
        }

        /// <summary>列出 verb 词表（编辑器物料区与 LLM 生成脚本时读它）。</summary>
        /// <summary>
        /// `state.digest`：把当前状态编译成**喂给 Laya 的一行摘要**（plan §4.2）。
        /// 人/LLM/编辑器都靠它看清"模型实际看到的是什么" —— 调判准的第一步。
        /// </summary>
        /// <summary>
        /// `ai.pool.status`：行为树池的现状与**下一次调度决策**（plan §4.8）。
        /// 只读：它按"当前活动树状态"算一次决策给人看，**不真的切树**
        /// （真正的切换要等 S1 的步骤边界，由运行时执行）。
        /// </summary>
        private static object PoolStatusCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            var pool = runtime.Pool;
            var keys = new List<string>();

            string configured = request.GetString("pool", null);
            if (!string.IsNullOrEmpty(configured))
            {
                string[] parts = configured.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string key = parts[i].Trim();
                    if (key.Length > 0)
                        keys.Add(key);
                }
            }
            else
            {
                // 默认：包目录里能找到的树包（人/编辑器也可以显式传 pool=）
                try
                {
                    List<string> found = runtime.Roots != null
                        ? runtime.Roots.ListFiles(PackageRoots.Extension)
                        : null;
                    if (found != null)
                    {
                        for (int i = 0; i < found.Count; i++)
                            keys.Add(System.IO.Path.GetFileNameWithoutExtension(found[i]));
                    }
                }
                catch (Exception)
                {
                    // 列不出来就当池空（下面会如实报 safeIdle）
                }
            }
            // 显式传了 pool= 才重装池；否则**保持运行时现有的池**（别把 ai.pool.on 装的池冲掉），
            // 只在"池空"时给一个默认（包目录里的树包）方便查看。
            if (!string.IsNullOrEmpty(configured))
                pool.SetPool(keys);
            else if (pool.Pool.Count == 0)
                pool.SetPool(keys);

            // 预编译常驻状态（池的"能不能立刻切"看这个）
            var prepared = new List<string>();
            if (runtime.Library != null)
            {
                IReadOnlyList<PreparedTree> ready = runtime.Library.Prepared;
                for (int i = 0; i < ready.Count; i++)
                    prepared.Add(ready[i].Key + (ready[i].Ready ? "" : "(not ready)"));
            }

            IAiTreeHost host = runtime.ResolveTreeHost();
            bool hasTree = host != null && host.HasTree;
            bool running = hasTree && !host.Tree.IsRunning;

            // 只读演示：假设"活动树刚跑完且成功"，看它会怎么调度。
            // ⚠️ 必须用 `Peek`（不改计数、**不消费 `pool.next`**）—— 以前调 `Decide`，
            //    于是"看一眼状态"会把别人写的 `pool.next` 吃掉、还把 switch 计数加上去，
            //    调度器的认知与真实活动树就此分叉（实机踩过）。
            PoolDecision decision = pool.Peek(host != null ? host.Blackboard : null,
                hasTree, false, true, false);

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["pool"] = pool.Describe(),
                ["prepared"] = prepared,
                ["host"] = host != null ? host.HostName : null,
                ["hasTree"] = hasTree,
                ["treeRunning"] = hasTree && host.Tree.IsRunning,
                ["wouldDo"] = decision.Describe(),
                ["nextEntry"] = decision.Entry != null ? decision.Entry.ToString() : null,
                ["blackboardKeys"] = new List<string> { PoolScheduler.KeyNext, PoolScheduler.KeySequence, PoolScheduler.KeyFallback },
                // 事件日志自身的计数：与 ai.logs 的 entries 对照即可判断"写入到底有没有落到同一个实例上"
                ["eventLogWriteCount"] = runtime.EventLog.WriteCount,
                ["eventLogRecent"] = runtime.EventLog.Recent(0).Count,
                ["eventLogLastError"] = runtime.EventLog.LastError,
                ["eventLogEnabled"] = runtime.EventLog.Enabled
            };
        }
        /// <summary>
        /// `ai.pool.on/off [pool=a,b,c] [sequence=a,b,c]`：开关池调度并（可选）装池。
        /// **默认关闭**：不打开时运行时行为与以前完全一致。
        /// </summary>
        private static object PoolControlCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            bool enable = string.Equals(request.Command, "ai.pool.on", StringComparison.OrdinalIgnoreCase);
            string pool = request.GetString("pool", null);
            if (!string.IsNullOrEmpty(pool))
            {
                var keys = new List<string>();
                string[] parts = pool.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string key = parts[i].Trim();
                    if (key.Length > 0)
                        keys.Add(key);
                }
                runtime.Pool.SetPool(keys);
            }

            string sequence = request.GetString("sequence", null);
            if (!string.IsNullOrEmpty(sequence))
            {
                var keys = new List<string>();
                string[] parts = sequence.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string key = parts[i].Trim();
                    if (key.Length > 0)
                        keys.Add(key);
                }
                runtime.Pool.SetSequence(keys);
            }

            // 装池但没给顺序时：预编译常驻（把切换成本提前付掉，切换才是"毫秒级"）
            if (enable && runtime.Pool.Pool.Count > 0 && runtime.Library != null)
            {
                for (int i = 0; i < runtime.Pool.Pool.Count; i++)
                {
                    try
                    {
                        runtime.Library.Prepare(runtime.Pool.Pool[i].Key);
                    }
                    catch (Exception)
                    {
                        // 预编译失败不阻塞启用（切换时会再试并如实报错）
                    }
                }
            }

            runtime.PoolEnabled = enable;
            runtime.EventLog.Write(enable ? "pool-on" : "pool-off", runtime.Pool.DescribePool());
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = runtime.PoolEnabled,
                ["pool"] = runtime.Pool.Describe()
            };
        }
        /// <summary>
        /// `ai.pool.bind [front=demo.front,world=demo.laya] [clear=true]`：**相位 → 树包**绑定（§4.13）。
        ///
        /// 语义（三条都要，否则会咬人）：
        ///   · 只写 `pool.next`，**换树仍由池在步骤边界做**（D9：换树只有一个来源；S1：不打断正在跑的一步）；
        ///   · **设置时立刻按当前相位应用一次**（否则"开局就在世界外"永远等不到相位边沿）；
        ///   · 相位名写错、或池里没有那棵树 → **如实报错/记一条跳过原因**，不静默。
        /// </summary>
        private static object PoolBindCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            bool clear = request.GetBoolean("clear", false);
            string spec = request.GetString("binding", request.GetString("bind", null));

            if (!clear && string.IsNullOrEmpty(spec))
            {
                // 只读：现在绑的是什么、当前相位会选谁
                PhaseTreeBinding current = runtime.PhaseBinding;
                string wouldPick = runtime.ApplyPhaseBinding(null);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["binding"] = current != null ? current.Describe() : null,
                    ["phase"] = runtime.Phase,
                    ["wouldSwitchTo"] = wouldPick,
                    ["pool"] = runtime.Pool.Describe()
                };
            }

            PhaseTreeBinding binding;
            string error;
            if (!PhaseTreeBinding.TryParse(clear ? string.Empty : spec, out binding, out error))
                throw new AiCommandException("invalid_argument", error);

            runtime.PhaseBinding = binding.Count > 0 ? binding : null;

            // **绑定在管事时，池不许自己往下轮**：否则池会在每次成功后按自然顺序换树，
            // 把绑定刚切过去的那棵树又换掉（实机踩过）。关掉"自动前进"之后，
            // 只有明确来源能动树：pool.next（绑定写的）/ pool.fallback / 显式 sequence。
            // 可以显式覆盖：`autoAdvance=true` 保持轮换（例如想"世界内轮流跑几棵"）。
            bool? autoAdvance = request.Has("autoAdvance")
                ? request.GetBoolean("autoAdvance", true) : (bool?)null;
            if (autoAdvance.HasValue)
                runtime.Pool.AutoAdvance = autoAdvance.Value;
            else
                runtime.Pool.AutoAdvance = runtime.PhaseBinding == null;

            runtime.EventLog.Write("pool-bind-set", (runtime.PhaseBinding != null
                ? runtime.PhaseBinding.Describe() : "(cleared)")
                + " autoAdvance=" + runtime.Pool.AutoAdvance);

            string applied = runtime.ApplyPhaseBinding(null);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["binding"] = runtime.PhaseBinding != null ? runtime.PhaseBinding.Describe() : null,
                ["phase"] = runtime.Phase,
                ["applied"] = applied,
                ["pool"] = runtime.Pool.Describe()
            };
        }

        // ---------------------------------------------------------------- 资源库（P6：存盘 / 内存 / 临时缓存）
        /// <summary>
        /// `ai.asset.status`：**资源库总览** —— 内存库里有几条、活动资源是谁、
        /// 自动缓存（`&lt;实例根&gt;/PlayerAi/.autosave/`）的版本头与最近一次被拒的原因。
        /// 只读；`cacheDrop=true` 才清掉缓存（**注意不是 `drop`** —— `ai.asset.drop` 是"丢弃内存改动"）。
        /// </summary>
        private static object AssetStatusCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            MemoryAssetStore store = runtime.Assets;

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["instanceRoot"] = runtime.AssetInstanceRoot,
                ["packageFolder"] = store != null && store.Roots != null && store.Roots.InstanceRoot != null
                    ? store.Roots.InstanceRoot.Path : null,
                ["autoSaveSeconds"] = PlayerAiConfig.AssetAutoSaveSeconds,
                ["active"] = runtime.AssetActiveName,
                ["dirtyPending"] = runtime.AssetDirtyPending,
                ["cache"] = runtime.AssetCache.Describe()
            };

            if (store != null)
            {
                result["kind"] = store.Kind;
                result["records"] = store.DescribeRecords();
                result["dirtyCount"] = store.DirtyCount;
            }

            // G25：待处置的重载冲突（编辑器据此弹"用磁盘覆盖 / 把内存另存为新包"）
            PackageReloader reloader = runtime.Reloader;
            result["conflicts"] = reloader != null
                ? reloader.DescribeConflicts() : new List<Dictionary<string, object>>();
            result["conflictCount"] = reloader != null ? reloader.ConflictCount : 0;

            if (request.GetBoolean("cacheDrop", false))
            {
                string error;
                bool dropped = runtime.AssetCache.TryDrop(out error);
                result["cacheDropped"] = dropped;
                result["cacheDropError"] = error;
                if (runtime.EventLog != null)
                    runtime.EventLog.Write("autosave-drop", dropped ? "ok" : (error ?? "failed"));
            }
            return result;
        }

        /// <summary>
        /// `ai.asset.list`：**三态视图** —— 每条资源同时给出"磁盘版 / 内存版（dirty）/ 缓存版"，
        /// 以及恢复决议（用哪一份、为什么）。编辑器物料区的角标与"将用缓存（新 X 分钟）"就吃这份数据。
        /// </summary>
        private static object AssetListCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            MemoryAssetStore store = RequireAssets(runtime);

            List<Dictionary<string, object>> records = runtime.DescribeAssets();
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["kind"] = store.Kind,
                ["active"] = runtime.AssetActiveName,
                ["dirtyCount"] = store.DirtyCount,
                ["manualSaveFolder"] = PackageRoots.SavesDirectoryFor(runtime.AssetInstanceRoot),
                ["records"] = records
            };
            return result;
        }

        /// <summary>
        /// `ai.asset.load`：把磁盘上的包**读进内存库**（plan §4.11「读进来才算加载」）。
        /// 只是"装进库"，**不切换活动树** —— 切树仍然是 `ai.tree.load` 的事。
        /// </summary>
        private static object AssetLoadCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            MemoryAssetStore store = RequireAssets(runtime);

            string name = AssetNameFrom(request, null);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument", "'name' is required (the package to load)");

            string error;
            AssetRecord record = store.Load(name, out error);
            if (record == null)
                throw new AiCommandException("pkg_missing", error ?? "load failed");

            runtime.EventLog.Write("asset-load", record.Describe());
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["loaded"] = true,
                ["name"] = record.Name,
                ["path"] = record.SourcePath,
                ["hash"] = AssetRecord.Short(record.MemoryHash),
                ["bytes"] = record.MemoryByteCount,
                ["dirty"] = record.Dirty,
                ["readIntoMemory"] = true
            };
        }

        /// <summary>
        /// `ai.asset.save`：把**内存版**存成正式包（手动存档）。
        ///
        /// 默认落点是 `&lt;实例根&gt;/PlayerAi/Saves/`（第 3 层，"人存档"），
        /// 想直接覆盖正在跑的那个包得显式 `dir=` 指到包目录，并且目标已存在时还需要
        /// `overwrite=true`（那时**先备份上一代**成 `name.vN.bak`）。
        /// </summary>
        private static object AssetSaveCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            MemoryAssetStore store = RequireAssets(runtime);

            string name = AssetNameFrom(request, runtime.AssetActiveName);
            if (string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument",
                    "'name' is required (or run a tree first so there is an active asset)");

            string directory = request.GetString("dir", null);
            if (string.IsNullOrEmpty(directory))
                directory = PackageRoots.SavesDirectoryFor(runtime.AssetInstanceRoot);
            if (string.IsNullOrEmpty(directory))
                throw new AiCommandException("not_ready", "no instance root to save into");

            bool overwrite = request.GetBoolean("overwrite", false);
            string path;
            string error;
            if (!store.Save(name, directory, overwrite, out path, out error))
                throw new AiCommandException(AssetErrorCode(error, "io_error"), error ?? "save failed");

            AssetRecord record;
            store.TryGet(name, out record);
            runtime.EventLog.Write("asset-save", path + " (" + (record != null ? record.Describe() : name) + ")");

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["saved"] = true,
                ["path"] = path,
                ["name"] = record != null ? record.Name : name,
                ["overwrite"] = overwrite,
                ["dirty"] = record != null && record.Dirty,
                ["note"] = overwrite
                    ? "the previous generation was backed up next to the package"
                    : "the original package was not modified"
            };
        }

        /// <summary>
        /// `ai.asset.drop`：**丢弃内存改动**，回到最近一次载入的磁盘版。
        /// 活树会一起退回（否则下一次 IsDirty 扫描又把改动捡回来，等于没丢）。
        /// </summary>
        private static object AssetDropCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            RequireAssets(runtime);

            string name = AssetNameFrom(request, null);
            string error;
            bool dropped = runtime.DropActiveAsset(name, out error);
            if (!dropped)
                throw new AiCommandException(AssetErrorCode(error, "not_ready"), error ?? "drop failed");

            string active = runtime.AssetActiveName;
            MemoryAssetStore store = runtime.Assets;
            AssetRecord record = null;
            if (store != null && !string.IsNullOrEmpty(active))
                store.TryGet(active, out record);

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dropped"] = true,
                ["active"] = active,
                ["dirty"] = record != null && record.Dirty,
                ["memoryHash"] = record != null ? AssetRecord.Short(record.MemoryHash) : null,
                ["note"] = "the memory version went back to the last package loaded from disk,"
                    + " and the live tree was reloaded from it"
            };
        }

        /// <summary>
        /// `ai.asset.autosave`：立刻把内存版写进 `.autosave/`（影子副本）。
        /// 会把活树的当前内容先抓进内存库 —— 不然缓存的是"上一次抓取"而不是"现在"。
        /// </summary>
        private static object AssetAutoSaveCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            MemoryAssetStore store = RequireAssets(runtime);

            if (string.IsNullOrEmpty(runtime.AssetActiveName))
                throw new AiCommandException("not_ready", "no active asset (load a tree first)");

            bool captured = true;
            if (request.GetBoolean("capture", true) && runtime.AssetDirtyPending)
                captured = runtime.CaptureLiveTree(AssetOrigin.Ai);

            string error;
            bool ok = runtime.TryAutoSaveActiveAsset(out error);
            if (!ok)
                throw new AiCommandException("io_error", error ?? "autosave failed");

            AssetRecord record;
            store.TryGet(runtime.AssetActiveName, out record);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["cached"] = true,
                ["captured"] = captured,
                ["name"] = runtime.AssetActiveName,
                ["dirty"] = record != null && record.Dirty,
                ["cacheDirectory"] = runtime.AssetCache.Directory
            };
        }

        /// <summary>
        /// `ai.asset.restore`：按**恢复决议**把缓存/磁盘版装回内存库。
        /// `all=true` 时对包目录里每个包都做一次决议（等同启动恢复流程，但由人显式触发）。
        /// </summary>
        private static object AssetRestoreCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            RequireAssets(runtime);

            bool all = request.GetBoolean("all", false);
            string name = all ? null : AssetNameFrom(request, runtime.AssetActiveName);
            if (!all && string.IsNullOrEmpty(name))
                throw new AiCommandException("invalid_argument",
                    "'name' is required (or pass all=true)");

            string error;
            List<string> lines = runtime.RestoreAssetsFromCache(name, out error);
            if (lines == null)
                throw new AiCommandException(AssetErrorCode(error, "not_ready"), error ?? "restore failed");

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["restored"] = lines.Count,
                ["all"] = all,
                ["lines"] = lines,
                ["active"] = runtime.AssetActiveName,
                ["note"] = "restoring only fills the memory store; it never writes to disk"
            };
        }

        /// <summary>
        /// `ai.asset.retry`：**拒包 / 重取的现状与总开关**（plan §4.12）。
        ///
        /// 只读时回答"哪些资源在等重取、等多久、哪些已经熔断"；带参数时改策略
        /// （`enabled=` / `maxAttempts=` / `baseDelayMs=` / `windowLimit=` / `layaAsk=`），
        /// 或用 `reset=name` / `resetAll=true` **解除熔断** —— 这就是"人处置之后恢复"的入口。
        /// </summary>
        private static object AssetRetryCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            AssetRecoveryTask tracker = runtime.AssetRetry;
            AssetRetryPolicy policy = tracker.Policy;

            var changed = new List<string>();

            if (request.Arguments.ContainsKey("enabled"))
            {
                policy.Enabled = request.GetBoolean("enabled", policy.Enabled);
                changed.Add("enabled=" + policy.Enabled);
            }
            if (request.Arguments.ContainsKey("maxAttempts"))
            {
                policy.MaxAttempts = request.GetInteger("maxAttempts", policy.MaxAttempts);
                changed.Add("maxAttempts=" + policy.MaxAttempts);
            }
            if (request.Arguments.ContainsKey("baseDelayMs"))
            {
                policy.BaseDelaySeconds = request.GetInteger("baseDelayMs",
                    (int)Math.Round(policy.BaseDelaySeconds * 1000.0)) / 1000.0;
                changed.Add("baseDelayMs=" + (int)Math.Round(policy.BaseDelaySeconds * 1000.0));
            }
            if (request.Arguments.ContainsKey("windowLimit"))
            {
                policy.WindowLimit = request.GetInteger("windowLimit", policy.WindowLimit);
                changed.Add("windowLimit=" + policy.WindowLimit);
            }
            if (request.Arguments.ContainsKey("windowSeconds"))
            {
                policy.WindowSeconds = request.GetInteger("windowSeconds",
                    (int)Math.Round(policy.WindowSeconds));
                changed.Add("windowSeconds=" + (int)Math.Round(policy.WindowSeconds));
            }
            if (request.Arguments.ContainsKey("layaAsk"))
            {
                policy.AskLaya = request.GetBoolean("layaAsk", policy.AskLaya);
                changed.Add("askLaya=" + policy.AskLaya);
            }

            policy.Validate();
            if (changed.Count > 0)
            {
                tracker.ApplyPolicy(policy.Clone());
                runtime.EventLog.Write("asset-retry-policy", string.Join(", ", changed.ToArray()));
            }

            // 复位要**先做**再快照：否则回包里的 `resources` 是复位之前的旧状态，
            // 人看到"已复位"却仍显示 exhausted（自检/实测都会以为复位没生效）。
            string resetTarget = null;
            bool resetAll = request.GetBoolean("resetAll", false);
            bool wasExhausted = false;
            if (resetAll)
            {
                tracker.ResetAll();
                runtime.ClearAssetMarks();
                runtime.EventLog.Write("asset-retry-reset", "all");
                resetTarget = "all";
            }
            else
            {
                string reset = request.GetString("reset", null);
                if (!string.IsNullOrEmpty(reset))
                {
                    wasExhausted = runtime.ResetAssetRetry(reset);
                    resetTarget = reset;
                }
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["policy"] = tracker.Policy.Describe(),
                ["pending"] = tracker.PendingCount,
                ["exhausted"] = tracker.ExhaustedCount,
                ["nextRetryMs"] = tracker.SecondsToNextRetry() < 0.0
                    ? -1 : (int)Math.Round(tracker.SecondsToNextRetry() * 1000.0),
                ["resources"] = tracker.DescribeResources(),
                ["changed"] = changed
            };

            if (resetTarget != null)
            {
                result["reset"] = resetTarget;
                result["wasExhausted"] = wasExhausted;
            }
            return result;
        }

        /// <summary>
        /// `ai.asset.conflict`：**G25 的重载冲突**（磁盘版撞上未保存的内存版）。
        ///
        /// 只读时列出待处置的冲突（三份哈希 + 每条冲突的两个选项）；带 `mode=` 时处置一条：
        ///   · `disk`   —— 用磁盘覆盖内存（= `ai.asset.drop` 回磁盘版 + 摘掉冲突）;
        ///   · `keep`   —— 保留内存（把那条通知丢掉，内存继续跑）;
        ///   · `saveAs` —— 把内存版另存为新包（`newName=`，落包目录），然后摘掉冲突。
        ///
        /// **默认什么都不做**：冲突就是"等人选"，不选就一直保留内存（运行态不被打断）。
        /// </summary>
        private static object AssetConflictCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = RequireRuntime();
            PackageReloader reloader = runtime.Reloader;
            if (reloader == null)
                throw new AiCommandException("not_ready", "the package reloader is not configured");

            string path = request.GetString("path", null);
            if (string.IsNullOrEmpty(path))
                path = request.GetString("name", null);
            string mode = request.GetString("mode", null);

            var result = new Dictionary<string, object>(StringComparer.Ordinal);

            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(mode))
            {
                ReloadConflict conflict;
                if (!reloader.TryFindConflict(path, out conflict))
                    throw new AiCommandException("not_found", "no pending conflict for '" + path + "'");

                string action = mode.Trim().ToLowerInvariant();
                if (action == "disk")
                {
                    // 用磁盘覆盖内存：内存库回到磁盘版，活树按磁盘版重装
                    string dropError;
                    bool dropped = runtime.DropActiveAsset(conflict.Path, out dropError);
                    reloader.ResolveConflict(conflict.Path, out conflict);
                    result["resolved"] = "disk";
                    result["dropped"] = dropped;
                    result["dropError"] = dropError;
                    runtime.EventLog.Write("asset-conflict", "use disk: " + conflict.Path);
                }
                else if (action == "keep")
                {
                    reloader.ResolveConflict(conflict.Path, out conflict);
                    result["resolved"] = "keep";
                    runtime.EventLog.Write("asset-conflict",
                        "keep memory (the disk version is ignored): " + conflict.Path);
                }
                else if (action == "saveas")
                {
                    string newName = request.GetString("newName", null);
                    if (string.IsNullOrEmpty(newName))
                        throw new AiCommandException("invalid_argument",
                            "mode=saveAs needs newName= (the new package)");

                    string directory = request.GetString("dir", null);
                    if (string.IsNullOrEmpty(directory))
                    {
                        directory = reloader.Roots != null && reloader.Roots.InstanceRoot != null
                            ? reloader.Roots.InstanceRoot.Path : null;
                    }
                    if (string.IsNullOrEmpty(directory))
                        throw new AiCommandException("not_ready", "no package folder to save into");

                    string saved;
                    string saveError;
                    MemoryAssetStore store = RequireAssets(runtime);
                    if (!store.SaveAs(conflict.Path, newName, directory,
                        request.GetBoolean("overwrite", false), out saved, out saveError))
                    {
                        throw new AiCommandException(AssetErrorCode(saveError, "io_error"),
                            saveError ?? "saveAs failed");
                    }
                    reloader.ResolveConflict(conflict.Path, out conflict);
                    result["resolved"] = "saveAs";
                    result["path"] = saved;
                    runtime.EventLog.Write("asset-conflict", "saved the memory version as " + saved);
                }
                else
                {
                    throw new AiCommandException("invalid_argument",
                        "mode must be disk | keep | saveAs");
                }
            }

            result["count"] = reloader.ConflictCount;
            result["total"] = reloader.ConflictTotal;
            result["conflicts"] = reloader.DescribeConflicts();
            result["options"] = new List<string> { "disk", "keep", "saveAs" };
            return result;
        }

        /// <summary>资源命令共用的运行时入口。</summary>
        private static PlayerAiRuntime RequireRuntime()        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");
            return runtime;
        }

        /// <summary>资源命令共用的库入口（包目录没配好时给稳定的错误码）。</summary>
        private static MemoryAssetStore RequireAssets(PlayerAiRuntime runtime)
        {
            MemoryAssetStore store = runtime.Assets;
            if (store == null)
                throw new AiCommandException("not_ready",
                    "the package folder is not configured (PlayerAi/BehaviorTrees)");
            return store;
        }

        /// <summary>`name=` 或 `file=`（与其它命令的参数习惯保持一致）。</summary>
        private static string AssetNameFrom(AiCommandRequest request, string fallback)
        {
            string name = request.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                name = request.GetString("file", null);
            return string.IsNullOrEmpty(name) ? fallback : name;
        }

        /// <summary>
        /// 把资源库的失败原因翻成**稳定的错误码**（调用方与脚本只认码，不认英文句子）。
        /// 判定顺序按"最具体的先说"，兜底给调用方自己指定的码。
        /// </summary>
        private static string AssetErrorCode(string error, string fallback)
        {
            if (string.IsNullOrEmpty(error))
                return fallback;
            if (error.StartsWith("already_exists", StringComparison.Ordinal))
                return "already_exists";
            if (error.StartsWith("nothing_to_drop", StringComparison.Ordinal))
                return "nothing_to_drop";
            if (error.StartsWith("not in the memory store", StringComparison.Ordinal))
                return "not_found";
            if (error.StartsWith("no instance root", StringComparison.Ordinal)
                || error.StartsWith("a target directory is required", StringComparison.Ordinal))
            {
                return "not_ready";
            }
            if (error.StartsWith("refusing", StringComparison.Ordinal))
                return "invalid_argument";
            return fallback;
        }

        /// <summary>
        /// `ai.laya.status`：Laya 服务现状（配置/密钥来源掩码/在途/缓存/失败计数）
        /// + **版本对账**（plan G18）：当前摘要布局版本 / 各问题库的内容版本与哈希 /
        /// **复盘表里的样本是不是当前版本产生的**。
        /// </summary>
        private static object LayaStatusCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            Dictionary<string, object> info = runtime.Laya.Describe();
            info["digestVersion"] = StateDigestCompiler.Version;

            // 已缓存的问题库：名字 + 内容版本 + 内容哈希短号（改过哪一本一眼看得出来）
            var banks = new List<Dictionary<string, object>>();
            var activeBankHash = new List<string>();
            IReadOnlyList<QuestionBank> cached = runtime.Laya.CachedBanks();
            for (int i = 0; i < cached.Count; i++)
            {
                QuestionBank bank = cached[i];
                string hash = bank.SourceHash;
                if (!string.IsNullOrEmpty(hash) && hash.Length > 12)
                    hash = hash.Substring(0, 12);
                banks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = bank.Id,
                    ["version"] = bank.Version,
                    ["questions"] = bank.Questions.Count,
                    ["hash"] = hash,
                    // 磁盘上的比缓存里的新？→ 提示"改了库但游戏还没重读"（下一次判定会自动重读）
                    ["changedOnDisk"] = runtime.Laya.IsBankStale(bank)
                });
                if (!string.IsNullOrEmpty(bank.SourceHash))
                    activeBankHash.Add(bank.SourceHash);
            }
            info["banks"] = banks;

            // 复盘样本 vs 当前版本：不一致就提示"建议重跑抽样表"（版本对账的全部意义就在这一句）
            DecisionLog log = runtime.Laya.Decisions;
            if (log != null)
            {
                info["samplesMatchCurrent"] = log.SamplesMatchCurrent(StateDigestCompiler.Version,
                    activeBankHash.Count == 1 ? activeBankHash[0] : null);
                List<DecisionRecord> recent = log.Recent(1);
                info["lastSampleVersion"] = recent.Count > 0
                    ? DecisionLog.DescribeVersion(recent[0]) : null;
            }
            return info;
        }

        /// <summary>
        /// `ai.laya.ask`：**给人/脚本手动问一次**（同步阻塞，只用于验证与调试；
        /// 行为树里请用 Task.LayaAsk，它是异步的、不阻塞游戏线程）。
        /// </summary>
        private static object LayaAskCommand(AiCommandRequest request, IAiCommandContext context)
        {
            PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
            if (runtime == null)
                throw new AiCommandException("not_ready", "PlayerAi runtime is not available");

            string bankName = request.GetString("questions", null);
            if (string.IsNullOrEmpty(bankName))
                bankName = request.GetString("bank", null);
            if (string.IsNullOrEmpty(bankName))
                throw new AiCommandException("invalid_argument", "'questions' (bank name) is required.");

            LayaRuntimeService service = runtime.Laya;
            QuestionBank bank;
            string bankError;
            if (!service.TryResolveBank(bankName, request.GetString("only", null), out bank, out bankError))
                throw new AiCommandException("file_missing", bankError);

            string digestError;
            string overrideDigest = request.GetString("digest", null);
            string digest;
            if (!string.IsNullOrEmpty(overrideDigest))
            {
                // `digest=`：**原样用这一段摘要**，不编译、不校验。
                //
                // 为什么需要这个口子：§5.4 的结论是"摘要本身是最主要的调参旋钮"，
                // 而 P2 还欠一件实测 —— **中文摘要在真实游戏状态题上的分离度**。
                // 那件事只能靠"同一套问题、只换摘要"来做 A/B，而默认路径永远拿当前游戏状态编译，
                // 没法构造对照。所以这里允许直接喂字面摘要（调参时人甚至要故意写怪摘要看模型怎么崩）。
                //
                // 纪律：**预算照样报出来**（`overBudget`/`budgetChars`）——
                // 放开的是"谁来写摘要"，不是"假装超长没关系"：超预算的请求在服务端会被静默截断，
                // 那正是预算守卫当初要防的东西，所以要让实验者一眼看到自己越线了。
                digest = overrideDigest;
                digestError = null;
            }
            else
            {
                digest = service.CompileDigest(null, out digestError);
            }
            if (digest == null)
                throw new AiCommandException("not_ready", digestError);

            LayaAnswer answer = service.Client.AskSync(digest, bank);
            int budget = service.Config != null ? service.Config.DigestBudgetChars : 0;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = answer.Ok,
                ["errorCode"] = answer.ErrorCode,
                ["error"] = answer.Error,
                ["digest"] = digest,
                ["budgetChars"] = budget,
                ["overBudget"] = budget > 0 && digest.Length > budget,
                ["fingerprint"] = answer.Fingerprint,
                ["summary"] = answer.Summarize(),
                ["answers"] = answer.Answers,
                ["elapsedMs"] = answer.ElapsedMs,
                ["inputTokens"] = answer.InputTokens
            };
        }
        /// <summary>
        /// `ai.laya.review`（P4 复盘）：最近几次判定的**聚合 + 明细 + 可复算的抽样表**。
        ///
        /// 参数：`count`（明细条数，默认 10，0 = 只要聚合）、`clear`（清空记录）、
        /// `format`（`json`（默认）/ `md` / `digest`）。
        ///
        /// 为什么要有 `digest` 这一档：§5.4 的结论是**摘要本身是最主要的调参旋钮**，
        /// 所以"逐条的原文摘要"要能一眼全看到（不夹杂其它列）。
        /// </summary>
        private static object LayaReviewCommand(AiCommandRequest request, IAiCommandContext context)
        {
            DecisionLog log = context.Decisions;
            if (log == null)
                throw new AiCommandException("not_ready", "the Laya decision log is not available");

            if (request.GetBoolean("clear", false))
                log.Clear();

            int count = request.GetInteger("count", 10);
            if (count < 0)
                count = 0;

            string format = (request.GetString("format", "json") ?? "json").Trim().ToLowerInvariant();
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["summary"] = log.Summarize()
            };

            if (string.Equals(format, "md", StringComparison.Ordinal))
            {
                result["markdown"] = log.ToMarkdown(count);
                return result;
            }
            if (string.Equals(format, "digest", StringComparison.Ordinal))
            {
                result["digests"] = log.RecentDigests(count);
                return result;
            }

            var recent = new List<Dictionary<string, object>>();
            List<DecisionRecord> records = log.Recent(count);
            for (int i = 0; i < records.Count; i++)
                recent.Add(records[i].ToDictionary());
            result["recent"] = recent;
            return result;
        }

        private static object StateDigestCommand(AiCommandRequest request, IAiCommandContext context)
        {
            CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade == null)
                throw new AiCommandException("not_ready", "CmdBridgeMod facade is not available");

            Dictionary<string, object> raw = facade.DescribeStateInputs();
            StateInputs inputs = StateInputs.FromObservation(raw);
            int budget = request.GetInteger("budget", StateDigestCompiler.DefaultBudgetChars);
            string digest = StateDigestCompiler.Compile(inputs, budget);

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["digest"] = digest,
                ["chars"] = digest.Length,
                ["budgetChars"] = budget,
                ["estimatedTokens"] = QuestionBankParser.EstimateTokens(digest),
                ["raw"] = raw
            };
        }
        private static object ActionScriptVerbs(AiCommandRequest request, IAiCommandContext context)
        {
            var verbs = new List<Dictionary<string, object>>();
            IReadOnlyList<string> names = ScriptCompiler.VerbNames;
            for (int i = 0; i < names.Count; i++)
            {
                ActionVerbSpec spec;
                if (!ScriptCompiler.TryGetVerb(names[i], out spec) || spec == null)
                    continue;

                var parameters = new List<Dictionary<string, object>>();
                for (int p = 0; p < spec.Parameters.Count; p++)
                {
                    ActionParamSpec param = spec.Parameters[p];
                    parameters.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = param.Name,
                        ["kind"] = param.Kind.ToString().ToLowerInvariant(),
                        ["required"] = param.Required,
                        ["default"] = param.DefaultValue,
                        ["allowed"] = param.Allowed != null ? new List<string>(param.Allowed) : null,
                        ["description"] = param.Description
                    });
                }

                verbs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = spec.Name,
                    ["category"] = spec.Category,
                    ["description"] = spec.Description,
                    ["parameters"] = parameters
                });
            }
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["verbs"] = verbs };
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

            // 拒包 / 重取（§4.12）：失败要**分流**（缺文件可重取、校验不过绝不重取），
            // 并把失败标记写进黑板供异常分支使用。成功则清掉历史。
            AssetFailureDecision decision = null;
            if (PlayerAiRuntime.Instance != null)
            {
                if (result.Replaced)
                {
                    PlayerAiRuntime.Instance.NoteAssetSuccess(name);
                }
                else
                {
                    PackageReport report = ValidateQuietly(reloader, name);
                    decision = PlayerAiRuntime.Instance.NoteAssetFailure(name,
                        AssetFailureClassifier.TextOf(report) ?? result.Describe(),
                        AssetFailureClassifier.DetailOf(report) ?? result.Reason,
                        AssetFailureClassifier.ClassifyReport(report));
                }
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
                ["issues"] = new List<string>(result.Issues),
                ["failure"] = decision != null ? decision.Code : null,
                ["retry"] = decision != null ? decision.StateName() : null,
                ["retryInMs"] = decision != null && decision.Retry
                    ? (int)Math.Round(decision.DelaySeconds * 1000.0) : 0
            };
        }

        /// <summary>
        /// 给分流用的**校验报告**（报告的错误码才是权威判据；取不到给 null，调用方退回文本分流）。
        ///
        /// ⚠️ 别用 `PackageReport.Summary()` —— 它只有 `"1 error(s)"` 这种计数、**没有码**，
        /// 拿它分流会把"缺文件"判成"校验不过"（实机踩过，见 A19）。
        /// </summary>
        private static PackageReport ValidateQuietly(PackageReloader reloader, string name)
        {
            if (reloader == null || string.IsNullOrEmpty(name))
                return null;
            try
            {
                return reloader.Validate(name);
            }
            catch (Exception)
            {
                return null;
            }
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
                    AiRecordingSelfTest.Run(), TreeEditSelfTest.Run(), ActionSelfTest.Run(), StateSelfTest.Run(), LayaSelfTest.Run(), PoolSelfTest.Run(), AutoSaveSelfTest.Run(), AssetSelfTest.Run(), AssetRetrySelfTest.Run(), PhaseSelfTest.Run());
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
