using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂动作脚本的**文件名常量**（`&lt;名字&gt;.aeact`），单独一份。
    ///
    /// **为什么不能直接引用 `ActionScriptTemplates` 里的常量**：脚本模板本体要 `Engine.Log`
    /// （安装时要写日志），属于游戏侧胶水 → **编辑器不编它**（`PlayerAiEditor.csproj` 的 Exclude）。
    /// 而编辑器要编的 `PackageTemplates`（出厂树包）需要这些名字来写"目标 → 脚本"那张映射表。
    /// 抄字面量的后果是"改名只改一处"：脚本换了名字、树包还指着旧名 —— 运行时表现是
    /// `action.fail=step_failed`，而两边代码看上去都对。所以常量放在**两边都编得到的地方**
    /// （与 `QuestionBank.FolderName` / `AssetFailureCodes.FailKey` 同一个理由，A29 的老坑）。
    /// </summary>
    public static class ActionScriptNames
    {
        // 通用示例
        public const string MineOnce = "mine_stone_once" + ActionScript.Extension;
        public const string TurnAround = "turn_around" + ActionScript.Extension;
        public const string UiClickPlay = "ui_click_play" + ActionScript.Extension;
        public const string FromBlackboard = "run_script_from_blackboard" + ActionScript.Extension;

        // 目标动作包（`world_goal.qbank` 的选项 → 这些脚本，见 PackageTemplates 的映射表）
        public const string EatOnce = "eat_once" + ActionScript.Extension;
        public const string SleepOnce = "sleep_once" + ActionScript.Extension;
        public const string AttackOnce = "attack_once" + ActionScript.Extension;
        public const string FleeOnce = "flee_once" + ActionScript.Extension;
        public const string GatherOnce = "gather_once" + ActionScript.Extension;
        public const string ExploreOnce = "explore_once" + ActionScript.Extension;
        public const string CraftOnce = "craft_once" + ActionScript.Extension;
    }
}
