using System.Collections.Generic;
using System.Reflection;
using GameLogic;
#if ENABLE_OBFUZ
using Obfuz;
#endif
using TEngine;
using UnityEngine;
#pragma warning disable CS0436


/// <summary>
/// 游戏App。
/// </summary>
#if ENABLE_OBFUZ
[ObfuzIgnore(ObfuzScope.TypeName | ObfuzScope.MethodName)]
#endif
public partial class GameApp
{
    private static List<Assembly> _hotfixAssembly;

    /// <summary>
    /// 热更域App主入口。
    /// </summary>
    /// <param name="objects"></param>
    public static void Entrance(object[] objects)
    {
        GameEventHelper.Init();
        _hotfixAssembly = (List<Assembly>)objects[0];
        Log.Warning("======= 看到此条日志代表你成功运行了热更新代码 =======");
        Log.Warning("======= Entrance GameApp =======");
        Utility.Unity.AddDestroyListener(Release);
        Log.Warning("======= StartGameLogic =======");
        StartGameLogic();
    }
    
    private static void StartGameLogic()
    {
        // 正式游戏框架启动。注册所有阶段与更新驱动。
        // 详见 DesignDocs/Game_Framework_Design.md §8。
        GameLogic.Stage.GameRoot.Startup();

        GameModule.UI.ShowUIAsync<BattleMainUI>();

        // UI Toolkit 版 HUD（battle-ui-toolkit/story-001，D1/D2）：不挂 [Window]，
        // 常驻单例自控显隐，与上面的旧 UGUI BattleMainUI 并存，按 U 键切换对比。
        new GameObject("BattleHudToolkit").AddComponent<BattleHudToolkit>();

        // UI Toolkit 版代谢切片装配面板（battle-ui-toolkit/story-002，D2）：同上不挂 [Window]，
        // M 键默认打开，旧 IMGUI MetabolicSlicePanel.DrawPanel() 改绑 L 键对照。
        new GameObject("BattleMetabolicUIToolkit").AddComponent<BattleMetabolicUIToolkit>();

        // UI Toolkit 版进化选卡面板（battle-ui-toolkit/story-003，D2）：同上不挂 [Window]，
        // 事件触发式默认弹出，旧 IMGUI CellDebugHud.DrawDraft() 改绑 K 键对照。
        new GameObject("BattleDraftUIToolkit").AddComponent<BattleDraftUIToolkit>();

        // UI Toolkit 版覆盖面板：卡组/商店/图鉴（battle-ui-toolkit/story-004，D2）：同上不挂 [Window]，
        // Tab/B/V 默认打开对应面板（互斥），旧 IMGUI 三面板改绑 J 键对照。
        new GameObject("BattleOverlayUIToolkit").AddComponent<BattleOverlayUIToolkit>();
    }
    
    private static void Release()
    {
        SingletonSystem.Release();
        Log.Warning("======= Release GameApp =======");
    }
}