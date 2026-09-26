using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Stage.CellStage;
using TEngine;
using UnityEngine;

namespace GameLogic.Stage
{
    /// <summary>
    /// 正式游戏根。持有 <see cref="StageDirector"/> 并驱动每帧更新。
    ///
    /// 接入方式（框架文档 §8）：GameApp.Entrance → GameRoot.Startup。
    /// 不再像 FP demo 那样挂独立场景与自己的 MonoBehaviour——
    /// 走 TEngine 的 Utility.Unity 更新驱动，与框架其余部分一致。
    /// </summary>
    public static class GameRoot
    {
        private static StageDirector _director;
        private static bool _started;
        // FG0-ARCH-01：三个区域控制器不再由 GameRoot 各自持有、互斥运行——它们是同一个世界模拟（WorldSimulation）里的三个地点，
        // 已载入的地点同时推进；镜头与输入由全局的 WorldView 持有。下面的属性只是读取入口（UI 与服务沿用）。

        public static StageDirector Director => _director;

        /// <summary>当前细胞阶段流程。UI 需要读它的状态。</summary>
        public static CellStageFlow CellStage =>
            _director?.Get<CellStageFlow>(StageId.Cell);

        /// <summary>归还谷地（家园）地点；未载入时为 null 或 IsLoaded=false。</summary>
        public static HomeValleyController HomeValley => WorldSimulation.Home;

        /// <summary>破碎都市地点（可能为 null——尚未派遣过）。</summary>
        public static FracturedCityController FracturedCity => WorldSimulation.FracturedCity;

        /// <summary>铸造前哨外围地点（可能为 null——尚未派遣过）。</summary>
        public static FoundryOutpostController FoundryOutpost => WorldSimulation.FoundryOutpost;

        /// <summary>“世界是否暂停”：旧细胞阶段仍用它自己的暂停；0.2 的世界只有一个统一时钟（FG0-ARCH-01，FGR-ARC-009）。</summary>
        public static bool IsWorldPaused =>
            (CellStage != null && CellStage.IsRunning && CellStage.Paused) ||
            (WorldSimulation.AnyLoaded && GameClock.Paused);

        /// <summary>是否在游戏世界里（有任一地点已载入；通知、暂停菜单、界面快捷键据此判断）。</summary>
        public static bool AnyRegionActive => WorldSimulation.AnyLoaded;

        /// <summary>“事件发生在哪个地点”：模拟步期间是正在推进的地点，否则是镜头正在观察的地点。不在世界里返回空串。</summary>
        public static string ActiveRegionId =>
            WorldSimulation.CurrentSiteId ?? (WorldSimulation.AnyLoaded ? WorldView.ObservedSiteId : null) ?? string.Empty;

        /// <summary>全局镜头（通知定位用）；不在世界里为 null。</summary>
        public static View.CameraDirector ActiveCameraDirector =>
            WorldSimulation.AnyLoaded && WorldView.Director.IsBound ? WorldView.Director : null;

        /// <summary>FG0-UX-01：把当前世界设为暂停 / 继续（暂停菜单、通知自动暂停）。与 <see cref="ToggleWorldPause"/> 同一落点。</summary>
        public static void SetWorldPaused(bool paused)
        {
            if (IsWorldPaused != paused)
            {
                ToggleWorldPause();
            }
        }

        /// <summary>FG0-UX-01（FGR-UX-021）：通知触发的自动暂停。只在世界正在运行时暂停并返回 true。</summary>
        private static bool AutoPauseForNotification()
        {
            if (!Application.isPlaying || !AnyRegionActive || IsWorldPaused)
            {
                return false;
            }
            SetWorldPaused(true);
            return IsWorldPaused;
        }

        /// <summary>FG0-UX-01 / FG0-ARCH-01（FGR-UX-020 定位）：镜头飞到通知位置——**跨地点也能飞**（整个世界同时运行，
        /// DEBT-FG0UX01-07 关闭）；地点已不在运行（远征已结束）时给出原因文本键。</summary>
        private static bool LocateForNotification(string regionId, Vector3 position, out string failureKey)
        {
            return WorldView.Locate(regionId, position, out failureKey);
        }

        /// <summary>单个镜头上的定位语义（FG0-UX-01）。FG0-ARCH-01 起正式定位走 <see cref="WorldView.Locate"/>（跨地点、平滑飞跃，
        /// 接入视角下同样先拉回战略并以目标为终点）；本方法保留同一表面内的旧语义，供 FgUiKitSelfCheck 的镜头回归直接驱动。
        /// 定位的真实镜头逻辑（自检用真实 <see cref="View.CameraDirector"/> 直接驱动这里）。
        /// 接入（直控）视角：先拉回战略并把过渡终点设成通知位置——不能先设焦点再 RequestStrategy，
        /// 后者会用当前镜头位置改写焦点，镜头停在自己机器上方。</summary>
        public static bool LocateOn(View.CameraDirector director, string activeRegionId, string regionId, Vector3 position, out string failureKey)
        {
            failureKey = null;
            if (!string.IsNullOrEmpty(regionId) && regionId != (activeRegionId ?? string.Empty))
            {
                failureKey = "ui.notify.other_region";
                return false;
            }
            if (director == null)
            {
                failureKey = "ui.notify.no_camera";
                return false;
            }
            var focus = new Unity.Mathematics.float2(position.x, position.z);
            if (director.Mode == View.ViewMode.Direct)
            {
                director.RequestStrategy(focus);
            }
            else
            {
                director.FocusStrategyOn(focus);
            }
            return true;
        }

        /// <summary>FG0-UX-01（暂停菜单“保存并返回主菜单”）：存档前把每个已载入地点的实时状态（机器位置、血量）写回记录，
        /// 不卸载（保存失败时玩家留在游戏里）。机器记录导出由 <see cref="Campaign.CampaignAutoSaveService.SaveWithExport"/> 负责。</summary>
        public static void SyncActiveRegionForSave()
        {
            WorldSimulation.SyncAllForSave();
        }

        /// <summary>HUD 暂停按钮的统一入口：旧细胞阶段用自己的暂停；世界里暂停 / 继续整个世界（统一时钟）。</summary>
        public static void ToggleWorldPause()
        {
            if (CellStage != null && CellStage.IsRunning)
            {
                CellStage.SetPaused(!CellStage.Paused, strategic: true);
            }
            else if (WorldSimulation.AnyLoaded)
            {
                GameClock.TogglePause();
            }
        }

        /// <summary>FG0-ARCH-01：通知与反馈时刻的“发生在哪个地点 / 镜头在哪个地点 / 点击定位”接到整个世界（Startup 调用；
        /// batchmode 自检不走 Startup，直接调用本方法接上同一套提供者）。</summary>
        public static void BindWorldProviders()
        {
            GameLogic.Notifications.NotificationCenter.LocateHandler = LocateForNotification;
            GameLogic.Notifications.NotificationCenter.RegionProvider = () => ActiveRegionId;
            // 反馈时刻（特效、音量）记下发生在哪个地点；镜头不在那个地点时不在眼前的画面上放特效、音量压到最低。
            Campaign.Feedback.FeedbackCues.SiteProvider = () => ActiveRegionId;
            Campaign.Feedback.FeedbackCues.ObservedSiteProvider = () => WorldSimulation.AnyLoaded ? WorldView.ObservedSiteId : null;
        }

        public static void Startup()
        {
            if (_started)
            {
                return;
            }
            _started = true;

            // 编辑器失焦会冻结播放循环，长时间挂机验证会中断。
            // 见 memory/unity-mcp-headless-testing。
            Application.runInBackground = true;

            _director = new StageDirector();

            // 注册所有阶段。后续阶段在这里各加一行，StageDirector 本身不改。
            _director.Register(new CellStageFlow());

            Utility.Unity.AddUpdateListener(OnUpdate);
            Utility.Unity.AddDestroyListener(Shutdown);

            MountDebugHud();

            // FG0-UX-01：通知中心的自动暂停 / 定位 / 区域来源接到当前运行的区域；UI 基础件（浮层、通知、按键面板、
            // 暂停菜单）从主菜单阶段就挂上（主菜单的改键冲突确认、全部按键面板也要用）。
            GameLogic.Notifications.NotificationCenter.AutoPauseHandler = AutoPauseForNotification;
            BindWorldProviders();
            UI.Kit.UiKitRuntime.Mount();

            Log.Info("[GameRoot] 启动完成。已注册阶段：Cell");
        }

        /// <summary>开始一局细胞阶段。</summary>
        public static void StartCellStage()
        {
            StartCellStage(CellStageEntryMode.NewRun);
        }

        /// <summary>从已有局面恢复细胞阶段；与 <see cref="StartCellStage"/> 的新局语义分开。</summary>
        public static void ResumeCellStage()
        {
            StartCellStage(CellStageEntryMode.Resume);
        }

        /// <summary>ER2-SCENE-01 / FG0-ARCH-01：新战役载入家园并把镜头放在家园。要求 <see cref="CampaignSession"/> 已经 Set 好。</summary>
        public static void StartHomeValley()
        {
            if (!_started)
            {
                Startup();
            }
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (home != null && home.IsLoaded)
            {
                WorldView.Observe(home.SiteId);
            }
        }

        /// <summary>读档进入家园；家园已经在运行（远征撤离 / 放弃后回来）时只把镜头飞回家园——**不重新进入**，
        /// 远征期间家园一直在运行（FG0-ARCH-01：派遣不退出家园）。</summary>
        public static void ResumeHomeValley()
        {
            // 世界已在运行（派遣 / 回家园）时不需要、也不重复启动；只有从主菜单进入时才启动。
            if (!_started && !WorldSimulation.AnyLoaded)
            {
                Startup();
            }
            HomeValleyController home = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded
                ? WorldSimulation.Home
                : WorldSimulation.LoadHome(resume: true);
            if (home != null && home.IsLoaded)
            {
                // 没有远征地点在运行时，世界的“当前地点”就是家园（读档恢复据此决定镜头放在哪里；与 Demo 回家园重新进入时写入的值一致）。
                if (WorldSimulation.ActiveExpedition == null && CampaignSession.Current != null)
                {
                    CampaignSession.Current.CurrentRegionId = HomeValleyLayout.RegionId;
                }
                WorldView.Observe(home.SiteId);
            }
        }

        /// <summary>DEBT-ER6REGION01-01：继续/读取战役时应恢复到哪个区域。此前主菜单固定回归还谷地——
        /// 在破碎都市/铸造前哨存档退出的玩家重进后被送回家园，而机器仍记在远征区域名下。
        /// 规则：存档的当前区域是远征区域且那里还有存活机器 → 回到该区域；否则回归还谷地。
        /// 纯函数，自检直接断言。</summary>
        public static string ResolveResumeRegion(CampaignState state)
        {
            string region = state?.CurrentRegionId;
            if ((region == FracturedCityLayout.RegionId || region == FoundryOutpostLayout.RegionId)
                && state.MachineRecords != null
                && state.MachineRecords.Any(m => m != null && m.IsAlive && m.RegionId == region))
            {
                return region;
            }
            return HomeValleyLayout.RegionId;
        }

        /// <summary>继续/读取战役的唯一入口（主菜单调用）。FG0-ARCH-01：整个世界一起恢复——家园总是载入并运行；存档时在外的远征
        /// （<see cref="ResolveResumeRegion"/>）也一起载入，镜头放在远征地点；远征地点拒绝载入时安全回退只看家园，不会停在空场景。</summary>
        public static void ResumeCampaign()
        {
            if (!_started)
            {
                Startup();
            }
            string region = ResolveResumeRegion(CampaignSession.Current);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            if (region == FracturedCityLayout.RegionId)
            {
                ResumeFracturedCity();
                if (WorldSimulation.FracturedCity != null && WorldSimulation.FracturedCity.IsLoaded)
                {
                    return;
                }
                Log.Warning("[GameRoot] 读档恢复破碎都市未能载入，镜头回到归还谷地。");
            }
            else if (region == FoundryOutpostLayout.RegionId)
            {
                ResumeFoundryOutpost();
                if (WorldSimulation.FoundryOutpost != null && WorldSimulation.FoundryOutpost.IsLoaded)
                {
                    return;
                }
                Log.Warning("[GameRoot] 读档恢复铸造前哨外围未能载入，镜头回到归还谷地。");
            }
            if (home != null && home.IsLoaded)
            {
                WorldView.Observe(home.SiteId);
            }
        }

        /// <summary>ER5-REGION-01 / FG0-ARCH-01：派遣入口——把 <paramref name="expeditionLogicIds"/> 指定的家园存活机器派到破碎都市。
        /// **家园继续运行**（不再 Exit），镜头飞到远征地点；要求区域已 Available，否则控制器拒绝载入并记录日志。
        /// 完整的准备 / 校验 / 事务编排在 <see cref="ExpeditionDepartureService.TryDepart"/>。</summary>
        public static void StartFracturedCity(System.Collections.Generic.IEnumerable<int> expeditionLogicIds)
        {
            // 世界已在运行（派遣 / 回家园）时不需要、也不重复启动；只有从主菜单进入时才启动。
            if (!_started && !WorldSimulation.AnyLoaded)
            {
                Startup();
            }
            FracturedCityController site = WorldSimulation.LoadFracturedCity(expeditionLogicIds, resume: false);
            if (site != null && site.IsLoaded)
            {
                WorldView.Observe(site.SiteId);
                GuidanceHooks.Raise(GuidanceHooks.WorldFirstDispatch);
            }
        }

        /// <summary>读档时上次保存点仍在破碎都市：用仍标记在那里的存活机器重新载入（家园也在运行）。</summary>
        public static void ResumeFracturedCity()
        {
            // 世界已在运行（派遣 / 回家园）时不需要、也不重复启动；只有从主菜单进入时才启动。
            if (!_started && !WorldSimulation.AnyLoaded)
            {
                Startup();
            }
            CampaignState state = CampaignSession.Current;
            var alreadyThere = state?.MachineRecords?
                .Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive)
                .Select(m => m.LogicId) ?? System.Array.Empty<int>();
            FracturedCityController site = WorldSimulation.LoadFracturedCity(alreadyThere, resume: true);
            if (site != null && site.IsLoaded)
            {
                WorldView.Observe(site.SiteId);
            }
        }

        /// <summary>破碎都市撤离 / 暂离出口：卸载远征地点（家园一直在运行）；镜头若在那里则回到家园。</summary>
        public static void ExitFracturedCity(bool evacuateSuccess)
        {
            WorldSimulation.FracturedCity?.Exit(evacuateSuccess);
            WorldView.EnsureObservedLoaded();
        }

        /// <summary>ER6-FOUNDRY-01 / FG0-ARCH-01：派遣到铸造前哨外围（同 <see cref="StartFracturedCity"/>）。</summary>
        public static void StartFoundryOutpost(System.Collections.Generic.IEnumerable<int> expeditionLogicIds)
        {
            // 世界已在运行（派遣 / 回家园）时不需要、也不重复启动；只有从主菜单进入时才启动。
            if (!_started && !WorldSimulation.AnyLoaded)
            {
                Startup();
            }
            FoundryOutpostController site = WorldSimulation.LoadFoundryOutpost(expeditionLogicIds, resume: false);
            if (site != null && site.IsLoaded)
            {
                WorldView.Observe(site.SiteId);
                GuidanceHooks.Raise(GuidanceHooks.WorldFirstDispatch);
            }
        }

        /// <summary>读档时上次保存点仍在铸造前哨外围：用同一批机器重新载入。</summary>
        public static void ResumeFoundryOutpost()
        {
            // 世界已在运行（派遣 / 回家园）时不需要、也不重复启动；只有从主菜单进入时才启动。
            if (!_started && !WorldSimulation.AnyLoaded)
            {
                Startup();
            }
            CampaignState state = CampaignSession.Current;
            var alreadyThere = state?.MachineRecords?
                .Where(m => m.RegionId == FoundryOutpostLayout.RegionId && m.IsAlive)
                .Select(m => m.LogicId) ?? System.Array.Empty<int>();
            FoundryOutpostController site = WorldSimulation.LoadFoundryOutpost(alreadyThere, resume: true);
            if (site != null && site.IsLoaded)
            {
                WorldView.Observe(site.SiteId);
            }
        }

        /// <summary>铸造前哨外围撤离 / 暂离出口（同 <see cref="ExitFracturedCity"/>）。</summary>
        public static void ExitFoundryOutpost(bool evacuateSuccess)
        {
            WorldSimulation.FoundryOutpost?.Exit(evacuateSuccess);
            WorldView.EnsureObservedLoaded();
        }

        /// <summary>结束当前局，回到无阶段状态。ER2-BOOT-01：唯一的"返回菜单"出口——不管调用方是
        /// 玩家主动退出、阶段自然结束（死亡/通关）还是调试/测试代码，一律重新打开正式主菜单，
        /// 不停在旧运行中枢首页或黑屏。ER2-SCENE-01 补充：同时收摊归还谷地并显式
        /// <see cref="CampaignSession.Clear"/>——ERD-PER-002 生命周期红线，回菜单不得残留上一局引用。</summary>
        public static void EndRun()
        {
            // FG0-UX-01：离开世界时统一收起世界里打开的界面基础件（暂停菜单、按键面板、确认框、右键菜单、拖放、
            // 样例页）并清空 Esc 栈——否则胜负页上按 Esc 打开的暂停菜单会带着世界暂停与模态残留盖在主菜单上。
            UI.Kit.UiKitRuntime.CloseWorldUi();
            // FG0-ARCH-01：整个世界一起卸载（远征地点、家园），全局镜头解绑并复位输入。
            WorldSimulation.UnloadAll();
            _director?.EndCurrent();
            CampaignSession.Clear();
            // ER2-INPUT-01 / FG0-ARCH-01：回菜单复位统一时钟（速度 1x、未暂停、时间轴归零），不让上一局的状态粘到下一局。
            GameClock.ResetSession();
            GameModule.UI.ShowUIAsync<MainMenuUI>();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// LookDev 对照沙盒入口（story-006）：抑制刷怪/阶段推进/玩家真实网格装配 Tick。
        /// 只在编辑器/开发构建可用，正式包不出现该入口。
        /// </summary>
        public static void StartLookDevSandbox()
        {
            StartCellStage(CellStageEntryMode.LookDevSandbox);
        }

        /// <summary>M2-06：进入独立固定试玩门，不读取续局控制状态，也不继承 LookDev 抑制态。</summary>
        public static void StartConsciousnessPlaytest()
        {
            StartCellStage(CellStageEntryMode.ConsciousnessPlaytest);
        }
#endif

        private static void StartCellStage(CellStageEntryMode mode)
        {
            if (!_started)
            {
                Startup();
            }
            CellStage?.PrepareNextEnter(mode);
            _director.GoTo(StageId.Cell);
        }

        private static void OnUpdate()
        {
            if (_director == null)
            {
                return;
            }

            float dt = Time.deltaTime;
            _director.Update(dt);
            // FG0-ARCH-01：整个世界（家园 + 远征地点 + 行进中的队伍）按统一时钟的固定步同时推进；镜头只决定玩家看哪里。
            WorldSimulation.Frame(Time.unscaledDeltaTime);
            // ER8-CONTENT-01：音量设置同步、音效预加载、字幕条过期；UI 缩放设置作用到界面。
            // 主菜单里也要跑（设置面板在那里）。
            Campaign.Feedback.FeedbackCues.Tick();
            // FG0-UX-01：通知中心——弹出条过期、跟随战役切换重新绑定历史（主菜单里也要跑：回菜单时清空）。
            GameLogic.Notifications.NotificationCenter.Tick();
            Settings.UiScaleApplier.Tick();
            UI.Common.ContentIcons.Tick();

            // 阶段自然结束（死亡或通关）时收摊并回主菜单。
            // 由 GameRoot 判断而不是阶段自己切换，保证阶段不需要知道 director。
            // 统一走 EndRun()（而不是直接 _director.EndCurrent()），使"自然结束"与"玩家/调试代码主动
            // 调用 EndRun()"两条路径共用同一套回菜单逻辑，不重复也不遗漏。
            CellStageFlow cell = CellStage;
            if (_director.CurrentId == StageId.Cell && cell != null && !cell.IsRunning)
            {
                EndRun();
            }
        }

        private static GameObject _hudHost;

        /// <summary>
        /// 挂载全构建都需要的装配状态桥；IMGUI/压力测试只保留在编辑器和开发构建，
        /// 正式玩家入口由 UI Toolkit 运行中枢与玩法面板负责。
        /// </summary>
        private static void MountDebugHud()
        {
            if (_hudHost != null)
            {
                return;
            }
            _hudHost = new GameObject("[GameUiSupport]");
            Object.DontDestroyOnLoad(_hudHost);
            _hudHost.AddComponent<UI.Battle.MetabolicSlicePanel>();
            // ER2-INPUT-01 AC-UI-005：战略速度/暂停常驻 HUD，细胞阶段与归还谷地共用同一个实例
            // （谁在跑就显示谁，见该类 Update() 里的可见性判断），不随任一场景的 Enter/Exit 增删。
            _hudHost.AddComponent<UI.Common.StrategyClockHudToolkit>();
            // ER3-ECO-01 AC-ECO-009：资源账本 HUD（可用/预留/最近收支），左上角，与右上角的速度
            // HUD 不重叠；只在归还谷地激活时可见，见该类 Update() 判断。独立 GameObject 而非挂在
            // _hudHost 上——UIDocument 组件不允许同一 GameObject 重复添加，StrategyClockHudToolkit
            // 已经在 _hudHost 上加了一个，本类 Start() 里再加会返回 null 导致 NRE（Play Mode 实测
            // 发现的真实 bug，已修复）。
            var economyHudHost = new GameObject("[EconomyHudHost]");
            Object.DontDestroyOnLoad(economyHudHost);
            economyHudHost.AddComponent<UI.Common.EconomyHudToolkit>();
            // ER3-WRK-01：工作单面板（Ready/Reserved/InProgress/Waiting 列表），同样独立 GameObject
            // （同一条 UIDocument 唯一性纪律），只在归还谷地激活时可见。
            var workOrderHudHost = new GameObject("[WorkOrderHudHost]");
            Object.DontDestroyOnLoad(workOrderHudHost);
            workOrderHudHost.AddComponent<UI.WorkOrder.WorkOrderPanelUIToolkit>();
            // ER4-FAC-01：装配站生产队列面板，同样独立 GameObject（同一条 UIDocument 唯一性纪律），
            // 只在归还谷地激活且玩家点开装配站时可见（见 FactoryPanelUIToolkit.Update）。
            var factoryHudHost = new GameObject("[HomeValleyFactoryHost]");
            Object.DontDestroyOnLoad(factoryHudHost);
            factoryHudHost.AddComponent<UI.Factory.FactoryPanelUIToolkit>();
            // ER4-PRIM-02：3×3 电路板面板，同样独立 GameObject（同一条 UIDocument 唯一性纪律）。
            // 面板自带常驻切换按钮，不依赖建筑点选路由，只在归还谷地激活时可见
            // （见 CircuitBoardPanelUIToolkit.Update）。
            var circuitBoardHudHost = new GameObject("[HomeValleyCircuitBoardHost]");
            Object.DontDestroyOnLoad(circuitBoardHudHost);
            circuitBoardHudHost.AddComponent<UI.CircuitBoard.CircuitBoardPanelUIToolkit>();
            // ER4-PRIM-04：合成台面板，同样独立 GameObject（同一条 UIDocument 唯一性纪律），
            // 只在归还谷地激活时可见（见 PrimitiveCraftPanelUIToolkit.Update）。
            var craftStationHudHost = new GameObject("[HomeValleyCraftStationHost]");
            Object.DontDestroyOnLoad(craftStationHudHost);
            craftStationHudHost.AddComponent<UI.PrimitiveCraft.PrimitiveCraftPanelUIToolkit>();
            // ER6-ANA-01：解析台面板，同样独立 GameObject（同一条 UIDocument 唯一性纪律），只在归还
            // 谷地激活且玩家点开已修复的解析台建筑时可见（见 AnalysisPanelUIToolkit.Update）。
            var analysisHudHost = new GameObject("[HomeValleyAnalysisHost]");
            Object.DontDestroyOnLoad(analysisHudHost);
            analysisHudHost.AddComponent<UI.Analysis.AnalysisPanelUIToolkit>();
            // ER3-SOFTLOCK-01：家园核心被毁的全屏失败面板，同样独立 GameObject（同一条 UIDocument
            // 唯一性纪律），只在归还谷地激活且核心已被摧毁时可见。
            var homeValleyFailureHost = new GameObject("[HomeValleyFailureHost]");
            Object.DontDestroyOnLoad(homeValleyFailureHost);
            homeValleyFailureHost.AddComponent<UI.HomeValleyFailure.HomeValleyFailureUIToolkit>();
            // ER5-EXP-01：远征准备面板，同样独立 GameObject（同一条 UIDocument 唯一性纪律），
            // 只在归还谷地激活且玩家点开信号塔时可见（见 ExpeditionPrepPanelUIToolkit.Update）。
            var expeditionPrepHost = new GameObject("[HomeValleyExpeditionPrepHost]");
            Object.DontDestroyOnLoad(expeditionPrepHost);
            expeditionPrepHost.AddComponent<UI.Expedition.ExpeditionPrepPanelUIToolkit>();
            // ER5-RETURN-01：撤离确认/放弃远征面板，同样独立 GameObject（同一条 UIDocument 唯一性
            // 纪律），只在破碎都市激活且到达撤离点/全灭时可见（见 ExpeditionReturnPanelUIToolkit.Update）。
            var expeditionReturnHost = new GameObject("[FracturedCityExpeditionReturnHost]");
            Object.DontDestroyOnLoad(expeditionReturnHost);
            expeditionReturnHost.AddComponent<UI.Expedition.ExpeditionReturnPanelUIToolkit>();
            // ER5-CMD-01：战略命令条（选择集/编组/Move·Attack·Guard·Retreat），同样独立 GameObject
            // （同一条 UIDocument 唯一性纪律）；归还谷地与破碎都市共用同一个实例，谁在跑就显示谁的
            // RegionSquadCommandSystem（见该类 Update() 判断）。
            var regionCommandBarHost = new GameObject("[RegionCommandBarHost]");
            Object.DontDestroyOnLoad(regionCommandBarHost);
            regionCommandBarHost.AddComponent<UI.RegionCommand.RegionCommandBarUIToolkit>();
            // ER7-CORE-01：Boss 血条/阶段/节点/预警/热量面板，同样独立 GameObject（同一条 UIDocument
            // 唯一性纪律），只在铸造前哨激活且核心分区 Boss 已初始化时可见（见该类 Update()）。
            var coreBossHudHost = new GameObject("[CoreBossHudHost]");
            Object.DontDestroyOnLoad(coreBossHudHost);
            coreBossHudHost.AddComponent<UI.CoreBoss.CoreBossHudUIToolkit>();
            // ER7-BEACON-01：信标启动二次确认面板，同样独立 GameObject（同一条 UIDocument 唯一性
            // 纪律），只在归还谷地激活且玩家对着已建成的信标按 E 时可见（见该类 Update()）。
            var beaconLaunchHost = new GameObject("[BeaconLaunchPanelHost]");
            Object.DontDestroyOnLoad(beaconLaunchHost);
            beaconLaunchHost.AddComponent<UI.Beacon.BeaconLaunchPanelUIToolkit>();
            // ER7-CREDITS-01：胜利页，同样独立 GameObject（同一条 UIDocument 唯一性纪律），只在信标
            // 真正启动完成（不在10秒演出中）时显示，与家园失败面板互斥（见该类 Update()）。
            var victoryPageHost = new GameObject("[VictoryPageHost]");
            Object.DontDestroyOnLoad(victoryPageHost);
            victoryPageHost.AddComponent<UI.Victory.VictoryPageUIToolkit>();
            // ER8-CONTENT-01 AC-AUD-001：反馈字幕条（每个关键声音的非声音等价反馈），同样独立 GameObject
            // （同一条 UIDocument 唯一性纪律）。全程常驻、不拦截点击，只渲染 FeedbackCues.ActiveCaptions。
            var feedbackCaptionHost = new GameObject("[FeedbackCaptionHudHost]");
            Object.DontDestroyOnLoad(feedbackCaptionHost);
            feedbackCaptionHost.AddComponent<UI.Feedback.FeedbackCaptionHudUIToolkit>();
            // ER8 收尾 DEBT-ER6LOOP01-01：常驻当前目标条 + 任务日志/战役地图窗口，各自独立 GameObject
            // （同一条 UIDocument 唯一性纪律）。只在三个区域之一激活时可见，只读追踪器与目标表。
            var objectiveHudHost = new GameObject("[ObjectiveHudHost]");
            Object.DontDestroyOnLoad(objectiveHudHost);
            objectiveHudHost.AddComponent<UI.Objective.ObjectiveHudUIToolkit>();
            var missionLogHost = new GameObject("[MissionLogHost]");
            Object.DontDestroyOnLoad(missionLogHost);
            missionLogHost.AddComponent<UI.Objective.MissionLogUIToolkit>();
            // ER8-CONTENT-01（DEBT-ER8CONTENT01-01 VFX 半边）：命中/击毁/敌方开火/重炮/反应/信号的世界特效，
            // 统一读 FeedbackCues 的带位置时刻；固定对象池，不随敌人数增长。
            var feedbackVfxHost = new GameObject("[FeedbackVfxHost]");
            Object.DontDestroyOnLoad(feedbackVfxHost);
            feedbackVfxHost.AddComponent<View.FeedbackVfxPresenter>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 已被 UI Toolkit 覆盖的旧 IMGUI 调试 HUD 不得默认盖在玩家界面上；
            // 如需做历史对照，可在运行时显式启用该组件。
            UI.Battle.CellDebugHud debugHud = _hudHost.AddComponent<UI.Battle.CellDebugHud>();
            debugHud.enabled = false;
            _hudHost.AddComponent<Battle.StressTestToggle>();
#endif
        }

        private static void Shutdown()
        {
            Utility.Unity.RemoveUpdateListener(OnUpdate);
            if (_hudHost != null)
            {
                Object.Destroy(_hudHost);
                _hudHost = null;
            }
            _director?.Dispose();
            _director = null;
            WorldSimulation.UnloadAll();
            Signals.Clear();
            _started = false;
            Log.Info("[GameRoot] 已关闭。");
        }
    }
}
