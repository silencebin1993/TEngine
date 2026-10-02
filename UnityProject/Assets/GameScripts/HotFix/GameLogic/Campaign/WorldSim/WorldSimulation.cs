using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-01（FG14 FGR-ARC-002 整个世界同时运行、FGR-ARC-009 统一时钟；FG00 FGR-BASE-021 观察不改变结果）：同一个世界模拟。
    ///
    /// Demo 的家园、破碎都市、铸造前哨三个区域控制器不再互斥：它们是同一个世界里的三个地点（<see cref="IWorldSite"/>），
    /// 已载入的地点**全部同时**按统一时钟（<see cref="GameClock"/>）的固定步推进，外加星球上行进中的队伍（<see cref="WorldTransitSystem"/>）。
    /// 镜头（<see cref="WorldView"/>）只决定玩家看哪里、输入给谁，**不决定谁在运行**。
    ///
    /// 每帧：全局输入与镜头 → 统一时钟给出本帧步数 → 逐步推进全部地点与队伍（固定顺序：家园、破碎都市、铸造前哨、队伍）→
    /// 被观察的地点处理玩家输入与画面对账 → 星球表现层（流式加载、地貌、队伍标记）。
    /// 热更层每帧开销 = O(地点数 × 步数) 次调度；地点内部的逐机器逻辑是 Demo 遗留（机器数个位～十位），
    /// 后期规模的逐单位 / 逐弹体逻辑在 FG0-ARCH-03 下沉内核（DEBT-FG0ARCH01-03）。
    ///
    /// 生命周期：新建 / 读档接到战役时（没有任何已载入地点）重置会话态（新的控制器实例、各系统的会话静态量、时钟从存档恢复）；
    /// 回主菜单 / 回滚时 <see cref="UnloadAll"/>。派遣远征只载入远征地点，**不退出家园**。
    /// </summary>
    public static class WorldSimulation
    {
        public static HomeValleyController Home { get; private set; }
        public static FracturedCityController FracturedCity { get; private set; }
        public static FoundryOutpostController FoundryOutpost { get; private set; }

        /// <summary>正在执行模拟步的地点 ID（只在某个地点的 SimStep 期间非空）——通知与反馈时刻据此记下“发生在哪个地点”。</summary>
        public static string CurrentSiteId { get; private set; }

        /// <summary>FG1-SIG-07：在模拟步里、但不在某个地点的 SimStep 之内（覆盖评估在步首统一做）时，把通知 / 反馈记到事件真正发生的地点。
        /// 执行完恢复原值；可嵌套。</summary>
        public static void RunAsSite(string siteId, Action action)
        {
            if (action == null)
            {
                return;
            }
            string prev = CurrentSiteId;
            CurrentSiteId = siteId;
            try
            {
                action();
            }
            finally
            {
                CurrentSiteId = prev;
            }
        }

        /// <summary>本会话执行过的模拟步数、单步耗时（性能证据）。</summary>
        public static long StepsExecuted { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        public static double LastFrameSimMs { get; private set; }
        public static int SessionResetCount { get; private set; }

        private static readonly List<IWorldSite> SitesScratch = new List<IWorldSite>(3);
        private static readonly HashSet<long> ActiveChunkSet = new HashSet<long>();
        private static readonly Stopwatch StepWatch = new Stopwatch();
        private static double _objectiveTimer;
        private static double _activityTimer;

        /// <summary>活跃区块（有己方实体 / 行进中的队伍 / 被观察）数量与查询（FGR-GEN-052 第 1、2 条；回收规则据此保留）。</summary>
        public static int ActiveChunkCount => ActiveChunkSet.Count;
        public static bool IsChunkActive(long key) => ActiveChunkSet.Contains(key);
        public static int ActivityRefreshCount { get; private set; }

        /// <summary>已载入的地点（固定顺序：家园、破碎都市、铸造前哨）。</summary>
        public static IReadOnlyList<IWorldSite> LoadedSites
        {
            get
            {
                SitesScratch.Clear();
                if (Home != null && Home.IsLoaded)
                {
                    SitesScratch.Add(Home);
                }
                if (FracturedCity != null && FracturedCity.IsLoaded)
                {
                    SitesScratch.Add(FracturedCity);
                }
                if (FoundryOutpost != null && FoundryOutpost.IsLoaded)
                {
                    SitesScratch.Add(FoundryOutpost);
                }
                return SitesScratch;
            }
        }

        public static bool AnyLoaded =>
            (Home != null && Home.IsLoaded) || (FracturedCity != null && FracturedCity.IsLoaded) || (FoundryOutpost != null && FoundryOutpost.IsLoaded);

        public static IWorldSite FindSite(string siteId)
        {
            if (string.IsNullOrEmpty(siteId))
            {
                return null;
            }
            if (siteId == HomeValleyLayout.RegionId)
            {
                return Home;
            }
            if (siteId == FracturedCityLayout.RegionId)
            {
                return FracturedCity;
            }
            if (siteId == FoundryOutpostLayout.RegionId)
            {
                return FoundryOutpost;
            }
            return null;
        }

        public static bool IsSiteLoaded(string siteId) => FindSite(siteId)?.IsLoaded ?? false;

        /// <summary>当前在外的远征地点（没有返回 null）。</summary>
        public static IWorldSite ActiveExpedition =>
            FracturedCity != null && FracturedCity.IsLoaded ? FracturedCity :
            FoundryOutpost != null && FoundryOutpost.IsLoaded ? (IWorldSite)FoundryOutpost : null;

        // ── 载入 / 卸载 ─────────────────────────────────────────────────────────

        /// <summary>接入一个战役：没有任何已载入地点时重置会话态（控制器用新实例、各系统会话静态量清零、时钟从存档恢复）。</summary>
        private static void EnsureSession(CampaignState state)
        {
            if (AnyLoaded)
            {
                return;
            }
            ResetSessionState();
            GameClock.Bind(state);
            SessionResetCount++;
        }

        /// <summary>重置本会话的运行时状态（不动存档数据）：Demo 服务里的静态计时 / 看门狗 / 去重集合也在这里清零，
        /// 否则上一次载入残留的计时会让同一存档读两次跑出不同结果。</summary>
        public static void ResetSessionState()
        {
            HomeValleyWorkOrders.ResetSessionState();
            Economy.HomeInventory.ResetSessionState(); // FG4-ECO-01：物资索引按新战役重建
            Economy.ItemFlowStats.ResetSessionState();
            Economy.ProductionStats.ResetSessionState(); // FG4-ECO-08：统计的会话计数（存档里的桶不动）
            Economy.ItemDistribution.Invalidate();
            HomeValleySoftlockGuard.ResetSessionState();
            HomeValleyAlarms.ResetSessionState();
            HomeValleyCombatTargets.ResetSessionState();
            HomeValleyFactory.ResetSessionState(); // FG4-ECO-03：装配站等料指纹与输入口堵塞提示按新会话重算
            WorldOutpostSystem.ResetSession();
            NavService.ResetCounters();
            ActiveChunkSet.Clear();
            _objectiveTimer = 0;
            _activityTimer = 0;
            StepsExecuted = 0;
            MaxStepMs = 0;
            LastStepMs = 0;
            _inStep = false;
            AfterStepActions.Clear();
        }

        public static HomeValleyController LoadHome(bool resume)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                Log.Error("[WorldSimulation] 没有活动战役，拒绝载入家园。");
                return Home;
            }
            if (Home != null && Home.IsLoaded)
            {
                return Home;
            }
            EnsureSession(state);
            // FG0-ARCH-06：星球表面的寻路内核先于家园建好（家园的战斗内核打开时绑定它的通行镜像；读档时恢复排队的请求与待采纳的结果）。
            NavService.Bind(state);
            Home = new HomeValleyController();
            Home.Enter(resume);
            NavService.AfterSitesLoaded(state);
            if (Home.IsLoaded)
            {
                BindLivePositions();
                HomeGridService.Streamer(state).KeepResident = IsChunkActive;
                // FG0-ARCH-02：星球表面的传送带内核随家园载入（从存档恢复），与家园同一生命周期。
                BeltNetworkService.Load(state);
                PipeNetworkService.Load(state);
                // FG4-ECO-02：生产建筑的运行时索引随家园载入（下一次对账按存档里的句柄核对管线内核）。
                Economy.ProductionService.OnLoad(state);
                RefreshActivity(state);
            }
            return Home;
        }

        /// <summary>派遣（或读档恢复）到破碎都市：只载入远征地点，家园继续运行。</summary>
        public static FracturedCityController LoadFracturedCity(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return FracturedCity;
            }
            if (FracturedCity != null && FracturedCity.IsLoaded)
            {
                return FracturedCity;
            }
            EnsureSession(state);
            FracturedCity = new FracturedCityController();
            FracturedCity.Enter(expeditionLogicIds, resume);
            BindLivePositions();
            return FracturedCity;
        }

        public static FoundryOutpostController LoadFoundryOutpost(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return FoundryOutpost;
            }
            if (FoundryOutpost != null && FoundryOutpost.IsLoaded)
            {
                return FoundryOutpost;
            }
            EnsureSession(state);
            FoundryOutpost = new FoundryOutpostController();
            FoundryOutpost.Enter(expeditionLogicIds, resume);
            BindLivePositions();
            return FoundryOutpost;
        }

        /// <summary>全部卸载（回主菜单 / 回滚 / 自检之间）。卸载顺序：远征地点先（撤离数据按“暂离”处理，不结算），家园最后。</summary>
        public static void UnloadAll()
        {
            FracturedCity?.Exit(evacuateSuccess: false);
            FoundryOutpost?.Exit(evacuateSuccess: false);
            Home?.Exit();
            BeltNetworkService.Unload();
            PipeNetworkService.Unload();
            Economy.ProductionService.OnUnload();
            HomeValleyPowerGrid.Unbind(); // FG3-LOG-06：电网内核随世界卸载（先把曲线与储能写回绑定的战役）。
            Combat.CombatSites.CloseAll(); // FG0-ARCH-03：保险——各地点 Exit 已各自释放内核，这里确保没有泄漏的原生容器。
            NavService.Unload(); // FG0-ARCH-06：寻路内核在战斗内核之后释放（战斗内核绑定着它的通行镜像）。
            Signal.SignalLinkService.ClearWatch(); // FG1-SIG-04：链路预警是运行时状态，随世界卸载清掉。
            Signal.SignalUplinkService.OnWorldUnloaded(); // FG1-SIG-07 审查修复：接入 / 跳回家园的过渡是运行时状态，不带进下一局或读档后的对局。
            Signal.SignalCoverageService.Clear(); // 覆盖源缓存引用着旧战役：一并清掉。
            GameLogic.View.SignalLinkView.Clear(); // 地图预警圈用共享材质：先于材质释放。
            GameLogic.View.SignalCoverageOverlayView.Clear(); // FG1-SIG-07：覆盖网络叠加层同样用共享材质。
            GameLogic.View.PowerCoverageOverlayView.Clear(); // FG3-LOG-06：电力覆盖叠加层同样用共享材质。
            GameLogic.View.MachineMorphView.Clear(); // FG1-VFX-01：形变部件的共享网格与材质随世界成对释放（部件已随各地点表现对象销毁）。
            GameLogic.View.ViewMaterials.ReleaseAll(); // FG0-ARCH-03：地点表现对象的共享材质与世界成对释放（各地点的表现对象此时已全部销毁）。
            FracturedCity = null;
            FoundryOutpost = null;
            Home = null;
            MachineRegistry.LivePositionProvider = null;
            WorldView.Reset();
            ActiveChunkSet.Clear();
            CurrentSiteId = null;
        }

        /// <summary>存档前把每个已载入地点的实时状态（机器位置、血量）写回记录——不卸载。</summary>
        public static void SyncAllForSave()
        {
            foreach (IWorldSite site in LoadedSites.ToArray())
            {
                site.SyncLiveStateForSave();
            }
            // FG0-ARCH-02：传送带内核快照（按网络分块）写进 BeltItemState。
            BeltNetworkService.WriteTo(BeltNetworkService.BoundState);
            PipeNetworkService.WriteTo(PipeNetworkService.BoundState);
            // FG3-LOG-06：电网曲线、电网编号锚点与储能存量写进 PowerGridState（拓扑本身读档后按建筑记录重算）。
            HomeValleyPowerGrid.WriteTo(HomeValleyPowerGrid.BoundState);
            // FG0-ARCH-03：每个已载入地点的战斗内核快照（单位、编队命令、冷却、热量、标记、飞行中的弹体）写进 CombatState。
            Combat.CombatSites.WriteTo(CampaignSession.Current);
            // FG0-ARCH-06：寻路内核的排队请求、待采纳结果、还没同步的格网变化写进 NavState（读档接着跑与不存档一致）。
            NavService.WriteTo(CampaignSession.Current);
        }

        private static void BindLivePositions()
        {
            MachineRegistry.LivePositionProvider = LivePosition;
        }

        /// <summary>机器实时位置：问每个已载入的地点（机器只会在一个地点里有表现对象）。</summary>
        public static Vector2? LivePosition(int logicId)
        {
            // 直接查三个地点字段（不用 LoadedSites 的共享缓冲：本方法会在其它遍历过程中被间接调用）。
            // 先按记录所在地点问（刚派遣出去的机器在家园下一步回收标记之前，家园仍留着它的旧标记，不能拿那个位置）。
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null)
            {
                if (rec.RegionId == FracturedCityLayout.RegionId)
                {
                    return FracturedCity != null && FracturedCity.IsLoaded ? FracturedCity.LivePosition(logicId) : null;
                }
                if (rec.RegionId == FoundryOutpostLayout.RegionId)
                {
                    return FoundryOutpost != null && FoundryOutpost.IsLoaded ? FoundryOutpost.LivePosition(logicId) : null;
                }
                if (rec.RegionId == HomeValleyLayout.RegionId)
                {
                    return Home != null && Home.IsLoaded ? Home.LivePosition(logicId) : null;
                }
            }
            Vector2? p = Home != null && Home.IsLoaded ? Home.LivePosition(logicId) : null;
            if (!p.HasValue && FracturedCity != null && FracturedCity.IsLoaded)
            {
                p = FracturedCity.LivePosition(logicId);
            }
            if (!p.HasValue && FoundryOutpost != null && FoundryOutpost.IsLoaded)
            {
                p = FoundryOutpost.LivePosition(logicId);
            }
            return p;
        }

        // ── 推进 ────────────────────────────────────────────────────────────────

        /// <summary>每帧（GameRoot.OnUpdate）。<paramref name="tickLimit"/> 只给自检用来精确停在同一步。</summary>
        public static void Frame(float realDt, long tickLimit = long.MaxValue)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !AnyLoaded)
            {
                return;
            }
            WorldView.FrameBegin(state);
            int steps = GameClock.Advance(realDt, tickLimit);
            double before = StepWatch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < steps; i++)
            {
                StepOnce(state);
                if (GameClock.Paused)
                {
                    // 步内触发了暂停（紧急通知自动暂停等）：停在这一步，剩下的步退回累计量，继续后再执行。
                    GameClock.RefundSteps(steps - i - 1);
                    break;
                }
            }
            LastFrameSimMs = StepWatch.Elapsed.TotalMilliseconds - before;
            // 目标重算只跟随游戏时间（StepOnce 里按 clock.objective_recompute_seconds）：暂停中不重算，继续后 0.5 游戏秒内补上——
            // 这样“暂停过几次”不会改变目标在哪一步完成（暂停 / 倍速结果与 1x 逐字段一致）。
            // FG1-SIG-03：信号接入过渡（真实时间，暂停中不走）——在模拟推进之后判定“过渡途中目标阵亡”，在被观察地点处理输入之前提交，
            // 提交后下一帧起新机器才收输入。
            Signal.SignalUplinkService.FrameTick(realDt);
            GameLogic.View.SignalLinkView.FrameTick(); // FG1-SIG-04：安全模式头顶图标、地图上的覆盖边缘预警（纯表现）。
            GameLogic.View.OverlayService.FrameTick(); // FG3-LOG-08：8 种叠加层的开关与表现（先于电力 / 信号视图，保证同帧互斥）。
            GameLogic.View.SignalCoverageOverlayView.FrameTick(); // FG1-SIG-07：覆盖网络叠加层（纯表现，网络变了才重画）。
            GameLogic.View.PowerCoverageOverlayView.FrameTick(); // FG3-LOG-06：电力覆盖叠加层（纯表现，电网拓扑变了才重画）。
            GameLogic.View.MachineMorphView.FrameTick(realDt); // FG1-VFX-01：机身形变过渡（真实时间、暂停不走；只推进正在过渡的机器）。
            IWorldSite observed = WorldView.ObservedSite;
            if (observed != null && observed.IsLoaded)
            {
                observed.FrameUpdate(realDt, GameClock.FrameScaledDt);
            }
            WorldView.FrameEnd(state);
        }

        /// <summary>执行 <paramref name="count"/> 个模拟步，不经过镜头与输入（无头推进：自检“不观察任何地点”的对照组，也是正式流程里同一段代码）。</summary>
        public static void StepMany(int count)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            for (int i = 0; i < count; i++)
            {
                StepOnce(state);
            }
        }

        /// <summary>当前是否在一个模拟步内部（地点 SimStep / 队伍推进尚未全部完成、时钟尚未提交）。</summary>
        public static bool InStep => _inStep;

        private static bool _inStep;
        private static readonly List<Action> AfterStepActions = new List<Action>();
        private static readonly List<Action> AfterStepRunning = new List<Action>();

        /// <summary>需要“整步提交之后”才能做的事（自动存档：ERD-SAV-002 不得在事务半提交中写入）。
        /// 在模拟步内调用时排到本步末尾（全部地点 + 队伍推进 + 时钟提交之后）按登记顺序执行；不在步内时立即执行。</summary>
        public static void RunAfterStep(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (_inStep)
            {
                AfterStepActions.Add(action);
            }
            else
            {
                action();
            }
        }

        private static void FlushAfterStep()
        {
            if (AfterStepActions.Count == 0)
            {
                return;
            }
            AfterStepRunning.Clear();
            AfterStepRunning.AddRange(AfterStepActions);
            AfterStepActions.Clear();
            foreach (Action action in AfterStepRunning)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error($"[WorldSimulation] 步末动作异常：{e}");
                }
            }
            AfterStepRunning.Clear();
        }

        /// <summary>一个固定模拟步：全部已载入地点 + 行进中的队伍，按固定顺序推进 dt = 1 / clock.sim_step_hz 游戏秒。</summary>
        private static void StepOnce(CampaignState state)
        {
            StepWatch.Start();
            long t0 = StepWatch.ElapsedTicks;
            float dt = GameClock.StepSeconds;
            _inStep = true;
            bool stepped = false;
            try
            {
                // FG0-ARCH-06：寻路流水线（格网变化推进镜像 → 到期批次采纳 → 被截断的路线重规划 → 调度下一批），只看步序号。
                NavService.BeginStep(state);
                // FG1-SIG-07：覆盖网络按游戏时间分桶（每 signal.coverage.refresh_seconds）在步首统一重建连通、评估每台机器在不在覆盖里
                // （逐单位在内核 Burst 作业里）——与是否被观察无关；同一步里后面的安全模式判定读的就是这次的结果。
                Signal.SignalCoverageService.BeginStep(state);
                if (Home != null && Home.IsLoaded)
                {
                    CurrentSiteId = Home.SiteId;
                    Home.SimStep(dt);
                }
                if (FracturedCity != null && FracturedCity.IsLoaded)
                {
                    CurrentSiteId = FracturedCity.SiteId;
                    FracturedCity.SimStep(dt);
                }
                if (FoundryOutpost != null && FoundryOutpost.IsLoaded)
                {
                    CurrentSiteId = FoundryOutpost.SiteId;
                    FoundryOutpost.SimStep(dt);
                }
                // 行进中的队伍在星球表面（家园所在的表面）上。
                CurrentSiteId = HomeValleyLayout.RegionId;
                WorldTransitSystem.Step(state, dt);
                // FG0-ARCH-06：敌方据点与巡逻（休眠判定、分帧唤醒、活跃的推进一步）。
                WorldOutpostSystem.Step(state, GameClock.Ticks);
                // FG0-ARCH-02：传送带内核（星球表面）按游戏时间累计推进到自己的 20 Hz（只看步序号，与镜头 / 帧率 / 倍速无关）。
                BeltNetworkService.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                // FG3-LOG-05：管线内核与传送带同一节拍（FGR-LOG-090）。
                PipeNetworkService.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                // FG4-ECO-02：采集与加工建筑（回收站、提取钻、精炼炉、精炼塔、调配站、废液池）：每 eco.prod.step_ticks 步推进一次、按 eco.prod.sync_seconds 对账流体口；
                // 只看步序号，与观察无关，O(生产建筑数)。放在传送带 / 管线之后：本步端口收到的料、管线送到的流体本步就能用。
                if (Home != null && Home.IsLoaded)
                {
                    Economy.ProductionService.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                }
                // FG4-ECO-01：物品库存采样（净速率），每 eco.flow.sample_seconds 游戏秒一次、只看步序号，与观察无关，O(物品种类)。
                Economy.ItemFlowStats.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                // FG4-ECO-08：持续赤字检查，每 eco.stats.deficit_check_seconds 游戏秒一次、只看步序号，与观察无关，O(检测窗口桶数 × 物品种类)；产量 / 消耗在发生时记账（不每帧遍历）。
                Economy.ProductionStats.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                // FG3-LOG-06：电网按游戏秒积分储能（没有储能时不做事）、按 power.sample_seconds 记曲线——只看步序号，与观察无关，O(电网数)。
                if (Home != null && Home.IsLoaded)
                {
                    HomeValleyPowerGrid.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                    // FG4-ECO-06：常驻规则——排队事件每步处理（O(事件数)），条件按 rules.check_seconds 定时检查（不每帧遍历建筑）；只看步序号，与观察无关。
                    Economy.StandingRuleService.WorldStep(state, GameClock.Ticks, GameClock.StepHz);
                    // FG4-ECO-09：离家报告与远征状态对齐（O(1)）：旧档读档时正在远征却没有报告 → 从此刻开一份；报告开着但远征已不在 → 按“其它方式结束”结算。
                    Economy.AwayReportService.Reconcile(state, ActiveExpedition?.SiteId);
                }
                // FG1-SIG-03：核心固件冷却（信号侧，按游戏时间）到期——与观察无关，O(信号核槽位数)。
                Signal.SignalUplinkService.SimStep(state);
                // FG1-SIG-06：常规裸跑固件的计次间隔到期（信号侧，按游戏时间，与观察无关），O(1)。
                Signal.RawFirmwareService.SimStep(state);
                // FG1-SIG-04：安全模式退出判定（按游戏时间每 0.5 秒，与观察无关），O(安全模式机器数)。
                Signal.SignalLinkService.SimStep(state);
                stepped = true;
            }
            finally
            {
                CurrentSiteId = null;
                if (!stepped)
                {
                    _inStep = false; // 地点步抛异常：不让“步内”标记卡住，之后的步末动作照常立即执行。
                }
            }
            GameClock.CommitStep(state);
            StepsExecuted++;

            _objectiveTimer -= dt;
            if (_objectiveTimer <= 0)
            {
                _objectiveTimer += Math.Max(0.05, GameClock.TuningOr("clock.objective_recompute_seconds", 0.5f));
                CampaignObjectiveTracker.Recompute(state);
            }
            _activityTimer -= dt;
            if (_activityTimer <= 0)
            {
                _activityTimer += Math.Max(0.1, GameClock.TuningOr("sim.activity_refresh_seconds", 1f));
                RefreshActivity(state);
            }
            _inStep = false;
            StepWatch.Stop();
            LastStepMs = (StepWatch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
            // 步末动作（自动存档写盘等）在整步提交之后执行，不计入模拟步耗时。
            FlushAfterStep();
        }

        // ── 活跃区块（FGR-GEN-052 第 1、2 条）──────────────────────────────────────

        /// <summary>重算星球表面的活跃区块：有己方建筑 / 机器的（<see cref="WorldActivity"/>）∪ 被观察的镜头窗口 ∪ 行进中的队伍所在区块。
        /// 只按 sim.activity_refresh_seconds 的间隔算（不每帧逐区块），开销 O(已加载区块 + 机器 + 队伍)。</summary>
        public static void RefreshActivity(CampaignState state)
        {
            if (state == null || !(Home != null && Home.IsLoaded))
            {
                ActiveChunkSet.Clear();
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            bool planetObserved = WorldView.ObservedSite != null && WorldView.ObservedSite.SurfaceKind == WorldSurfaceKind.Planet;
            GridCell focus = planetObserved ? WorldView.PlanetFocusCell : HomeGridService.CorePivot(state);
            int radius = planetObserved ? Math.Max(0, GridContent.TuningInt("world.view_radius_chunks")) : -1;
            WorldActivity.ActiveChunks(state, map, focus, radius, ActiveChunkSet);
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (g == null)
                {
                    continue;
                }
                ChunkAddress a = GridMath.Address(new GridCell((int)Math.Floor(g.PosX + 0.5), (int)Math.Floor(g.PosY + 0.5)), map.ChunkSize);
                ActiveChunkSet.Add(HomeGridMap.Key(a.ChunkX, a.ChunkY));
            }
            // FG0-ARCH-06：醒着的巡逻所在区块（“行进中的巡逻”完整模拟；休眠的不计）。
            WorldOutpostSystem.AddActivePatrolChunks(state, map.ChunkSize, ActiveChunkSet);
            ActivityRefreshCount++;
        }
    }
}
