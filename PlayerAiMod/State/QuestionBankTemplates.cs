using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 出厂问题库（P2）：随 Mod 安装到 `<实例根>/PlayerAi/Questions/`。
    ///
    /// 与包/脚本模板同一套约定：**缺什么补什么，绝不覆盖**（用户/LLM 调过的措辞不能被升级冲掉）。
    ///
    /// 措辞纪律（都来自实测，见 plan §4.3/§5.4）：
    ///   · **是非题一律写成两项 `choice`** —— `noul` 那种问法实测会答错；
    ///   · 每个选项都要有 `description`（描述才是模型判别的东西）；
    ///   · 选项数 4~8（4→9/10、8→7/10、12→5/10）；
    ///   · 选项 `key` 是**稳定 id**：可以改描述，不要改 key（改了旧树/旧黑板就对不上了）。
    /// </summary>
    public static class QuestionBankTemplates
    {
        public const string QuestionsFolder = QuestionBank.FolderName;

        /// <summary>世界内的主问题库：下一步做什么 + 威胁/进食两个门。</summary>
        public const string WorldGoalFile = QuestionBank.WorldGoalFileName;

        /// <summary>世界外的界面问题库：这一步在界面上做什么。</summary>
        public const string FrontGoalFile = QuestionBank.FrontGoalFileName;

        /// <summary>
        /// **异常处理专用问题库**（plan §4.12）：拿不到资源时问"现在怎么办"。
        ///
        /// 只在**确实有可选路径**时问；`retry` 与 `use_backup` 的**具体动作仍由 C# 确定性执行**
        /// （§4.9：Laya 给枚举，C# 给具体值）。Laya 自己不可用时**不会**走它 ——
        /// 否则就成了"服务都连不上，还去问服务怎么办"的死循环。
        /// </summary>
        public const string PkgRecoverFile = QuestionBank.RecoverBankFileName;

        /// <summary>
        /// **聊天兜底问题库**（2026-09-26）：短语表没命中时，用"那句话"问一次模型。
        ///
        /// 与其它库的区别只有一处、但很关键：**它的摘要不是角色状态，而是那句话**
        /// （树里写 `Task.LayaAsk digestKey=chat.digest`）—— 往 `world_goal` 的摘要里
        /// 塞聊天原文会把那个问题的答案带偏（§12.1：摘要多一个 token 就能翻面）。
        ///
        /// 选项 key 与 `ChatIntents`（C# 的闭集）一一对应：模型只能在这几个里选，
        /// 而"选完之后具体怎么走/走多远"全是 C# 的事（§4.9）。
        ///
        /// 2026-09-26 加了第五个 `hunt`：**只让模型判"这是不是让我去打猎"，打谁不由它说** ——
        /// 物种词由 `ChatAnimalAliases`（`animals.json`）在 C# 侧解析。让模型编物种名的后果是
        /// "答了却什么都不发生"（§4.7），而且它编得出树里没有的动物。
        /// 5 选项仍在前面的精度区间内（实测 4→9/10；再多就该拆问题了）。
        /// </summary>
        public const string ChatIntentFile = QuestionBank.ChatIntentFileName;

        public static Dictionary<string, string> All()
        {
            var banks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            banks[WorldGoalFile] =
                "{\r\n"
                + "  \"format\": \"qbank\", \"version\": 1,\r\n"
                + "  \"id\": \"world_goal\", \"name\": \"世界内·下一步做什么\",\r\n"
                + "  \"description\": \"选项 key 稳定；描述可改（改描述会改变判准，属正常调参）。\",\r\n"
                + "  \"questions\": [\r\n"
                + "    { \"id\": \"goal\", \"type\": \"choice\",\r\n"
                + "      \"instructions\": \"What should this character do next?\",\r\n"
                + "      \"options\": [\r\n"
                + "        { \"key\": \"mine\",    \"description\": \"break the block being aimed at\" },\r\n"
                + "        { \"key\": \"gather\",  \"description\": \"pick up nearby items\" },\r\n"
                + "        { \"key\": \"craft\",   \"description\": \"craft from the inventory\" },\r\n"
                + "        { \"key\": \"eat\",     \"description\": \"consume food now\" },\r\n"
                + "        { \"key\": \"sleep\",   \"description\": \"go to sleep\" },\r\n"
                + "        { \"key\": \"fight\",   \"description\": \"attack the hostile\" },\r\n"
                + "        { \"key\": \"flee\",    \"description\": \"run away from danger\" },\r\n"
                + "        { \"key\": \"explore\", \"description\": \"walk to new terrain\" }\r\n"
                + "      ] }\r\n"
                // **删掉了 `threat` 与 `can_reach` 两问**（2026-09-26 实测后，plan §12.1/§12.3#1）。
                //
                // 它们在这套"条件语言"里是**断言式（是非）问题**，而这个模型对断言式问题
                // **根本不看状态**：2 选项恒 `yes`、3 选项恒 `none`，与状态和选项顺序都无关
                //（PC 与平板逐条复现；真实决策循环里 43/43 全是 `threat=yes can_reach=yes`）。
                // 树**从来没用过**它们（分支只按 `goal` 走），出厂 demo 也已改成 `only=goal`。
                // 留着它们只有一个后果：**下一任作者照着库里有的问题去用，然后拿到恒真的门**。
                // 需要的那两件事本来就折在 `goal` 的动作选项里（`fight`/`flee`/`gather`…）。
                + "  ]\r\n"
                + "}\r\n";

            banks[FrontGoalFile] =
                "{\r\n"
                + "  \"format\": \"qbank\", \"version\": 1,\r\n"
                + "  \"id\": \"front_goal\", \"name\": \"世界外·界面该做什么\",\r\n"
                + "  \"description\": \"世界外没有角色与地图，问题只问界面；翻页/滚动由 C# 做，模型只选序号。\",\r\n"
                + "  \"questions\": [\r\n"
                + "    { \"id\": \"front_goal\", \"type\": \"choice\",\r\n"
                + "      \"instructions\": \"What should be done on this menu screen?\",\r\n"
                + "      \"options\": [\r\n"
                + "        { \"key\": \"open_ui\", \"description\": \"press the main button on this screen\" },\r\n"
                + "        { \"key\": \"back\",    \"description\": \"go back to the previous screen\" },\r\n"
                + "        { \"key\": \"wait\",    \"description\": \"stand by; the screen is still animating\" },\r\n"
                + "        { \"key\": \"cancel\",  \"description\": \"dismiss the dialog or cancel\" }\r\n"

                + "      ] },\r\n"
                + "    { \"id\": \"dialog_action\", \"type\": \"choice\",\r\n"
                + "      \"instructions\": \"A dialog is open. What should be done with it?\",\r\n"
                + "      \"options\": [\r\n"
                + "        { \"key\": \"confirm\", \"description\": \"accept the dialog\" },\r\n"
                + "        { \"key\": \"cancel\",  \"description\": \"dismiss the dialog\" }\r\n"
                + "      ] }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            banks[PkgRecoverFile] =
                "{\r\n"
                + "  \"format\": \"qbank\", \"version\": 1,\r\n"
                + "  \"id\": \"pkg_recover\", \"name\": \"异常处理·资源拿不到怎么办\",\r\n"
                + "  \"description\": \"只在确实有可选路径时问；retry/use_backup 的具体动作由 C# 确定性执行。Laya 不可用时不会问它。\",\r\n"
                + "  \"questions\": [\r\n"
                + "    { \"id\": \"recover\", \"type\": \"choice\",\r\n"
                + "      \"instructions\": \"A required file for the current plan is unavailable. What now?\",\r\n"
                + "      \"options\": [\r\n"
                + "        { \"key\": \"retry\",      \"description\": \"wait and try the same file again\" },\r\n"
                + "        { \"key\": \"use_backup\", \"description\": \"switch to the backup question bank or action set\" },\r\n"
                + "        { \"key\": \"skip_step\",  \"description\": \"skip this step and continue the plan\" },\r\n"
                + "        { \"key\": \"abort_plan\", \"description\": \"stop the plan and stand by\" }\r\n"
                + "      ] }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            banks[ChatIntentFile] =
                "{\r\n"
                + "  \"format\": \"qbank\", \"version\": 1,\r\n"
                + "  \"id\": \"chat_intent\", \"name\": \"聊天·这句话要我做什么\",\r\n"
                + "  \"description\": \"只判「这句话是不是给我的指令」；摘要是那句话本身（digestKey=chat.digest），不是角色状态。\",\r\n"
                + "  \"questions\": [\r\n"
                + "    { \"id\": \"chat_intent\", \"type\": \"choice\",\r\n"
                + "      \"instructions\": \"Someone said this in chat. What should this character do?\",\r\n"
                + "      \"options\": [\r\n"
                + "        { \"key\": \"come\",   \"description\": \"go to the speaker and stop near them\" },\r\n"
                + "        { \"key\": \"follow\", \"description\": \"follow the speaker around\" },\r\n"
                + "        { \"key\": \"stop\",   \"description\": \"stop moving and stay where it is\" },\r\n"
                + "        { \"key\": \"hunt\",   \"description\": \"kill an animal the speaker named (the animal name is resolved outside the model)\" },\r\n"
                + "        { \"key\": \"ignore\", \"description\": \"not an instruction; keep doing its own work\" }\r\n"
                + "      ] }\r\n"
                + "  ]\r\n"
                + "}\r\n";

            return banks;
        }
        /// <summary>安装到 `<实例根>/PlayerAi/Questions/`；**已存在的一律不动**。</summary>
        public static int Install(string instanceRoot, out List<string> installed, out string error)
        {
            installed = new List<string>();
            error = null;
            if (string.IsNullOrEmpty(instanceRoot))
            {
                error = "no instance root";
                return 0;
            }

            string directory = DirectoryFor(instanceRoot);
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
            var utf8 = new UTF8Encoding(false);
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
                Engine.Log.Information("[PlayerAi][state] installed " + written
                    + " example question bank(s) into " + directory);
            }
            return written;
        }

        public static string DirectoryFor(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return Path.Combine(instanceRoot, "PlayerAi", QuestionsFolder);
        }
    }
}
