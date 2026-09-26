using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.UI.Common;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-FOUNDRY-01：铸造前哨外围（第 2 次出击）空间/持久化流程，与
    /// <see cref="FracturedCityController"/> 同一"director 之外的常驻子系统"处理方式，由
    /// <see cref="GameLogic.Stage.GameRoot"/> 直接持有并驱动。
    ///
    /// ── 范围边界（见 <see cref="FoundryOutpostLayout"/> 类注释的完整裁剪说明）──
    /// 核心区门禁/三灯显示/撤离确认 UI 属于 ER6-REGION-01；本类的 <see cref="Exit"/> 只做数据层结算
    /// （同 <see cref="FracturedCityController.Exit"/> 在 ER5-RETURN-01 补 UI 之前的裸实现先例），
    /// 供未来 Story 挂 UI。远征出发/往返事务的正式编排属于 ER6-REGION-01 对 <see cref="ExpeditionDepartureService"/>
    /// 的扩展（目前该服务硬编码目标破碎都市），本类的 <see cref="Enter"/> 是它将要调用的最小可用
    /// 切场入口，与 ER5-REGION-01 对 <see cref="FracturedCityController.Enter"/> 的定位完全对称。</summary>
    public sealed class FoundryOutpostController : IWorldSite
    {
        /// <summary>FG0-ARCH-01：本地点已载入、正在被世界模拟推进（与镜头在不在这里无关；派遣不再退出家园）。</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>已载入**并且**镜头正在观察这里（界面面板、输入据此判断）。模拟推进不看它（FGR-BASE-021）。</summary>
        public bool IsActive => IsLoaded && WorldView.IsObserved(SiteId);

        /// <summary>FG0-ARCH-01：暂停属于整个世界（<see cref="GameClock"/>）。</summary>
        public bool IsPaused => GameClock.Paused;
        public bool IsWiped => _wipeResolved;
        /// <summary>ER6-REGION-01：撤离确认面板开关——与 <see cref="FracturedCityController.IsEvacPanelOpen"/>
        /// 同一定位。</summary>
        public bool IsEvacPanelOpen { get; private set; }
        public void SetEvacPanelOpen(bool open) => IsEvacPanelOpen = open;

        private GameObject _root;
        private Camera _camera;
        /// <summary>FG0-ARCH-01：镜头归全局镜头管理器（<see cref="WorldView"/>）；本地点被观察时借用。</summary>
        private CameraDirector _cameraDirector => IsActive && WorldView.Director.IsBound ? WorldView.Director : null;
        private WorldCameraProfile _cameraProfile;

        /// <summary>FG0-UX-01：通知“定位”要让当前区域的镜头飞到事件位置（只读访问，不改所有权）。</summary>
        public CameraDirector CameraDirector => _cameraDirector;
        /// <summary>本地点机器的逻辑句柄（战斗内核单位的门面，不是表现对象；FG0-ARCH-03）。</summary>
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(5);
        /// <summary>FG0-ARCH-03：本地点的战斗内核（机器、敌人、首领主核心的逐单位逻辑都在里面）。</summary>
        private CombatSite _combat;
        private FoundryOutpostCombatRules _combatRules;
        private RegionSquadCommandContext _squadCtx;
        /// <summary>上一次与内核对账时的敌人记录数组（首领初始化 / 召唤会替换数组；引用变了才对账，O(1) 判定）。</summary>
        private RegionEnemyRecord[] _lastRoster;
        public CombatSite Combat => _combat;
        private HomeValleyMachineMarker _selected;
        private HomeValleyMachineMarker _possessed;
        private bool _wipeResolved;
        /// <summary>FG0-ARCH-03：全灭检测里“记录侧还有存活机器”的缓存，按 <see cref="MachineRegistry.RosterRevision"/> 失效
        /// （名册没变的步不再逐台扫描记录，O(1)）。</summary>
        private int _wipeRosterRevision = int.MinValue;
        private bool _wipeRecordAlive;

        // FG0-ARCH-03：核心门三灯的输入快照。三灯要逐台机器解析当前蓝图（O(机器数)，还有分配），只在输入变化时重算：
        // 机器名册版本号（存活 / 地点 / 改造）、解锁表、事件账本（熔穿过载充能）、蓝图表、地点记录表都是整体替换的数组，按引用比较即可。
        private int _gateRosterRevision = int.MinValue;
        private object _gateStateRef;
        private object _gateUnlockRef;
        private object _gateLedgerRef;
        private object _gateBlueprintsRef;
        private object _gateRegionsRef;

        /// <summary>自检用：核心门三灯真正重算（O(机器数)）的次数；输入没变的步不增加。</summary>
        public int CoreGateRecomputeCount { get; private set; }

        public const float InteractRange = 3f;

        /// <summary>暂停 / 继续整个世界（FG0-ARCH-01：统一时钟）。</summary>
        public void SetPaused(bool paused) => GameClock.SetPaused(paused);

        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();
        public readonly RegionControlSystem Control = new RegionControlSystem();
        public readonly RegionInteractionSystem Interact = new RegionInteractionSystem();
        private Vector2 _lastFacing = new Vector2(1f, 0f);
        private const float AttackRange = 7f;

        private List<(Vector2 Position, float Radius)> _obstacles;

        public void Enter(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            if (IsLoaded)
            {
                Log.Warning("[FoundryOutpostController] Enter 被重复调用，忽略（已处于激活状态）。");
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                Log.Error("[FoundryOutpostController] 没有活动战役（CampaignSession.Current 为空），拒绝进入铸造前哨。");
                return;
            }

            FoundryOutpostRegion.EnsureRegionRecordSeeded(state);
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || region.State == RegionState.Locked)
            {
                Log.Error("[FoundryOutpostController] 铸造前哨尚未解锁（跨派系蓝图未保存/ERC-003 未改造），拒绝进入。");
                return;
            }

            FoundryOutpostRegion.EnsureEnemiesSeeded(state);
            if (region.State == RegionState.Available)
            {
                region.State = RegionState.Active;
            }
            FoundryOutpostRegion.RecoveryLockerCheck(state);
            FoundryOutpostRegion.RecomputeCoreGate(state);

            TransportMachinesIn(state, expeditionLogicIds);
            RegisterAllRegionMachineLoadouts(state);
            state.CurrentRegionId = FoundryOutpostLayout.RegionId;

            _obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (FoundryOutpostLayout.Anchor anchor in FoundryOutpostLayout.AllAnchors())
            {
                _obstacles.Add((anchor.Position, anchor.ClearanceRadius));
            }

            // FG0-ARCH-03：机器与敌人进本地点的战斗内核（读档时从快照恢复；新派遣按记录建）。表现对象只在被观察时建（SetObserved）。
            OpenCombat(state, resume);
            SetupCameraProfile();
            SetupSquadCommands();
            SetupControlSystem();
            SetupInteraction();

            IsLoaded = true;
            // FG0-UX-01（FGR-UX-020 定位）：机器实时位置由 WorldSimulation.LivePosition 统一向各地点查询。
            _wipeResolved = false;
            _wipeRosterRevision = int.MinValue;
            _gateRosterRevision = int.MinValue;
            _gateStateRef = null;

            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
            if (!saveResult.Success)
            {
                Log.Warning($"[FoundryOutpostController] ExpeditionDepartConfirm 自动存档未成功：" +
                    $"{saveResult.Outcome} {saveResult.Message}");
            }

            Log.Info($"[FoundryOutpostController] 已进入铸造前哨外围（resume={resume}），机器 {_machineMarkers.Count} 台。");
        }

        private static void TransportMachinesIn(CampaignState state, IEnumerable<int> logicIds)
        {
            if (logicIds == null)
            {
                return;
            }
            Vector2 spawnBase = FoundryOutpostLayout.EntryEvac.Position;
            int i = 0;
            foreach (int logicId in logicIds)
            {
                if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord record) || !record.IsAlive)
                {
                    continue;
                }
                if (record.RegionId == FoundryOutpostLayout.RegionId)
                {
                    i++;
                    continue;
                }
                CombatSites.ExportMachine(logicId); // FG0-ARCH-03：热量 / 冷却随机器走，不因换地点清零。
                MachineRegistry.MoveToRegion(record, FoundryOutpostLayout.RegionId);
                Vector2 offset = new Vector2((i % 3 - 1) * 2.5f, -1f - (i / 3) * 2.5f);
                record.WorldPosition = spawnBase + offset;
                i++;
            }
        }

        private static void RegisterAllRegionMachineLoadouts(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != FoundryOutpostLayout.RegionId || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                CircuitOpResult result = MachineLoadoutRegistry.Register(state, m.LogicId, m.BlueprintId, m.BlueprintVersion);
                if (!result.Success)
                {
                    Log.Warning($"[FoundryOutpostController] 机器 {m.LogicId} 装配登记失败：{result.Message}");
                }
            }
        }

        // ── FG0-ARCH-01：世界地点（IWorldSite）──────────────────────────────────

        public string SiteId => FoundryOutpostLayout.RegionId;
        public WorldSurfaceKind SurfaceKind => WorldSurfaceKind.LegacyRegion;
        public WorldCameraProfile CameraProfile => _cameraProfile;
        public Vector2? LivePosition(int logicId) => FindMachineMarkerPosition(logicId);

        public int LiveMachineCount => _combat != null ? _combat.CountAliveMachines() : 0;

        /// <summary>“飞到远征地点”的落点：存活机器的中心（内核位置）；全灭时取镜头起始点。</summary>
        public Vector2 DefaultFocus =>
            _combat != null && _combat.TryMachineCentroid(out Vector2 c)
                ? c
                : FoundryOutpostLayout.CameraFocusStart;

        /// <summary>FG0-ARCH-01：镜头观察 / 离开本地点。离开时释放接入、收起撤离确认；表现对象隐藏不销毁，模拟照常推进。</summary>
        public void SetObserved(bool observed)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (!observed)
            {
                if (_possessed != null)
                {
                    Control.ReleaseToStrategy();
                }
                IsEvacPanelOpen = false;
                // FG0-ARCH-03（DEBT-FG0ARCH01-03）：不被观察时不保留任何表现对象，模拟照常在内核里跑。
                DestroyVisuals();
                SquadCommands.ReleaseVisuals();
                _combat?.ReleaseRender();
                return;
            }
            if (_root == null && state != null)
            {
                BuildVisuals(state);
            }
            if (state != null)
            {
                SyncWorldVisuals(state);
            }
        }

        /// <summary>FG0-ARCH-01：镜头在这里时每帧——玩家输入（选择、下令、接入、交互）与画面对账。</summary>
        public void FrameUpdate(float realDt, float frameScaledDt)
        {
            if (!IsLoaded)
            {
                return;
            }
            bool paused = GameClock.Paused;
            // ER5-CTL-01：经 Control.ReleaseToStrategy 统一处理（取消 Move/Attack 残留命令、发布 RegionControlledUnitChangedSignal）。
            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                Control.ReleaseToStrategy();
            }
            // ER5-CMD-01：战略暂停下仍要能选人/排队命令。
            SquadCommands.TickInput(paused);
            HandleSelectionClick();
            HandleDirectControl(frameScaledDt);
            Interact.Tick(frameScaledDt); // ER5-INT-01：候选/进度推进——暂停时为 0，进度天然冻结。

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            if (!paused)
            {
                if (_possessed != null)
                {
                    // ER6-EXPOSE-01："一次远征中每累计30秒直控+5"——只在真正接入且未暂停时累计（接入是玩家本帧的操作，世界锁 1x）。
                    RegionRecord region = FoundryOutpostRegion.Find(state);
                    if (region != null)
                    {
                        CampaignExposureLedger.TickDirectControlExposure(state, region, frameScaledDt);
                    }
                }
                // ER5-CTL-01：干扰宽限与受控机死亡回弹（在本帧全部模拟步之后，敌人本帧打死受控机能在同一帧被侦测到）。
                Control.Tick(frameScaledDt);
            }
            // FG0-ARCH-03：表现对象按内核位置插值（一次 Burst 作业）+ 实例化绘制（常数次调用，与单位数无关）。
            _combat?.FrameRender(_camera, GameClock.StepAlpha);
            SyncWorldVisuals(state);
        }

        /// <summary>FG0-ARCH-01：一个固定模拟步。由 <see cref="WorldSimulation"/> 调用——**无论镜头在不在这里都执行**
        /// （玩家看着家园时，远征队照样在打仗、敌人照样在巡逻；FGR-BASE-021）。本方法不得读取观察状态。</summary>
        public void SimStep(float dt)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            SquadCommands.TickSim(dt);
            RecomputeCoreGateIfInputsChanged(state);
            TickCoreZoneEntry(state);
            FoundryOutpostCoreBoss.Tick(state, dt); // 首领阶段状态机（O(1)）；主核心开火在内核里。
            SyncEnemyRosterIfChanged(state);          // 首领初始化 / 召唤替换了记录数组时才对账。
            CombatDemoContent.SyncBossFlags(_combat, state); // 阶段 → 内核标志（O(3)）。
            FoundryOutpostRegion.TickEnemies(state, dt); // 警戒值衰减（O(1)）。
            // FG0-ARCH-03：机器移动与编队命令、重炮散热、护甲机 / 步进炮 / 维修机 / 干扰支援 / 主核心的 AI 与攻击、兴趣点发现——全部在战斗内核的一步里（Burst）。
            _combat?.Step(dt, GameClock.GameSeconds);
            TickWipeDetection(state);
        }

        /// <summary>ER7-CORE-01："进入前存安全档"——首次有任意区域机器越过核心分区封锁线（门已解锁，
        /// Boss 尚未初始化）时触发一次 <see cref="SaveReason.BossEngageEnter"/> 自动档，再
        /// <see cref="FoundryOutpostCoreBoss.EnsureInitialized"/>。不限直控，编队 Move 命令把机器带
        /// 进去同样触发——两条穿越路径（直控 WASD/编队 Move）都要能触发首次进场，不只认直控。</summary>
        /// <summary>FG0-ARCH-03：每步刷新 <see cref="RegionRecord.CoreGateUnlocked"/>（Demo 每帧整套重算）——输入没变时 O(1) 跳过，
        /// 结果与每步重算相同（自检“核心门三灯按输入变化重算”对照）。进场时仍整套重算一次。</summary>
        private void RecomputeCoreGateIfInputsChanged(CampaignState state)
        {
            if (_gateRosterRevision == MachineRegistry.RosterRevision && ReferenceEquals(_gateStateRef, state)
                && ReferenceEquals(_gateUnlockRef, state.UnlockedContentIds) && ReferenceEquals(_gateLedgerRef, state.EventLedger)
                && ReferenceEquals(_gateBlueprintsRef, state.BlueprintRecords) && ReferenceEquals(_gateRegionsRef, state.RegionRecords))
            {
                return;
            }
            _gateRosterRevision = MachineRegistry.RosterRevision;
            _gateStateRef = state;
            _gateUnlockRef = state.UnlockedContentIds;
            _gateLedgerRef = state.EventLedger;
            _gateBlueprintsRef = state.BlueprintRecords;
            _gateRegionsRef = state.RegionRecords;
            CoreGateRecomputeCount++;
            FoundryOutpostRegion.RecomputeCoreGate(state);
        }

        private void TickCoreZoneEntry(CampaignState state)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !region.CoreGateUnlocked || FoundryOutpostCoreBoss.IsInitialized(region))
            {
                return;
            }
            bool anyCrossed = _combat != null && _combat.AnyMachineBeyondY(FoundryOutpostLayout.CoreGateBlockLineY);
            if (!anyCrossed)
            {
                return;
            }
            if (_bossEngagePending)
            {
                return;
            }
            // FG0-ARCH-01 修复（ERD-SAV-002“自动档不得在事务半提交中写入”）：本方法在模拟步中途执行（家园已推进本步、
            // 队伍与时钟还没推进）。存档 + Boss 初始化一起排到本步整步提交之后，存档里的时钟与各地点状态落在同一步；
            // Boss 从下一步开始计时（晚 1 个固定步），读档后越线检测会同样再触发一次，行为一致。
            _bossEngagePending = true;
            WorldSim.WorldSimulation.RunAfterStep(() =>
            {
                _bossEngagePending = false;
                CampaignState current = CampaignSession.Current;
                RegionRecord currentRegion = current != null ? FoundryOutpostRegion.Find(current) : null;
                if (!IsLoaded || currentRegion == null || FoundryOutpostCoreBoss.IsInitialized(currentRegion))
                {
                    return;
                }
                SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.BossEngageEnter);
                if (!saveResult.Success)
                {
                    Log.Warning($"[FoundryOutpostController] BossEngageEnter 自动存档未成功（{saveResult.Outcome} " +
                        $"{saveResult.Message}），仍继续初始化 Boss 战（不因存档失败卡住玩家，下次自然存档点会补上）。");
                }
                FoundryOutpostCoreBoss.EnsureInitialized(current, currentRegion);
            });
        }

        private bool _bossEngagePending;

        private void TickWipeDetection(CampaignState state)
        {
            if (_wipeResolved || _machineMarkers.Count == 0)
            {
                return;
            }
            // 内核存活计数（AOT）+ 记录侧存活（只在机器名册版本号变化后重扫一次，平时 O(1)）。
            bool kernelAlive = _combat != null && _combat.CountAliveMachines() > 0;
            if (kernelAlive && _wipeRosterRevision != MachineRegistry.RosterRevision)
            {
                _wipeRosterRevision = MachineRegistry.RosterRevision;
                _wipeRecordAlive = MachineRegistry.AllRecords.Any(m => m != null && m.RegionId == FoundryOutpostLayout.RegionId && m.IsAlive);
            }
            bool anyAlive = kernelAlive && _wipeRecordAlive;
            if (anyAlive)
            {
                return;
            }
            FoundryOutpostRegion.ResolveExtraction(state, Array.Empty<int>());
            _wipeResolved = true;
            Log.Info("[FoundryOutpostController] 全灭：区域内所有机器阵亡，已结算携带中的关键物为 Lost。");
            // ER8-CONTENT-01 AC-AUD-001 失败：全灭结算的唯一一次性边沿（_wipeResolved）。
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionWiped, "携带中的关键物已遗失");
        }

        /// <summary>撤离结算数据层——同 <see cref="FracturedCityController.Exit"/> 在 ER5-RETURN-01 补 UI
        /// 之前的裸实现（ER6-REGION-01 将在此之上加撤离确认面板/封锁门三灯）。</summary>
        public void Exit(bool evacuateSuccess)
        {
            if (!IsLoaded)
            {
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
                // FG0-ARCH-03：内核随后释放——积压的玩法事件（大量同时阵亡时跨步排队的阵亡、掉落、反馈）先全部结算，不随队列丢掉。
                _combat?.FlushPendingEvents();
                SyncLiveStateBackToRecords();
                if (evacuateSuccess && !_wipeResolved)
                {
                    List<int> survivors = MachineRegistry.AllRecords
                        .Where(m => m != null && m.RegionId == FoundryOutpostLayout.RegionId && m.IsAlive)
                        .Select(m => m.LogicId).ToList();
                    FoundryOutpostRegion.ResolveExtraction(state, survivors);
                    foreach (int logicId in survivors)
                    {
                        if (MachineRegistry.TryGetRecord(logicId, out MachineRecord record))
                        {
                            MachineRegistry.MoveToRegion(record, HomeValleyLayout.RegionId);
                            record.WorldPosition = HomeValleyLayout.Core.Position + new Vector2(2f, -2f);
                        }
                    }
                }
            }

            if (state != null && evacuateSuccess && state.CurrentRegionId == FoundryOutpostLayout.RegionId)
            {
                // FG0-ARCH-01：远征结束，世界的“当前远征地点”回到家园（读档恢复按它决定观察哪里）。
                state.CurrentRegionId = HomeValleyLayout.RegionId;
            }
            DestroyVisuals();
            SquadCommands.ReleaseVisuals();
            // FG0-ARCH-03：远征结束（或整个世界卸载），本地点的战斗内核释放；存档里的快照随之删除（记录已写回）。
            CombatSites.Close(SiteId, state, dropRecord: true);
            _combat = null;
            _lastRoster = null;
            _machineMarkers.Clear();
            // FG0-ARCH-01：镜头归全局镜头管理器；观察中的地点被卸载时它会自动回到家园（WorldView.FrameEnd）。
            _possessed = null;
            _selected = null;
            SquadCommands.Unbind();
            Control.Unbind();
            Interact.Unbind();
            IsLoaded = false;
            Log.Info($"[FoundryOutpostController] 已退出铸造前哨外围（evacuateSuccess={evacuateSuccess}）。");
        }

        /// <summary>FG0-UX-01：暂停菜单“保存并返回主菜单”存档前调用——只写回实时状态，不卸载区域。</summary>
        private Vector2? FindMachineMarkerPosition(int logicId) =>
            _combat != null && _combat.TryGetMachinePosition(logicId, out Vector2 p) ? p : (Vector2?)null;

        public void SyncLiveStateForSave()
        {
            if (IsLoaded)
            {
                SyncLiveStateBackToRecords();
            }
        }

        /// <summary>内核实时状态写回记录（机器位置 / 热量 / 冷却、敌人位置 / 血量 / 计时、标记）。内核快照本身由 CombatSites.WriteTo 写。</summary>
        private void SyncLiveStateBackToRecords()
        {
            CampaignState state = CampaignSession.Current;
            if (_combat == null || _combat.IsDisposed || state == null)
            {
                return;
            }
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker != null)
                {
                    _combat.ExportMachine(marker.LogicId, includePosition: true);
                }
            }
            _combat.ExportEnemies(state);
            _combat.ExportMarks(FoundryOutpostRegion.Find(state), GameClock.GameSeconds);
        }

        // ── 交互挂载点 ────────────────────────────────────────────────────

        private bool TryFindNearbyMachine(Vector2 anchorPosition, out int logicId)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null)
                {
                    continue;
                }
                if (Vector2.Distance(marker.Position, anchorPosition) <= InteractRange)
                {
                    logicId = marker.LogicId;
                    return true;
                }
            }
            logicId = 0;
            return false;
        }

        public FoundryOutpostRegion.ActionResult TryInteractCrate(string crateId)
        {
            Vector2 position = crateId switch
            {
                FoundryOutpostLayout.Crate1Id => FoundryOutpostLayout.Crate1.Position,
                FoundryOutpostLayout.Crate2Id => FoundryOutpostLayout.Crate2.Position,
                FoundryOutpostLayout.Crate3Id => FoundryOutpostLayout.Crate3.Position,
                _ => Vector2.zero,
            };
            if (!TryFindNearbyMachine(position, out _))
            {
                return FoundryOutpostRegion.ActionResult.Fail("没有机器在箱子交互范围内。");
            }
            return FoundryOutpostRegion.TryOpenCrate(CampaignSession.Current, crateId);
        }

        public FoundryOutpostRegion.ActionResult TryInteractTechCache(string cacheId)
        {
            Vector2 position = cacheId switch
            {
                FoundryOutpostLayout.ArmorCacheId => FoundryOutpostLayout.ArmorCache.Position,
                FoundryOutpostLayout.HeatSinkCacheId => FoundryOutpostLayout.HeatSinkCache.Position,
                FoundryOutpostLayout.ArmorPierceCacheId => FoundryOutpostLayout.ArmorPierceCache.Position,
                _ => Vector2.zero,
            };
            if (!TryFindNearbyMachine(position, out _))
            {
                return FoundryOutpostRegion.ActionResult.Fail("没有机器在技术缓存交互范围内。");
            }
            return FoundryOutpostRegion.TryOpenTechCache(CampaignSession.Current, cacheId);
        }

        public FoundryOutpostRegion.ActionResult TryCollectNearestQuestItem(string salvageInstanceId)
        {
            CampaignState state = CampaignSession.Current;
            RegionQuestItemRecord item = FoundryOutpostRegion.FindQuestItem(state, salvageInstanceId);
            if (item == null || item.State != RegionQuestItemState.OnGround)
            {
                return FoundryOutpostRegion.ActionResult.Fail("地面上没有该物品。");
            }
            if (!TryFindNearbyMachine(item.Position, out int logicId))
            {
                return FoundryOutpostRegion.ActionResult.Fail("没有机器在拾取范围内。");
            }
            return FoundryOutpostRegion.TryCollectQuestItem(state, salvageInstanceId, logicId);
        }

        // ── 选中/直控 ─────────────────────────────────────────────────────

        public bool TrySelectMachine(int logicId)
        {
            HomeValleyMachineMarker marker = FindMarker(logicId);
            if (marker == null)
            {
                return false;
            }
            _selected?.SetSelected(false);
            _selected = marker;
            _selected.SetSelected(true);
            return true;
        }

        public bool IsMachineDirectControlled(int logicId) => _possessed != null && _possessed.LogicId == logicId;
        public int? PossessedMachineLogicId => _possessed != null ? _possessed.LogicId : (int?)null;
        public bool RequestDirectView() => _cameraDirector != null && _cameraDirector.RequestDirect();

        private void HandleSelectionClick()
        {
            if (_camera == null || SquadCommands.ConsumedClickThisFrame || !InputRouter.GetMouseButtonUp(0, InputScope.Strategy))
            {
                return;
            }
            if (!InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer))
            {
                return;
            }
            Ray ray = _camera.ScreenPointToRay(pointer);
            if (!Physics.Raycast(ray, out RaycastHit hit, 500f))
            {
                return;
            }

            HomeValleyMachineMarker marker = hit.collider.GetComponent<MachineView>()?.Marker;
            if (marker != null)
            {
                _selected?.SetSelected(false);
                _selected = marker;
                _selected.SetSelected(true);
                SquadCommands.SelectSingle(marker.LogicId);
            }
        }

        private void HandleDirectControl(float dt)
        {
            if (_possessed == null)
            {
                return;
            }

            if (InputRouter.ConsumeAction(GameActionId.CycleControlTarget, InputScope.Direct))
            {
                RegionControlSwitchResult tabResult = Control.TrySwitchControlledUnit(null);
                if (!tabResult.Success && tabResult.Failure != RegionControlFailure.AlreadyControlled)
                {
                    Log.Info($"[FoundryOutpostController] Tab 切换接管目标失败：{tabResult.PlayerText}");
                }
            }

            float x = 0f, z = 0f;
            if (InputRouter.GetActionKey(GameActionId.MoveLeft, InputScope.Direct)) { x -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveRight, InputScope.Direct)) { x += 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct)) { z -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct)) { z += 1f; }
            if (x != 0f || z != 0f)
            {
                _lastFacing = new Vector2(x, z).normalized;
            }
            // FG0-ARCH-03：直控输入交给内核（模拟步里按机器速度移动并按封锁线钳制；暂停时不走步）。
            _possessed.SetDirectInput(new Vector2(x, z));
            UpdateDirectClamp();

            if (InputRouter.GetMouseButtonDown(0, InputScope.Direct) &&
                InputRouter.TryGetPointer(InputScope.Direct, out Vector3 aimPointer))
            {
                TryDirectAttackEnemy(aimPointer);
            }
        }

        /// <summary>ER6-REGION-01 / ER7-CORE-01：直控的两重物理钳制（门锁定挡“进”：y 上限 = 封锁线；Phase2 区域封锁挡“出”：y 下限 = 封锁线）。
        /// 钳制在内核的直控移动里做（同一步内生效，不产生可感知的“穿模一下”），这里每帧只写两个数（O(1)）。</summary>
        private void UpdateDirectClamp()
        {
            CampaignState state = CampaignSession.Current;
            RegionRecord region = state != null ? FoundryOutpostRegion.Find(state) : null;
            double maxY = region != null && !region.CoreGateUnlocked ? FoundryOutpostLayout.CoreGateBlockLineY : CombatConst.NoClamp;
            double minY = region != null && region.CoreLockoutActive ? FoundryOutpostLayout.CoreLockoutLineY : -CombatConst.NoClamp;
            _combat?.SetDirectClamp(minY, maxY);
        }

        private void TryDirectAttackEnemy(Vector3 screenPointer)
        {
            if (_camera == null || _possessed == null)
            {
                return;
            }

            Ray ray = _camera.ScreenPointToRay(screenPointer);
            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (!groundPlane.Raycast(ray, out float enter))
            {
                return;
            }
            Vector3 worldPoint = ray.GetPoint(enter);

            Vector3 originV3 = _possessed.Position3;
            Vector2 origin = new Vector2(originV3.x, originV3.z);
            Vector2 aimDir = new Vector2(worldPoint.x, worldPoint.z) - origin;

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }

            RegionEnemyRecord target = FoundryOutpostRegion.TryFindEnemyInAim(state, origin, aimDir);
            if (target == null)
            {
                Log.Info("[FoundryOutpostController] 直控攻击：瞄准方向/射程内没有可命中的敌人。");
                return;
            }

            FoundryOutpostRegion.TryAttackEnemy(state, _possessed.LogicId, target.EnemyInstanceId, state.RandomSeed,
                isAiSource: false, attackerPosition: origin);
        }

        // ── 敌人 AI 传感器数据 / 视线判定 ─────────────────────────────────


        private bool EnsureDirectTarget()
        {
            if (_selected == null)
            {
                return false;
            }
            RegionControlSwitchResult result = Control.TrySwitchControlledUnit(_selected.LogicId);
            if (!result.Success)
            {
                Log.Info($"[FoundryOutpostController] 接管请求被拒绝：{result.PlayerText}");
            }
            return result.Success;
        }

        private void OnControlPossessCommitted(int logicId)
        {
            MachineRegistry.RecordControlled(logicId);
            MachineRegistry.TryMarkExperience(logicId, MachineExperienceFlags.Controlled);

            HomeValleyMachineMarker marker = FindMarker(logicId);
            if (marker != null)
            {
                _selected?.SetSelected(false);
                _selected = marker;
                _selected.SetSelected(true);
            }
        }

        private void SetupControlSystem()
        {
            Control.Bind(new RegionControlContext
            {
                RegionId = FoundryOutpostLayout.RegionId,
                Markers = _machineMarkers,
                GetPossessed = () => _possessed,
                SetPossessed = SetPossessedMarker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = null, // 铸造前哨外围没有干扰机制（干扰是破碎都市专属）。
                SquadCommands = SquadCommands,
                OnPossessCommitted = OnControlPossessCommitted,
                OnReleased = null,
                FallbackAnchor = () => FoundryOutpostLayout.EntryEvac.Position,
                JamGraceSeconds = 2f,
            });
        }

        /// <summary>接入状态写进内核：受控机不执行编队命令，位移来自直控输入（释放时清零输入）。</summary>
        private void SetPossessedMarker(HomeValleyMachineMarker marker)
        {
            if (_possessed != null && _possessed != marker)
            {
                _combat?.SetPossessed(_possessed.LogicId, false);
            }
            _possessed = marker;
            if (marker != null)
            {
                _combat?.SetPossessed(marker.LogicId, true);
            }
        }

        private bool TryGetPossessedAnchor(out float2 anchor)
        {
            if (_possessed != null)
            {
                // 镜头跟随插值后的画面位置（有表现对象时），60 Hz 模拟在高帧率下镜头也不一顿一顿；没有表现对象时读内核位置。
                Vector3 v = _possessed.View != null ? _possessed.View.transform.position : _possessed.Position3;
                anchor = new float2(v.x, v.z);
                return true;
            }
            anchor = float2.zero;
            return false;
        }

        private void SetupInteraction()
        {
            Interact.Bind(new RegionInteractContext
            {
                GetPossessed = () => _possessed,
                GateCheck = () => InputRouter.ModalUiOpen ? RegionInteractFailure.ModalBlocked : RegionInteractFailure.None,
                BuildCandidates = BuildInteractCandidates,
                GetFacing = () => _lastFacing,
                Obstacles = _obstacles,
            });
        }

        private List<RegionInteractCandidate> BuildInteractCandidates()
        {
            var list = new List<RegionInteractCandidate>(8);
            CampaignState state = CampaignSession.Current;
            if (state == null || _possessed == null)
            {
                return list;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);

            AddCrateCandidate(list, region, FoundryOutpostLayout.Crate1Id, FoundryOutpostLayout.Crate1.Position);
            AddCrateCandidate(list, region, FoundryOutpostLayout.Crate2Id, FoundryOutpostLayout.Crate2.Position);
            AddCrateCandidate(list, region, FoundryOutpostLayout.Crate3Id, FoundryOutpostLayout.Crate3.Position);
            AddTechCacheCandidate(list, region, FoundryOutpostLayout.ArmorCacheId, FoundryOutpostLayout.ArmorCache.Position, "重甲");
            AddTechCacheCandidate(list, region, FoundryOutpostLayout.HeatSinkCacheId, FoundryOutpostLayout.HeatSinkCache.Position, "散热鳍");
            AddTechCacheCandidate(list, region, FoundryOutpostLayout.ArmorPierceCacheId, FoundryOutpostLayout.ArmorPierceCache.Position, "装甲击穿");

            if (state.RegionQuestItems != null)
            {
                foreach (RegionQuestItemRecord item in state.RegionQuestItems)
                {
                    if (item.RegionId != FoundryOutpostLayout.RegionId || item.State != RegionQuestItemState.OnGround)
                    {
                        continue;
                    }
                    string salvageInstanceId = item.SalvageInstanceId;
                    bool isKeyItem = item.ContentId == FoundryOutpostLayout.CannonModuleContentId;
                    list.Add(new RegionInteractCandidate
                    {
                        Id = "quest:" + salvageInstanceId,
                        Category = RegionInteractCategory.LootLoad,
                        Position = item.Position,
                        HoldSeconds = 0.5f,
                        Priority = isKeyItem ? 25 : 18,
                        ActionVerb = "装载",
                        Validate = () =>
                        {
                            RegionQuestItemRecord q = FoundryOutpostRegion.FindQuestItem(CampaignSession.Current, salvageInstanceId);
                            return q == null || q.State != RegionQuestItemState.OnGround
                                ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "地面上没有该物品。")
                                : RegionInteractResult.Ok();
                        },
                        Complete = () => ToInteractResult(TryCollectNearestQuestItem(salvageInstanceId), "物品已装入货舱。"),
                    });
                }
            }

            if (state.GroundItems != null)
            {
                foreach (GroundItemRecord item in state.GroundItems)
                {
                    if (item.RegionId != FoundryOutpostLayout.RegionId)
                    {
                        continue;
                    }
                    string groundItemId = item.GroundItemId;
                    Vector2 pos = item.Position;
                    list.Add(new RegionInteractCandidate
                    {
                        Id = "loot:" + groundItemId,
                        Category = RegionInteractCategory.LootLoad,
                        Position = pos,
                        HoldSeconds = 0.5f,
                        Priority = 10,
                        ActionVerb = "装载",
                        Validate = () =>
                        {
                            GroundItemRecord g = HomeValleyCargo.FindGroundItem(CampaignSession.Current, groundItemId);
                            return g == null
                                ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "地面物已不存在。")
                                : RegionInteractResult.Ok();
                        },
                        Complete = () => CompleteLootLoad(groundItemId),
                    });
                }
            }

            // ER6-REGION-01：撤离确认——按住 E 只打开确认面板，真正的回城结算只在玩家在面板内点击
            // "确认撤离"后由 ExpeditionReturnService 执行，同 FracturedCityController 同一先例
            // （ER5-RETURN-01 类注释）。外围撤离点一直可用（DEMO-CONTENT-LOCK.md §4.2第3条）。
            if (!IsEvacPanelOpen)
            {
                list.Add(new RegionInteractCandidate
                {
                    Id = "evac:" + FoundryOutpostLayout.EntryEvacId,
                    Category = RegionInteractCategory.EvacConfirm,
                    Position = FoundryOutpostLayout.EntryEvac.Position,
                    HoldSeconds = 1f,
                    Priority = -10,
                    ActionVerb = "查看撤离清单",
                    Validate = () => RegionInteractResult.Ok(),
                    Complete = () =>
                    {
                        SetEvacPanelOpen(true);
                        return RegionInteractResult.Ok("撤离清单已打开。");
                    },
                });
            }

            // ER6-REGION-01：封锁门状态查看——"交互拒绝"这一重控制。锁定时给出缺项原因；解锁后仅
            // 确认状态（核心分区战斗内容属于 ER7-CORE-01，本 Story 不切场，见
            // FoundryOutpostRegion.CanEnterCoreZone 类注释）。
            list.Add(new RegionInteractCandidate
            {
                Id = "coregate:" + FoundryOutpostLayout.CoreGateId,
                Category = RegionInteractCategory.GateCheck,
                Position = FoundryOutpostLayout.CoreGate.Position,
                HoldSeconds = 0.5f,
                Priority = 5,
                ActionVerb = "查看",
                Validate = () =>
                {
                    FoundryOutpostRegion.ActionResult gate = FoundryOutpostRegion.CanEnterCoreZone(CampaignSession.Current);
                    return gate.Success
                        ? RegionInteractResult.Ok()
                        : RegionInteractResult.Fail(RegionInteractFailure.GateLocked, gate.FailureReason);
                },
                Complete = () =>
                {
                    FoundryOutpostRegion.ActionResult gate = FoundryOutpostRegion.CanEnterCoreZone(CampaignSession.Current);
                    return gate.Success
                        ? RegionInteractResult.Ok("核心分区封锁门已开启，正式核心战由后续内容衔接。")
                        : RegionInteractResult.Fail(RegionInteractFailure.GateLocked, gate.FailureReason);
                },
            });

            return list;
        }

        private void AddCrateCandidate(List<RegionInteractCandidate> list, RegionRecord region, string crateId, Vector2 position)
        {
            bool looted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(crateId);
            if (looted)
            {
                return;
            }
            list.Add(new RegionInteractCandidate
            {
                Id = "crate:" + crateId,
                Category = RegionInteractCategory.LootLoad,
                Position = position,
                HoldSeconds = 1f,
                Priority = 12,
                ActionVerb = "打开",
                Validate = () =>
                {
                    RegionRecord r = FoundryOutpostRegion.Find(CampaignSession.Current);
                    return r?.LootedContainerIds != null && r.LootedContainerIds.Contains(crateId)
                        ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "该箱子已打开过。")
                        : RegionInteractResult.Ok();
                },
                Complete = () => ToInteractResult(TryInteractCrate(crateId),
                    $"箱子已打开：{FoundryOutpostLayout.CrateScrapAmount} 废料已落地。"),
            });
        }

        private void AddTechCacheCandidate(List<RegionInteractCandidate> list, RegionRecord region, string cacheId, Vector2 position, string displayName)
        {
            bool looted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(cacheId);
            if (looted)
            {
                return;
            }
            list.Add(new RegionInteractCandidate
            {
                Id = "cache:" + cacheId,
                Category = RegionInteractCategory.WreckageSalvage,
                Position = position,
                HoldSeconds = 1.5f,
                Priority = 14,
                ActionVerb = "领取",
                Validate = () =>
                {
                    RegionRecord r = FoundryOutpostRegion.Find(CampaignSession.Current);
                    return r?.LootedContainerIds != null && r.LootedContainerIds.Contains(cacheId)
                        ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "该技术缓存已领取过。")
                        : RegionInteractResult.Ok();
                },
                Complete = () => ToInteractResult(TryInteractTechCache(cacheId), $"{displayName}技术缓存已领取。"),
            });
        }

        private RegionInteractResult CompleteLootLoad(string groundItemId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.NoControlledUnit, "没有活动的铸造前哨会话。");
            }
            HomeValleyCargo.HaulTicket ticket = HomeValleyCargo.TryReserveHaul(state, groundItemId);
            if (ticket == null)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "地面物已不存在。");
            }
            HomeValleyCargo.StoreResult store = HomeValleyCargo.CommitHaul(state, ticket);
            if (!store.Success)
            {
                RegionInteractFailure failure = store.FailureReason != null && store.FailureReason.StartsWith("storage-full")
                    ? RegionInteractFailure.CargoFull
                    : RegionInteractFailure.TargetGone;
                return RegionInteractResult.Fail(failure, "装载失败：" + store.FailureReason);
            }
            return RegionInteractResult.Ok("装载完成。");
        }

        private static RegionInteractResult ToInteractResult(FoundryOutpostRegion.ActionResult result, string successText)
        {
            return result.Success
                ? RegionInteractResult.Ok(successText)
                : RegionInteractResult.Fail(RegionInteractFailure.TargetGone, result.FailureReason);
        }

        private HomeValleyMachineMarker FindMarker(int logicId)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker.LogicId == logicId)
                {
                    return marker;
                }
            }
            return null;
        }

        // ── 相机 ──────────────────────────────────────────────────────────

        /// <summary>FG0-ARCH-01：本地点的镜头配置（与 Demo 逐值一致）；镜头本身由 <see cref="WorldView"/> 持有。
        /// 本地点仍是 Demo 的手工地图（独立表面、局部坐标），平移范围沿用方形边界（DEBT-FG0ARCH01-01）。</summary>
        private void SetupCameraProfile()
        {
            _camera = WorldView.EnsureCamera();
            _cameraProfile = new WorldCameraProfile
            {
                DirectAnchor = TryGetPossessedAnchor,
                EnsureDirectTarget = EnsureDirectTarget,
                ArenaHalfExtent = FoundryOutpostLayout.CameraBoundsHalfExtentX,
                FollowOffset = new Vector3(0f, 40f, 0f),
                InitialStrategyOrthographicSize = FoundryOutpostLayout.CameraBoundsHalfExtentZ,
                InitialDirectOrthographicSize = 14f,
                StartFocus = FoundryOutpostLayout.CameraFocusStart,
                Background = new Color(0.09f, 0.08f, 0.06f),
            };
        }

        // ── 战略命令 ──────────────────────────────────────────────────────

        private void SetupSquadCommands()
        {
            _squadCtx = new RegionSquadCommandContext
            {
                Camera = _camera,
                Markers = _machineMarkers,
                VisualRoot = _root,
                IsDirectControlled = IsMachineDirectControlled,
                IsEligible = logicId => MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) &&
                    rec.IsAlive && rec.RegionId == FoundryOutpostLayout.RegionId,
                CancelWorkIfAny = null,
                SafePoint = FoundryOutpostLayout.EntryEvac.Position,
                Obstacles = _obstacles,
                FindHostileNear = FindEnemyHostileNear,
                ResolveHostile = ResolveEnemyHostile,
                Site = _combat,
                HostileUnit = id => _combat != null && _combat.TryGetEnemyUnit(id, out int u) ? u : 0,
                AttackRange = AttackRange,
                AttackCooldownSeconds = 1.2f,
                ClampDestination = ClampAgainstCoreGate,
            };
            SquadCommands.Bind(_squadCtx);
        }

        /// <summary>ER6-REGION-01："导航阻挡"这一重控制——编队 Move 命令的目的地不能越过核心分区
        /// 封锁线（门锁定时）。只钳制 Y 分量、保留 X（沿门前排队而不是被强行拉回中轴线），与
        /// <see cref="FoundryOutpostRegion.IsBeyondCoreGateLine"/> 同一判据。
        ///
        /// ── ER7-CORE-01 追加：Phase2 区域封锁（方向相反的第二条钳制）──
        /// 门锁定挡的是"进"（目的地 Y 上限），<see cref="RegionRecord.CoreLockoutActive"/> 挡的是
        /// "出"（目的地 Y 下限，不让编队 Move 命令把机器带出核心分区）——两条判据方向相反、互不冲突，
        /// 同一方法内先后各裁一次即可（不会同时触发，门解锁后才可能进入核心分区触发 Boss 战，此时
        /// CoreGateUnlocked 恒真，第一段判定天然不生效）。</summary>
        private Vector2 ClampAgainstCoreGate(Vector2 target)
        {
            CampaignState state = CampaignSession.Current;
            RegionRecord region = state != null ? FoundryOutpostRegion.Find(state) : null;
            if (region == null)
            {
                return target;
            }
            if (!region.CoreGateUnlocked && FoundryOutpostRegion.IsBeyondCoreGateLine(target))
            {
                return new Vector2(target.x, FoundryOutpostLayout.CoreGateBlockLineY);
            }
            if (region.CoreLockoutActive && target.y < FoundryOutpostLayout.CoreLockoutLineY)
            {
                return new Vector2(target.x, FoundryOutpostLayout.CoreLockoutLineY);
            }
            return target;
        }

        /// <summary>点选敌人：内核里离点击点最近的存活敌人（并列取靠后者，同 Demo）。</summary>
        private RegionHostileInfo? FindEnemyHostileNear(Vector2 worldPoint, float pickRadius)
        {
            if (_combat == null || _combat.IsDisposed)
            {
                return null;
            }
            return _combat.TryFindNearestEnemy(worldPoint, pickRadius, out string id) ? ResolveEnemyHostile(id) : null;
        }

        private RegionHostileInfo? ResolveEnemyHostile(string hostileId)
        {
            if (_combat == null || !_combat.TryGetEnemyPosition(hostileId, out Vector2 pos))
            {
                return null;
            }
            return new RegionHostileInfo(hostileId, pos, _combat.IsEnemyAlive(hostileId));
        }

        // ── 可视化（占位几何体，同 FracturedCityController 手法）───────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[FoundryOutpostRoot]");
            _enemyBadges.Clear();

            BuildAnchorVisual(FoundryOutpostLayout.EntryEvac, PrimitiveType.Cylinder, new Color(0.2f, 0.7f, 0.9f, 0.5f), 0.05f, true);
            BuildAnchorVisual(FoundryOutpostLayout.RecoveryLocker, PrimitiveType.Cube, new Color(0.6f, 0.5f, 0.2f), 0.8f, false);
            BuildAnchorVisual(FoundryOutpostLayout.Crate1, PrimitiveType.Cube, CrateColor(state, FoundryOutpostLayout.Crate1Id), 0.5f, false);
            BuildAnchorVisual(FoundryOutpostLayout.Crate2, PrimitiveType.Cube, CrateColor(state, FoundryOutpostLayout.Crate2Id), 0.5f, false);
            BuildAnchorVisual(FoundryOutpostLayout.Crate3, PrimitiveType.Cube, CrateColor(state, FoundryOutpostLayout.Crate3Id), 0.5f, false);
            BuildAnchorVisual(FoundryOutpostLayout.ArmorCache, PrimitiveType.Cube, CacheColor(state, FoundryOutpostLayout.ArmorCacheId), 0.6f, false);
            BuildAnchorVisual(FoundryOutpostLayout.HeatSinkCache, PrimitiveType.Cube, CacheColor(state, FoundryOutpostLayout.HeatSinkCacheId), 0.6f, false);
            BuildAnchorVisual(FoundryOutpostLayout.ArmorPierceCache, PrimitiveType.Cube, CacheColor(state, FoundryOutpostLayout.ArmorPierceCacheId), 0.6f, false);
            BuildAnchorVisual(FoundryOutpostLayout.CoreGate, PrimitiveType.Cube, GateColor(state), 1.5f, false);
            BuildEnemyVisuals(state);

            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker != null && MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord m) && m.IsAlive)
                {
                    BuildMachineView(marker, m);
                }
            }
            if (_squadCtx != null)
            {
                _squadCtx.VisualRoot = _root;
            }
        }

        private void BuildAnchorVisual(FoundryOutpostLayout.Anchor anchor, PrimitiveType shape, Color color, float height, bool isMarkerRing)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = (isMarkerRing ? "Marker_" : "Poi_") + anchor.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(anchor.Position.x, height, anchor.Position.y);
            go.transform.localScale = isMarkerRing
                ? new Vector3(anchor.ClearanceRadius * 2f, 0.1f, anchor.ClearanceRadius * 2f)
                : new Vector3(1.2f, height * 2f, 1.2f);
            if (isMarkerRing)
            {
                GameLogic.View.UnityObjects.Release(go.GetComponent<Collider>());
            }
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(color);
        }

        private static Color CrateColor(CampaignState state, string crateId)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            bool looted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(crateId);
            return looted ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.85f, 0.75f, 0.35f);
        }

        private static Color CacheColor(CampaignState state, string cacheId)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            bool looted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(cacheId);
            return looted ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.35f, 0.55f, 0.85f);
        }

        /// <summary>ER6-REGION-01：门色随三灯实时变化——红＝锁定，绿＝已开启（不切场，核心分区内容
        /// 留 ER7-CORE-01）。</summary>
        private static Color GateColor(CampaignState state)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            return region != null && region.CoreGateUnlocked
                ? new Color(0.2f, 0.65f, 0.25f)
                : new Color(0.5f, 0.15f, 0.15f);
        }

        private void BuildEnemyVisuals(CampaignState state)
        {
            if (state.RegionEnemies == null)
            {
                return;
            }
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != FoundryOutpostLayout.RegionId || _enemyBadges.ContainsKey(enemy.EnemyInstanceId))
                {
                    continue;
                }
                Vector2 at = _combat != null && _combat.TryGetEnemyPosition(enemy.EnemyInstanceId, out Vector2 live) ? live : enemy.Position;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Enemy_" + enemy.EnemyInstanceId;
                go.transform.SetParent(_root.transform, false);
                go.transform.position = new Vector3(at.x, 1f, at.y);
                if (_combat != null && _combat.TryGetEnemyUnit(enemy.EnemyInstanceId, out int enemyUnit))
                {
                    _combat.BindView(enemyUnit, go.transform, 1f); // 位置每帧由内核插值写入（FrameRender）。
                }
                Renderer renderer = go.GetComponent<Renderer>();
                Color baseColor = EnemyBaseColor(enemy.EnemyTypeId);
                renderer.sharedMaterial = ViewMaterials.Standard(enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f));
                // AC-THEME-002：铸造阵营方正厚重剪影；供能节点/主核心是大块结构（胶囊只留作命中盒）。
                PlaceholderSilhouette.ApplyEnemy(go, renderer, enemy.EnemyTypeId);

                // ER8-CONTENT-01 AC-ACC-002：敌人头顶的类型标记（倒三角＝敌方，角标圆点/方块＝静默/铸造阵营），
                // 敌我不再只靠胶囊体颜色区分。胶囊体等比缩放，标记直接挂在它下面随之移动。
                WorldBadge enemyBadge = WorldBadge.Create(go.transform, "Badge", go.transform.position + Vector3.up * 1.9f, 1.1f);
                enemyBadge.SetIcon(EnemyBadgeIcon(enemy.EnemyTypeId));
                enemyBadge.SetVisible(enemy.IsAlive);
                _enemyBadges[enemy.EnemyInstanceId] = enemyBadge;
            }
        }

        private static Color EnemyBaseColor(string enemyTypeId)
        {
            if (enemyTypeId == EnemyCatalog.ArmorBotId)
            {
                return new Color(0.5f, 0.45f, 0.15f); // 厚甲：暗黄铜。
            }
            if (enemyTypeId == EnemyCatalog.StriderId)
            {
                return new Color(0.7f, 0.25f, 0.1f); // 重炮：橙红。
            }
            return new Color(0.2f, 0.55f, 0.4f); // 维修机：青绿（区别于攻击单位的暖色系）。
        }

        private void BuildMachineView(HomeValleyMachineMarker marker, MachineRecord machine)
        {
            Vector2 at = marker.Position;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId + "_" + machine.LogicId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(at.x, 1f, at.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.75f, 0.8f, 0.9f); // 与另两区域机器配色区分（偏冷灰蓝）。
            renderer.sharedMaterial = ViewMaterials.Standard(baseColor);
            PlaceholderSilhouette.ApplyMachine(go, renderer, machine.ChassisId); // AC-THEME-002 车辆剪影。

            MachineView view = go.AddComponent<MachineView>();
            view.Initialize(renderer, baseColor);
            marker.AttachView(view);
            _combat?.BindView(marker.UnitId, go.transform, 1f);
        }

        /// <summary>FG0-ARCH-03：进场 / 读档时建立本地点的战斗内核：障碍、兴趣点、机器、敌人、标记、首领阶段标志。</summary>
        private void OpenCombat(CampaignState state, bool resume)
        {
            _combatRules = new FoundryOutpostCombatRules { OnEnemyVisualChanged = RefreshEnemyView };
            _combat = CombatSites.Open(SiteId, state, resume, _combatRules, out bool restored);
            _combat.Squad = SquadCommands;
            _combat.SetObstacles(CombatDemoContent.Obstacles(_obstacles));
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (!restored)
            {
                var pois = new List<float3>();
                var reached = new List<bool>();
                foreach (FoundryOutpostLayout.Anchor a in _combatRules.Pois)
                {
                    pois.Add(new float3(a.Position.x, a.Position.y, FoundryOutpostLayout.PoiDiscoveryRadius));
                    reached.Add(region?.DiscoveredNodes != null && region.DiscoveredNodes.Contains(a.Id));
                }
                _combat.SetPois(pois, reached);
            }
            SyncCombatMachines(state);
            CombatDemoContent.ReconcileEnemies(_combat, state, FoundryOutpostLayout.RegionId);
            _lastRoster = state.RegionEnemies;
            if (!restored)
            {
                _combat.ImportMarks(region);
            }
            CombatDemoContent.SyncBossFlags(_combat, state);
            _combat.RefreshAllMachineWeapons(state);
        }

        /// <summary>本区域存活机器 ↔ 内核单位对账（进场、读档）；逻辑句柄按记录顺序排列。</summary>
        private void SyncCombatMachines(CampaignState state)
        {
            _machineMarkers.Clear();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != FoundryOutpostLayout.RegionId)
                {
                    continue;
                }
                HomeValleyMachineMarker marker = _combat.SpawnMachine(state, m, m.WorldPosition, autoEngage: false);
                if (marker != null)
                {
                    _machineMarkers.Add(marker);
                }
            }
            foreach (int stale in _combat.MachinesNotIn(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive && r.RegionId == FoundryOutpostLayout.RegionId))
            {
                _combat.RemoveMachine(stale, export: false);
            }
        }

        /// <summary>首领初始化 / 过渡召唤 / 反制增援会替换敌人记录数组：引用变了才对账（O(1) 判定），被观察时补建新敌人的表现对象。</summary>
        private void SyncEnemyRosterIfChanged(CampaignState state)
        {
            if (_combat == null || ReferenceEquals(state.RegionEnemies, _lastRoster))
            {
                return;
            }
            CombatDemoContent.ReconcileEnemies(_combat, state, FoundryOutpostLayout.RegionId);
            _lastRoster = state.RegionEnemies;
            if (_root != null)
            {
                BuildEnemyVisuals(state);
            }
        }

        /// <summary>敌人阵亡等状态变化时刷新它的表现（事件驱动，不每帧扫全部敌人）。</summary>
        private void RefreshEnemyView(string enemyInstanceId)
        {
            RegionEnemyRecord enemy = FoundryOutpostRegion.FindEnemy(CampaignSession.Current, enemyInstanceId);
            if (_root == null || enemy == null)
            {
                return;
            }
            RefreshColor("Enemy_" + enemy.EnemyInstanceId, enemy.IsAlive ? EnemyBaseColor(enemy.EnemyTypeId) : new Color(0.25f, 0.25f, 0.25f));
            if (_enemyBadges.TryGetValue(enemy.EnemyInstanceId, out WorldBadge badge) && badge != null)
            {
                badge.SetVisible(enemy.IsAlive);
            }
        }

        private void SyncWorldVisuals(CampaignState state)
        {
            if (_root == null)
            {
                return;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);

            RefreshColor("Poi_" + FoundryOutpostLayout.Crate1Id, CrateColor(state, FoundryOutpostLayout.Crate1Id));
            RefreshColor("Poi_" + FoundryOutpostLayout.Crate2Id, CrateColor(state, FoundryOutpostLayout.Crate2Id));
            RefreshColor("Poi_" + FoundryOutpostLayout.Crate3Id, CrateColor(state, FoundryOutpostLayout.Crate3Id));
            RefreshColor("Poi_" + FoundryOutpostLayout.ArmorCacheId, CacheColor(state, FoundryOutpostLayout.ArmorCacheId));
            RefreshColor("Poi_" + FoundryOutpostLayout.HeatSinkCacheId, CacheColor(state, FoundryOutpostLayout.HeatSinkCacheId));
            RefreshColor("Poi_" + FoundryOutpostLayout.ArmorPierceCacheId, CacheColor(state, FoundryOutpostLayout.ArmorPierceCacheId));
            RefreshColor("Poi_" + FoundryOutpostLayout.CoreGateId, GateColor(state));

            // FG0-ARCH-03：敌人位置由内核每帧插值写入（FrameRender）；颜色 / 头顶标记在阵亡事件时刷新（RefreshEnemyView），不每帧扫全部敌人。
        }

        /// <summary>ER8-CONTENT-01：敌人类型 → 头顶标记图标。Boss 节点/主核心不是内容目录条目，统一用主核心图标。</summary>
        private static string EnemyBadgeIcon(string enemyTypeId)
        {
            if (enemyTypeId == FoundryOutpostLayout.BossNodeTypeId || enemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
            {
                return ContentIcons.IconIdFor(EnemyCatalog.CoreBossId);
            }
            return ContentIcons.IconIdFor(enemyTypeId);
        }

        /// <summary>ER8-CONTENT-01：敌人头顶标记，按 EnemyInstanceId 索引。</summary>
        private readonly Dictionary<string, WorldBadge> _enemyBadges = new Dictionary<string, WorldBadge>();

        private void RefreshColor(string childName, Color color)
        {
            Transform t = _root.transform.Find(childName);
            Renderer r = t != null ? t.GetComponent<Renderer>() : null;
            if (r != null)
            {
                ViewMaterials.Recolor(r, color);
            }
        }

        private void DestroyVisuals()
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                marker?.DetachView();
            }
            _combat?.ClearViews();
            _enemyBadges.Clear();
            if (_squadCtx != null)
            {
                _squadCtx.VisualRoot = null;
            }
            if (_root != null)
            {
                GameLogic.View.UnityObjects.Release(_root);
                _root = null;
            }
        }
    }
}
