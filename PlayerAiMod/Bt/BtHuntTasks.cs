using System;
using System.Collections.Generic;
using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// **换武器**：把最好用的近战家伙拿到手上再开打（用户 2026-09-26 的要求）。
    ///
    /// 为什么值得单独立一个节点：SC 的近战伤害 = `手上物品的 GetMeleePower × 攻击力 ÷ 目标韧性`
    /// （`ComponentMiner.cs:311`），空手/拿方块打大牲口几乎打不动 —— 换一把刀不是"优化"，
    /// 是"能不能打死"的前提。取什么、从哪取，全在 <see cref="PlayerEquipment"/> 里说清楚。
    ///
    /// `required=false` 时"没武器"也判成功：拿不到刀不该让整条打猎分支失败，
    /// 顶多是打得慢（日志里会写清 `not equipped: ...`）。
    /// </summary>
    public sealed class BtEquipWeaponTask : BtTaskNode
    {
        /// <summary>武器优先级（逗号分隔，从好到差）。默认钻石砍刀优先。</summary>
        public string Priority { get; set; } = PlayerEquipment.DefaultPriority;

        /// <summary>创造模式下允许直接取（引擎自带的"取物品"路径）。</summary>
        public bool AllowCreativeGrab { get; set; } = true;

        /// <summary>把武器名写进哪个黑板键（空 = 不写）。</summary>
        public string WeaponKey { get; set; }

        /// <summary>拿不到武器时是否判失败（默认否：打得慢也好过不打）。</summary>
        public bool Required { get; set; }

        public override string NodeType
        {
            get { return "Task.EquipWeapon"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        private BtResult Step(BtContext context)
        {
            // ⚠️ 运行时拿到的是**控制器传感器**（包装层），不是 PlayerSensor ——
            //    所以要一路剥到真实观察层（`PlayerEquipment.FindPlayerSensor`）；
            //    直接 `as PlayerSensor` 会永远得到 null，表现是"任务跑了、什么都不做、也不报错"。
            PlayerSensor sensor = PlayerEquipment.FindPlayerSensor(context.Sensors);
            if (sensor == null || sensor.PlayerOrNull == null)
            {
                // 自检（桩传感器）与无头环境走这条：如实说"没拿到玩家"，并按要求决定成败。
                // 用 Log 不用 Warn：Warn 不进 PlayerAi.log，而"为什么没换上武器"是必须能看到的
                context.Log("EquipWeapon: no real player sensor (stub or headless) -> nothing equipped");
                if (!string.IsNullOrEmpty(WeaponKey) && context.Blackboard != null)
                    context.Blackboard.Set(new AiBlackboardKey<string>(WeaponKey), string.Empty);
                return Required ? BtResult.Failed : BtResult.Succeeded;
            }

            PlayerEquipment.WeaponResult result = PlayerEquipment.TryEquip(
                sensor.PlayerOrNull, Priority, AllowCreativeGrab);

            if (!string.IsNullOrEmpty(WeaponKey) && context.Blackboard != null)
                context.Blackboard.Set(new AiBlackboardKey<string>(WeaponKey),
                    result.Equipped ? result.Name : string.Empty);

            context.Log("EquipWeapon: " + result.Describe());
            if (result.Equipped)
                return BtResult.Succeeded;
            return Required ? BtResult.Failed : BtResult.Succeeded;
        }
    }

    /// <summary>
    /// **打猎回传**：把"打死没有"变成一条能读到的结果（日志 + 黑板 + 事件日志）。
    ///
    /// 用户 2026-09-26 的要求原话是"不需要设置打死的时间，只需要检查动物是否打死，回传就行了" ——
    /// 于是判据是**血量**（`Task.Attack` 边打边采样，`ComponentHealth.Health` 到 0 即死亡），
    /// 这里只负责把结论落成三个键：
    ///   · `hunt.result`：人读的一句话（`killed Black Bull (total 2)` / `lost Black Bull`）；
    ///   · `hunt.kills`：本次指令累计打死几只（`Round` 模式下每打死一只 +1）；
    ///   · 事件日志一行 `hunt`，于是 `sccmd raw ai.logs` 里能直接看到。
    ///
    /// `Summary=true` 时不计数、只把累计结果落一遍 —— 放在打猎分支的末尾当总结。
    /// </summary>
    public sealed class BtHuntReportTask : BtTaskNode
    {
        /// <summary>`Task.Attack` 写进来的"打死了吗"（bool）。</summary>
        public string KilledKey { get; set; } = "hunt.killed";

        /// <summary>目标物种名（服务写的锁定物种）。</summary>
        public string SpeciesKey { get; set; } = "hunt.species";

        /// <summary>累计打死数量（本节点自己维护）。</summary>
        public string CountKey { get; set; } = "hunt.kills";

        /// <summary>给人读的结果写进哪个键。</summary>
        public string ResultKey { get; set; } = "hunt.result";

        /// <summary>总结模式：不计数，只报告累计结果。</summary>
        public bool Summary { get; set; }

        public override string NodeType
        {
            get { return "Task.HuntReport"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            return Step(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            return Step(context);
        }

        private BtResult Step(BtContext context)
        {
            AiBlackboard board = context.Blackboard;
            if (board == null)
                return BtResult.Succeeded;

            string species = ReadString(board, SpeciesKey);
            int kills = ReadInt(board, CountKey);
            string result;

            if (Summary)
            {
                result = kills > 0
                    ? "killed " + kills + " x " + Describe(species)
                    : "nothing killed" + (string.IsNullOrEmpty(species) ? string.Empty : " (" + species + ")");
            }
            else
            {
                bool killed;
                bool known = board.TryGet(KilledKey, out killed);
                if (known && killed)
                {
                    kills++;
                    board.Set(new AiBlackboardKey<int>(CountKey), kills);
                    result = "killed " + Describe(species) + " (total " + kills + ")";
                }
                else
                {
                    result = "lost " + Describe(species)
                        + (known ? " (it got away before its health reached 0)" : " (no health sample)");
                }
            }

            board.Set(new AiBlackboardKey<string>(ResultKey), result);
            context.Log("HuntReport: " + result);
            // 事件日志单独留一行：`sccmd raw ai.logs` 里能直接看到"打死了没有"
            AiEventLog eventLog = context.Runtime != null ? context.Runtime.EventLog : null;
            if (eventLog != null)
                eventLog.Write("hunt", result);
            return BtResult.Succeeded;
        }

        private static string Describe(string species)
        {
            return string.IsNullOrEmpty(species) ? "the target" : species;
        }

        private static string ReadString(AiBlackboard board, string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            string value;
            return board.TryGet(key, out value) ? value : null;
        }

        private static int ReadInt(AiBlackboard board, string key)
        {
            if (string.IsNullOrEmpty(key))
                return 0;
            int value;
            return board.TryGet(key, out value) ? value : 0;
        }
    }
}
