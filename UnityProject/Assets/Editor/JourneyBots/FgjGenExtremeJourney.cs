namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG3-GEN-01（FGT-GEN-009“极端世界设置下，第一幕旅程机器人仍能推进”；FG17 第 5 节“世界设置取极端值……第一幕仍然可以推进”）：
    /// 与 FGJ-M1（第一幕出口旅程）同一串步骤、同一颗测试种子，只是在主菜单“新建”后的新游戏设置里**真实点选**
    /// 资源低 + 据点高 + 污染高 + 领地近（分项代码 R0O2P2D0S0）再开始。种子核对步骤按这组设置独立重算世界比对，
    /// 并确认起始区四级保证满足；之后修复、编辑接入口、装固件、生产、远征、接入、存读档、断链、跳转、撤离全部照走。
    /// 用法：<c>bash tools/unity-journey.sh FGJ-GEN9</c>。
    /// </summary>
    public static class FgjGenExtremeJourney
    {
        public const string Id = "FGJ-GEN9";

        public static JourneyDef Build()
        {
            JourneyDef def = FgjM1Journey.Build();
            def.Id = Id;
            def.Title = "FGT-GEN-009：极端世界设置（资源低 + 据点高 + 污染高 + 领地近）下走完第一幕旅程（FGJ-M1 的全部步骤）";
            // 进 Play 会重载域：在主菜单步骤（Play 之后、不再重载）里设定要点的世界设置。
            JourneyStep menu = def.Steps.Find(s => s.Id == "menu_new");
            System.Action<JourneyContext> enter = menu.OnEnter;
            menu.OnEnter = c =>
            {
                JourneyCommon.WorldSettingsForNewGame = FgWorldGenHomeSelfCheck.ExtremeCode;
                enter?.Invoke(c);
            };
            menu.Title = "主菜单点“新建”（固定测试种子；新游戏设置里点选极端世界设置）";
            return def;
        }
    }
}
