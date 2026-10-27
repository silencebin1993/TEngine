namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG3-GEN-01（FGT-GEN-009“极端世界设置下，第一幕旅程机器人仍能推进”；FG17 第 5 节“世界设置取极端值……第一幕仍然可以推进”）：
    /// FG9-E2E-01 起（DEBT-FG3GEN01-10）与 FGJ-M9（第一幕全流程：正式节奏开新档 → 第一次远征归来 → 远征铸造前哨 → 击败铸造主核心 → 第一幕结束）同一串步骤、
    /// 同一颗测试种子，只是在主菜单“新建”后的新游戏设置里**真实点选**资源低 + 据点高 + 污染高 + 领地近（分项代码 R0O2P2D0S0）再开始。
    /// 种子核对步骤按这组设置独立重算世界比对，并确认起始区四级保证满足；之后的修复、出厂、远征、行军、断链、交战、首领战、第一幕结束全部照走。
    /// 用法：<c>bash tools/unity-journey.sh FGJ-GEN9</c>（断点与 FGJ-M9 相同：<c>--from m9_fix</c>）。
    /// </summary>
    public static class FgjGenExtremeJourney
    {
        public const string Id = "FGJ-GEN9";

        public static JourneyDef Build()
        {
            JourneyDef def = FgjM9Journey.Build();
            def.Id = Id;
            def.Title = "FGT-GEN-009：极端世界设置（资源低 + 据点高 + 污染高 + 领地近）下走完第一幕（FGJ-M9 的全部步骤：主菜单新游戏 → 击败铸造主核心 → 第一幕结束）";
            // 进 Play 会重载域：在主菜单步骤（Play 之后、不再重载）里设定要点的世界设置。
            JourneyStep menu = def.Steps.Find(s => s.Id == "menu_new");
            System.Action<JourneyContext> enter = menu.OnEnter;
            menu.OnEnter = c =>
            {
                JourneyCommon.WorldSettingsForNewGame = FgWorldGenHomeSelfCheck.ExtremeCode;
                enter?.Invoke(c);
            };
            menu.Title = "主菜单点“新建”（固定测试种子；新游戏设置里点选极端世界设置）";
            // FGJ-M9 的新游戏步骤（FGJ-M3 同款：种子框输入 42 → 开始）不点世界设置分项：面板出现后先真实点选极端设置的各分项按钮，再交给原步骤输入种子、点开始。
            JourneyStep newGame = def.Steps.Find(s => s.Id == "new_game");
            System.Func<JourneyContext, StepOutcome> typed = newGame.Tick;
            newGame.Tick = c => ClickExtremeLevels(c) is StepOutcome o ? o : typed(c);
            newGame.Title = "新游戏设置：点选极端世界设置 R0O2P2D0S0 的各分项按钮 → 种子框输入“42” → 点“开始” → 进入归还谷地";
            return def;
        }

        /// <summary>新游戏设置面板出现 0.8 秒后点一次各分项按钮（同 <see cref="JourneyCommon.TickNewGame"/> 的点选写法）；点完或面板没开返回 null 交给原步骤。</summary>
        private static StepOutcome? ClickExtremeLevels(JourneyContext c)
        {
            if (c.GetInt("gen9.levels") != 0 || !UI.Kit.NewGamePanelUIToolkit.IsOpen)
            {
                return null;
            }
            if (c.GetLong("gen9.seenMs") <= 0)
            {
                c.SetLong("gen9.seenMs", System.Math.Max(1, (long)(c.StepElapsed * 1000)));
                return StepOutcome.Wait;
            }
            if (c.StepElapsed * 1000 - c.GetLong("gen9.seenMs") < 800)
            {
                return StepOutcome.Wait;
            }
            string want = FgWorldGenHomeSelfCheck.ExtremeCode;
            for (int a = 0; a < Campaign.WorldGen.WorldSettings.Axes.Length; a++)
            {
                int level = want[a * 2 + 1] - '0';
                if (!JourneyInput.ClickUitk("[NewGameHost]", $"NewGameLevel_{Campaign.WorldGen.WorldSettings.Axes[a]}_{level}"))
                {
                    return StepOutcome.Fail($"新游戏设置里点不到分项按钮 {Campaign.WorldGen.WorldSettings.Axes[a]} 第 {level} 档：" + JourneyInput.LastUiFailure);
                }
            }
            c.SetInt("gen9.levels", 1);
            c.Log($"新游戏设置：点分项按钮选 {want}（{UI.Kit.NewGamePanelUIToolkit.Instance?.CurrentSettings().DisplayName()}）");
            return StepOutcome.Wait;
        }
    }
}
