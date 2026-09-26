using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.UI.Common;
using GameLogic.Settings;
using GameLogic.Campaign;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER2-SCENE-01：归还谷地空间/持久化流程。不是 <see cref="GameLogic.Stage.IStageFlow"/>——
    /// <see cref="GameLogic.Stage.StageId"/> 枚举（Cell/Organ/Creature/...）是产品宏观演化阶段骨架，
    /// 归还谷地是 Cell 时代内的一个"战略基地区域"，与远征战斗（未来复用 CellStageFlow 的 SimBridge
    /// 内核）是并列关系，不应该塞进同一个枚举维度。<see cref="GameLogic.Stage.GameRoot"/> 直接持有
    /// 并驱动本类，和 HUD host 是同一种"director 之外的常驻子系统"处理方式。
    ///
    /// 范围边界（2026-09-21 用户裁决"填完再结"）：建筑修复/残骸拆解的资源事务+电网仲裁+
    /// 点选移动+到点自动干活，均在本类内实现并实测（AC-JRN-002 的"可工作"journey 端到端可玩）。
    /// 勘查确认 <c>CellPlayerController</c>（单一意识附体换人模型，`Bind` 要求 StatSheet/
    /// AbilitySystem/ResourceWallet 这类玩家进化属性）与"多台平等工作机器"语义不符，不能直接复用；
    /// 因此移动走独立的 Transform 插值（<see cref="HomeValleyMachineMarker.CommandMoveTo"/>），
    /// 不接 SimBridge——避免在没把握的情况下往共享战斗内核里加东西。统一 Direct/Strategy/
    /// Transition/Modal 输入域表、WASD 直控换乘、真正的镜头接管仍是 ER2-INPUT-01 的范围，
    /// E 交互按钮/UI 面板仍是 ER5-INT-01/UI-04 的范围（DIGEST 早已登记，不是本 Story 新开的口子）。</summary>
    public sealed class HomeValleyController : IWorldSite
    {
        /// <summary>FG0-ARCH-01：本地点已载入、正在被世界模拟推进（与镜头在不在这里无关）。</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>已载入**并且**镜头正在观察这里（界面面板、输入据此判断“玩家在不在家园”）。
        /// 模拟推进不看它（FGR-BASE-021），见 <see cref="SimStep"/>。</summary>
        public bool IsActive => IsLoaded && WorldView.IsObserved(SiteId);

        private GameObject _root;
        private Camera _camera;
        /// <summary>家园机器的逻辑句柄（战斗内核单位的门面，不是表现对象；FG0-ARCH-03）。</summary>
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(4);
        /// <summary>FG0-ARCH-03：家园（星球表面）的战斗内核：机器移动（工作赶路 / 编队命令 / 直控）、训练靶自动交战，
        /// 以及家园突袭的突袭者与炮塔（FG6 接入；性能场景与测试捷径已可用）。</summary>
        private CombatSite _combat;
        private HomeValleyCombatRules _combatRules;
        private RegionSquadCommandContext _squadCtx;
        public CombatSite Combat => _combat;
        /// <summary>ER8-CONTENT-01：建筑状态悬浮标记，按 BuildingTypeId 索引。</summary>
        private readonly Dictionary<string, WorldBadge> _buildingBadges = new Dictionary<string, WorldBadge>();
        /// <summary>FG0-ARCH-04：本帧仍存在的建筑本地键（对账时移除已拆除建筑的可视化）。复用同一个集合，不每帧分配。</summary>
        private readonly HashSet<string> _liveBuildingKeys = new HashSet<string>(StringComparer.Ordinal);
        // FG0-ARCH-04 复审：建筑占位方块按本地键缓存（替代逐帧 Transform.Find 线性扫子节点，对账从 O(N²) 降到 O(N)）；
        // “已消失的建筑”扫描只在建筑记录数组被替换（新增 / 移除都会换数组）时做，不再每帧读 child.name 分配字符串。
        private readonly Dictionary<string, Transform> _buildingVisuals = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly List<string> _goneBuildingKeys = new List<string>(4);
        private BuildingRecord[] _visualsRecordsRef;
        private float _objectiveRecomputeTimer;
        /// <summary>ER8 收尾（UI-14“世界标记使用同一目标状态”）：当前目标下一步所在位置的定位针，唯一一个。</summary>
        private WorldBadge _objectiveMarker;
        private HomeValleyMachineMarker _selected;

        /// <summary>FG0-ARCH-01：镜头归全局镜头管理器（<see cref="WorldView"/>）所有；本地点被观察时借用它，否则为 null。</summary>
        private CameraDirector _cameraDirector => IsActive && WorldView.Director.IsBound ? WorldView.Director : null;
        private WorldCameraProfile _cameraProfile;

        /// <summary>FG0-UX-01：通知“定位”要让当前区域的镜头飞到事件位置（只读访问，不改所有权）。</summary>
        public CameraDirector CameraDirector => _cameraDirector;
        /// <summary>当前被直控（WASD 亲自开）的机器。null＝没有接管，处于战略选中+下令模式。</summary>
        private HomeValleyMachineMarker _possessed;
        /// <summary>FG0-ARCH-01：暂停属于整个世界（<see cref="GameClock"/>），不再是本区域自己的字段。</summary>
        public bool IsPaused => GameClock.Paused;

        /// <summary>供 HUD 暂停按钮直接调用：暂停 / 继续整个世界。</summary>
        public void SetPaused(bool paused) => GameClock.SetPaused(paused);

        /// <summary>ER5-CMD-01：战略命令正式化——多选/编组/Move·Attack·Guard·Retreat，与
        /// <see cref="FracturedCityController"/> 共用同一套引擎（见该类自己的实例），避免两个区域
        /// 各写一套判定逻辑。UI（<c>RegionCommandBarUIToolkit</c>）与热键都通过它读写状态。</summary>
        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();

        /// <summary>ER5-CTL-01：任意接管正式化——Tab 候选/候选条点击/M 键首次接管统一走
        /// <see cref="RegionControlSystem.TrySwitchControlledUnit"/>，与 <see cref="SquadCommands"/>
        /// 同一委托范式，同一实例贯穿本区域生命周期。</summary>
        public readonly RegionControlSystem Control = new RegionControlSystem();

        /// <summary>ER5-INT-01：Direct 的 E 交互唯一实现——候选排序/校验/按住进度框架，与
        /// <see cref="FracturedCityController.Interact"/> 共用同一引擎、各自一份实例。</summary>
        public readonly RegionInteractionSystem Interact = new RegionInteractionSystem();

        /// <summary>FG0-ARCH-04：家园的建造模式（放置 / 旋转 / 拆除，走正式输入）。家园激活期间经
        /// <see cref="HomeValleyBuildMode.Current"/> 暴露给 HUD 与自检。</summary>
        public readonly HomeValleyBuildMode BuildMode = new HomeValleyBuildMode();

        /// <summary>ER5-INT-01："指向"排序用——WASD 最近一次非零输入方向，供 E 交互候选排序参考
        /// （没有独立瞄准输入，直控移动方向是最自然的"朝向"近似，同 <see cref="TryDirectAttack"/>
        /// 用鼠标瞄准是两个不同的交互——世界物体交互没有理由强制玩家先拿鼠标点一下）。</summary>
        private Vector2 _lastFacing = new Vector2(1f, 0f);

        /// <summary>本类由 <see cref="GameLogic.Stage.GameRoot"/> 用 <c>??=</c> 惰性创建、跨多局
        /// 复用同一实例（同一进程内先后玩过 A、B 两局）。ER3-WRK-01 起 WorkOrder 的进度
        /// （<see cref="WorkOrderRecord.Progress"/>/<see cref="WorkOrderRecord.Duration"/>）已经落在
        /// <see cref="CampaignState"/> 本体里，不再需要本类持有按 BuildingId 做键的纯内存计时字典
        /// （ER2-SCENE-01 曾在此踩过"A 局残留计时污染 B 局同名建筑"的真实 bug，起因正是那类字典）；
        /// <see cref="HomeValleyWorkOrders"/> 内部仅有的瞬态看门狗字典按 WorkOrderId（含 GUID）做键，
        /// 天然不会跨局撞名。<c>_boundCampaignId</c> 仍保留，供将来其它运行时缓存复用同一纪律。</summary>
        private string _boundCampaignId;

        public void Enter(bool resume)
        {
            if (IsLoaded)
            {
                Log.Warning("[HomeValleyController] Enter 被重复调用，忽略（已处于激活状态）。");
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                Log.Error("[HomeValleyController] 没有活动战役（CampaignSession.Current 为空），拒绝进入归还谷地。");
                return;
            }

            _boundCampaignId = state.CampaignId;

            EnsureRegionSeeded(state);
            // ER4-PRIM-02：蓝图播种必须先于机器播种——ERC-001/002 的 MachineRecord.BlueprintId 现在
            // 指向真实 BlueprintRecord（bp_erc001/bp_hauler），不再是占位字符串，见 EnsureMachinesSeeded。
            HomeValleyFactory.EnsureBlueprintsSeeded(state); // ER4-FAC-01：装配站默认三条生产蓝图 + ER4-PRIM-02 电路板数据。
            PrimitiveInventory.EnsureSeeded(state); // ER4-PRIM-03：战役唯一基元仓，开局8格+1件聚焦镜，幂等。
            HomeValleyCombatTargets.EnsureSeeded(state); // ER4-PRIM-05：低威胁残骸靶，幂等。
            HomeValleySignal.EnsureSeeded(state); // ER5-SIG-01：破碎都市 Locked 区域记录，幂等。
            FoundryOutpostRegion.EnsureRegionRecordSeeded(state); // ER6-FOUNDRY-01：铸造前哨外围 Locked 区域记录，幂等。
            EnsureMachinesSeeded(state);
            // ER4-BLP-02 STORY-EXECUTION-CARDS.md 第3条："区域卸载/重新生成……时登记/解绑装配登记表"。
            // MachineLoadoutRegistry 是本次会话内的"哪台机当前装配是什么"缓存（同 MachineRegistry 自身
            // Bind/Unbind 的既有纪律：只清映射，不清 CampaignState 里的长期记录）——EnsureMachinesSeeded
            // 只在"首次生成"那一刻单独登记新机，第二次进入（机器已存在、SpawnIfMissing 早退）不会重新
            // 登记，必须在这里对当前区域全部存活机器统一补一遍，否则读档/重进就会看到空注册表。
            RegisterAllRegionMachineLoadouts(state);
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            HomeValleyPowerGrid.Recompute(state); // 幂等：新建战役刚播种、或读档恢复旧存档，都用当前数据重算一次。
            HomeGridService.MapFor(state); // FG0-ARCH-04：旧档迁移到格网 + 占用层重建（幂等）。

            // FG0-ARCH-03：机器进家园的战斗内核（读档时从快照恢复，含编队命令；新战役按记录建）。表现对象只在被观察时建（SetObserved）。
            OpenCombat(state, resume);
            SetupCameraProfile();
            SetupSquadCommands();
            SetupControlSystem();
            SetupInteraction();

            IsLoaded = true;
            HomeValleyBuildMode.Bind(BuildMode);
            // FG0-UX-01（FGR-UX-020 定位）：机器实时位置由 WorldSimulation.LivePosition 统一向各地点查询。

            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            if (!saveResult.Success)
            {
                Log.Warning($"[HomeValleyController] HomeEntryComplete 自动存档未成功：" +
                    $"{saveResult.Outcome} {saveResult.Message}");
            }

            Log.Info($"[HomeValleyController] 已进入归还谷地（resume={resume}），" +
                $"建筑 {CountRegionBuildings(state)} 项，机器 {_machineMarkers.Count} 台。");
        }

        // ── FG0-ARCH-01：世界地点（IWorldSite）──────────────────────────────────

        public string SiteId => HomeValleyLayout.RegionId;
        public WorldSurfaceKind SurfaceKind => WorldSurfaceKind.Planet;
        public bool IsWiped => false;
        public int LiveMachineCount => _combat != null ? _combat.CountAliveMachines() : 0;
        public WorldCameraProfile CameraProfile => _cameraProfile;
        public Vector2 DefaultFocus => HomeValleyLayout.Core.Position;
        public Vector2? LivePosition(int logicId) => FindMachineMarkerPosition(logicId);

        /// <summary>FG0-ARCH-01：镜头观察 / 离开家园。离开时释放接入、收起建造模式与家园面板（这些属于“玩家在看家园”的界面状态），
        /// 表现对象隐藏但不销毁——模拟照常推进（Demo 的机器位置仍记在表现对象的 Transform 上）。回来时画面对账一次。</summary>
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
                BuildMode.Close();
                _factoryPanelOpen = false;
                _expeditionPrepPanelOpen = false;
                _beaconLaunchPanelOpen = false;
                _circuitBoardPanelOpen = false;
                _craftStationPanelOpen = false;
                _analysisPanelOpen = false;
                SquadCommands.PointerSuppressed = false;
                // FG0-ARCH-03（DEBT-FG0ARCH01-03）：不被观察时不保留表现对象（建筑方块、机器、标记都销毁），模拟照常在内核与记录里跑。
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
                RefreshObjectiveMarker(state);
            }
        }

        /// <summary>FG0-ARCH-01：镜头在家园时每帧——玩家输入（建造、选择、下令、接入）与画面对账。不推进任何模拟计时
        /// （接入移动与交互进度是玩家本帧的实时输入，按统一时钟缩放过的本帧时间走，暂停时为 0）。</summary>
        public void FrameUpdate(float realDt, float frameScaledDt)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool paused = GameClock.Paused;

            // 目标定位针（纯表现）每 0.5 秒真实时间对账一次；目标本身由世界模拟按游戏时间重算。
            _objectiveRecomputeTimer -= realDt;
            if (_objectiveRecomputeTimer <= 0f)
            {
                _objectiveRecomputeTimer = 0.5f;
                RefreshObjectiveMarker(state);
            }

            if (state != null && HomeValleySoftlockGuard.IsCoreDestroyed(state))
            {
                BuildMode.Close(); // 失败页期间不能规划。
                // ER3-SOFTLOCK-01 AC-ECO-011"核心被毁只能失败界面"：不再接受选中/命令/移动（模拟侧见 SimStep 的同一判定）。
                return;
            }

            // 落回战略视角才清空接管——过渡途中不清，否则接管请求白做（ER5-CTL-01 经 Control.ReleaseToStrategy 统一处理）。
            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                Control.ReleaseToStrategy();
            }

            // ER5-CMD-01 / FG0-ARCH-04：建造模式先拿输入（B / X / R、放置与拆除的鼠标）；开着时框选与点选下令让位。战略暂停下也能规划。
            // 本帧开始时开着、或本帧刚打开的建造模式都拥有本帧的鼠标（右键退出建造模式的那一下不能再被当成“取消工单”）。
            bool buildModeWasOpen = BuildMode.IsOpen;
            BuildMode.Tick(_camera, state, _cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy);
            bool buildModeOwnsPointer = buildModeWasOpen || BuildMode.IsOpen;
            SquadCommands.PointerSuppressed = buildModeOwnsPointer;
            SquadCommands.TickInput(paused);
            if (!buildModeOwnsPointer)
            {
                HandleSelectionClick();
            }

            HandleDirectControl(frameScaledDt);
            Interact.Tick(frameScaledDt); // ER5-INT-01：候选/进度推进——暂停时为 0，进度天然冻结。
            if (!paused && state != null)
            {
                TickDirectSalvageRangeGuard(state); // ER5-INT-01：直控拆解离开3米即取消并恢复原状。
                Control.Tick(frameScaledDt); // ER5-CTL-01：受控机死亡回弹侦测。
            }
            if (state != null)
            {
                // 画面对账（纯表现：建筑、残骸、地面物、靶子）；暂停中规划的建筑也要立即显示。
                SyncWorldVisuals(state);
            }
            // FG0-ARCH-03：机器表现对象按内核位置插值 + 突袭者 / 炮塔 / 弹体实例化绘制（常数次调用，与单位数无关）。
            _combat?.FrameRender(_camera, GameClock.StepAlpha);
        }

        /// <summary>FG0-ARCH-01：一个固定模拟步（dt = 1 / clock.sim_step_hz 游戏秒）。由 <see cref="WorldSimulation"/> 调用——
        /// **无论镜头在不在家园都执行**（FGR-BASE-021：远征时家园照常生产、施工、解析；DEBT-FG0ARCH04-14 关闭）。
        /// 本方法不得读取观察状态；自检用“观察 / 不观察”对照逐字段比较来守护这一条。</summary>
        public void SimStep(float dt)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null || HomeValleySoftlockGuard.IsCoreDestroyed(state))
            {
                return; // 核心被毁：家园冻结，只剩失败页（ER3-SOFTLOCK-01）。
            }
            SyncSimEntities(state);
            HomeValleyCombatTargets.SyncDummy(_combat, state); // 训练靶血量镜像（O(1)）。
            SquadCommands.TickSim(dt);
            // FG0-ARCH-03：机器的工作赶路、编队命令、直控移动、训练靶自动交战（射程与间隔）、家园突袭的突袭者与炮塔、弹体——
            // 全部在家园战斗内核的一步里（Burst）；到达回调、对机器 / 训练靶的伤害结算作为事件交回，每步有上限。
            _combat?.Step(dt, GameClock.GameSeconds);
            HomeValleyWorkOrders.Tick(state, dt, GetMachinePosition, ReleaseMachineMovement,
                IsMachineDirectControlled, BeginAutoAssignedMovement);
            HomeValleyFactory.Tick(state, dt); // ER4-FAC-01：装配站生产队列。
            PrimitiveCraftStation.Tick(state, dt); // ER4-PRIM-04：合成台升级/拆解队列。
            HomeValleyAnalysis.Tick(state, dt); // ER6-ANA-01：解析台队列。
            HomeValleyCombatTargets.Tick(state, dt); // ER4-PRIM-05：低威胁残骸靶被动再生。
            // ER4-PRIM-05：AI 同出口自动交战——射程 / 间隔判定在内核（AutoEngage 行为），结算走 HomeValleyCombatRules.OnEngageRequest。
            HomeValleySignal.RecomputeUnlock(state); // ER5-SIG-01：破碎都市解锁判定。
            FoundryOutpostRegion.RecomputeUnlock(state); // ER6-FOUNDRY-01：铸造前哨外围解锁判定。
            CampaignExposureLedger.TickTowerBroadcastOff(state, dt); // ER6-EXPOSE-01：塔关广播每10秒-2。
            HomeValleyBeacon.Tick(state, dt); // ER7-BEACON-01：信标启动10秒不可取消演出计时。
            HomeValleySoftlockGuard.Tick(state, dt, BeginAutoAssignedMovement);
        }

        private readonly HashSet<int> _markerIds = new HashSet<int>();

        /// <summary>FG0-ARCH-03：上次名册对账时的 <see cref="MachineRegistry.RosterRevision"/>（int.MinValue = 下一步必须对账）。</summary>
        private int _rosterSynced = int.MinValue;

        /// <summary>自检用：名册对账真正执行（O(机器数)）的次数。名册没变的步不增加（DEBT-FG0ARCH03-06 收口的守护）。</summary>
        public int RosterSyncCount { get; private set; }

        /// <summary>FG0-ARCH-01：让“家园里有哪些机器”的战斗内核句柄与记录一致——新出厂、紧急救援、远征归来的机器补上；
        /// 被派遣出去的机器移走。属于模拟（与观察无关）。
        /// FG0-ARCH-03：只在机器名册版本号（<see cref="MachineRegistry.RosterRevision"/>：登记、读档、换地点、阵亡、改造）
        /// 变化后做一次 O(机器数)；名册没变的步 O(1)。有机器没能建出句柄时不记为已对账，下一步重试。</summary>
        private void SyncSimEntities(CampaignState state)
        {
            int revision = MachineRegistry.RosterRevision;
            if (_rosterSynced == revision)
            {
                return;
            }
            RosterSyncCount++;
            bool complete = true;
            _markerIds.Clear();
            for (int i = _machineMarkers.Count - 1; i >= 0; i--)
            {
                HomeValleyMachineMarker m = _machineMarkers[i];
                if (m == null)
                {
                    _machineMarkers.RemoveAt(i);
                    continue;
                }
                if (!MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec) || rec.RegionId != HomeValleyLayout.RegionId)
                {
                    // 被派遣到远征地点（或记录已不存在）：这台机器不在家园了。
                    if (_possessed == m)
                    {
                        Control.ReleaseToStrategy();
                        _possessed = null;
                    }
                    if (_selected == m)
                    {
                        _selected = null;
                    }
                    // 派遣时机器状态已经由出发事务写回记录（CombatSites.ExportMachine）；这里不再导出，免得用家园位置覆盖远征落点。
                    if (m.View != null)
                    {
                        GameLogic.View.UnityObjects.Release(m.View.gameObject);
                    }
                    m.DetachView();
                    _combat?.RemoveMachine(m.LogicId, export: false);
                    _machineMarkers.RemoveAt(i);
                    continue;
                }
                _markerIds.Add(m.LogicId);
            }
            foreach (MachineRecord machine in MachineRegistry.AllRecords)
            {
                if (machine != null && machine.RegionId == HomeValleyLayout.RegionId && machine.IsAlive && !_markerIds.Contains(machine.LogicId))
                {
                    complete &= CreateMachineHandle(machine) != null;
                    _markerIds.Add(machine.LogicId);
                }
            }
            _rosterSynced = complete ? revision : int.MinValue;
        }

        /// <summary>供 <see cref="HomeValleyWorkOrders.Tick"/> 赶路阶段路径停滞看门狗查询机器实时坐标
        /// （<see cref="MachineRecord.WorldPosition"/> 只在 Exit 时才同步，赶路途中是过期值，必须读
        /// Transform 实时位置）。</summary>
        private Vector2? GetMachinePosition(int logicId)
        {
            // FG0-ARCH-03：句柄按 LogicId 在战斗地点的字典里（O(1)），不逐台扫描。
            HomeValleyMachineMarker marker = FindMarker(logicId);
            return marker != null ? marker.Position : (Vector2?)null;
        }

        /// <summary>PathBlocked 触发时的释放回调：让对应 marker 停止赶路，不留一个订单已经
        /// Waiting/释放但视觉上机器还在朝旧目标走的不一致状态。</summary>
        private void ReleaseMachineMovement(int logicId)
        {
            FindMarker(logicId)?.CancelCommandMove();
        }

        /// <summary>FG0-ARCH-03：按 LogicId 取家园里的机器句柄——战斗地点的字典查找（O(1)）；
        /// 句柄集合与 <see cref="_machineMarkers"/> 一致（两者只在 <see cref="CreateMachineHandle"/> / 对账 / 进出场时同步增删）。</summary>
        private HomeValleyMachineMarker FindMarker(int logicId)
        {
            return _combat != null && _combat.TryGetMachineMarker(logicId, out HomeValleyMachineMarker marker) ? marker : null;
        }

        /// <summary>ERD-WRK-002"直控机器暂不领取新单"的判定来源——<see cref="HomeValleyWorkOrders.Tick"/>
        /// 的分配引擎不下沉持有 <see cref="_possessed"/>，只能靠委托查询。ER4-RETROFIT-01：改造面板
        /// （<c>UI.Factory.FactoryPanelUIToolkit</c>）同样需要"目标机是否正被直控"这一判定，改为公开。</summary>
        public bool IsMachineDirectControlled(int logicId)
        {
            return _possessed != null && _possessed.LogicId == logicId;
        }

        /// <summary>ER3-WRK-02 分配引擎选中一台空闲机器后的回调：与玩家点选下令
        /// （<see cref="CommandWork"/>/<see cref="CommandHaul"/>）共用同一条移动链
        /// （<see cref="BeginMovementForOrder"/>），只是移动指令的发起方从"玩家点击"变成
        /// "分配引擎"，目的地也相应从"鼠标射线落点"改用 <see cref="HomeValleyWorkOrders.ResolveWorkPosition"/>
        /// 的精确锚点坐标（两者在建筑/残骸/地面物上的取值本就是同一位置，行为一致）。</summary>
        private void BeginAutoAssignedMovement(WorkOrderRecord order)
        {
            HomeValleyMachineMarker marker = FindMarker(order.AssignedMachineLogicId);
            if (marker == null)
            {
                // ER3-SOFTLOCK-01：紧急救援机是本帧才登记进 MachineRegistry 的（不像开局两台机器
                // 在 Enter() 的 BuildVisuals 里就有可视化对象），当场补建一个再继续——不能指望
                // SyncWorldVisuals 的逐帧对账（那是本帧 Update 更晚才跑，等它跑到时这次移动指令
                // 已经错过），否则一台机器生成后要等到下一次真正调用它才会显形。
                if (MachineRegistry.TryGetRecord(order.AssignedMachineLogicId, out MachineRecord record) && record.IsAlive)
                {
                    CreateMachineHandle(record);
                    marker = FindMarker(order.AssignedMachineLogicId);
                }
            }
            if (marker == null)
            {
                // 理论上不应再发生（上面已经补建过一次）；真出现就跳过这一轮，订单已是 Reserved，
                // 下一次 0.5 秒评估会因为它一直没有实际动起来而继续留在候选池里（不会丢单）。
                Log.Warning($"[HomeValleyController] 自动分配命中订单 {order.WorkOrderId}，" +
                    $"但机器 {order.AssignedMachineLogicId} 无可视化对象且补建失败，本轮跳过移动。");
                return;
            }

            // ER5-CMD-01：分配引擎即将接管这台机器的 Transform，先取消可能正在执行的战略命令
            // （理论上分配引擎只挑选空闲机器，但受控/战略命令中的机器不参与分配——这里仍双保险一次，
            // 免得未来分配条件变化时悄悄引入两套移动系统同帧打架的问题）。
            SquadCommands.CancelCommandFor(order.AssignedMachineLogicId);

            Vector2 pos2 = HomeValleyWorkOrders.ResolveWorkPosition(CampaignSession.Current, order);
            Vector3 destination = new Vector3(pos2.x, 1f, pos2.y);
            BeginMovementForOrder(marker, destination, order);
        }

        /// <summary>ER2-INPUT-01：Tab 循环切换接管目标 + WASD 直控移动。仅在真正处于 Direct
        /// 视角（不含过渡中）且确有接管目标时生效；<see cref="InputRouter"/> 的域互斥已经保证
        /// 战略视角下这里的 GetActionKey/ConsumeAction 全部读不到东西，不需要再判一次 Mode。</summary>
        private void HandleDirectControl(float dt)
        {
            if (_possessed == null)
            {
                return;
            }

            if (InputRouter.ConsumeAction(GameActionId.CycleControlTarget, InputScope.Direct))
            {
                // ER5-CTL-01：Tab 循环与候选条点击/M 键首次接管统一走 TrySwitchControlledUnit——
                // 失败（本区域只有一台合法机器时 NextCandidate 返回自己 → AleadyControlled）只记日志，
                // 不改变当前受控目标。
                RegionControlSwitchResult tabResult = Control.TrySwitchControlledUnit(null);
                if (!tabResult.Success && tabResult.Failure != RegionControlFailure.AlreadyControlled)
                {
                    Log.Info($"[HomeValleyController] Tab 切换接管目标失败：{tabResult.PlayerText}");
                }
            }

            float x = 0f;
            float z = 0f;
            if (InputRouter.GetActionKey(GameActionId.MoveLeft, InputScope.Direct)) { x -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveRight, InputScope.Direct)) { x += 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct)) { z -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct)) { z += 1f; }

            if (x != 0f || z != 0f)
            {
                _lastFacing = new Vector2(x, z).normalized; // ER5-INT-01：E 候选"指向"排序读这个。
            }

            // FG0-ARCH-03：直控输入交给内核（模拟步里按机器速度移动；暂停时不走步）。
            _possessed.SetDirectInput(new Vector2(x, z));

            // ER4-PRIM-05 STORY-EXECUTION-CARDS.md 第1条"鼠标瞄准+左键攻击"——归还谷地 WASD 直控此前
            // 只有移动，这是本 Story 唯一的全新玩法机制（其余都是把已有编译/装配出口接起来）。左键复用
            // 与 Strategy 域选中点击（HandleSelectionClick）同一套 InputRouter.GetMouseButtonDown 用法，
            // 只是域换成 Direct——两个域互斥，同一物理键不会被重复消费。
            if (InputRouter.GetMouseButtonDown(0, InputScope.Direct) &&
                InputRouter.TryGetPointer(InputScope.Direct, out Vector3 aimPointer))
            {
                TryDirectAttack(aimPointer);
            }
        }

        /// <summary>直控攻击：鼠标屏幕位置反投影到地面（y=0）算出瞄准方向，交给
        /// <see cref="HomeValleyCombatTargets.TryFindTargetInAim"/> 做锥形+射程判定；命中后走
        /// <see cref="HomeValleyCombatTargets.TryAttack"/> 唯一结算入口（与 AI 共用同一实现）。
        /// 瞄准落空/没有装配输出都是合法负向路径，只记日志不抛错。</summary>
        private void TryDirectAttack(Vector3 screenPointer)
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

            CombatTargetRecord target = HomeValleyCombatTargets.TryFindTargetInAim(state, origin, aimDir);
            if (target == null)
            {
                Log.Info("[HomeValleyController] 直控攻击：瞄准方向/射程内没有可命中的低威胁残骸靶。");
                return;
            }

            HomeValleyCombatTargets.TryAttack(state, _possessed.LogicId, target.TargetId, state.RandomSeed, isAiSource: false);
        }

        /// <summary>ER4-PRIM-05 STORY-EXECUTION-CARDS.md 第2条"玩家/AI 各使用一次同一出口"——未被直控、空闲、在射程内的机器每隔 5 秒自动打一次训练靶。
        /// FG0-ARCH-03：逐机器的射程 / 间隔判定在家园战斗内核（AutoEngage 行为，每步 O(机器数) 在 AOT）；命中结算仍走
        /// <see cref="HomeValleyCombatTargets.TryAttack"/>（<c>isAiSource: true</c>，经 HomeValleyCombatRules.OnEngageRequest），与玩家直控同一出口。</summary>
        public const float AiEngageIntervalSeconds = 5f;

        public int? SelectedMachineLogicId => _selected != null ? _selected.LogicId : (int?)null;

        /// <summary>ER5-CTL-01：HUD 显示"编号/蓝图"用——当前受控机器 LogicId，没有接管返回 null。</summary>
        public int? PossessedMachineLogicId => _possessed != null ? _possessed.LogicId : (int?)null;

        /// <summary>ER5-CTL-01：候选条 UI 点击一台合法机器后，若镜头尚未处于 Direct，补一次正式的
        /// 0.35 秒过渡请求（Tab/M 键路径已经在各自域内触发，这条专供候选条这种"战略视角下点击 UI
        /// 直接接管"的新入口）。已经在 Direct 时是安全 no-op（不产生第二次过渡）。</summary>
        public bool RequestDirectView() => _cameraDirector != null && _cameraDirector.RequestDirect();

        /// <summary>ER4-FAC-01：装配站面板开关状态。点击装配站建筑切换（见 <see cref="HandleSelectionClick"/>），
        /// 独立于机器选中/移动指令——面板是管理界面，不要求玩家先选一台机器才能打开。</summary>
        private bool _factoryPanelOpen;
        public bool IsFactoryPanelOpen => _factoryPanelOpen;
        public void SetFactoryPanelOpen(bool open) => _factoryPanelOpen = open;

        /// <summary>ER5-EXP-01：远征准备面板开关状态。点击已修复（Operational）的信号塔切换（见
        /// <see cref="HandleSelectionClick"/>）——同一栋建筑 Damaged 时点击仍走既有"修复"下令路径，
        /// 不冲突（两者按建筑当前施工状态互斥分流）。信号塔是叙事上的"往破碎都市广播/建立航线"的
        /// 那个建筑，AC-JRN-006 原文"修复信号塔；破碎都市从 Locked 变 Available；远征准备列出机器
        /// 能力和带宽"三件事本就是同一条玩家旅程的连续三步，复用它做出征准备的入口而不是另建一个
        /// 专属"远征闸门"建筑物，避免内容锁定表再加一条无预算的新建筑。</summary>
        private bool _expeditionPrepPanelOpen;
        public bool IsExpeditionPrepPanelOpen => _expeditionPrepPanelOpen;
        public void SetExpeditionPrepPanelOpen(bool open) => _expeditionPrepPanelOpen = open;

        /// <summary>ER7-BEACON-01：信标启动确认面板开关状态——同 <see cref="IsExpeditionPrepPanelOpen"/>
        /// 先例，E 交互 `Complete` 只负责打开面板（"二次确认"的第一次确认已经是"按住E"本身，面板里的
        /// 确认按钮才是第二次），真正调用 <see cref="HomeValleyBeacon.TryStartLaunch"/> 的是面板按钮。</summary>
        private bool _beaconLaunchPanelOpen;
        public bool IsBeaconLaunchPanelOpen => _beaconLaunchPanelOpen;
        public void SetBeaconLaunchPanelOpen(bool open) => _beaconLaunchPanelOpen = open;

        /// <summary>ER4-PRIM-02：电路板面板开关状态，由 <c>CircuitBoardPanelUIToolkit</c> 自身的常驻
        /// 切换按钮驱动（不占用建筑点选路由——正式"家园蓝图"容器入口留 ER4-BLP-01，本 Story 先提供一个
        /// 独立可达的入口，不强求等那个容器落地才能测试/使用电路板）。</summary>
        private bool _circuitBoardPanelOpen;
        public bool IsCircuitBoardPanelOpen => _circuitBoardPanelOpen;
        public void SetCircuitBoardPanelOpen(bool open) => _circuitBoardPanelOpen = open;

        /// <summary>ER4-PRIM-04：合成台面板开关状态，同 <see cref="IsCircuitBoardPanelOpen"/> 先例——
        /// 自带常驻切换按钮，不占用建筑点选路由。</summary>
        private bool _craftStationPanelOpen;
        public bool IsCraftStationPanelOpen => _craftStationPanelOpen;
        public void SetCraftStationPanelOpen(bool open) => _craftStationPanelOpen = open;

        /// <summary>ER6-ANA-01：解析台面板开关状态——同 <see cref="IsExpeditionPrepPanelOpen"/> 先例，
        /// 点击已修复（Operational）的解析台建筑切换（专属建筑，不像装配站要身兼三职，不需要常驻
        /// 切换按钮那一套）。</summary>
        private bool _analysisPanelOpen;
        public bool IsAnalysisPanelOpen => _analysisPanelOpen;
        public void SetAnalysisPanelOpen(bool open) => _analysisPanelOpen = open;

        /// <summary>供工作单面板"点击定位"（AC-UI-003）调用：把选中切到该订单当前指派的机器并高亮，
        /// 与鼠标直接点机器同一套视觉反馈。订单尚未指派机器（Ready/Waiting）时无具体对象可定位，
        /// 返回 false，调用方保持原选中不报错——完整的"打开恢复面板"仍是 ER5-INT-01/UI-04 范围。</summary>
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

        /// <summary>供工作单面板"调整工作偏好"控件调用：写入 <see cref="MachineRecord.WorkPriorities"/>
        /// 并立即唤醒分配引擎（STORY-EXECUTION-CARDS.md"优先级变化即时更新"），不必等满 0.5 秒
        /// 轮询窗口。0＝该机器永久禁用这一类自动分配，不影响玩家直接点选下令（显式命令绕过偏好）。</summary>
        public static bool TrySetMachineWorkPriority(int logicId, WorkOrderKind kind, int priority)
        {
            bool ok = MachineRegistry.TrySetWorkPriority(logicId, kind, priority);
            if (ok)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
            }
            return ok;
        }

        /// <summary>ER2-INPUT-01：CameraDirector 请求"给我一个直控目标"时的钩子（M 键从战略切
        /// 直控那一刻触发）。用当前选中的机器；没有选中就拒绝，镜头会照常留在战略视角
        /// （与细胞阶段"没有可接管的身体"是同一失败语义）。ER5-CTL-01 起校验/提交都经
        /// <see cref="Control"/>，本方法只负责"没有显式选中直接拒绝"这条前置门槛
        /// （<see cref="RegionControlSystem.TrySwitchControlledUnit"/> 的 <c>explicitLogicId</c> 为
        /// null 时走 Tab 循环语义，不是"没选中就拒绝"，两者不能共用同一条路径）。</summary>
        private bool EnsureDirectTarget()
        {
            if (_selected == null)
            {
                return false;
            }
            RegionControlSwitchResult result = Control.TrySwitchControlledUnit(_selected.LogicId);
            if (!result.Success)
            {
                Log.Info($"[HomeValleyController] 接管请求被拒绝：{result.PlayerText}");
            }
            return result.Success;
        }

        /// <summary>ER5-CTL-01：<see cref="Control"/> 接管成功提交后的收尾——同旧
        /// EnsureDirectTarget/CycleControlTarget 的既有纪律（工作单让位 + 选中同步高亮 +
        /// MachineRegistry 统计），现在两条路径与候选条 UI 共用同一个回调。</summary>
        private void OnControlPossessCommitted(int logicId)
        {
            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
                // ERD-WRK-003 第三条：接管中的机器不再持有在办订单（保留已搬货物/已扣资源，订单回
                // Ready 等待重新指派），不能一边被玩家亲自开、一边又被工作单状态机继续推进。
                HomeValleyWorkOrders.OnMachinePossessed(state, logicId);
            }
            // ER4-MCH-01：任何一次真实接管都补记统计/经历（含 Tab 循环/候选条点击，不只是首次 M 键）。
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

        /// <summary>ER5-CTL-01：<see cref="Control"/> 释放一台机器时的收尾（切换出去/回战略/死亡/
        /// 失联全部经这条口子）——"退出后重评估"：这台机器不再受玩家亲自驾驶，立即让分配引擎把它
        /// 纳入下一轮候选，不必等满 0.5 秒轮询窗口。</summary>
        private void OnControlReleased(int logicId)
        {
            HomeValleyWorkOrders.MarkAssignmentDirty();
        }

        /// <summary>把归还谷地的具体绑定接进共享的 <see cref="RegionControlSystem"/>；归还谷地没有
        /// 干扰机制，<see cref="RegionControlContext.IsPositionJammed"/> 留 null——SignalJammed/
        /// Suspended 两个分支在本区域永远不会触发，这是区域差异，不是遗漏（同 <c>CancelWorkIfAny</c>
        /// 在 <see cref="SetupSquadCommands"/> 的既有先例：破碎都市传 null 是同一处理方式的镜像）。</summary>
        private void SetupControlSystem()
        {
            Control.Bind(new RegionControlContext
            {
                RegionId = HomeValleyLayout.RegionId,
                Markers = _machineMarkers,
                GetPossessed = () => _possessed,
                SetPossessed = SetPossessedMarker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = null,
                SquadCommands = SquadCommands,
                OnPossessCommitted = OnControlPossessCommitted,
                OnReleased = OnControlReleased,
                FallbackAnchor = () => HomeValleyLayout.Core.Position,
            });
        }

        // ── ER5-INT-01：E 交互（残骸拆解/战利品装载/信标启动占位）───────────────────

        /// <summary>把归还谷地的候选来源/朝向/门槛接进共享的 <see cref="RegionInteractionSystem"/>。
        /// 可达性判定复用 <see cref="SetupSquadCommands"/> 同一份锚点净空障碍列表——两处都是"锚点当
        /// 局部静态障碍"的同一份事实，不重复建第二份。</summary>
        private void SetupInteraction()
        {
            var obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (HomeValleyLayout.Anchor anchor in HomeValleyLayout.AllAnchors())
            {
                obstacles.Add((anchor.Position, anchor.ClearanceRadius));
            }

            Interact.Bind(new RegionInteractContext
            {
                GetPossessed = () => _possessed,
                GateCheck = () =>
                {
                    // 归还谷地没有干扰机制（Control 绑定时 IsPositionJammed 传 null），不会有 Suspended
                    // 态。DEBT-ER5INT01-03：MachineLostControl 判定口与 FracturedCityController 同款
                    // 理由，见该处注释——"控制彻底丢失"由 Tick() 顶部 possessed==null 分支统一处理为
                    // NoControlledUnit，效果一致。
                    return InputRouter.ModalUiOpen ? RegionInteractFailure.ModalBlocked : RegionInteractFailure.None;
                },
                BuildCandidates = BuildInteractCandidates,
                GetFacing = () => _lastFacing,
                Obstacles = obstacles,
            });
        }

        private List<RegionInteractCandidate> BuildInteractCandidates()
        {
            var list = new List<RegionInteractCandidate>(4);
            CampaignState state = CampaignSession.Current;
            if (state == null || _possessed == null)
            {
                return list;
            }

            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            AddWreckageCandidate(list, region, HomeValleyLayout.Wreckage1NodeId, HomeValleyLayout.Wreckage1.Position);
            AddWreckageCandidate(list, region, HomeValleyLayout.Wreckage2NodeId, HomeValleyLayout.Wreckage2.Position);

            if (state.GroundItems != null)
            {
                foreach (GroundItemRecord item in state.GroundItems)
                {
                    if (item.RegionId != HomeValleyLayout.RegionId)
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

            // ER7-BEACON-01：信标启动——只在建筑真实存在时提供候选（未建成时"按E"没有意义，玩家应该
            // 去点建造位造它，不是对着预留位空按E）。真正的启动判定（Operational/Powered/是否已启动过）
            // 全部委托 HomeValleyBeacon，本候选的 Complete 只打开二次确认面板，不直接调用 TryStartLaunch
            // （"E 交互要求玩家二次确认"字面要求——按住E是第一次确认，面板里再点一次确认按钮才真正启动）。
            if (HomeValleyBeacon.Exists(state))
            {
                list.Add(new RegionInteractCandidate
                {
                    Id = "beacon:" + HomeValleyLayout.BeaconSlotId,
                    Category = RegionInteractCategory.BeaconActivate,
                    // FG0-ARCH-04：信标可以放在格网上任意合法位置，交互点跟着建筑走。
                    Position = HomeValleyBeacon.FindBuilding(state)?.Position ?? HomeValleyLayout.BeaconSlot.Position,
                    HoldSeconds = 1.5f,
                    Priority = -10,
                    ActionVerb = "启动信标",
                    Validate = () =>
                    {
                        CampaignState s = CampaignSession.Current;
                        if (HomeValleyBeacon.IsLaunched(s) || HomeValleyBeacon.IsLaunching(s))
                        {
                            return RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "信标已启动。");
                        }
                        return RegionInteractResult.Ok();
                    },
                    Complete = () =>
                    {
                        SetBeaconLaunchPanelOpen(true);
                        return RegionInteractResult.Ok();
                    },
                });
            }

            return list;
        }

        private void AddWreckageCandidate(List<RegionInteractCandidate> list, RegionRecord region, string nodeId, Vector2 position)
        {
            bool destroyed = region?.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(nodeId);
            if (destroyed)
            {
                return;
            }
            list.Add(new RegionInteractCandidate
            {
                Id = "wreckage:" + nodeId,
                Category = RegionInteractCategory.WreckageSalvage,
                Position = position,
                HoldSeconds = 0f, // 点击即启动/加入——真正耗时由 HomeValleyWorkOrders 的 Duration 状态机负责。
                Priority = 20,
                ActionVerb = "拆解",
                Validate = () =>
                {
                    CampaignState s = CampaignSession.Current;
                    RegionRecord r = s?.RegionRecords?.FirstOrDefault(x => x.RegionId == HomeValleyLayout.RegionId);
                    return r?.DestroyedNodeIds != null && r.DestroyedNodeIds.Contains(nodeId)
                        ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "残骸已被拆解。")
                        : RegionInteractResult.Ok();
                },
                Complete = () => CompleteWreckageSalvageInteract(nodeId),
            });
        }

        /// <summary>E 触发的直控拆解：与点选下令同一条 <see cref="HomeValleyWorkOrders.TryCreateSalvage"/>
        /// 状态机——机器已经站在范围内，等同"已到达"，立即推进 Reserved→InProgress，不再走 CommandMoveTo
        /// 赶路那一段（不需要，也不应该：直控机器本来就不接受 CommandMoveTo 覆盖，见类注释既有纪律）。</summary>
        private RegionInteractResult CompleteWreckageSalvageInteract(string nodeId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || _possessed == null)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.NoControlledUnit, "没有受控机器。");
            }
            HomeValleyWorkOrders.WorkOrderOpResult result = HomeValleyWorkOrders.TryCreateSalvage(state, nodeId, _possessed.LogicId);
            if (!result.Success)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "拆解无法开始：" + result.FailureReason);
            }
            HomeValleyWorkOrders.OnArrivedAtWork(state, result.WorkOrderId);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            return RegionInteractResult.Ok("拆解已开始。");
        }

        /// <summary>E 触发的战利品装载——通用地面物两阶段搬运票据一次性走完（同
        /// <see cref="TryCollectWreckageDrop"/> 先例，泛化到任意本区域地面物，不限定拆解掉落）。</summary>
        private RegionInteractResult CompleteLootLoad(string groundItemId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.NoControlledUnit, "没有活动的归还谷地会话。");
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

        /// <summary>ER5-INT-01"离开3米时停止并恢复原物状态"——直控拆解没有独立的距离守卫（既有
        /// WorkOrder Tick 只在赶路阶段 Reserved 才看位置），玩家用 WASD 亲自把机器开走属于本 Story
        /// 引入的新场景，需要单独看住。<see cref="HomeValleyWorkOrders.CancelOrder"/> 会原样退还预留的
        /// 废料事务、不留半点产出——"恢复原物状态"字面意义上的满足（节点没被标记摧毁，没有资源变化）。</summary>
        private void TickDirectSalvageRangeGuard(CampaignState state)
        {
            if (_possessed == null)
            {
                return;
            }
            WorkOrderRecord order = HomeValleyWorkOrders.FindActiveOrderForMachine(state, _possessed.LogicId);
            if (order == null || order.Kind != WorkOrderKind.Salvage)
            {
                return;
            }
            Vector2 targetPos = HomeValleyWorkOrders.ResolveWorkPosition(state, order);
            Vector3 p = _possessed.Position3;
            if (Vector2.Distance(new Vector2(p.x, p.z), targetPos) <= RegionInteractionSystem.InteractRange)
            {
                return;
            }
            Vector2 currentPos = new Vector2(p.x, p.z);
            HomeValleyWorkOrders.CancelOrder(state, order.WorkOrderId, currentPos);
            Interact.NotifySubtitle("已离开交互范围，拆解已取消并恢复原状。");
            Log.Info($"[HomeValleyController] 直控拆解 {order.WorkOrderId} 因离开交互范围（>{RegionInteractionSystem.InteractRange}米）被取消。");
        }

        /// <summary>CameraDirector 的直控锚点来源：接管中的机器的世界 XZ 位置；没有接管返回 false。</summary>
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

        /// <summary>退出归还谷地（进远征/回主菜单）。只清理本区域的运行时表现，
        /// <see cref="MachineRegistry"/>/<see cref="CampaignState"/> 记录原样保留——
        /// 这就是"持久化"要求：回城/再次读档要看到同一批建筑与残骸状态，不是重新生成。</summary>
        public void Exit()
        {
            if (!IsLoaded)
            {
                return;
            }

            // FG0-ARCH-03：内核随后释放——积压的玩法事件先全部结算，不随队列丢掉。
            _combat?.FlushPendingEvents();
            SyncLiveStateBackToRecords();
            // FG0-ARCH-03：家园战斗内核随家园卸载释放（整个世界卸载 = 回主菜单 / 回滚）；存档里的快照随之删除（记录已写回）。
            CombatSites.Close(SiteId, CampaignSession.Current, dropRecord: true);
            _combat = null;
            BuildMode.Shutdown(); // FG0-ARCH-04：建造模式的虚影 / 叠加层 / 输入上下文与区域成对释放。
            HomeGridService.ShutdownStreaming(); // FG0-ARCH-05：在飞的区块生成任务与区域成对释放（格网本身保留）。
            HomeValleyBuildMode.Unbind(BuildMode);
            SquadCommands.PointerSuppressed = false;
            DestroyVisuals();
            _machineMarkers.Clear();
            _rosterSynced = int.MinValue;
            _selected = null;
            // ER4-BLP-02：区域卸载与登记表解绑成对——清空当前会话的装配登记缓存（不影响
            // CampaignState.MachineRecords 本身，机器长期记录原样保留，下次 Enter 重新登记）。
            MachineLoadoutRegistry.Clear();
            // FG0-ARCH-01：镜头归全局镜头管理器（WorldSimulation.UnloadAll → WorldView.Reset 统一解绑并复位输入）。
            _possessed = null;
            _factoryPanelOpen = false;
            _expeditionPrepPanelOpen = false;
            // ER5-CMD-01：选择集/编组/排队命令/最近事件全部清空——不跨局残留（DestroyVisuals 已经
            // 摧毁 _root，SquadCommands 内部对选中环/目的地标记的 Destroy 调用在此之后只是安全的
            // no-op，真正要紧的是清掉 C# 侧的字典/列表状态）。
            SquadCommands.Unbind();
            Control.Unbind();
            Interact.Unbind();
            IsLoaded = false;
            Log.Info("[HomeValleyController] 已退出归还谷地。");
        }

        // ── 首次进入播种 / 持久化 ────────────────────────────────────────────

        private static bool TryFindRegion(CampaignState state, out RegionRecord region)
        {
            region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            return region != null;
        }

        private static int CountRegionBuildings(CampaignState state)
        {
            return state.BuildingRecords?.Count(b => b.RegionId == HomeValleyLayout.RegionId) ?? 0;
        }

        /// <summary>首次进入才建立 RegionRecord + 7 条 BuildingRecord；已存在则原样跳过——
        /// 这是"第二次进入/读档不重复对象"负向用例的核心保证。</summary>
        private static void EnsureRegionSeeded(CampaignState state)
        {
            if (TryFindRegion(state, out _))
            {
                return;
            }

            var region = new RegionRecord
            {
                RegionId = HomeValleyLayout.RegionId,
                State = RegionState.Active,
                DiscoveredNodes = new[] { HomeValleyLayout.Wreckage1NodeId, HomeValleyLayout.Wreckage2NodeId },
                DestroyedNodeIds = Array.Empty<string>(),
                LootedContainerIds = Array.Empty<string>(),
                LostQuestSalvageIds = Array.Empty<string>(),
                EnemyAlertLevel = 0f,
                AdaptationId = null,
                ExpeditionCount = 0,
                CoreGateUnlocked = false,
                CoreState = null,
            };
            state.RegionRecords = (state.RegionRecords ?? Array.Empty<RegionRecord>())
                .Append(region).ToArray();

            // FG0-ARCH-04（FGR-ARC-001“迁移”）：Demo 的 7 座固定锚点建筑改为按开局布局表 fg.TbStartLayout 生成的格网建筑
            // （位置 = 核心枢轴格 + 偏移，朝向写在表里，占地来自 fg.TbBuildingGrid）；唯一生成入口 HomeGridService.CreateStartBuildings。
            List<BuildingRecord> buildings = HomeGridService.CreateStartBuildings(state);
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>())
                .Concat(buildings).ToArray();

            // DEMO-CONTENT-LOCK.md §2.2：核心基础电力 20、带宽 3——只是占位显示值，Enter() 里
            // 紧接着的 HomeValleyPowerGrid.Recompute 会用同样的常量重新算一遍并覆盖（此刻发电机/
            // 信号塔都还是 Damaged，算出来的结果与这里完全一致，这两行只防御"万一 Recompute
            // 调用点被后续改动移除"的极端情况，不是第二份权威来源）。
            state.PowerCapacity = HomeValleyLayout.BaseCoreSupply;
            state.SignalBandwidth = HomeValleyLayout.BaseSignalBandwidth;

            Log.Info("[HomeValleyController] 首次进入归还谷地：已播种 RegionRecord + 7 条建筑记录。");
        }

        /// <summary>首次进入才登记 ERC-001/002；已有记录（新战役当局已生成，或读档已恢复）时
        /// 原样复用，绝不重复 SpawnMachine——否则每次回城都会多出一台机器（AC-LIFE-001/002）。
        ///
        /// ER4-PRIM-02 前：blueprintId 传的是字面 "placeholder:erc_001" 占位串，没有对应的真实
        /// <see cref="BlueprintRecord"/>——"默认机器"这个说法只停留在纸面。本 Story 起改传真实蓝图 ID
        /// （bp_erc001/bp_hauler，由 <see cref="HomeValleyFactory.EnsureBlueprintsSeeded"/> 保证在本方法
        /// 调用前已经播种好，见 <see cref="Enter"/> 调用顺序调整）。</summary>
        private static void EnsureMachinesSeeded(CampaignState state)
        {
            SpawnIfMissing(HomeValleyLayout.Erc001Spawn, HomeValleyLayout.BlueprintErc001Id);
            SpawnIfMissing(HomeValleyLayout.Erc002Spawn, HomeValleyLayout.BlueprintHaulerId);

            void SpawnIfMissing(HomeValleyLayout.Anchor spawn, string blueprintId)
            {
                bool exists = MachineRegistry.AllRecords.Any(r =>
                    r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == spawn.Id && r.IsAlive);
                if (exists)
                {
                    return;
                }

                // ER4-BLP-02：开局机同样是"生产"的一种（只是不经装配站队列），同样要写真实版本号+签名，
                // 不能让 ERC-001/002 这两台永远停留在 BlueprintVersion=1/LoadoutSignature="" 的假状态。
                // 装配登记表本身由调用方 Enter() 在 EnsureMachinesSeeded 之后统一跑一遍
                // RegisterAllRegionMachineLoadouts 补齐，这里不重复登记。
                BlueprintRecord bp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == blueprintId);
                BlueprintVersionRecord version = bp?.Versions?.FirstOrDefault(v => v.Version == bp.ActiveVersion);

                MachineOpResult result = MachineRegistry.SpawnMachine(
                    chassisId: spawn.Id,
                    blueprintId: blueprintId,
                    regionId: HomeValleyLayout.RegionId,
                    position: spawn.Position,
                    health: 100f,
                    maxHealth: 100f,
                    blueprintVersion: version?.Version ?? 1,
                    loadoutSignature: version?.CompileSignature);

                if (!result.Success)
                {
                    Log.Error($"[HomeValleyController] 登记机器 {spawn.Id} 失败：{result.Error} {result.Message}");
                }
            }
        }

        /// <summary>ER4-BLP-02：把当前区域全部存活机器的 (BlueprintId, BlueprintVersion) 重新登记进
        /// <see cref="MachineLoadoutRegistry"/>——覆盖"读档/重进直接带着已存在的机器记录"这条
        /// <see cref="EnsureMachinesSeeded"/> 的"首次生成"分支不会走到的路径。版本记录解析不到时只记警告
        /// 跳过该台（"任何环节绑定失败进入可见错误并拒绝生成默认强力替身"），不阻断其余机器登记或整个
        /// Enter 流程。</summary>
        private static void RegisterAllRegionMachineLoadouts(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                CircuitOpResult result = MachineLoadoutRegistry.Register(state, m.LogicId, m.BlueprintId, m.BlueprintVersion);
                if (!result.Success)
                {
                    Log.Warning($"[HomeValleyController] 机器 {m.LogicId} 装配登记失败：{result.Message}");
                }
            }
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

        private void SyncLiveStateBackToRecords()
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null)
                {
                    continue;
                }
                // FG0-ARCH-01：刚派遣出去的机器在家园下一步回收标记之前仍留有旧标记——不许用家园坐标覆盖它在远征地点的记录。
                if (MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord rec) && rec.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                // FG0-ARCH-03：位置、热量、冷却的真相在内核（血量真相在记录里，不动）。
                _combat?.ExportMachine(marker.LogicId, includePosition: true);
            }
        }

        // ── 资源事务展示 ─────────────────────────────────────────────────────────

        /// <summary>核心"应急缓存180/180"是 <see cref="CampaignState.Scrap"/> 的封顶展示，不是独立
        /// 容器——ERD-ECO-001 完整的多容器资源事务模型（核心缓存/仓库/机器货舱互相转移、总量守恒）
        /// 属于 ER3-ECO-01；本 Story 只保证"180"这个数字来源可追溯、不双记账。</summary>
        public static (int Current, int Cap) GetCoreCacheDisplay(CampaignState state)
        {
            const int cap = 180;
            return (Mathf.Clamp(state.Scrap, 0, cap), cap);
        }

        /// <summary>把一处残骸拆解产出的地面物交付进家园存量。仓满时保持在地面（不丢弃），供玩家在
        /// 腾出空间（如修复仓库）后重试；幂等——同一 nodeId 的拆解物只会被拾取/交付一次，重复调用
        /// 在已交付后找不到地面物直接返回失败，不会二次发放。</summary>
        public HomeValleyCargo.StoreResult TryCollectWreckageDrop(string nodeId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !IsLoaded)
            {
                return HomeValleyCargo.StoreResult.Fail("没有活动的归还谷地会话。");
            }

            string salvageInstanceId = nodeId + ":salvage-drop";
            GroundItemRecord item = HomeValleyCargo.FindGroundItemBySalvageId(state, salvageInstanceId);
            if (item == null)
            {
                return HomeValleyCargo.StoreResult.Fail("地面上没有待收集的残骸拆解物。");
            }

            HomeValleyCargo.HaulTicket ticket = HomeValleyCargo.TryReserveHaul(state, item.GroundItemId);
            return HomeValleyCargo.CommitHaul(state, ticket, nodeId + ":salvage-tx");
        }

        /// <summary>定位针悬在建筑状态标记（y=2.9）上方，与目标条读同一份 <see cref="CampaignObjectiveCatalog"/>；
        /// 下一步不在具体位置上（例如去蓝图编辑器保存）时隐藏，不指错地方。</summary>
        private void RefreshObjectiveMarker(CampaignState state)
        {
            if (_objectiveMarker == null)
            {
                return;
            }
            if (state == null || !CampaignObjectiveCatalog.TryGetHomeMarker(state, out Vector2 where))
            {
                _objectiveMarker.SetVisible(false);
                return;
            }
            _objectiveMarker.transform.position = new Vector3(where.x, ObjectiveMarkerHeight, where.y);
            _objectiveMarker.SetIcon(ContentIcons.ObjectiveMarker);
            _objectiveMarker.SetVisible(true);
        }

        private const float ObjectiveMarkerHeight = 4.4f;

        private void RefreshBuildingVisual(BuildingRecord building)
        {
            if (_root == null)
            {
                return;
            }
            string key = LocalKey(building.BuildingId);
            Transform go = _buildingVisuals.TryGetValue(key, out Transform cached) && cached != null ? cached : null;
            Renderer renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer != null)
            {
                renderer.sharedMaterial = ViewMaterials.Standard(ColorForBuilding(building));
                PlaceBuildingTransform(go, building); // FG0-ARCH-04：旋转后占地 / 朝向跟着变（格网是唯一真相）。
            }
            if (_buildingBadges.TryGetValue(key, out WorldBadge badge) && badge != null)
            {
                badge.SetIcon(StateIconFor(building));
            }
        }

        /// <summary>FG0-ARCH-04：建筑 ID → 可视化节点名里的本地键（"home_valley:generator_2#2" → "generator_2#2"）。
        /// 开局建筑与每类第一座的本地键就是建筑类型 ID，Demo 以来按 "Building_" + 类型 查节点的代码与冒烟不受影响。</summary>
        public static string LocalKey(string buildingId)
        {
            string prefix = HomeValleyLayout.RegionId + ":";
            return buildingId != null && buildingId.StartsWith(prefix, StringComparison.Ordinal) ? buildingId.Substring(prefix.Length) : buildingId;
        }

        /// <summary>占位方块按格网占地摆放：中心 = 占地中心，尺寸 = 占地格数（略缩一圈留出格线），绕 Y 轴按朝向旋转。</summary>
        private static void PlaceBuildingTransform(Transform t, BuildingRecord building)
        {
            Vector2Int size = GridContent.TryGetBuilding(building.BuildingTypeId, out GameConfig.fg.BuildingGrid g)
                ? new Vector2Int(g.FootprintW, g.FootprintH)
                : new Vector2Int(3, 3);
            // 规划中 / 施工中（还没建成）：扁平的“虚影”方块，与完工建筑明显不同（FGR-LOG-006 原型）。
            bool ghost = IsPlannedGhost(building);
            float height = ghost ? 0.5f : 2f;
            t.position = new Vector3(building.Position.x, height * 0.5f, building.Position.y);
            t.rotation = Quaternion.Euler(0f, GridMath.NormalizeRotation(building.Rotation), 0f);
            t.localScale = new Vector3(Mathf.Max(0.5f, size.x - 0.2f), height, Mathf.Max(0.5f, size.y - 0.2f));
        }

        // ── 可视化（占位几何体）────────────────────────────────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[HomeValley]");
            _buildingBadges.Clear();
            _buildingVisuals.Clear();
            _visualsRecordsRef = null;

            foreach (BuildingRecord building in state.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId))
            {
                BuildBuildingVisual(building);
            }

            RegionRecord region = state.RegionRecords.First(r => r.RegionId == HomeValleyLayout.RegionId);
            if (!region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage1NodeId))
            {
                BuildWreckageVisual(HomeValleyLayout.Wreckage1);
            }
            if (!region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage2NodeId))
            {
                BuildWreckageVisual(HomeValleyLayout.Wreckage2);
            }

            // ER7-BEACON-01：解锁前（OBJ-09 未完成）仍是不可交互的"预留位"标记；解锁后且尚未建成时
            // 换成真正可点选建造的 BuildSite（同发电机2 同一套可视化+点击建造管线）。
            bool beaconBuilt = HomeValleyBeacon.Exists(state);
            if (!beaconBuilt)
            {
                if (HomeValleyBeacon.IsUnlocked(state))
                {
                    BuildBuildSiteVisual(HomeValleyLayout.BeaconSlot, HomeValleyLayout.BuildingTypeBeacon);
                }
                else
                {
                    BuildBeaconSlotVisual();
                }
            }
            BuildCombatTargetVisual();

            _objectiveMarker = WorldBadge.Create(_root.transform, "Badge_Objective", new Vector3(0f, ObjectiveMarkerHeight, 0f), 1.6f);
            RefreshObjectiveMarker(state);

            if (!state.BuildingRecords.Any(b => b.BuildingId == HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeGenerator2))
            {
                BuildBuildSiteVisual(HomeValleyLayout.Generator2Site, HomeValleyLayout.BuildingTypeGenerator2);
            }

            foreach (GroundItemRecord item in state.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (item.RegionId == HomeValleyLayout.RegionId)
                {
                    BuildGroundItemVisual(item);
                }
            }

            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null || !MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord machine) || !machine.IsAlive)
                {
                    continue;
                }
                BuildMachineView(marker, machine);
            }
            if (_squadCtx != null)
            {
                _squadCtx.VisualRoot = _root;
            }
        }

        /// <summary>ER3-WRK-01：把当前 <see cref="CampaignState"/> 与已生成的占位可视化对账，覆盖
        /// "订单在游玩过程中完工/产生新地面物"这些 <see cref="BuildVisuals"/>（只在 Enter 时跑一次）
        /// 覆盖不到的场景。个位数量级的建筑/地面物，逐帧对账开销可忽略，不违反热更层性能纪律
        /// （那条规则约束的是战斗热路径，参见 <see cref="HomeValleyWorkOrders"/> 类注释同一处说明）。</summary>
        private void SyncWorldVisuals(CampaignState state)
        {
            if (_root == null)
            {
                return;
            }

            bool recordsChanged = !ReferenceEquals(_visualsRecordsRef, state.BuildingRecords);
            _visualsRecordsRef = state.BuildingRecords;
            if (recordsChanged)
            {
                _liveBuildingKeys.Clear();
            }
            foreach (BuildingRecord building in state.BuildingRecords)
            {
                if (building == null || building.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                string key = LocalKey(building.BuildingId);
                if (recordsChanged)
                {
                    _liveBuildingKeys.Add(key);
                }
                if (!_buildingVisuals.TryGetValue(key, out Transform go) || go == null)
                {
                    BuildBuildingVisual(building);
                    // 这一类的“建议建造位”已被真正的建筑取代（首座建成 / 规划就占用了这个本地键）。
                    if (building.BuildingId == HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeGenerator2)
                    {
                        DestroyChild("BuildSite_" + HomeValleyLayout.Generator2Site.Id);
                    }
                    else if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeBeacon)
                    {
                        DestroyChild("BuildSite_" + HomeValleyLayout.BeaconSlot.Id);
                    }
                }
                else
                {
                    RefreshBuildingVisual(building);
                }
            }
            // FG0-ARCH-04：拆除完成 / 取消规划后建筑记录消失，占位方块与状态标记一并移除（此前拆掉的建筑会一直留在画面上）。
            // 只在记录数组被替换时扫描（记录的增删都会换数组；旋转 / 状态变化是原地改字段，由上面的逐座刷新处理）。
            if (recordsChanged)
            {
                _goneBuildingKeys.Clear();
                foreach (KeyValuePair<string, Transform> kv in _buildingVisuals)
                {
                    if (!_liveBuildingKeys.Contains(kv.Key))
                    {
                        _goneBuildingKeys.Add(kv.Key);
                    }
                }
                foreach (string gone in _goneBuildingKeys)
                {
                    if (_buildingVisuals.TryGetValue(gone, out Transform t) && t != null)
                    {
                        GameLogic.View.UnityObjects.Release(t.gameObject);
                    }
                    _buildingVisuals.Remove(gone);
                    DestroyChild("Badge_" + gone);
                    _buildingBadges.Remove(gone);
                }
            }
            // 取消了规划、类型又回到 0 座时，Demo 的建议建造位重新出现。
            if (!state.BuildingRecords.Any(b => b.BuildingId == HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeGenerator2)
                && _root.transform.Find("BuildSite_" + HomeValleyLayout.Generator2Site.Id) == null)
            {
                BuildBuildSiteVisual(HomeValleyLayout.Generator2Site, HomeValleyLayout.BuildingTypeGenerator2);
            }

            // ER7-BEACON-01：OBJ-09 可能在玩家已经站在归还谷地期间完成（远征回城撤离那一刻）——
            // BuildVisuals 的"锁定预留位 vs 可点选建造位"判断只在 Enter 时跑一次，这里补一次逐帧对账，
            // 把还没来得及切换的锁定预留位换成真正可建造的 BuildSite。
            bool beaconBuiltNow = HomeValleyBeacon.Exists(state);
            if (!beaconBuiltNow && HomeValleyBeacon.IsUnlocked(state) && _root.transform.Find("BeaconSlot_Reserved") != null)
            {
                DestroyChild("BeaconSlot_Reserved");
                BuildBuildSiteVisual(HomeValleyLayout.BeaconSlot, HomeValleyLayout.BuildingTypeBeacon);
            }
            else if (!beaconBuiltNow && HomeValleyBeacon.IsUnlocked(state) && _root.transform.Find("BuildSite_" + HomeValleyLayout.BeaconSlot.Id) == null)
            {
                BuildBuildSiteVisual(HomeValleyLayout.BeaconSlot, HomeValleyLayout.BuildingTypeBeacon); // 取消了信标规划后建造位回来。
            }

            // FG0-ARCH-01：运行时新出现的机器（紧急救援、出厂、远征归来）改由 SyncSimEntities 在每个模拟步补上表现对象——
            // 机器位置记在表现对象上，属于模拟；放在画面对账里会让“有没有人在看家园”改变结果（FGR-BASE-021）。

            foreach (CombatTargetRecord target in state.CombatTargets ?? Array.Empty<CombatTargetRecord>())
            {
                if (target.RegionId == HomeValleyLayout.RegionId)
                {
                    RefreshCombatTargetVisual(target);
                }
            }

            RegionRecord region = state.RegionRecords?.FirstOrDefault(r => r.RegionId == HomeValleyLayout.RegionId);
            if (region != null)
            {
                if (region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage1NodeId))
                {
                    DestroyChild("Wreckage_" + HomeValleyLayout.Wreckage1NodeId);
                }
                if (region.DestroyedNodeIds.Contains(HomeValleyLayout.Wreckage2NodeId))
                {
                    DestroyChild("Wreckage_" + HomeValleyLayout.Wreckage2NodeId);
                }
            }

            var liveGroundItemIds = new HashSet<string>();
            foreach (GroundItemRecord item in state.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (item.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                liveGroundItemIds.Add(item.GroundItemId);
                if (_root.transform.Find("GroundItem_" + item.GroundItemId) == null)
                {
                    BuildGroundItemVisual(item);
                }
            }
            for (int i = _root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = _root.transform.GetChild(i);
                const string prefix = "GroundItem_";
                if (child.name.StartsWith(prefix, StringComparison.Ordinal)
                    && !liveGroundItemIds.Contains(child.name.Substring(prefix.Length)))
                {
                    GameLogic.View.UnityObjects.Release(child.gameObject);
                }
            }
        }

        private void DestroyChild(string childName)
        {
            Transform child = _root != null ? _root.transform.Find(childName) : null;
            if (child != null)
            {
                GameLogic.View.UnityObjects.Release(child.gameObject);
            }
        }

        private void BuildBuildingVisual(BuildingRecord building)
        {
            string key = LocalKey(building.BuildingId);
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Building_" + key;
            go.transform.SetParent(_root.transform, false);
            _buildingVisuals[key] = go.transform;
            PlaceBuildingTransform(go.transform, building);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(ColorForBuilding(building));

            // ER8-CONTENT-01 AC-ACC-002：状态标记挂在区域根节点、悬在建筑正上方（建筑立方体是非等比缩放，
            // 挂在它下面会把贴图拉斜）。没有碰撞体，不影响点选建筑的射线。
            WorldBadge badge = WorldBadge.Create(_root.transform, "Badge_" + key,
                new Vector3(building.Position.x, 2.9f, building.Position.y), 1.3f);
            _buildingBadges[key] = badge;
            badge.SetIcon(StateIconFor(building));
        }

        /// <summary>建筑状态 → 悬浮标记（外形区分：损坏✕圆 / 欠电闪电三角 / 出口堵塞横杠八边形 /
        /// 断电电源符号圆）；正常运转不显示。电源类建筑（发电机）不参与用电，不标“断电”。</summary>
        public static string StateIconFor(BuildingRecord building)
        {
            if (building.ConstructionState == BuildingConstructionState.Damaged)
            {
                return ContentIcons.StateDamaged;
            }
            bool consumer = HomeValleyLayout.PowerProfile.ContainsKey(building.BuildingTypeId);
            if (building.ConstructionState == BuildingConstructionState.Disabled)
            {
                return consumer ? ContentIcons.StateUnpowered : null;
            }
            if (building.ConstructionState != BuildingConstructionState.Operational)
            {
                return null;
            }
            switch (building.PowerState)
            {
                case BuildingPowerState.Brownout:
                    return ContentIcons.StateBrownout;
                case BuildingPowerState.OutputBlocked:
                    return ContentIcons.StateBlocked;
                case BuildingPowerState.Powered:
                    return null;
                default:
                    return consumer ? ContentIcons.StateUnpowered : null;
            }
        }

        private void BuildWreckageVisual(HomeValleyLayout.Anchor wreckage)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Wreckage_" + wreckage.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(wreckage.Position.x, 0.5f, wreckage.Position.y);
            go.transform.localScale = new Vector3(2.5f, 0.5f, 2.5f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(new Color(0.45f, 0.35f, 0.25f));
        }

        /// <summary>ER3-WRK-01 Build：尚未建成的建造位占位（半透明，与 <see cref="BuildBeaconSlotVisual"/>
        /// 同一手法），可点选发起 <see cref="HomeValleyWorkOrders.TryCreateBuild"/>。</summary>
        private void BuildBuildSiteVisual(HomeValleyLayout.Anchor site, string typeId)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "BuildSite_" + site.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(site.Position.x, 0.1f, site.Position.y);
            Vector2Int size = GridContent.TryGetBuilding(typeId, out GameConfig.fg.BuildingGrid g) ? new Vector2Int(g.FootprintW, g.FootprintH) : new Vector2Int(3, 3);
            go.transform.localScale = new Vector3(size.x, 0.2f, size.y);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(new Color(0.3f, 0.6f, 0.9f, 0.4f));
        }

        /// <summary>ER3-WRK-01 Haul：地面待搬运物品的可视化，可点选发起
        /// <see cref="HomeValleyWorkOrders.TryCreateHaul"/>。</summary>
        private void BuildGroundItemVisual(GroundItemRecord item)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "GroundItem_" + item.GroundItemId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(item.Position.x, 0.35f, item.Position.y);
            go.transform.localScale = new Vector3(0.9f, 0.7f, 0.9f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(new Color(0.85f, 0.75f, 0.35f));
        }

        /// <summary>ER4-PRIM-05：低威胁残骸靶占位可视化（同 <see cref="BuildWreckageVisual"/> 手法，
        /// 独立颜色区分"可命中的靶"与"可拆解的残骸"两个不同概念）。<see cref="SyncWorldVisuals"/>
        /// 每帧按 HP 比例刷新颜色，是本 Story"占位 VFX"承诺的最小落地（真正的命中/聚焦差异证据仍是
        /// <see cref="HomeValleyCombatTargets.HitResult"/> 事件与日志，颜色只是补充反馈，不是唯一证据，
        /// 符合 `.claude/rules/projecta-spec-completeness.md` 第4条"占位VFX不能单独标Done"的字面要求
        /// ——这里 VFX 只是辅助，主证据在事件数据）。</summary>
        private void BuildCombatTargetVisual()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "CombatTarget_" + HomeValleyCombatTargets.LowThreatTargetId;
            go.transform.SetParent(_root.transform, false);
            Vector2 pos = HomeValleyLayout.LowThreatTargetPosition;
            go.transform.position = new Vector3(pos.x, 0.6f, pos.y);
            go.transform.localScale = new Vector3(1.6f, 0.6f, 1.6f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(new Color(0.8f, 0.2f, 0.2f));
        }

        /// <summary>目标 HP 比例→颜色：满血深红（危险靶标配色，与"可拆解残骸"的棕色区分），
        /// 打空后变灰（再生冷却中），复位后重新变红。</summary>
        private void RefreshCombatTargetVisual(CombatTargetRecord target)
        {
            if (_root == null)
            {
                return;
            }
            Transform go = _root.transform.Find("CombatTarget_" + target.TargetId);
            Renderer renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer == null)
            {
                return;
            }
            float fraction = target.MaxHealth > 0f ? Mathf.Clamp01(target.Health / target.MaxHealth) : 0f;
            renderer.sharedMaterial = ViewMaterials.Standard(target.Health <= 0f
                ? new Color(0.4f, 0.4f, 0.4f)
                : Color.Lerp(new Color(0.55f, 0.1f, 0.1f), new Color(0.9f, 0.25f, 0.2f), fraction));
        }

        private void BuildBeaconSlotVisual()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "BeaconSlot_Reserved";
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(HomeValleyLayout.BeaconSlot.Position.x, 0.05f,
                HomeValleyLayout.BeaconSlot.Position.y);
            go.transform.localScale = new Vector3(HomeValleyLayout.BeaconSlot.ClearanceRadius * 2f, 0.1f,
                HomeValleyLayout.BeaconSlot.ClearanceRadius * 2f);
            GameLogic.View.UnityObjects.Release(go.GetComponent<Collider>());
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(new Color(0.3f, 0.5f, 0.8f, 0.4f));
        }

        /// <summary>一台家园机器进内核并建立逻辑句柄；被观察时同时建表现对象。</summary>
        private HomeValleyMachineMarker CreateMachineHandle(MachineRecord machine)
        {
            if (_combat == null || machine == null)
            {
                return null;
            }
            HomeValleyMachineMarker existing = FindMarker(machine.LogicId);
            if (existing != null)
            {
                return existing;
            }
            HomeValleyMachineMarker marker = _combat.SpawnMachine(CampaignSession.Current, machine, machine.WorldPosition, autoEngage: true);
            if (marker == null)
            {
                return null;
            }
            _machineMarkers.Add(marker);
            if (_root != null)
            {
                BuildMachineView(marker, machine);
            }
            return marker;
        }

        private void BuildMachineView(HomeValleyMachineMarker marker, MachineRecord machine)
        {
            Vector2 at = marker.Position;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(at.x, 1f, at.y);
            go.transform.localScale = new Vector3(1f, 1f, 1f);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.7f, 0.75f, 0.8f);
            renderer.sharedMaterial = ViewMaterials.Standard(baseColor);
            // AC-THEME-002：按底盘拼车辆剪影（胶囊只留作命中盒），与静默/铸造敌人在灰度下也能分清。
            PlaceholderSilhouette.ApplyMachine(go, renderer, machine.ChassisId);

            MachineView view = go.AddComponent<MachineView>();
            view.Initialize(renderer, baseColor);
            marker.AttachView(view);
            _combat?.BindView(marker.UnitId, go.transform, 1f);
        }

        /// <summary>FG0-ARCH-03：进场 / 读档时建立家园的战斗内核（机器、训练靶；突袭者与炮塔随存档快照恢复）。</summary>
        private void OpenCombat(CampaignState state, bool resume)
        {
            _combatRules = new HomeValleyCombatRules { IsDirectControlled = IsMachineDirectControlled };
            _combat = CombatSites.Open(SiteId, state, resume, _combatRules, out _);
            _combat.Squad = SquadCommands;
            var obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (HomeValleyLayout.Anchor anchor in HomeValleyLayout.AllAnchors())
            {
                obstacles.Add((anchor.Position, anchor.ClearanceRadius));
            }
            _combat.SetObstacles(CombatDemoContent.Obstacles(obstacles));
            _machineMarkers.Clear();
            _rosterSynced = int.MinValue; // 新内核：第一步做一次完整对账。
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId)
                {
                    HomeValleyMachineMarker marker = _combat.SpawnMachine(state, m, m.WorldPosition, autoEngage: true);
                    if (marker != null)
                    {
                        _machineMarkers.Add(marker);
                        _combat.SetMachineInFactory(m.LogicId, m.IsInFactory); // 读档恢复的单位按记录对齐“厂内”标志（O(1)）。
                    }
                }
            }
            foreach (int stale in _combat.MachinesNotIn(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive && r.RegionId == HomeValleyLayout.RegionId))
            {
                _combat.RemoveMachine(stale, export: false);
            }
            LastResumedWorkArrivals = ResumeWorkArrivals(state, _machineMarkers);
            int dummy = HomeValleyCombatTargets.EnsureDummyUnit(_combat, state);
            _combat.SetEngage(dummy, HomeValleyCombatTargets.EngageRange, AiEngageIntervalSeconds);
            HomeValleyCombatTargets.SyncDummy(_combat, state);
            _combat.RefreshAllMachineWeapons(state);
        }

        /// <summary>FG0-ARCH-04：还没建成的格网建筑（规划中 / 已预留材料 / 施工中）。</summary>
        public static bool IsPlannedGhost(BuildingRecord building) =>
            building.ConstructionState == BuildingConstructionState.Planned
            || building.ConstructionState == BuildingConstructionState.MaterialReserved
            || building.ConstructionState == BuildingConstructionState.Building;

        public static Color ColorForBuilding(BuildingRecord building)
        {
            // ER8-CONTENT-01：设置“色盲安全图标”开启时换用 Okabe-Ito 色盲友好色板（红/绿对立改为
            // 朱红/蓝绿）；无论哪套颜色，状态本身都由头顶的形状标记表达（AC-ACC-002 不只靠颜色）。
            bool cvd = GameSettings.ColorblindSafeIconsEnabled;
            if (IsPlannedGhost(building))
            {
                return new Color(0.45f, 0.65f, 0.95f); // 淡蓝：规划中的虚影（形状上也是扁平方块，不只靠颜色）。
            }
            if (building.ConstructionState == BuildingConstructionState.Damaged)
            {
                return cvd ? new Color(0.84f, 0.37f, 0f) : new Color(0.75f, 0.25f, 0.2f); // 红：Damaged
            }
            // 电源类建筑（发电机）本身不用电，PowerState 恒为未接电；运转中应显示“在工作”，
            // 此前一直被涂成“未接电”的灰色，容易误读成停机。
            if (building.ConstructionState == BuildingConstructionState.Operational
                && HomeValleyLayout.PowerSupplyProfile.ContainsKey(building.BuildingTypeId))
            {
                return cvd ? new Color(0f, 0.62f, 0.45f) : new Color(0.25f, 0.7f, 0.3f);
            }
            switch (building.PowerState)
            {
                case BuildingPowerState.Brownout:
                    return cvd ? new Color(0.94f, 0.89f, 0.26f) : new Color(0.85f, 0.7f, 0.15f); // 黄：Brownout
                case BuildingPowerState.OutputBlocked:
                    return cvd ? new Color(0.9f, 0.62f, 0f) : new Color(0.9f, 0.45f, 0.1f); // 橙：OutputBlocked
                case BuildingPowerState.Powered:
                    return cvd ? new Color(0f, 0.62f, 0.45f) : new Color(0.25f, 0.7f, 0.3f); // 绿：Operational + Powered
                default:
                    return new Color(0.5f, 0.55f, 0.6f); // 灰：Operational 但未接电（Unpowered/NotApplicable）
            }
        }

        // ── 相机（ER2-INPUT-01：CameraDirector 驱动，Strategy 起始 + WASD 接管）───

        /// <summary>FG0-ARCH-01：本地点的镜头配置（初始朝向 / 背景 / 裁剪面与 Demo 逐值一致）。镜头本身由 <see cref="WorldView"/> 持有。
        /// 家园在星球表面上：平移范围不再钳在谷地里，而是已探索区域 + 边距（DEBT-FG0ARCH05-01），并包含行进中的队伍。</summary>
        private void SetupCameraProfile()
        {
            _camera = WorldView.EnsureCamera();
            Vector2 focus = HomeValleyLayout.ClampToBounds(HomeValleyLayout.CameraFocusStart);
            _cameraProfile = new WorldCameraProfile
            {
                DirectAnchor = TryGetPossessedAnchor,
                EnsureDirectTarget = EnsureDirectTarget,
                ArenaHalfExtent = HomeValleyLayout.CameraBoundsHalfExtentX,
                DynamicBounds = () => WorldView.PlanetBounds(CampaignSession.Current),
                FollowOffset = new Vector3(0f, 40f, 0f),
                InitialStrategyOrthographicSize = HomeValleyLayout.CameraBoundsHalfExtentZ,
                InitialDirectOrthographicSize = 14f,
                StartFocus = focus,
                Background = new Color(0.05f, 0.07f, 0.10f),
            };
        }

        // ── ER5-CMD-01：战略命令（多选/编组/Move·Attack·Guard·Retreat）────────

        /// <summary>把归还谷地的具体数据/规则接进共享的 <see cref="RegionSquadCommandSystem"/>。
        /// 障碍物用 <see cref="HomeValleyLayout.AllAnchors"/> 的既有锚点表（含 ClearanceRadius）
        /// 当最小局部避障的圆形障碍，不新造一份布局数据。</summary>
        private void SetupSquadCommands()
        {
            var obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (HomeValleyLayout.Anchor anchor in HomeValleyLayout.AllAnchors())
            {
                obstacles.Add((anchor.Position, anchor.ClearanceRadius));
            }

            _squadCtx = new RegionSquadCommandContext
            {
                Camera = _camera,
                Markers = _machineMarkers,
                VisualRoot = _root,
                IsDirectControlled = IsMachineDirectControlled,
                IsEligible = logicId => MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec.IsAlive && !rec.IsInFactory,
                CancelWorkIfAny = CancelActiveWorkOrderIfAny,
                SafePoint = HomeValleyLayout.Core.Position,
                Obstacles = obstacles,
                FindHostileNear = FindLowThreatHostileNear,
                ResolveHostile = ResolveLowThreatHostile,
                Site = _combat,
                HostileUnit = id => _combat != null && _combat.TryGetEnemyUnit(id, out int u) ? u : 0,
                AttackRange = HomeValleyCombatTargets.EngageRange,
                AttackCooldownSeconds = 1.2f,
            };
            SquadCommands.Bind(_squadCtx);
        }

        /// <summary>ER5-CMD-01 下令前置："换目标"式的既有纪律——下一条军事命令视同玩家显式取消
        /// 当前在办工作单（同右键取消/CommandWork 换目标同一条规矩），不能一边被战略命令牵着走、
        /// 一边工作单状态机还在推进同一台机器。</summary>
        private void CancelActiveWorkOrderIfAny(int logicId)
        {
            CampaignState state = CampaignSession.Current;
            HomeValleyMachineMarker marker = FindMarker(logicId);
            if (state == null || marker == null)
            {
                return;
            }
            WorkOrderRecord active = HomeValleyWorkOrders.FindActiveOrderForMachine(state, logicId);
            if (active == null)
            {
                return;
            }
            Vector3 pos = marker.Position3;
            HomeValleyWorkOrders.CancelOrder(state, active.WorkOrderId, new Vector2(pos.x, pos.z));
        }

        /// <summary>归还谷地目前只有唯一一个低威胁残骸靶可当 Attack 目标——武装攻击点击/命中判定
        /// 复用同一份 <see cref="HomeValleyCombatTargets"/> 数据，不重复定义"敌方"概念。</summary>
        private RegionHostileInfo? FindLowThreatHostileNear(Vector2 worldPoint, float pickRadius)
        {
            CampaignState state = CampaignSession.Current;
            CombatTargetRecord target = HomeValleyCombatTargets.Find(state, HomeValleyCombatTargets.LowThreatTargetId);
            if (target == null || target.Health <= 0f || Vector2.Distance(worldPoint, target.Position) > pickRadius)
            {
                return null;
            }
            return new RegionHostileInfo(target.TargetId, target.Position, target.Health > 0f);
        }

        private RegionHostileInfo? ResolveLowThreatHostile(string hostileId)
        {
            CampaignState state = CampaignSession.Current;
            CombatTargetRecord target = HomeValleyCombatTargets.Find(state, hostileId);
            return target == null ? (RegionHostileInfo?)null : new RegionHostileInfo(target.TargetId, target.Position, target.Health > 0f);
        }

        // ── 机器选择 / 点选移动 / 到点自动干活 / WASD 接管 ────────────────────

        /// <summary>左键点机器＝选中；已选中时左键点建筑/残骸＝下令走过去、到点自动
        /// 修复/拆解；点空地＝纯移动。这是归还谷地范围内的最小 RTS 式"选中+下令"，M 键切 Direct
        /// 后改由 <see cref="HandleDirectControl"/> 的 WASD 接管——两者互斥：本方法走
        /// <see cref="InputScope.Strategy"/>，Direct/Transition 期间 <see cref="InputRouter"/>
        /// 天然读不到，不需要在这里另判一次镜头模式。</summary>
        private void HandleSelectionClick()
        {
            if (_camera == null)
            {
                return;
            }

            // 右键＝取消选中机器当前的在办工作单（STORY-EXECUTION-CARDS.md #ER3-WRK-01 要求的
            // "取消"矩阵列在本 Story 唯一的真实触发入口——正式取消按钮留 ER5-INT-01/UI-04）。
            // 取消不清空选中/不影响移动指令本身，机器停在原地等待下一次点选下令。
            // FG0-UX-01：同一次右键已用来取消“武装待命”时，不再顺带取消机器的在办工单。
            if (_selected != null && !SquadCommands.ConsumedSecondaryThisFrame && InputRouter.GetMouseButtonDown(1, InputScope.Strategy))
            {
                CampaignState cancelState = CampaignSession.Current;
                WorkOrderRecord active = cancelState != null
                    ? HomeValleyWorkOrders.FindActiveOrderForMachine(cancelState, _selected.LogicId)
                    : null;
                if (active != null)
                {
                    Vector3 pos = _selected.Position3;
                    HomeValleyWorkOrders.CancelOrder(cancelState, active.WorkOrderId, new Vector2(pos.x, pos.z));
                    _selected.CancelCommandMove();
                    Log.Info($"[HomeValleyController] 已取消 {_selected.LogicId} 的在办订单 {active.WorkOrderId}。");
                }
                return;
            }

            // ER5-CMD-01：框选/武装命令确认点击由 SquadCommands.Tick（本帧更早跑过）先处理；
            // 真发生了框选或确认了武装命令，这次释放就不该再落到下面的单点选中/工作下令分支
            // （否则拖框松手的位置会被当成移动/修复目标点）。触发点从 GetMouseButtonDown 改成
            // GetMouseButtonUp——与 SquadCommands 内部判断"点击还是拖拽"用的同一次抬起事件同步，
            // 纯点击（未发生拖动）时对时序/结果没有可感知影响，逐字保留原有分支顺序。
            if (SquadCommands.ConsumedClickThisFrame)
            {
                return;
            }

            if (!InputRouter.GetMouseButtonUp(0, InputScope.Strategy))
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
                SquadCommands.SelectSingle(marker.LogicId); // 单点选中同步进战略命令的选择集。
                return;
            }

            // ER4-FAC-01：装配站是管理面板入口，不是工作单目标——独立于机器选中状态拦截在最前面，
            // 不落到下面"_selected == null 就忽略"或"Operational 非核心建筑=拆除"的通用建筑路由。
            string earlyBuildingTypeId = BuildingTypeIdFromHit(hit);
            if (earlyBuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation)
            {
                _factoryPanelOpen = !_factoryPanelOpen;
                return;
            }

            // ER5-EXP-01：信号塔已修复（Operational）时点击切换远征准备面板，不落到下面的
            // "Operational 非核心建筑=拆除"通用分流——把信号塔拆掉会让已解锁的破碎都市重新变得
            // 不可达，明显不是玩家点它的意图。Damaged 时不拦截，落到下面正常的"修复"下令路径。
            if (earlyBuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
            {
                CampaignState towerState = CampaignSession.Current;
                BuildingRecord tower = towerState?.BuildingRecords?.FirstOrDefault(b =>
                    b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);
                if (tower != null && tower.ConstructionState == BuildingConstructionState.Operational)
                {
                    _expeditionPrepPanelOpen = !_expeditionPrepPanelOpen;
                    return;
                }
            }

            // ER6-ANA-01：解析台已修复（Operational）时点击切换解析面板，同信号塔先例——不落到下面
            // "Operational 非核心建筑=拆除"通用分流，把解析台拆掉会让已带回但未解析的关键模块永久卡死
            // （没有第二个解析入口），明显不是玩家点它的意图。
            if (earlyBuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench)
            {
                CampaignState benchState = CampaignSession.Current;
                BuildingRecord bench = benchState?.BuildingRecords?.FirstOrDefault(b =>
                    b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench);
                if (bench != null && bench.ConstructionState == BuildingConstructionState.Operational)
                {
                    _analysisPanelOpen = !_analysisPanelOpen;
                    return;
                }
            }

            if (_selected == null)
            {
                return;
            }

            HomeValleyMachineMarker moving = _selected;
            Vector3 destination = hit.point;
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            // ER5-CMD-01：接下来的所有分支都会让"工作"移动系统接管这台机器的 Transform
            // （CommandWork/CommandHaul/纯移动 fallback）；先取消它可能正在执行的战略命令，
            // 避免两套移动来源同一帧争抢同一个 transform.position。
            SquadCommands.CancelCommandFor(moving.LogicId);

            // ER4-FAC-01：机器收到的第一条真实命令即视为"驶出工厂"——占用出口的完工机器只有在玩家
            // 真正开始使用它之后才让位，让下一项排队机器有机会生成，见 HomeValleyFactory 类注释。
            if (MachineRegistry.TryGetRecord(moving.LogicId, out MachineRecord movingRecord) && movingRecord.IsInFactory)
            {
                HomeValleyFactory.ReleaseFromFactory(moving.LogicId);
            }

            // FG0-ARCH-04：点中的是哪一座（本地键 → 建筑 ID → 记录），而不是“这一类的第一座”。
            BuildingRecord clicked = earlyBuildingTypeId != null
                ? state.BuildingRecords?.FirstOrDefault(b => b.BuildingId == HomeValleyLayout.RegionId + ":" + earlyBuildingTypeId)
                : null;
            string buildingTypeId = clicked?.BuildingTypeId ?? earlyBuildingTypeId;
            if (buildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                CommandWork(moving, destination, "recharge:" + moving.LogicId,
                    () => HomeValleyWorkOrders.TryCreateRecharge(state, moving.LogicId), "充电");
                return;
            }
            if (buildingTypeId != null)
            {
                // ER3-SOFTLOCK-01 AC-ECO-012：同一次点击按建筑当前状态分流——Damaged 只能修复
                // （原有行为不变），Operational 的非核心建筑改为拆除（50%返还实际投入）。两种状态
                // 互斥，不需要额外输入手势区分"想修复"还是"想拆除"。Core 已在上面分流去 Recharge，
                // 不会走到这里。
                BuildingRecord targetBuilding = clicked;
                if (targetBuilding != null && targetBuilding.ConstructionState == BuildingConstructionState.Operational)
                {
                    CommandWork(moving, destination, targetBuilding.BuildingId,
                        () => HomeValleyWorkOrders.TryCreateDemolishBuilding(state, targetBuilding.BuildingId, moving.LogicId), "拆除 " + buildingTypeId);
                    return;
                }

                CommandWork(moving, destination, HomeValleyLayout.RegionId + ":" + buildingTypeId,
                    () => HomeValleyWorkOrders.TryCreateRepair(state, buildingTypeId, moving.LogicId), "修复 " + buildingTypeId);
                return;
            }

            string buildSiteId = BuildSiteIdFromHit(hit);
            if (buildSiteId != null)
            {
                // FG0-ARCH-04：建造位是开局布局里的“建议位置”，放置经格网校验（HomeValleyWorkOrders.TryCreateBuild → HomeGridService.TryPlace）。
                GridContent.TryGetLayout(buildSiteId, out GameConfig.fg.StartLayout siteRow);
                CommandWork(moving, destination, HomeValleyLayout.RegionId + ":" + (siteRow != null ? siteRow.TypeId : buildSiteId),
                    () => HomeValleyWorkOrders.TryCreateBuild(state, buildSiteId, moving.LogicId), "建造 " + buildSiteId);
                return;
            }

            string wreckageNodeId = WreckageNodeIdFromHit(hit);
            if (wreckageNodeId != null)
            {
                CommandWork(moving, destination, wreckageNodeId,
                    () => HomeValleyWorkOrders.TryCreateSalvage(state, wreckageNodeId, moving.LogicId), "拆解 " + wreckageNodeId);
                return;
            }

            string groundItemId = GroundItemIdFromHit(hit);
            if (groundItemId != null)
            {
                CommandHaul(moving, destination, groundItemId, state);
                return;
            }

            moving.CommandMoveTo(destination);
        }

        /// <summary>"换目标"守卫：点选新工作前，若该机器已经在办一个指向**不同**目标的订单（还在
        /// 赶路或正在工作），先按玩家取消处理——否则旧订单会被静默丢弃在 Reserved/InProgress，货舱/
        /// 已扣资源永久悬空（ER3-STO-01"100次...换目标压力测试"同一类真实 bug，这里同样要守）。
        /// 点的是**同一个**目标（重复点同一栋楼/同一残骸）时刻意不取消——那种场景要落到各 TryCreateX
        /// 内部"按 TargetId 复用/拒绝已在办"的既有逻辑，不能在这里先取消再重新创建导致重复扣款。</summary>
        private static void EnsureMachineFree(HomeValleyMachineMarker moving, CampaignState state, string newTargetId)
        {
            WorkOrderRecord active = HomeValleyWorkOrders.FindActiveOrderForMachine(state, moving.LogicId);
            if (active == null || active.TargetId == newTargetId)
            {
                return;
            }
            Vector3 pos = moving.Position3;
            HomeValleyWorkOrders.CancelOrder(state, active.WorkOrderId, new Vector2(pos.x, pos.z));
        }

        /// <summary>Repair/Build/Salvage/Recharge 共用的下令模板：点击那一刻立即创建/复用订单（真正
        /// 扣款/校验在这里发生，不是到达后才发生——见 <see cref="HomeValleyWorkOrders"/> 类注释
        /// "订单何时创建"），成功才真正下达移动指令；失败只记日志，机器原地不动、不白跑一趟。
        /// <paramref name="prospectiveTargetId"/> 用于 <see cref="EnsureMachineFree"/> 判断是否真的
        /// "换了目标"——必须与对应 TryCreateX 内部登记的 TargetId 完全一致（Repair/Build 传建筑
        /// BuildingId，Salvage 传 nodeId，Recharge 传 "recharge:"+LogicId），否则会误判成换目标。</summary>
        private static void CommandWork(HomeValleyMachineMarker moving, Vector3 destination, string prospectiveTargetId,
            Func<HomeValleyWorkOrders.WorkOrderOpResult> create, string label)
        {
            CampaignState state = CampaignSession.Current;
            EnsureMachineFree(moving, state, prospectiveTargetId);
            HomeValleyWorkOrders.WorkOrderOpResult r = create();
            if (!r.Success)
            {
                Log.Warning($"[HomeValleyController] 下令{label}失败：{r.FailureReason}");
                // ER8-NEG-01：此前只写日志，玩家点了没有任何反应。
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, HomeValleyWorkOrders.DescribeCommandFailure(r.FailureReason));
                return;
            }
            WorkOrderRecord order = HomeValleyWorkOrders.Find(state, r.WorkOrderId);
            if (order != null)
            {
                BeginMovementForOrder(moving, destination, order);
            }
        }

        /// <summary>Haul 专属下令：先创建订单，再走与其它四类共用的 <see cref="BeginMovementForOrder"/>
        /// （两腿——拾取/交付——的分支就在那个方法内部，按 <see cref="WorkOrderKind.Haul"/> 判断）。</summary>
        private static void CommandHaul(HomeValleyMachineMarker moving, Vector3 destination, string groundItemId, CampaignState state)
        {
            EnsureMachineFree(moving, state, groundItemId);
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateHaul(state, groundItemId, moving.LogicId);
            if (!r.Success)
            {
                Log.Warning($"[HomeValleyController] 下令搬运 {groundItemId} 失败：{r.FailureReason}");
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, HomeValleyWorkOrders.DescribeCommandFailure(r.FailureReason));
                return;
            }
            WorkOrderRecord order = HomeValleyWorkOrders.Find(state, r.WorkOrderId);
            if (order != null)
            {
                BeginMovementForOrder(moving, destination, order);
            }
        }

        /// <summary>五类工作单共用的"发起移动、到点触发对应回调"链——玩家点选下令
        /// （<see cref="CommandWork"/>/<see cref="CommandHaul"/>）与 ER3-WRK-02 自动分配
        /// （<see cref="BeginAutoAssignedMovement"/>）在订单已创建/已指派之后，都走这一条，
        /// 保证两种下令来源的移动/到达行为完全一致。Haul 是两腿（拾取→交付核心），其余四类
        /// 到点直接触发 <see cref="HomeValleyWorkOrders.OnArrivedAtWork"/>。</summary>
        private static void BeginMovementForOrder(HomeValleyMachineMarker moving, Vector3 destination, WorkOrderRecord order)
        {
            string workOrderId = order.WorkOrderId;

            if (order.Kind == WorkOrderKind.Haul)
            {
                moving.CommandMoveTo(destination, HaulSourceArrival(moving, workOrderId));
                return;
            }

            moving.CommandMoveTo(destination, WorkArrival(workOrderId));
        }

        // 到达回调（三种）：派工时挂上；读档后按订单状态重挂（ResumeWorkArrivals）。两处用同一组构造，行为一致。
        private static Action WorkArrival(string workOrderId) =>
            () => HomeValleyWorkOrders.OnArrivedAtWork(CampaignSession.Current, workOrderId);

        private static Action HaulDestinationArrival(string workOrderId) =>
            () => HomeValleyWorkOrders.OnArrivedAtHaulDestination(CampaignSession.Current, workOrderId);

        private static Action HaulSourceArrival(HomeValleyMachineMarker moving, string workOrderId) =>
            () =>
            {
                if (!HomeValleyWorkOrders.OnArrivedAtHaulSource(CampaignSession.Current, workOrderId))
                {
                    return;
                }
                Vector3 corePos = new Vector3(HomeValleyLayout.Core.Position.x, 1f, HomeValleyLayout.Core.Position.y);
                moving.CommandMoveTo(corePos, HaulDestinationArrival(workOrderId));
            };

        /// <summary>
        /// FG0-ARCH-03：读档恢复后，给“带到达回调下达、还在赶路”的机器按它在办的工作订单重挂回调（回调只在内存里，赶路命令随内核快照进存档）。
        /// 映射与派工时一致：搬运且订单仍是 Reserved = 去取货那一腿；搬运且 InProgress = 送回核心那一腿；其余四类 Reserved = 到点开工。
        /// 对不上的（订单已终结 / 等待中）不挂，交给停滞看门狗与分配引擎（与 Demo 相同的兜底）。O(家园机器数)，只在载入时一次。
        /// </summary>
        private static int ResumeWorkArrivals(CampaignState state, IEnumerable<HomeValleyMachineMarker> markers)
        {
            int resumed = 0;
            if (state == null)
            {
                return 0;
            }
            foreach (HomeValleyMachineMarker marker in markers)
            {
                if (marker == null || !marker.AwaitsArrivalAction)
                {
                    continue;
                }
                WorkOrderRecord order = HomeValleyWorkOrders.FindActiveOrderForMachine(state, marker.LogicId);
                Action action = null;
                if (order != null && order.Kind == WorkOrderKind.Haul)
                {
                    action = order.State == WorkOrderState.Reserved ? HaulSourceArrival(marker, order.WorkOrderId)
                        : order.State == WorkOrderState.InProgress ? HaulDestinationArrival(order.WorkOrderId)
                        : null;
                }
                else if (order != null && order.State == WorkOrderState.Reserved)
                {
                    action = WorkArrival(order.WorkOrderId);
                }
                if (action != null)
                {
                    marker.ResumeArrivalAction(action);
                    resumed++;
                }
            }
            return resumed;
        }

        /// <summary>最近一次载入时重挂的工作赶路回调数（自检读）。</summary>
        public int LastResumedWorkArrivals { get; private set; }

        private static string BuildingTypeIdFromHit(RaycastHit hit)
        {
            const string prefix = "Building_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private static string WreckageNodeIdFromHit(RaycastHit hit)
        {
            const string prefix = "Wreckage_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private static string BuildSiteIdFromHit(RaycastHit hit)
        {
            const string prefix = "BuildSite_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private static string GroundItemIdFromHit(RaycastHit hit)
        {
            const string prefix = "GroundItem_";
            string name = hit.collider.gameObject.name;
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : null;
        }

        private void DestroyVisuals()
        {
            if (_root != null)
            {
                GameLogic.View.UnityObjects.Release(_root);
                _root = null;
            }
            _objectiveMarker = null;
            // FG0-ARCH-03：机器逻辑句柄不随表现对象销毁（地点不被观察时模拟照常）；只断开画面。
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                marker?.DetachView();
            }
            _combat?.ClearViews();
            _buildingVisuals.Clear();
            _buildingBadges.Clear();
            _visualsRecordsRef = null;
            if (_squadCtx != null)
            {
                _squadCtx.VisualRoot = null;
            }
        }

        // ── 自检（ER2-SCENE-01 负向矩阵，供 execute_code / 自动化验收直接断言）───

        /// <summary>负向矩阵自检："第二次进入/读档重复对象"——建筑/机器记录数必须恰好等于
        /// 播种数量，多次 Enter/Exit 循环不产生重复条目。</summary>
        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            int buildingCount = CountRegionBuildings(state);
            if (buildingCount != 7)
            {
                violations.Add($"归还谷地建筑记录数应为 7，实际 {buildingCount}（疑似重复播种）。");
            }

            int erc001 = MachineRegistry.AllRecords.Count(r =>
                r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == HomeValleyLayout.Erc001ChassisId);
            int erc002 = MachineRegistry.AllRecords.Count(r =>
                r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == HomeValleyLayout.Erc002ChassisId);
            if (erc001 != 1)
            {
                violations.Add($"ERC-001 机器记录数应为 1，实际 {erc001}。");
            }
            if (erc002 != 1)
            {
                violations.Add($"ERC-002 机器记录数应为 1，实际 {erc002}。");
            }

            return violations;
        }
    }
}
