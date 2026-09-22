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
    
    /// <summary>ER2-BOOT-01：战斗/旧运行中枢 UI 是否已挂载。冷启动不再无条件挂载它们——
    /// 那一整套（GameShellUIToolkit 的“工坊/仓/图鉴”等）仍是切产品前的旧生物题材命名，
    /// 直接摆在冷启动第一屏会让玩家看到未改名的旧入口。真正的冷启动第一屏是
    /// <see cref="GameLogic.MainMenuUI"/>；只有玩家从主菜单成功新建/继续/读取战役后，
    /// 才调用 <see cref="MountGameplayUi"/> 把这些 UI 挂上去。</summary>
    private static bool _gameplayUiMounted;

    private static void StartGameLogic()
    {
        // 正式游戏框架启动。注册所有阶段与更新驱动。
        // 详见 DesignDocs/Game_Framework_Design.md §8。
        GameLogic.Stage.GameRoot.Startup();

        FixUiRootReferenceResolution();

        // ER2-BOOT-01：冷启动第一屏必须是《地球归还》正式主菜单（新建/继续/读取/设置/退出），
        // 不允许通过 GM/测试菜单进入 Demo——旧运行中枢与战斗 UI 延后到 MountGameplayUi()。
        GameModule.UI.ShowUIAsync<GameLogic.MainMenuUI>();
    }

    /// <summary>共享 <c>UIRoot</c>（<c>Assets/TEngine/</c> 框架资产，禁止直接改）上的 CanvasScaler
    /// 遗留手机竖屏参考分辨率 750x1334，与本项目实际目标 1920x1080 横屏不符——UI Toolkit 侧
    /// <c>BattleHudPanelSettings</c> 已是 1920x1080，本项目不是竖屏手游。嵌套 Canvas（每个 UIWindow
    /// 自己的 Canvas）上的 CanvasScaler 在 Unity 里不参与实际缩放计算，只有这个共享根 Canvas 的
    /// CanvasScaler 才真正生效，所以必须在这里统一纠正一次，不能指望各窗口各自在运行时打补丁
    /// （旧写法曾经这样做，副作用是给嵌套 Canvas 设置 renderMode 会转发改写这个共享根 Canvas）。</summary>
    private static void FixUiRootReferenceResolution()
    {
        // 直接找场景里的 UIRoot（跟 UIModule.OnInit 用同一种查法），不依赖 UIModule 单例是否已经
        // OnInit 过——这里跑在 StartGameLogic 最早期，第一次真正触发 GameModule.UI 还在后面一行，
        // 此时读 UIModule.UIRoot 静态属性只会拿到 null（曾经这样写过，静默跳过、完全没生效）。
        GameObject uiRootGo = GameObject.Find("UIRoot");
        UnityEngine.UI.CanvasScaler scaler = uiRootGo != null
            ? uiRootGo.GetComponentInChildren<UnityEngine.UI.CanvasScaler>()
            : null;
        if (scaler == null)
        {
            Log.Warning("[GameApp] UIRoot 未找到 CanvasScaler，跳过参考分辨率纠正。");
            return;
        }

        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
    }

    /// <summary>ER2-BOOT-01：把战斗/旧运行中枢 UI 的常驻挂载从冷启动延后到"玩家从主菜单真正开局"之后。
    /// 同进程内幂等——新建/继续/读取反复调用只会真正挂载一次。调试/回归测试如果绕开 MainMenuUI
    /// 直接调用 GameRoot.StartCellStage()/ResumeCellStage()，需要自己先调用本方法，否则战斗 HUD
    /// 等面板不存在（历史上这些面板一直随 StartGameLogic 无条件挂载，直连测试沿用的就是那批实例；
    /// 现在需要显式挂载一次，行为等价，只是时机从"进程启动"改成"本方法被调用时"）。</summary>
    public static void MountGameplayUi()
    {
        if (_gameplayUiMounted)
        {
            return;
        }
        _gameplayUiMounted = true;

        // 运行中枢必须先于战斗页常驻：无阶段时它提供开局入口，运行中它提供唯一的跨玩法导航。
        new GameObject("GameShellUIToolkit").AddComponent<GameLogic.UI.GameShell.GameShellUIToolkit>();

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

        // UI Toolkit 版萌生腔面板（M4-R00-02 队列②号项第6条，M3-R05）：同上不挂 [Window]，
        // 常驻单例默认隐藏，X 键切换，sortingOrder=7。模板/萌生列表从 CellDebugHud 的 Y 键
        // 调试面板迁到正式入口，只做"萌生"一块（模板编辑/回巢/野生器官三块转正登记为独立后续故事）。
        new GameObject("BattleGerminationUIToolkit").AddComponent<BattleGerminationUIToolkit>();

        // 补齐原型期仍在 IMGUI / 键盘里的玩家玩法入口：模板版本、回巢改造与数字编队。
        new GameObject("LineageWorkshopUIToolkit").AddComponent<GameLogic.UI.LineageWorkshop.LineageWorkshopUIToolkit>();
        new GameObject("TacticalCommandUIToolkit").AddComponent<GameLogic.UI.TacticalCommand.TacticalCommandUIToolkit>();
    }
    
    private static void Release()
    {
        SingletonSystem.Release();
        // 场景/域卸载时挂载的 GameObject 会一并销毁，标记复位，避免下次 Entrance 误判"已挂载"而跳过。
        _gameplayUiMounted = false;
        Log.Warning("======= Release GameApp =======");
    }
}
