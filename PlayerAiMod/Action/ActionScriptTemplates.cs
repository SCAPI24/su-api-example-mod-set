using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂示例动作脚本（P1）：随 Mod 安装到 `<实例根>/PlayerAi/Scripts/`。
    ///
    /// 与 <see cref="PackageTemplates"/> 同一套约定：**缺什么补什么，绝不覆盖**
    /// （用户/编辑器和 AI 改过的脚本不会被 Mod 更新冲掉）。
    ///
    /// 为什么要有出厂脚本：动作层光有代码是"看不见"的 —— 装完 Mod 应当能立刻
    /// `sccmd ai.action.script.list` 看到东西、`ai.action.script.run name=...` 跑起来。
    /// </summary>
    public static class ActionScriptTemplates
    {
        public const string ScriptsFolder = "Scripts";

        /// <summary>挖一次石头：选镐子 → 瞄准星目标 → 挖 0.9 秒 → 等一下。</summary>
        public const string MineOnceFile = ActionScriptNames.MineOnce;

        /// <summary>挥一下手（转向指定角度）。</summary>
        public const string TurnFile = ActionScriptNames.TurnAround;

        /// <summary>界面脚本示例：点一个 UI 目标（世界外也能用）。</summary>
        public const string UiClickFile = ActionScriptNames.UiClickPlay;

        /// <summary>一条"看情况跑脚本"的示例：脚本名从黑板键取（Laya 判定的落点）。</summary>
        public const string FromBlackboardFile = ActionScriptNames.FromBlackboard;

        // ---------------------------------------------------------------- 目标动作包（Laya 决定的落点）
        //
        // `world_goal.qbank` 的八个选项各对应下面一条脚本：**Laya 只选枚举，具体动作是 C# 确定性给的**
        // （plan §4.9 纪律：模型不输出时长/角度/坐标）。它们也是"基础动作包混搭"的最小样例 ——
        // 每条都只是几个 verb 串起来，读一遍就知道怎么拼自己的。
        // 文件名常量在 ActionScriptNames（出厂树包也要引用它们，而本文件编辑器不编）。

        /// <summary>`goal=eat` → 吃一次（复合 verb）。</summary>
        public const string EatOnceFile = ActionScriptNames.EatOnce;

        /// <summary>`goal=sleep` → 睡觉（复合 verb：开衣物面板 → 点睡觉）。</summary>
        public const string SleepOnceFile = ActionScriptNames.SleepOnce;

        /// <summary>`goal=fight` → 看向最近的实体并打一下。</summary>
        public const string AttackOnceFile = ActionScriptNames.AttackOnce;

        /// <summary>`goal=flee` → 转身 + 跑两段（远离威胁）。</summary>
        public const string FleeOnceFile = ActionScriptNames.FleeOnce;

        /// <summary>`goal=gather` → 走上前与前方目标交互两次（捡东西）。</summary>
        public const string GatherOnceFile = ActionScriptNames.GatherOnce;

        /// <summary>`goal=explore` → 换个方向走一段 + 跳一下。</summary>
        public const string ExploreOnceFile = ActionScriptNames.ExploreOnce;

        /// <summary>
        /// `goal=craft` → **占位**：开/关背包看一眼。
        /// 真正的合成配方 UI 交互还没做（合成要选配方 + 点产物格），所以这条明说是占位，
        /// 而不是假装能合成 —— 用户看到 `goal=craft` 时就知道该去编辑器里把这条换成自己的。
        /// </summary>
        public const string CraftOnceFile = ActionScriptNames.CraftOnce;

        public static Dictionary<string, string> All()
        {
            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            scripts[MineOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\",\r\n"
                + "  \"version\": 1,\r\n"
                + "  \"id\": \"mine_stone_once\",\r\n"
                + "  \"name\": \"挖一次石头\",\r\n"
                + "  \"description\": \"选镐子 → 瞄向准星目标 → 持续挖掘 → 停一下。演示：参数化目标 + 看门狗。\",\r\n"
                // `aim.block`：**没瞄着方块就别挖**（第 41 轮的实测结论：模型会对"什么都没瞄"答 mine，
                // 所以可行性由 C# 把住，Laya 只决定"做哪一件"）。
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"aim.block\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"hotbar\", \"slot\": 1 },\r\n"
                + "    { \"verb\": \"lookAt\", \"target\": \"aim\" },\r\n"
                + "    { \"verb\": \"dig\", \"ms\": 900, \"timeoutMs\": 3000 },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 150 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[TurnFile] =
                "{\r\n"
                + "  \"format\": \"aea\",\r\n"
                + "  \"version\": 1,\r\n"
                + "  \"id\": \"turn_around\",\r\n"
                + "  \"name\": \"原地转身\",\r\n"
                + "  \"description\": \"走两步、转个身、再走回来。演示：基础动作混搭（move/turn 组合）。\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 400 },\r\n"
                + "    { \"verb\": \"turn\", \"dyaw\": 180, \"ms\": 150 },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 100 },\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 400 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[UiClickFile] =
                "{\r\n"
                + "  \"format\": \"aea\",\r\n"
                + "  \"version\": 1,\r\n"
                + "  \"id\": \"ui_click_play\",\r\n"
                + "  \"name\": \"点主菜单的 Play\",\r\n"
                + "  \"description\": \"纯界面脚本：世界外也能跑（走 CmdBridge 的 UI 语义目标，点击那一刻现解析坐标）。\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 300 },\r\n"
                + "    { \"verb\": \"ui\", \"click\": \"Play\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 400 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[FromBlackboardFile] =
                "{\r\n"
                + "  \"format\": \"aea\",\r\n"
                + "  \"version\": 1,\r\n"
                + "  \"id\": \"run_script_from_blackboard\",\r\n"
                + "  \"name\": \"按黑板里的脚本名执行\",\r\n"
                + "  \"description\": \"演示脚本参数化：这里只放一个占位动作；真正跑哪条脚本由行为树节点 Task.RunActionScript 的 scriptKey 决定。\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"jump\" }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[EatOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"eat_once\",\r\n"
                + "  \"name\": \"吃一次东西\",\r\n"
                + "  \"description\": \"goal=eat 的落点。复合 verb：选食物槽位 → 瞄向空地 → 右键一次。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"modal.none\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"eat\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 250 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[SleepOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"sleep_once\",\r\n"
                + "  \"name\": \"睡觉\",\r\n"
                + "  \"description\": \"goal=sleep 的落点。sleep 是复合 verb：开衣物面板 → 等按钮 → 点睡觉。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"dialog.none\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"sleep\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 300 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[AttackOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"attack_once\",\r\n"
                + "  \"name\": \"看向目标打一下\",\r\n"
                + "  \"description\": \"goal=fight 的落点。target=entity 由 C# 解析成最近的目标（模型不给坐标）。\",\r\n"
                // `aim.entity`：**没瞄着生物就别打**（与挖矿那条同源：可行性由 C# 把住）
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"modal.none\", \"aim.entity\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"attack\", \"target\": \"entity\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 200 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[FleeOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"flee_once\",\r\n"
                + "  \"name\": \"转身跑开\",\r\n"
                + "  \"description\": \"goal=flee 的落点。转身 180 度再跑两段：方向由玩家当时的朝向决定，脚本不写死。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"turn\", \"dyaw\": 180 },\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 1200 },\r\n"
                + "    { \"verb\": \"turn\", \"dyaw\": 90 },\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 600 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[GatherOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"gather_once\",\r\n"
                + "  \"name\": \"捡身前的东西\",\r\n"
                + "  \"description\": \"goal=gather 的落点。走上前 + 右键交互两次（掉在地上的东西要交互才捡得起来）。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"modal.none\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 400 },\r\n"
                + "    { \"verb\": \"interact\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 200 },\r\n"
                + "    { \"verb\": \"interact\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 200 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[ExploreOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"explore_once\",\r\n"
                + "  \"name\": \"换个方向走一段\",\r\n"
                + "  \"description\": \"goal=explore 的落点。转 90 度 + 走 1.5 秒 + 跳一下（翻过一格台阶）。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"turn\", \"dyaw\": 90 },\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 1500 },\r\n"
                + "    { \"verb\": \"jump\" },\r\n"
                + "    { \"verb\": \"move\", \"dir\": \"f\", \"ms\": 600 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            scripts[CraftOnceFile] =
                "{\r\n"
                + "  \"format\": \"aea\", \"version\": 1, \"id\": \"craft_once\",\r\n"
                + "  \"name\": \"看背包（合成占位）\",\r\n"
                + "  \"description\": \"goal=craft 的**占位**动作：开背包 → 等一下 → 关掉。真正的配方 UI 交互（选配方 + 点产物格）还没做，"
                + "在编辑器里把这条换掉即可。\",\r\n"
                + "  \"guards\": [\"player.alive\", \"world.loaded\", \"dialog.none\"],\r\n"
                + "  \"onFail\": \"abort\",\r\n"
                + "  \"steps\": [\r\n"
                + "    { \"verb\": \"openInventory\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 400 },\r\n"
                + "    { \"verb\": \"openInventory\" },\r\n"
                + "    { \"verb\": \"wait\", \"ms\": 200 }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            return scripts;
        }

        /// <summary>
        /// 安装到 `<实例根>/PlayerAi/Scripts/`。返回写入的文件数；
        /// **已存在的文件一律不动**（用户/AI 改过的内容优先）。
        /// </summary>
        public static int Install(string instanceRoot, out List<string> installed, out string error)
        {
            installed = new List<string>();
            error = null;
            if (string.IsNullOrEmpty(instanceRoot))
            {
                error = "no instance root";
                return 0;
            }

            string directory = Path.Combine(instanceRoot, "PlayerAi", ScriptsFolder);
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                error = "cannot create " + directory + ": " + exception.Message;
                return 0;
            }

            int written = 0;
            var utf8 = new UTF8Encoding(false); // 与包格式一致：UTF-8 无 BOM
            foreach (KeyValuePair<string, string> pair in All())
            {
                string path = Path.Combine(directory, pair.Key);
                if (File.Exists(path))
                    continue;
                try
                {
                    File.WriteAllText(path, pair.Value, utf8);
                    installed.Add(path);
                    written++;
                }
                catch (Exception exception)
                {
                    error = "cannot write " + path + ": " + exception.Message;
                }
            }

            if (written > 0)
            {
                Engine.Log.Information("[PlayerAi][act] installed " + written
                    + " example action script(s) into " + directory);
            }
            return written;
        }

        /// <summary>脚本目录（与安装点一致；运行时的搜索顺序见 <see cref="ActionScriptLibrary"/>）。</summary>
        public static string DirectoryFor(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return Path.Combine(instanceRoot, "PlayerAi", ScriptsFolder);
        }
    }
}
