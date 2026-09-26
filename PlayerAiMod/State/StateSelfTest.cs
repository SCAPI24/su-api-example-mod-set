using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 状态摘要 + 问题库自检（P2）：**纯逻辑、不依赖游戏**。
    ///
    /// 为什么这两块特别需要自检：
    ///   · 摘要字段是**长期要调参**的东西（实测"摘要长度会改变决策"），规则必须能逐条钉住；
    ///   · 预算守卫是**唯一能拦住"静默截断"的防线**（超长 state 不报错、只截断），
    ///     它一旦失灵，线上表现是"答案莫名其妙变差"，最难查。
    /// </summary>
    public static class StateSelfTest
    {
        public static BtSelfTest.TestResult Run()
        {
            var result = new BtSelfTest.TestResult { Label = "StateSelfTest" };

            try { CaseBands(result); } catch (Exception e) { result.Check("case:bands", false, e.Message); }
            try { CaseDigestShape(result); } catch (Exception e) { result.Check("case:digest shape", false, e.Message); }
            try { CaseDigestBudget(result); } catch (Exception e) { result.Check("case:digest budget", false, e.Message); }
            try { CaseNoDump(result); } catch (Exception e) { result.Check("case:no dump", false, e.Message); }
            try { CaseQuestionBankParse(result); } catch (Exception e) { result.Check("case:question bank parse", false, e.Message); }
            try { CaseBudgetGuard(result); } catch (Exception e) { result.Check("case:budget guard", false, e.Message); }
            try { CaseWireShape(result); } catch (Exception e) { result.Check("case:wire shape", false, e.Message); }
            try { CaseEvaluateMatchesDigest(result); } catch (Exception e) { result.Check("case:evaluate vs digest", false, e.Message); }
            try { CaseUiList(result); } catch (Exception e) { result.Check("case:ui list", false, e.Message); }
            try { CaseHudStatus(result); } catch (Exception e) { result.Check("case:hud status", false, e.Message); }
            try { CaseWireState(result); } catch (Exception e) { result.Check("case:wire state", false, e.Message); }

            return result;
        }

        /// <summary>
        /// G19：HUD 那一行状态。它是**给人看的**，所以规则比摘要更"死"：
        /// 一行、无空格、只 ASCII、超预算从后往前丢并留 `+N`。
        /// 最容易写错的两条：暂停时还得更新（否则人看到的是上一次状态），
        /// 以及"AI 关掉了"要**整行隐藏**而不是显示 idle（否则人以为它还活着）。
        /// </summary>
        private static void CaseHudStatus(BtSelfTest.TestResult result)
        {
            // 关掉 = 整行不显示（`Visible=false` → null）
            result.Check("an inactive AI shows no line at all (not 'idle')",
                HudStatusLine.Compile(new HudStatusFacts { Visible = false }) == null);
            result.Check("null facts compile to null (no crash)",
                HudStatusLine.Compile(null) == null);

            // 世界里正常跑：最重要的几项都在，且按优先级排列
            string run = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true,
                Phase = "world",
                Mode = "control",
                TreeId = "demo.laya",
                TreeRunning = true,
                Loops = 11,
                Node = "go_mine",
                Action = "mine_stone_once",
                ActionSeconds = 2.14,
                FailCount = 2
            });
            result.Check("a running world line reads as expected",
                run == "ai=run phase=world mode=control tree=demo.laya#11 "
                    + "act=mine_stone_once@2.1s node=go_mine fail=2", run);
            result.Check("the line is one token per item (no spaces inside values)",
                run != null && run.Split(' ').Length == 7, run);

            // 暂停：`ai=` 要立刻说 paused，并带上原因（原因会被消毒）
            string paused = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Paused = true, PauseReason = "user asked", Phase = "world",
                Mode = "control", TreeId = "demo.laya", TreeRunning = false
            });
            result.Check("*** a paused AI says so on the first token ***",
                paused != null && paused.StartsWith("ai=paused:user_asked ", StringComparison.Ordinal), paused);
            result.Check("paused wins over 'not running' (idle would be misleading)",
                paused != null && paused.IndexOf("ai=idle", StringComparison.Ordinal) < 0, paused);

            // 过渡态闸门：不是卡死，是一个正常状态，要能看出来
            string gated = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "loading", GateActive = true, Mode = "control",
                TreeRunning = false
            });
            result.Check("the loading gate reads as 'gated', not 'idle'",
                gated != null && gated.StartsWith("ai=gated ", StringComparison.Ordinal), gated);

            // 等 Laya：thinking + ask=秒数
            string thinking = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "world", Mode = "control", TreeId = "demo.laya",
                TreeRunning = true, LayaBusy = true, LayaSeconds = 0.42, LayaQuestions = 3
            });
            result.Check("an in-flight ask shows as 'thinking' with its wait time",
                thinking != null && thinking.StartsWith("ai=thinking ", StringComparison.Ordinal)
                && thinking.Contains("ask=3q@0.4s"), thinking);

            // 预算：长了就从后往前丢，并且**丢了几项要看得见**
            string crowded = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "world", Mode = "control",
                TreeId = "a_very_long_tree_package_name_here",
                TreeRunning = true, Loops = 123456, Node = "go_somewhere_far_away",
                Action = "an_extremely_long_action_script_name",
                LayaBusy = true, LayaSeconds = 12.3, FailCount = 7,
                Error = "guard_failed - guard not satisfied: player.alive"
            });
            result.Check("a crowded line stays inside the budget",
                crowded != null && crowded.Length <= HudStatusLine.MaxChars + 3,
                "len=" + (crowded == null ? 0 : crowded.Length) + " :: " + crowded);
            result.Check("...and says how many items it dropped",
                crowded != null && crowded.Contains(" +"), crowded);
            result.Check("...but never drops the leading state token",
                crowded != null && crowded.StartsWith("ai=", StringComparison.Ordinal), crowded);

            // 错误信息会被截断 + 消毒（它常是一整句话，还带空格）
            string withError = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "world", Mode = "control", TreeRunning = true,
                Error = "laya_timeout: the model did not answer in 4000ms"
            });
            result.Check("a long error is truncated and tokenised",
                withError != null && withError.Contains("err=laya_timeout:")
                && withError.Substring(withError.IndexOf("err=", StringComparison.Ordinal)).IndexOf(' ') < 0, withError);

            // ---- 实机踩到的两个：任何值里的空格都会把一个值撑成好几个 token
            string withSpaces = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "world", Mode = "control", TreeId = "demo laya",
                TreeRunning = true, Node = "Root#root > Selector#sel > Task.Wait#idle",
                Action = "mine stone once", ActionSeconds = 1.0
            });
            result.Check("*** no value can smuggle a space into the line (it would forge tokens) ***",
                withSpaces != null && withSpaces.IndexOf("demo_laya", StringComparison.Ordinal) >= 0
                && withSpaces.IndexOf('>') < 0, withSpaces);
            result.Check("...and every token still looks like key=value",
                withSpaces != null && AllTokensArePairs(withSpaces), withSpaces);
            result.Check("*** ...and a long node value cannot crowd out the action ***",
                withSpaces != null && withSpaces.IndexOf("act=mine_stone_once@1.0s", StringComparison.Ordinal) >= 0,
                withSpaces);

            // 没有活动节点时，那个哨兵不该被当成一个节点名显示出来
            string noneNode = HudStatusLine.Compile(new HudStatusFacts
            {
                Visible = true, Phase = "front", Mode = "idle", Node = "<none>"
            });
            result.Check("the '<none>' path sentinel is never shown as a node",
                noneNode != null && noneNode.IndexOf("node=", StringComparison.Ordinal) < 0, noneNode);
        }

        /// <summary>
        /// **上线那条状态**（wire）的两条语义：① 良性值不发；② 顺序按紧迫程度。
        ///
        /// 为什么必须有这一组：短状态只放得下一个事实，而"放哪个"以前是按字段类型定的 ——
        /// 实测出现过"角色已经死了、在挨饿、还在挨冻，而发上去的却是 `aim=Grass@6.58`"。
        /// 同时钉住反向纪律：**黑板（Evaluate）与人工摘要不许跟着变短**，
        /// 否则树里按 `state.hp` 分流的条件会在角色健康时静默失效。
        /// </summary>
        private static void CaseWireState(BtSelfTest.TestResult result)
        {
            var healthy = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 1f, Stamina = 1f, Sleep = 0.1f,
                Temperature = 12f, Wetness = 0.05f, IsNight = false,
                AimKind = "block", AimBlockType = "StoneBlock", AimDistance = 2f,
                HoldingBlockType = "StonePickaxeBlock", Screen = "GameScreen"
            };
            string wire = StateDigestCompiler.Compile(healthy, 28, wire: true);
            result.Check("*** a healthy character's wire state leads with what is in front of it ***",
                wire.StartsWith("aim=", StringComparison.Ordinal), wire);
            result.Check("...and it fits the measured-good length",
                wire.Length <= 28 + 4, "len=" + wire.Length + " :: " + wire);
            result.Check("...with the benign vitals left out entirely",
                wire.IndexOf("hp=", StringComparison.Ordinal) < 0
                && wire.IndexOf("night=", StringComparison.Ordinal) < 0
                && wire.IndexOf("temp=", StringComparison.Ordinal) < 0, wire);

            var hungry = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 0.05f, Stamina = 1f, Sleep = 0.1f,
                Temperature = 12f, Wetness = 0.05f, IsNight = false,
                AimKind = "block", AimBlockType = "StoneBlock", AimDistance = 2f,
                Screen = "GameScreen"
            };
            result.Check("*** a starving character's wire state leads with food ***",
                StateDigestCompiler.Compile(hungry, 28, wire: true).StartsWith("food=", StringComparison.Ordinal),
                StateDigestCompiler.Compile(hungry, 28, wire: true));

            var hurt = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 0.2f, Food = 1f, Stamina = 1f, Sleep = 0.1f,
                Temperature = 12f, Wetness = 0.05f, IsNight = false,
                AimKind = "block", AimBlockType = "StoneBlock", AimDistance = 2f,
                Screen = "GameScreen"
            };
            result.Check("*** a hurt character's wire state leads with hp ***",
                StateDigestCompiler.Compile(hurt, 28, wire: true).StartsWith("hp=", StringComparison.Ordinal),
                StateDigestCompiler.Compile(hurt, 28, wire: true));

            var sleepy = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 1f, Stamina = 1f, Sleep = 0.9f,
                Temperature = 12f, Wetness = 0.05f, IsNight = true,
                AimKind = "block", AimBlockType = "StoneBlock", AimDistance = 2f,
                Screen = "GameScreen"
            };
            string nightWire = StateDigestCompiler.Compile(sleepy, 28, wire: true);
            result.Check("*** an exhausted character at night leads with sleep ***",
                nightWire.StartsWith("sleep=", StringComparison.Ordinal), nightWire);

            // 反向纪律：**黑板不许跟着变短**（健康时 state.hp / state.night 仍然在）
            List<KeyValuePair<string, string>> pairs = StateDigestCompiler.Evaluate(healthy);
            var keys = new List<string>();
            for (int i = 0; i < pairs.Count; i++)
                keys.Add(pairs[i].Key);
            result.Check("*** the blackboard keeps the benign keys (state.hp/state.night must not vanish) ***",
                keys.Contains("hp") && keys.Contains("night") && keys.Contains("temp"),
                string.Join(",", keys.ToArray()));

            // **A56 的前提**：一个"完全良性"的角色（生命/饥饿/困倦/体温/潮湿/体力全正常、
            // 也没瞄任何东西）编译出来的**上线状态必须是空的** ——
            // 因为模型对"空/无内容"的输入会答 `danger`/`mine` 这类无中生有的警报，
            // 而节点见到空状态就**不问**（走 `onUnavailable` 降级链，出厂 demo 是 `keep`）。
            var benign = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 1f, Stamina = 1f, Sleep = 0.1f,
                Temperature = 12f, Wetness = 0.05f, IsNight = false,
                AimKind = null, HoldingBlockType = null, Screen = "GameScreen"
            };
            result.Check("*** a fully benign character compiles to an EMPTY wire state ***",
                StateDigestCompiler.Compile(benign, 28, wire: true).Trim().Length == 0,
                "'" + StateDigestCompiler.Compile(benign, 28, wire: true) + "'");
            result.Check("...while the human digest still describes that state",
                StateDigestCompiler.Compile(benign, 200).Length > 0,
                StateDigestCompiler.Compile(benign, 200));

            // **A55**：上线状态里不许出现 `+N` 丢弃标记。
            // 实测：同一个状态 `sleep=exhausted aim=none` 答 `sleep`，加上 `+7` 就答 `mine`
            // （`+1`/`+0`/`x7` 同样翻面，而 `7`/`+12` 不翻）—— 任意尾随 token 都能改变答案，
            // 而这个标记的值**随状态变化**，等于往提示词里注入一个与语义无关的抖动变量。
            result.Check("*** the wire state never carries the dropped-fields marker ***",
                wire.IndexOf(" +", StringComparison.Ordinal) < 0, wire);
            string tightRich = StateDigestCompiler.Compile(healthy, 30);
            result.Check("...while the human digest still says how many fields were dropped",
                tightRich.IndexOf(" +", StringComparison.Ordinal) >= 0, tightRich);

            // 人工/复盘那份也保持完整（含良性值）
            string richDigest = StateDigestCompiler.Compile(healthy, 200);
            result.Check("the human/review digest still shows the benign values",
                richDigest.IndexOf("hp=", StringComparison.Ordinal) >= 0
                && richDigest.IndexOf("night=", StringComparison.Ordinal) >= 0, richDigest);
        }

        /// <summary>每个以空格分开的 token 都该是 `key=value`（末尾的 `+N` 标记除外）。</summary>
        private static bool AllTokensArePairs(string line)
        {
            string[] tokens = line.Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length == 0 || tokens[i][0] == '+')
                    continue;
                if (tokens[i].IndexOf('=') <= 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// G16：列表候选行。世界外"选哪个世界 / 要不要翻页"的答案就是"第几行"，
        /// 而 SC 的列表行是**自绘**的（条目不是控件）—— 摘要不发候选，模型就只能瞎猜。
        ///
        /// 这一组钉住四件事：编码不会被内容弄坏、选中行看得见、丢行有计数、
        /// **读失败不等于空列表**（后者会让模型决定"去创建一个新世界"）。
        /// </summary>
        private static void CaseUiList(BtSelfTest.TestResult result)
        {
            // ---- 基本形状：面板名 + 行 + 总数 + 还能往下滚
            var list = new UiListSnapshot
            {
                Panel = "WorldsList",
                ItemsCount = 9,
                SelectedIndex = 0,
                CanScrollDown = true,
                Rows = new List<UiListRow>
                {
                    new UiListRow { Index = 0, Text = "Base", Selected = true, Visible = true },
                    new UiListRow { Index = 1, Text = "Cave", Visible = true },
                    new UiListRow { Index = 2, Text = "Nether", Visible = true }
                }
            };
            string text = UiListDigest.Compile(list);
            result.Check("list digest names the panel, its rows and the total",
                text == "WorldsList[0:Base*|1:Cave|2:Nether]/9v", text);
            result.Check("scrolled-out rows are not double-counted as dropped",
                text != null && !text.Contains("+"), text);

            // ---- 选中行不在候选里：必须显式说"你看不到现在选的是哪个"
            var offWindow = new UiListSnapshot
            {
                Panel = "WorldsList",
                ItemsCount = 9,
                SelectedIndex = 7,
                CanScrollUp = true,
                Rows = new List<UiListRow>
                {
                    new UiListRow { Index = 4, Text = "D", Visible = true },
                    new UiListRow { Index = 5, Text = "E", Visible = true }
                }
            };
            string off = UiListDigest.Compile(offWindow);
            result.Check("a selection outside the visible rows is spelled out",
                off == "WorldsList[4:D|5:E]/9!sel=7^", off);

            // ---- 被 maxRows 挤掉的行要计数
            var crowded = new UiListSnapshot { Panel = "WorldsList", ItemsCount = 6 };
            for (int i = 0; i < 6; i++)
                crowded.Rows.Add(new UiListRow { Index = i, Text = "w" + i, Visible = true });
            string squeezed = UiListDigest.Compile(crowded, 4, 14);
            result.Check("rows past the cap are counted, and only the visible ones are sent",
                squeezed == "WorldsList[0:w0|1:w1|2:w2|3:w3]/6+2", squeezed);

            // ---- 选中行在可视区里、但被 maxRows 挤掉了：**必须挤进来**
            // （实机踩到：在"内容管理"里点了第 12 行之后，发出去的窗口是 4~7，
            //  模型看不到"现在选的是哪个" —— 而那正是"要不要换一行/删掉它"的关键输入）
            var selectedBelow = new UiListSnapshot
            {
                Panel = "ContentList", ItemsCount = 14, SelectedIndex = 12
            };
            for (int i = 4; i <= 12; i++)
                selectedBelow.Rows.Add(new UiListRow { Index = i, Text = "r" + i, Visible = true });
            string withSelection = UiListDigest.Compile(selectedBelow, 4, 14);
            result.Check("*** the selected row is pulled into the window even past the cap ***",
                withSelection == "ContentList[4:r4|5:r5|6:r6|12:r12]/14+5", withSelection);
            result.Check("...and then it needs no 'you cannot see the selection' marker",
                withSelection != null && !withSelection.Contains("!sel"), withSelection);

            // ---- 消毒：世界名里的空格 / 竖线 / 星号不能破坏编码
            var nasty = new UiListSnapshot { Panel = "WorldsList", ItemsCount = 2 };
            nasty.Rows.Add(new UiListRow { Index = 0, Text = "My |World*", Visible = true });
            nasty.Rows.Add(new UiListRow { Index = 1, Text = "A  B\tC", Visible = true });
            string cleaned = UiListDigest.Compile(nasty);
            result.Check("structural characters in a name cannot forge a row boundary",
                cleaned == "WorldsList[0:My_World|1:A_B_C]/2", cleaned);

            // ---- 截断必须**含**省略号在内的上限（预算是硬的）
            var longName = new UiListSnapshot { Panel = "WorldsList", ItemsCount = 1 };
            longName.Rows.Add(new UiListRow { Index = 0, Text = "0123456789ABCDEFGH", Visible = true });
            string shortened = UiListDigest.Compile(longName, 4, 8);
            result.Check("a long name is truncated inside the per-row cap",
                shortened == "WorldsList[0:012345..]/1", shortened);

            // ---- 多个可见列表 = 有歧义，必须标出来（不能让调用方以为"就是它"）
            var ambiguous = new UiListSnapshot
            {
                Panel = "WorldsList", ItemsCount = 1, VisibleCandidates = 2
            };
            ambiguous.Rows.Add(new UiListRow { Index = 0, Text = "Solo", Visible = true });
            string marked = UiListDigest.Compile(ambiguous);
            result.Check("several visible lists are flagged as ambiguous",
                marked == "?WorldsList[0:Solo]/1", marked);

            // ---- "列表存在但是空的"是**事实**，要发出去（与"没有列表"不同）
            var empty = new UiListSnapshot { Panel = "WorldsList", ItemsCount = 0 };
            result.Check("an existing but empty list is a fact, not silence",
                UiListDigest.Compile(empty) == "WorldsList[]/0", UiListDigest.Compile(empty));

            // ---- 没有列表 = 整个字段不发
            result.Check("no list -> no field at all (not 'list=none')",
                UiListDigest.Compile(null) == null);

            // ---- 观察解析：读失败 ≠ 空列表
            var failed = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["uiList"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["error"] = "NullReferenceException: boom"
                }
            };
            UiListSnapshot broken;
            string brokenError;
            bool parsed = UiListSnapshot.TryFromObservation(failed, out broken, out brokenError);
            result.Check("*** a failed read is NOT reported as an empty list ***",
                !parsed && broken == null && brokenError != null,
                "parsed=" + parsed + " error=" + brokenError);

            result.Check("a missing uiList key means no list on this screen",
                UiListSnapshot.FromObservation(new Dictionary<string, object>(StringComparer.Ordinal)) == null);

            // ---- 观察解析：正常块读得回来（含 selected/visible）
            var wire = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["uiList"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["panel"] = "WorldsList",
                    ["itemsCount"] = 3,
                    ["selectedIndex"] = 1,
                    ["canScrollDown"] = true,
                    ["visibleCandidates"] = 1,
                    ["rows"] = new List<object>
                    {
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["index"] = 0, ["text"] = "Base", ["selected"] = false, ["visible"] = true
                        },
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["index"] = 1, ["text"] = "Cave", ["selected"] = true, ["visible"] = true
                        }
                    }
                }
            };
            UiListSnapshot round;
            string roundError;
            bool roundTripped = UiListSnapshot.TryFromObservation(wire, out round, out roundError);
            result.Check("the observation block parses back with its row facts",
                roundTripped && round != null && round.Panel == "WorldsList"
                && round.ItemsCount == 3 && round.SelectedIndex == 1 && round.CanScrollDown
                && round.Rows.Count == 2 && round.Rows[1].Selected && round.Rows[1].Visible
                && round.Rows[0].Text == "Base",
                roundError ?? (round == null ? "null" : UiListDigest.Compile(round)));

            // ---- 进摘要：位置在 scr 之后；进 Evaluate（= 进黑板 state.list）
            var front = new StateInputs
            {
                WorldLoaded = false,
                Screen = "MainMenu",
                UiList = round
            };
            string frontDigest = StateDigestCompiler.Compile(front);
            result.Check("the front digest carries the row candidates",
                frontDigest.Contains("list=WorldsList[0:Base|1:Cave*]/3v"), frontDigest);
            result.Check("it sits right after the screen name (world-out priority)",
                frontDigest.IndexOf("scr=MainMenu list=", StringComparison.Ordinal) >= 0, frontDigest);

            var pairs = StateDigestCompiler.Evaluate(front);
            string blackboard = null;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key == "list")
                    blackboard = pairs[i].Value;
            }
            result.Check("the tree can read the same rows off the blackboard (state.list)",
                blackboard == "WorldsList[0:Base|1:Cave*]/3v", blackboard);

            // ---- 世界内没有列表：摘要不该多出这一项（不占预算）
            string worldDigest = StateDigestCompiler.Compile(new StateInputs
            {
                WorldLoaded = true, HasPlayer = true, Health = 1f
            });
            result.Check("in the world (no list) the field simply does not appear",
                worldDigest.IndexOf("list=", StringComparison.Ordinal) < 0, worldDigest);
        }

        /// <summary>
        /// `Evaluate`（树内分支用的字段表）与 `Compile`（喂模型的摘要）**必须同源**。
        ///
        /// 为什么这条要钉：模型按 `phase` 判、树按另一个口径判 → **边界上自相矛盾**
        /// （摘要说世界外、树却按世界内动手），而两边代码看上去都对。这里就比"摘要里的每一项
        /// 都能在 Evaluate 里找到同名同值"，外加"Evaluate 是超集"（预算裁掉的字段树也查得到）。
        /// </summary>
        private static void CaseEvaluateMatchesDigest(BtSelfTest.TestResult result)
        {
            var inputs = new StateInputs
            {
                WorldLoaded = true,
                HasPlayer = true,
                Screen = "GameScreen",
                Health = 0.72f,
                Food = 0.25f,
                Stamina = 0.8f,
                Sleep = 0.15f,
                Temperature = 7.5f,
                Wetness = 0.1f,
                Day = 2333,
                Hour = 14.2f,
                IsNight = false,
                AimKind = "block",
                AimBlockType = "StoneBlock",
                AimDistance = 2.1f,
                HoldingBlockType = "StonePickaxeBlock",
                InventoryItems = 4,
                GameMode = "Survival"
            };

            string digest = StateDigestCompiler.Compile(inputs);
            List<KeyValuePair<string, string>> fields = StateDigestCompiler.Evaluate(inputs);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Count; i++)
                map[fields[i].Key] = fields[i].Value;

            result.Check("Evaluate returns the digest fields", fields.Count > 0, "count=" + fields.Count);

            // 摘要里出现的每一项都必须在字段表里同名同值
            string[] pairs = digest.Split(' ');
            int compared = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0 || pair[0] == '+')
                    continue;   // `+N` 是"被裁掉几项"的标记，不是字段
                int split = pair.IndexOf('=');
                if (split <= 0)
                    continue;
                string key = pair.Substring(0, split);
                string value = pair.Substring(split + 1);
                string fromFields;
                result.Check("digest field is in Evaluate: " + key,
                    map.TryGetValue(key, out fromFields) && fromFields == value,
                    "digest=" + value + " evaluate=" + (fromFields ?? "<missing>"));
                compared++;
            }
            result.Check("the comparison actually compared something", compared >= 8, "compared=" + compared);

            // 预算裁掉字段时：摘要变短，但 Evaluate 仍然是完整的（树不该因为"喂模型的那份被裁了"而瞎）
            string tight = StateDigestCompiler.Compile(inputs, 60);
            List<KeyValuePair<string, string>> tightFields = StateDigestCompiler.Evaluate(inputs);
            result.Check("Evaluate does not apply the digest budget",
                tight.Length < digest.Length && tightFields.Count == fields.Count,
                "tight=" + tight.Length + " full=" + digest.Length
                + " fields=" + fields.Count + "/" + tightFields.Count);

            // 世界外：相位字段必须跟着走（这是 §4.13 唯一的口径）
            var front = new StateInputs { WorldLoaded = false };
            var frontFields = new Dictionary<string, string>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> frontList = StateDigestCompiler.Evaluate(front);
            for (int i = 0; i < frontList.Count; i++)
                frontFields[frontList[i].Key] = frontList[i].Value;
            string frontPhase;
            result.Check("world-outside Evaluate still reports phase=front",
                frontFields.TryGetValue("phase", out frontPhase) && frontPhase == PhaseNames.Front,
                frontPhase ?? "<missing>");
        }

        private static void CaseBands(BtSelfTest.TestResult result)        {
            result.Check("health bands are ordered and complete",
                StateDigestCompiler.HealthBand(0f) == "dead"
                && StateDigestCompiler.HealthBand(0.1f) == "crit"
                && StateDigestCompiler.HealthBand(0.3f) == "low"
                && StateDigestCompiler.HealthBand(0.6f) == "ok"
                && StateDigestCompiler.HealthBand(1f) == "full");

            result.Check("food bands",
                StateDigestCompiler.FoodBand(0.05f) == "starving"
                && StateDigestCompiler.FoodBand(0.2f) == "hungry"
                && StateDigestCompiler.FoodBand(0.4f) == "ok"
                && StateDigestCompiler.FoodBand(0.9f) == "full");

            result.Check("temperature bands (12 is the comfortable zone in SC)",
                StateDigestCompiler.TemperatureBand(2f) == "freezing"
                && StateDigestCompiler.TemperatureBand(8f) == "cold"
                && StateDigestCompiler.TemperatureBand(12f) == "normal"
                && StateDigestCompiler.TemperatureBand(18f) == "hot"
                && StateDigestCompiler.TemperatureBand(30f) == "burning");

            result.Check("sleep bands",
                StateDigestCompiler.SleepBand(0.1f) == "rested"
                && StateDigestCompiler.SleepBand(0.4f) == "sleepy"
                && StateDigestCompiler.SleepBand(0.9f) == "exhausted");
        }

        private static void CaseDigestShape(BtSelfTest.TestResult result)
        {
            var inputs = new StateInputs
            {
                WorldLoaded = true,
                HasPlayer = true,
                Health = 0.72f,
                Food = 0.25f,
                Stamina = 0.8f,
                Sleep = 0.15f,
                Temperature = 7.5f,
                Wetness = 0.1f,
                AimKind = "block",
                AimBlockType = "StoneBlock",
                AimDistance = 2.1f,
                HoldingBlockType = "StonePickaxeBlock",
                Day = 2333,
                Hour = 14.2f,
                IsNight = false,
                Season = "Summer",
                InventoryItems = 4,
                GameMode = "Survival"
            };

            string digest = StateDigestCompiler.Compile(inputs);
            result.Check("digest is not empty", !string.IsNullOrEmpty(digest), digest);
            result.Check("digest says we are in the world", digest.Contains("phase=world"), digest);
            result.Check("digest carries health with its band", digest.Contains("hp=0.72(ok)"), digest);
            result.Check("digest carries food with its band", digest.Contains("food=0.25(hungry)"), digest);
            result.Check("digest carries the temperature band", digest.Contains("temp=cold"), digest);
            result.Check("digest carries the aim target with distance (type name shortened)",
                digest.Contains("aim=Stone@2.1"), digest);
            result.Check("digest carries the held item", digest.Contains("hold=StonePickaxe"), digest);
            result.Check("digest carries the day and hour", digest.Contains("day=2333/14h"), digest);
            result.Check("digest says it is day (night flag present)", digest.Contains("night=0"), digest);
            result.Check("digest omits rain when there is none", !digest.Contains("rain="), digest);
            result.Check("digest is within the default budget", digest.Length <= 200,
                "len=" + digest.Length + " :: " + digest);

            // 世界外：摘要必须是"界面态"，而不是一堆空的角色字段
            var front = new StateInputs { WorldLoaded = false };
            string frontDigest = StateDigestCompiler.Compile(front);
            result.Check("front-end digest says phase=front and ui=menu",
                frontDigest.Contains("phase=front") && frontDigest.Contains("ui=menu"), frontDigest);
            result.Check("front-end digest has no player fields at all",
                !frontDigest.Contains("hp=") && !frontDigest.Contains("food="), frontDigest);

            // 模态界面：ui 用面板名（并把 Widget 后缀瘦身）
            var modal = new StateInputs { WorldLoaded = true, HasPlayer = true, ModalPanel = "FullInventoryWidget" };
            string modalDigest = StateDigestCompiler.Compile(modal);
            result.Check("modal digest shows the panel without the Widget suffix",
                modalDigest.Contains("ui=FullInventory"), modalDigest);

            // 睡着时 sleep 说 now（比"困倦档位"更重要）
            var sleeping = new StateInputs { WorldLoaded = true, HasPlayer = true, Sleeping = true, Sleep = 0.9f };
            result.Check("sleeping is reported as sleep=now",
                StateDigestCompiler.Compile(sleeping).Contains("sleep=now"),
                StateDigestCompiler.Compile(sleeping));

            // 没有 player（加载中）
            var loading = new StateInputs { WorldLoaded = true, HasPlayer = false };
            result.Check("world loaded but no player -> phase=loading",
                StateDigestCompiler.Compile(loading).Contains("phase=loading"),
                StateDigestCompiler.Compile(loading));
        }

        private static void CaseDigestBudget(BtSelfTest.TestResult result)
        {
            var rich = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Screen = "GameScreen", ModalPanel = null,
                Health = 0.5f, Food = 0.5f, Stamina = 0.5f, Sleep = 0.5f,
                Temperature = 12f, Wetness = 0.5f,
                AimKind = "block", AimBlockType = "VeryLongBlockTypeNameForTesting", AimDistance = 3.33f,
                HoldingBlockType = "AnotherVeryLongBlockTypeName", Day = 12345, Hour = 23.9f,
                IsNight = true, Season = "Winter", Precipitation = 0.8f,
                InventoryItems = 12, GameMode = "Survival", CanSleep = true
            };

            string tight = StateDigestCompiler.Compile(rich, 90);
            result.Check("a tight budget still produces a digest", !string.IsNullOrEmpty(tight), tight);
            // 顺序 = 优先级，而优先级是**实测**出来的（plan §9「P2 实测 #1」）：
            // 这个模型只看摘要开头，所以开头必须是"眼前是什么/我缺什么"，不能是界面名。
            result.Check("*** a tight budget leads with a decision-relevant fact, not the screen ***",
                tight.StartsWith("aim=", StringComparison.Ordinal)
                || tight.StartsWith("food=", StringComparison.Ordinal)
                || tight.StartsWith("hp=", StringComparison.Ordinal),
                tight);
            result.Check("...and never leads with the interface descriptors any more",
                !tight.StartsWith("phase=", StringComparison.Ordinal)
                && !tight.StartsWith("ui=", StringComparison.Ordinal)
                && !tight.StartsWith("scr=", StringComparison.Ordinal), tight);
            result.Check("*** the budget is a HARD limit, even for a single oversized field ***",
                tight.Length <= 90 + 4, "len=" + tight.Length + " :: " + tight);
            result.Check("a tight budget marks how many fields were dropped",
                tight.Contains(" +"), "len=" + tight.Length + " :: " + tight);
            result.Check("the drop marker itself is short (a few chars)",
                tight.Length <= 90 + 6, "len=" + tight.Length + " :: " + tight);

            // 上线状态（默认 28）必须真的短：模型对长摘要给的是默认答案
            string wire = StateDigestCompiler.Compile(rich, 28);
            result.Check("*** the wire state stays inside the measured-good length ***",
                wire.Length <= 28 + 4, "len=" + wire.Length + " :: " + wire);

            // 反向：预算裁剪**不许**跳过一项、再留下后面更短的一项 —— 字段表是"顺序即优先级"
            string cut = StateDigestCompiler.Compile(rich, 90);
            List<KeyValuePair<string, string>> all = StateDigestCompiler.Evaluate(rich);
            var order = new List<string>();
            for (int i = 0; i < all.Count; i++)
                order.Add(all[i].Key);

            var shown = new List<string>();
            string[] tokens = cut.Split(' ');
            for (int i = 0; i < tokens.Length; i++)
            {
                int eq = tokens[i].IndexOf('=');
                if (eq > 0)
                    shown.Add(tokens[i].Substring(0, eq));
            }
            bool prefix = true;
            for (int i = 0; i < shown.Count; i++)
            {
                if (i >= order.Count || order[i] != shown[i])
                    prefix = false;
            }
            result.Check("*** a tight budget keeps a PREFIX of the fields (order = priority) ***",
                prefix && shown.Count > 0 && shown.Count < order.Count,
                "shown=" + string.Join(",", shown.ToArray()) + " :: " + cut);

            string roomy = StateDigestCompiler.Compile(rich, 320);
            result.Check("a roomy budget keeps more fields than a tight one",
                roomy.Length > tight.Length, "tight=" + tight.Length + " roomy=" + roomy.Length);
            result.Check("a roomy budget drops nothing (no +N marker)",
                !roomy.Contains(" +"), roomy);

            // 空输入：不能抛，也不能给出误导性的字段
            string empty = StateDigestCompiler.Compile(new StateInputs { WorldLoaded = false });
            result.Check("empty inputs still compile to a front-end digest",
                !string.IsNullOrEmpty(empty) && empty.Contains("phase=front"), empty);

            result.Check("null inputs compile to empty (no crash)",
                StateDigestCompiler.Compile(null) == string.Empty);
        }

        /// <summary>
        /// **反向纪律**：摘要里绝不能出现"环境/dump 类"字段。
        /// 实测：把无关字段塞进去会让答案直接翻转（§5.4 ③），所以字段是白名单，
        /// 这条用例是"白名单被偷偷扩宽"的警报。
        /// </summary>
        private static void CaseNoDump(BtSelfTest.TestResult result)
        {
            var inputs = new StateInputs
            {
                WorldLoaded = true, HasPlayer = true,
                Health = 1f, Food = 1f
            };
            string digest = StateDigestCompiler.Compile(inputs);

            string[] forbidden = { "pos=", "vel=", "blocks=", "entities=", "render", "fps=", "chunk", "biome" };
            string found = null;
            for (int i = 0; i < forbidden.Length; i++)
            {
                if (digest.IndexOf(forbidden[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    found = forbidden[i];
            }
            result.Check("digest contains no environment/dump fields", found == null,
                found == null ? digest : "found '" + found + "' in: " + digest);

            // 字段表本身也要有界（防止有人往 Fields() 里塞几十项把预算吃光）
            List<DigestField> fields = StateDigestCompiler.Fields();
            result.Check("field table is present and bounded", fields.Count > 5 && fields.Count <= 24,
                "fields=" + fields.Count);
        }

        private const string SampleBank = @"{
          ""format"": ""qbank"", ""version"": 1,
          ""id"": ""world_goal"", ""name"": ""世界内目标"",
          ""questions"": [
            { ""id"": ""goal"", ""type"": ""choice"",
              ""instructions"": ""What should this character do next?"",
              ""options"": [
                { ""key"": ""mine"", ""description"": ""break the targeted block"" },
                { ""key"": ""eat"", ""description"": ""consume food now"" },
                { ""key"": ""flee"", ""description"": ""escape the nearby hostile"" },
                { ""key"": ""wait"", ""description"": ""stand still and watch"" }
              ] },
            { ""id"": ""threat"", ""type"": ""choice"",
              ""instructions"": ""Is a hostile close enough to attack right now?"",
              ""options"": [
                { ""key"": ""yes"", ""description"": ""a hostile is within a few blocks"" },
                { ""key"": ""no"", ""description"": ""nothing hostile is within reach"" }
              ] }
          ] }";

        private static void CaseQuestionBankParse(BtSelfTest.TestResult result)
        {
            string error;
            QuestionBank bank = QuestionBankParser.Parse(SampleBank, "test", out error);
            result.Check("valid question bank parses", bank != null, error);
            if (bank == null)
                return;

            result.Check("bank id/questions read", bank.Id == "world_goal" && bank.Questions.Count == 2);
            QuestionTemplate goal = bank.Find("goal");
            result.Check("question lookup works", goal != null && goal.IsChoice && goal.Options.Count == 4);
            result.Check("question outlines its option keys",
                goal != null && goal.OptionKeys().Count == 4 && goal.OptionKeys().Contains("mine"));
            result.Check("select() subsets by id",
                bank.Select(new[] { "threat" }).Questions.Count == 1);

            // 错误路径
            result.Check("missing questions -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\"}", "t", out error) == null);
            result.Check("missing id -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"questions\":[{\"id\":\"a\",\"instructions\":\"i\"}]}",
                    "t", out error) == null);
            result.Check("bad type -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\",\"questions\":[{\"id\":\"a\",\"type\":\"vibes\",\"instructions\":\"i\"}]}",
                    "t", out error) == null);
            result.Check("choice without options -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\",\"questions\":[{\"id\":\"a\",\"type\":\"choice\",\"instructions\":\"i\"}]}",
                    "t", out error) == null);
            result.Check("missing instructions -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\",\"questions\":[{\"id\":\"a\",\"type\":\"noul\"}]}",
                    "t", out error) == null);
            result.Check("duplicate question id -> error",
                QuestionBankParser.Parse("{\"format\":\"qbank\",\"version\":1,\"id\":\"x\",\"questions\":["
                    + "{\"id\":\"a\",\"type\":\"noul\",\"instructions\":\"i\"},"
                    + "{\"id\":\"a\",\"type\":\"noul\",\"instructions\":\"j\"}]}", "t", out error) == null);
            result.Check("wrong format -> error",
                QuestionBankParser.Parse("{\"format\":\"scbt\",\"version\":1,\"id\":\"x\"}", "t", out error) == null);
        }

        private static void CaseBudgetGuard(BtSelfTest.TestResult result)
        {
            string error;
            QuestionBank bank = QuestionBankParser.Parse(SampleBank, "test", out error);
            string digest = StateDigestCompiler.Compile(new StateInputs
            {
                WorldLoaded = true, HasPlayer = true, Health = 0.5f, Food = 0.5f, AimKind = "block",
                AimBlockType = "StoneBlock", AimDistance = 2f
            });

            List<string> issues = QuestionBankParser.CheckBudget(bank, digest);
            result.Check("a reasonable digest + 2 questions passes the budget guard",
                issues.Count == 0, QuestionBankParser.DescribeIssues(issues));

            // 超长摘要必须被拦（这是"静默截断"的唯一防线）
            var huge = new System.Text.StringBuilder();
            for (int i = 0; i < 4000; i++)
                huge.Append('x');
            List<string> tooLong = QuestionBankParser.CheckBudget(bank, huge.ToString());
            result.Check("an oversized digest is rejected locally (not silently truncated)",
                tooLong.Count > 0, QuestionBankParser.DescribeIssues(tooLong));

            // 选项太多 → 精度警报
            string manyOptions = "{\"format\":\"qbank\",\"version\":1,\"id\":\"many\",\"questions\":["
                + "{\"id\":\"q\",\"type\":\"choice\",\"instructions\":\"pick\",\"options\":[";
            for (int i = 0; i < 20; i++)
                manyOptions += (i > 0 ? "," : string.Empty) + "{\"key\":\"k" + i + "\",\"description\":\"option " + i + "\"}";
            manyOptions += "]}]}";
            QuestionBank wide = QuestionBankParser.Parse(manyOptions, "t", out error);
            List<string> wideIssues = QuestionBankParser.CheckBudget(wide, "phase=world");
            result.Check("too many options is rejected (accuracy falls off past ~8)",
                wideIssues.Count > 0, QuestionBankParser.DescribeIssues(wideIssues));

            // 选项没有描述 → 必须报（描述才是模型判别的东西）
            QuestionBank noDesc = QuestionBankParser.Parse(
                "{\"format\":\"qbank\",\"version\":1,\"id\":\"nd\",\"questions\":["
                + "{\"id\":\"q\",\"type\":\"choice\",\"instructions\":\"pick\",\"options\":["
                + "{\"key\":\"a\"},{\"key\":\"b\"}]}]}", "t", out error);
            List<string> noDescIssues = QuestionBankParser.CheckBudget(noDesc, "phase=world");
            result.Check("option without a description is rejected",
                noDescIssues.Count > 0, QuestionBankParser.DescribeIssues(noDescIssues));

            result.Check("empty bank is rejected", QuestionBankParser.CheckBudget(null, "x").Count > 0);
        }

        private static void CaseWireShape(BtSelfTest.TestResult result)
        {
            string error;
            QuestionBank bank = QuestionBankParser.Parse(SampleBank, "test", out error);
            Dictionary<string, object> wire = QuestionBankParser.ToWireQuestions(bank);
            result.Check("wire has one entry per question", wire.Count == 2, "count=" + wire.Count);

            var goal = wire["goal"] as Dictionary<string, object>;
            result.Check("wire entry carries type + instructions",
                goal != null && Equals(goal["type"], "choice") && goal["instructions"] != null);
            var criteria = goal != null ? goal["criteria"] as Dictionary<string, object> : null;
            result.Check("choice criteria is a key->description object with all options",
                criteria != null && criteria.Count == 4 && criteria.ContainsKey("mine"));
            result.Check("criteria value is the description (what the model discriminates on)",
                criteria != null && Convert.ToString(criteria["mine"]) == "break the targeted block");

            // score / noul 的形状
            QuestionBank mixed = QuestionBankParser.Parse(
                "{\"format\":\"qbank\",\"version\":1,\"id\":\"mixed\",\"questions\":["
                + "{\"id\":\"urgent\",\"type\":\"score\",\"instructions\":\"how urgent\",\"options\":["
                + "{\"key\":\"0\",\"description\":\"calm\"},{\"key\":\"1\",\"description\":\"busy\"},"
                + "{\"key\":\"2\",\"description\":\"critical\"}]},"
                + "{\"id\":\"gate\",\"type\":\"noul\",\"instructions\":\"in danger?\","
                + "\"trueCriteria\":\"a hostile is close\",\"falseCriteria\":\"nothing close\"}]}", "t", out error);
            Dictionary<string, object> mixedWire = QuestionBankParser.ToWireQuestions(mixed);
            var urgent = mixedWire["urgent"] as Dictionary<string, object>;
            result.Check("score criteria is an ordered array of levels",
                urgent != null && urgent["criteria"] is List<object>
                && ((List<object>)urgent["criteria"]).Count == 3);
            var gate = mixedWire["gate"] as Dictionary<string, object>;
            result.Check("noul criteria carries true/false wording",
                gate != null && gate["criteria"] is Dictionary<string, object>
                && ((Dictionary<string, object>)gate["criteria"]).ContainsKey("true"));

            // token 估算：中文 ≈1/字，英文明显更省（用于预算守卫的保守估计）
            result.Check("token estimate: 100 CJK chars ~= 100 tokens",
                Math.Abs(QuestionBankParser.EstimateTokens(new string('中', 100)) - 100) <= 2);
            result.Check("token estimate: ASCII is much cheaper per char",
                QuestionBankParser.EstimateTokens(new string('a', 100)) < 50);
        }
    }
}
