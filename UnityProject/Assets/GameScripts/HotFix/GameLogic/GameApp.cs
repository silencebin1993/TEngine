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

        // 006（F6b，preflight-decisions.md）摘空保壳：旧 3×3 装配渲染已下线，本类只剩
        // Instance/SetVisible/IsPanelVisible 三个成员供 BattleOverlayUIToolkit 的 Esc 互斥逻辑用，
        // 挂载行不删（删文件/挂载行会让暂停菜单编译失败，须独立 story 收口）。
        new GameObject("BattleMetabolicUIToolkit").AddComponent<BattleMetabolicUIToolkit>();

        // UI Toolkit 版进化选卡面板（battle-ui-toolkit/story-003，D2）：同上不挂 [Window]，
        // 事件触发式默认弹出，旧 IMGUI CellDebugHud.DrawDraft() 改绑 K 键对照。
        new GameObject("BattleDraftUIToolkit").AddComponent<BattleDraftUIToolkit>();

        // UI Toolkit 版覆盖面板：卡组/商店/图鉴（battle-ui-toolkit/story-004，D2）：同上不挂 [Window]，
        // Tab/B/V 默认打开对应面板（互斥），旧 IMGUI 三面板改绑 J 键对照。
        new GameObject("BattleOverlayUIToolkit").AddComponent<BattleOverlayUIToolkit>();

        // UI Toolkit 版结算回顾面板（battle-ui-polish/story-003，D2）：同上不挂 [Window]，
        // 局外且有结算结果时默认显示，旧 IMGUI 结算块改绑 I 键对照。
        new GameObject("BattleResultUIToolkit").AddComponent<BattleResultUIToolkit>();

        // UI Toolkit 版 Carrier 器官栏 + 插槽条（organ-socket-slice/story-005，D1/D2）：同上不挂 [Window]，
        // 常驻单例自控显隐，sortingOrder=4 压在代谢面板之上、不与选卡争位。
        new GameObject("BattleCarrierUIToolkit").AddComponent<BattleCarrierUIToolkit>();

        // UI Toolkit 版 LookDev 自由装配沙盒（任务四：UI 重设计）：同上不挂 [Window]，
        // 常驻单例默认隐藏，CellDebugHud「LookDev 沙盒」菜单按钮唤起，sortingOrder=5。
        new GameObject("BattleSandboxUIToolkit").AddComponent<BattleSandboxUIToolkit>();
    }
    
    private static void Release()
    {
        SingletonSystem.Release();
        Log.Warning("======= Release GameApp =======");
    }
}