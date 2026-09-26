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
    /// <summary>ER5-REGION-01：破碎都市（第 1 次出击）空间/持久化流程，与 <see cref="HomeValleyController"/>
    /// 同一"director 之外的常驻子系统"处理方式，由 <see cref="GameLogic.Stage.GameRoot"/> 直接持有并驱动。
    ///
    /// ── 范围边界（本 Story 明确点名，非自行裁剪）──
    /// 真正的远征准备/出发确认/往返快照事务属于 ER5-EXP-01——本类的 <see cref="Enter"/> 只是它将要
    /// 调用的最小可用切场入口（调用方直接传要带上的机器 LogicId 列表），不做人数/装配/带宽校验、
    /// 不做冻结输入→自动存档→快照的完整编排。战略命令的"正式化"（编队/1-9调组/Ctrl保存组）属于
    /// ER5-CMD-01——本类复用 <see cref="HomeValleyMachineMarker.CommandMoveTo"/> 同款最小点选移动。
    /// 任意接管的"正式化"（TrySwitchControlledUnit 九类失败码/0.35s相机过渡）属于 ER5-CTL-01——本类
    /// 只保证"干扰场内新接管请求被拒绝、已接管机器2秒宽限后回弹"这条本 Story 独立点名的硬要求真实
    /// 可发生。E 交互正式输入链路属于 ER5-INT-01——本类把交互动作做成可直接调用的公开方法
    /// （<see cref="TryInteractListeningNode"/> 等），供未来 E 键调用，也供本 Story 自身的
    /// execute_code/Play Mode 验收直接调用。</summary>
    public sealed class FracturedCityController : IWorldSite
    {
        /// <summary>FG0-ARCH-01：本地点已载入、正在被世界模拟推进（与镜头在不在这里无关；派遣不再退出家园）。</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>已载入**并且**镜头正在观察这里（界面面板、输入据此判断）。模拟推进不看它（FGR-BASE-021）。</summary>
        public bool IsActive => IsLoaded && WorldView.IsObserved(SiteId);

        /// <summary>FG0-ARCH-01：暂停属于整个世界（<see cref="GameClock"/>）。</summary>
        public bool IsPaused => GameClock.Paused;
        /// <summary>ER5-RETURN-01：全灭检测结果只读暴露——<see cref="ExpeditionReturnService"/> 据此
        /// 区分"玩家主动撤离"与"全灭放弃远征"两条确认路径，不在服务类里另存一份判定。</summary>
        public bool IsWiped => _wipeResolved;
        /// <summary>ER5-RETURN-01：撤离确认面板开关——同 <see cref="HomeValleyController.IsExpeditionPrepPanelOpen"/>
        /// 先例，由到达撤离点的 E 交互打开，玩家在面板内确认或取消。</summary>
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
        /// <summary>FG0-ARCH-03：本地点的战斗内核（机器、敌人的逐单位逻辑都在里面）。</summary>
        private CombatSite _combat;
        private FracturedCityCombatRules _combatRules;
        private RegionSquadCommandContext _squadCtx;
        /// <summary>敌人表现对象（被观察时才有），按实例 ID。</summary>
        private readonly Dictionary<string, GameObject> _enemyViews = new Dictionary<string, GameObject>();
        public CombatSite Combat => _combat;
        private HomeValleyMachineMarker _selected;
        private HomeValleyMachineMarker _possessed;
        private bool _wipeResolved;
        /// <summary>FG0-ARCH-03：全灭检测里“记录侧还有存活机器”的缓存，按 <see cref="MachineRegistry.RosterRevision"/> 失效
        /// （名册没变的步不再逐台扫描记录，O(1)）。</summary>
        private int _wipeRosterRevision = int.MinValue;
        private bool _wipeRecordAlive;

        /// <summary>玩家/机器与固定物件的最小交互距离（"距离≤3米"，同 ER5-INT-01 卡片点名的数字，
        /// 本 Story 先用它做交互挂载点的距离判定，不等该 Story 落地才补）。</summary>
        public const float InteractRange = 3f;

        /// <summary>暂停 / 继续整个世界（FG0-ARCH-01：统一时钟）。</summary>
        public void SetPaused(bool paused) => GameClock.SetPaused(paused);

        /// <summary>ER5-CMD-01：与 <see cref="HomeValleyController.SquadCommands"/> 同一套引擎、
        /// 各自一份实例（两区域互斥运行，但各自的选择集/编组/命令状态不该跨区域串）。</summary>
        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();
        /// <summary>ER5-CTL-01：任意接管正式化，与 <see cref="HomeValleyController.Control"/> 同一
        /// 共享引擎的各自一份实例（干扰场/宽限恢复绑定见 <see cref="SetupControlSystem"/>）。</summary>
        public readonly RegionControlSystem Control = new RegionControlSystem();
        /// <summary>ER5-INT-01：Direct 的 E 交互唯一实现，与 <see cref="HomeValleyController.Interact"/>
        /// 共用同一引擎、各自一份实例。</summary>
        public readonly RegionInteractionSystem Interact = new RegionInteractionSystem();
        /// <summary>ER5-INT-01："指向"排序用——直控移动最近一次非零方向，同 HomeValleyController 先例。</summary>
        private Vector2 _lastFacing = new Vector2(1f, 0f);
        /// <summary>Attack 命令的接战距离——破碎都市锚点间距比归还谷地紧凑（10～15 量级），
        /// 沿用 <see cref="InteractRange"/> 的同一空间尺度加倍，不直接照抄归还谷地的 12 米。</summary>
        private const float AttackRange = 6f;

        /// <summary>ER5-SILENT-01：锚点净空障碍物列表，建一份供 <see cref="SquadCommands"/>/
        /// <see cref="Interact"/>/敌人 AI 视线判定共用（此前 SetupSquadCommands/SetupInteraction 各自
        /// 重建一份等价列表，这里合并为单一来源，行为不变）。</summary>
        private List<(Vector2 Position, float Radius)> _obstacles;

        /// <summary>ER5-SILENT-01：静默侦察机扫描脉冲的可视化到期时间（Unity 真实时间，不受战略
        /// 暂停/慢放影响——纯反馈动画，不是游戏状态），键为敌人实例 ID。</summary>
        private readonly Dictionary<string, float> _scanPulseExpireRealtime = new Dictionary<string, float>();

        /// <summary>最小可用切场入口：把 <paramref name="expeditionLogicIds"/> 指定的家园存活机器
        /// 转移进破碎都市（RegionId 改写+定位到入口安全区），要求区域已经 <see cref="RegionState.Available"/>
        /// 或以上（由 ER5-SIG-01 的 <see cref="HomeValleySignal.RecomputeUnlock"/> 真实判定，本类不重复
        /// 校验解锁条件，只读结果）。</summary>
        public void Enter(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            if (IsLoaded)
            {
                Log.Warning("[FracturedCityController] Enter 被重复调用，忽略（已处于激活状态）。");
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                Log.Error("[FracturedCityController] 没有活动战役（CampaignSession.Current 为空），拒绝进入破碎都市。");
                return;
            }

            FracturedCityRegion.EnsureRegionRecordSeeded(state);
            RegionRecord region = FracturedCityRegion.Find(state);
            if (region == null || region.State == RegionState.Locked)
            {
                Log.Error("[FracturedCityController] 破碎都市尚未解锁（信号塔未修复/ERC-003 未生产），拒绝进入。");
                return;
            }

            FracturedCityRegion.EnsureEnemiesSeeded(state);
            if (region.State == RegionState.Available)
            {
                region.State = RegionState.Active;
            }
            FracturedCityRegion.RecoveryLockerCheck(state);

            TransportMachinesIn(state, expeditionLogicIds);
            // ER5-EXP-01：区域卸载（HomeValleyController.Exit）无条件 MachineLoadoutRegistry.Clear()
            // （ER4-BLP-02 既定纪律："区域卸载与登记表解绑成对"），本类此前不会在载入时重新登记——
            // 出征机器进入破碎都市后会立刻查不到自己的装配，MachineLoadoutRegistry.Resolve 拒绝生成
            // 默认强力替身，战斗直接失效。这是一个真实存在的既有缺口，不是本 Story 顺手加的功能；
            // 补在这里（而不是调用方 ExpeditionDepartureService）是因为 ResumeFracturedCity（读档/
            // 暂离后继续，同样先经过一次 HomeValleyController 生命周期或进程重启）走的是同一个
            // Enter 方法，必须同样补上，不能只覆盖出发这一条路径。
            RegisterAllRegionMachineLoadouts(state);
            state.CurrentRegionId = FracturedCityLayout.RegionId;

            _obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (FracturedCityLayout.Anchor anchor in FracturedCityLayout.AllAnchors())
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

            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
            if (!saveResult.Success)
            {
                Log.Warning($"[FracturedCityController] ExpeditionDepartConfirm 自动存档未成功：" +
                    $"{saveResult.Outcome} {saveResult.Message}");
            }

            Log.Info($"[FracturedCityController] 已进入破碎都市（resume={resume}），机器 {_machineMarkers.Count} 台。");
        }

        private static void TransportMachinesIn(CampaignState state, IEnumerable<int> logicIds)
        {
            if (logicIds == null)
            {
                return;
            }
            Vector2 spawnBase = FracturedCityLayout.EntryEvac.Position;
            int i = 0;
            foreach (int logicId in logicIds)
            {
                if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord record) || !record.IsAlive)
                {
                    continue;
                }
                if (record.RegionId == FracturedCityLayout.RegionId)
                {
                    i++;
                    continue; // 已在本区域（读档恢复/重复调用场景），不重复定位打断玩家当前进度。
                }
                CombatSites.ExportMachine(logicId); // FG0-ARCH-03：热量 / 冷却随机器走，不因换地点清零。
                MachineRegistry.MoveToRegion(record, FracturedCityLayout.RegionId);
                Vector2 offset = new Vector2((i % 3 - 1) * 2.5f, -1f - (i / 3) * 2.5f);
                record.WorldPosition = spawnBase + offset;
                i++;
            }
        }

        /// <summary>ER5-EXP-01：与 <c>HomeValleyController.RegisterAllRegionMachineLoadouts</c> 同一份
        /// 逻辑，按当前区域全部存活机器补登记（不是仅出征名单——读档恢复/暂离后继续时，
        /// <paramref name="state"/> 里可能已有多批曾经出征过的机器，全部需要重新登记，不只是本次
        /// Enter 调用刚传入的那一批）。幂等：已登记的机器重复调用直接覆盖同一份数据，无副作用。</summary>
        private static void RegisterAllRegionMachineLoadouts(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != FracturedCityLayout.RegionId || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                CircuitOpResult result = MachineLoadoutRegistry.Register(state, m.LogicId, m.BlueprintId, m.BlueprintVersion);
                if (!result.Success)
                {
                    Log.Warning($"[FracturedCityController] 机器 {m.LogicId} 装配登记失败：{result.Message}");
                }
            }
        }

        // ── FG0-ARCH-01：世界地点（IWorldSite）──────────────────────────────────

        public string SiteId => FracturedCityLayout.RegionId;
        public WorldSurfaceKind SurfaceKind => WorldSurfaceKind.LegacyRegion;
        public WorldCameraProfile CameraProfile => _cameraProfile;
        public Vector2? LivePosition(int logicId) => FindMachineMarkerPosition(logicId);

        public int LiveMachineCount => _combat != null ? _combat.CountAliveMachines() : 0;

        /// <summary>“飞到远征地点”的落点：存活机器的中心（内核位置）；全灭时取镜头起始点。</summary>
        public Vector2 DefaultFocus =>
            _combat != null && _combat.TryMachineCentroid(out Vector2 c)
                ? c
                : FracturedCityLayout.CameraFocusStart;

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
                // FG0-ARCH-03（DEBT-FG0ARCH01-03）：不被观察时不保留任何表现对象（渲染器、碰撞体、材质都销毁），模拟照常在内核里跑。
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
                    RegionRecord region = FracturedCityRegion.Find(state);
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
            FracturedCityRegion.TickEnemies(state, dt); // 警戒值衰减（O(1)）。
            // FG0-ARCH-03：机器移动与编队命令、重炮散热、侦察 / 干扰机 AI、攻击、标记、兴趣点发现——全部在战斗内核的一步里（Burst）；
            // 需要玩法层结算的（敌人阵亡掉落、对机器的伤害、标记记录、发现）作为事件交回，每步有上限。
            _combat?.Step(dt, GameClock.GameSeconds);
            TickWipeDetection(state);
        }

        /// <summary>全灭检测：本区域曾经有过机器、当前全部阵亡时，自动结算 Lost（不自动弹出结算 UI/
        /// 强制退出场景——完整的失败回城结算属于 ER5-RETURN-01，本 Story 只保证数据层真相正确，
        /// 幂等，同一次全灭只结算一次。</summary>
        /// <summary>ER5-RETURN-01 实测发现的真实缺陷：原实现读 <c>state.MachineRecords</c>——那是
        /// <see cref="MachineRegistry.ExportToCampaignState"/>（存档前/显式调用）才会更新的克隆快照，
        /// 不是实时数据（同 ER5-SILENT-01 已记录的"血量字段陷阱"同一性质，这里是第二处真实踩坑，
        /// 后果更严重：全灭检测在没有任何存档动作发生的连续 Play 过程中会一直读到"全部存活"的过期
        /// 快照，永远侦测不到全灭，直到凑巧发生一次自动存档）。改读 <see cref="MachineRegistry.AllRecords"/>
        /// （权威实时来源，同 <c>SetupSquadCommands.IsEligible</c> 等既有用法一致）。</summary>
        private void TickWipeDetection(CampaignState state)
        {
            if (_wipeResolved || _machineMarkers.Count == 0)
            {
                return;
            }
            // FG0-ARCH-03：机器阵亡由记录驱动、内核同步（MachineRegistry.MachineDied），这里读内核的存活计数（O(1) 次 AOT 调用）。
            // 内核存活计数（AOT）+ 记录侧存活（只在机器名册版本号变化后重扫一次，平时 O(1)）。
            bool kernelAlive = _combat != null && _combat.CountAliveMachines() > 0;
            if (kernelAlive && _wipeRosterRevision != MachineRegistry.RosterRevision)
            {
                _wipeRosterRevision = MachineRegistry.RosterRevision;
                _wipeRecordAlive = MachineRegistry.AllRecords.Any(m => m != null && m.RegionId == FracturedCityLayout.RegionId && m.IsAlive);
            }
            bool anyAlive = kernelAlive && _wipeRecordAlive;
            if (anyAlive)
            {
                return;
            }
            FracturedCityRegion.ResolveExtraction(state, Array.Empty<int>());
            _wipeResolved = true;
            Log.Info("[FracturedCityController] 全灭：区域内所有机器阵亡，已结算携带中的关键物为 Lost。");
            // ER8-CONTENT-01 AC-AUD-001 失败：全灭结算的唯一一次性边沿（_wipeResolved）。
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionWiped, "携带中的关键物已遗失");
        }

        /// <summary>撤离结算：把当前存活于本区域的机器货物按 Recovered 处理并送回归还谷地。
        /// <paramref name="evacuateSuccess"/> 为 false 时只是"暂离/调试退出"，不结算、不移动机器——
        /// 供玩家中途保存退出后下次继续，不强行判定成功或失败。</summary>
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
                    // ER5-RETURN-01 实测发现的真实缺陷：同 TickWipeDetection，此前读 state.MachineRecords
                    // 是过期快照——若有机器在"最近一次自动存档之后、Exit 之前"这段时间阵亡（例如本 Story
                    // 起真实敌方 AI 能杀死玩家机器），旧代码仍会把它当存活者一起送回家园、错误地把它
                    // 携带的关键物标记 Recovered。改读 MachineRegistry.AllRecords（权威实时来源）。
                    List<int> survivors = MachineRegistry.AllRecords
                        .Where(m => m != null && m.RegionId == FracturedCityLayout.RegionId && m.IsAlive)
                        .Select(m => m.LogicId).ToList();
                    FracturedCityRegion.ResolveExtraction(state, survivors);
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

            if (state != null && evacuateSuccess && state.CurrentRegionId == FracturedCityLayout.RegionId)
            {
                // FG0-ARCH-01：远征结束，世界的“当前远征地点”回到家园（读档恢复按它决定观察哪里）。
                state.CurrentRegionId = HomeValleyLayout.RegionId;
            }
            DestroyVisuals();
            SquadCommands.ReleaseVisuals();
            // FG0-ARCH-03：远征结束（或整个世界卸载），本地点的战斗内核释放；存档里的快照随之删除（记录已写回）。
            CombatSites.Close(SiteId, state, dropRecord: true);
            _combat = null;
            _machineMarkers.Clear();
            // FG0-ARCH-01：镜头归全局镜头管理器；观察中的地点被卸载时它会自动回到家园（WorldView.FrameEnd）。
            _possessed = null;
            _selected = null;
            SquadCommands.Unbind();
            Control.Unbind();
            Interact.Unbind();
            IsLoaded = false;
            Log.Info($"[FracturedCityController] 已退出破碎都市（evacuateSuccess={evacuateSuccess}）。");
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
            _combat.ExportMarks(FracturedCityRegion.Find(state), GameClock.GameSeconds);
        }

        // ── 交互挂载点（供 ER5-INT-01 的正式 E 输入调用，本 Story 先暴露真实底层动作）───

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

        public FracturedCityRegion.ActionResult TryInteractListeningNode()
        {
            if (!TryFindNearbyMachine(FracturedCityLayout.ListeningNode.Position, out _))
            {
                return FracturedCityRegion.ActionResult.Fail("没有机器在监听节点交互范围内。");
            }
            return FracturedCityRegion.TryDestroyListeningNode(CampaignSession.Current);
        }

        public FracturedCityRegion.ActionResult TryInteractTerminal()
        {
            if (!TryFindNearbyMachine(FracturedCityLayout.Terminal.Position, out _))
            {
                return FracturedCityRegion.ActionResult.Fail("没有机器在终端交互范围内。");
            }
            return FracturedCityRegion.TryReadTerminal(CampaignSession.Current);
        }

        public FracturedCityRegion.ActionResult TryInteractCrate(string crateId)
        {
            FracturedCityLayout.Anchor anchor = crateId == FracturedCityLayout.Crate1Id ? FracturedCityLayout.Crate1
                : crateId == FracturedCityLayout.Crate2Id ? FracturedCityLayout.Crate2
                : FracturedCityLayout.Crate3;
            if (!TryFindNearbyMachine(anchor.Position, out _))
            {
                return FracturedCityRegion.ActionResult.Fail("没有机器在箱子交互范围内。");
            }
            return FracturedCityRegion.TryOpenCrate(CampaignSession.Current, crateId);
        }

        /// <summary>把地面上的关键物装进最近的存活机器货舱——本 Story 的"战利品装载"最小可用版本。</summary>
        public FracturedCityRegion.ActionResult TryCollectNearestQuestItem(string salvageInstanceId)
        {
            CampaignState state = CampaignSession.Current;
            RegionQuestItemRecord item = FracturedCityRegion.FindQuestItem(state, salvageInstanceId);
            if (item == null || item.State != RegionQuestItemState.OnGround)
            {
                return FracturedCityRegion.ActionResult.Fail("地面上没有该关键物。");
            }
            if (!TryFindNearbyMachine(item.Position, out int carrierLogicId))
            {
                return FracturedCityRegion.ActionResult.Fail("没有机器在关键物交互范围内。");
            }
            return FracturedCityRegion.TryCollectQuestItem(state, salvageInstanceId, carrierLogicId);
        }

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

        /// <summary>ER5-CTL-01：HUD 显示"编号/蓝图"用——当前受控机器 LogicId，没有接管返回 null。</summary>
        public int? PossessedMachineLogicId => _possessed != null ? _possessed.LogicId : (int?)null;

        /// <summary>同 <see cref="HomeValleyController.RequestDirectView"/> 先例：候选条 UI 点击后若镜头
        /// 尚未处于 Direct，补一次正式过渡请求。</summary>
        public bool RequestDirectView() => _cameraDirector != null && _cameraDirector.RequestDirect();

        // ── 选中/移动/直控（复用 HomeValleyMachineMarker 同款最小 RTS 手感）───────────

        /// <summary>ER5-CMD-01：本类原先"点空地＝CommandMoveTo"的最小点选移动（类注释点名要被本
        /// Story 取代）已移除——移动现在只能通过 <see cref="SquadCommands"/> 的正式 Move 命令下达。
        /// 点击行为因此只剩"选中"：框选/武装命令确认点击由 <see cref="SquadCommands"/> 先处理，
        /// 这里只处理单点选中一台机器。</summary>
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
                // ER5-CTL-01：破碎都市此前完全没有接线 Tab 循环——本 Story 补上，与归还谷地共用
                // 同一个 TrySwitchControlledUnit 入口。
                RegionControlSwitchResult tabResult = Control.TrySwitchControlledUnit(null);
                if (!tabResult.Success && tabResult.Failure != RegionControlFailure.AlreadyControlled)
                {
                    Log.Info($"[FracturedCityController] Tab 切换接管目标失败：{tabResult.PlayerText}");
                }
            }

            float x = 0f, z = 0f;
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

            // ER5-SILENT-01：破碎都市此前完全没有直控攻击入口（只有战略 Attack 命令）——验收卡"玩家
            // 策略/直控各打一场"需要这一条。同 HomeValleyController.TryDirectAttack 同一套鼠标瞄准
            // 手感（左键、Direct 域，两域互斥不会与 Strategy 点选冲突）。
            if (InputRouter.GetMouseButtonDown(0, InputScope.Direct) &&
                InputRouter.TryGetPointer(InputScope.Direct, out Vector3 aimPointer))
            {
                TryDirectAttackEnemy(aimPointer);
            }
        }

        /// <summary>直控攻击：鼠标屏幕位置反投影到地面（y=0）算出瞄准方向，交给
        /// <see cref="FracturedCityRegion.TryFindEnemyInAim"/> 做锥形+射程判定；命中后走
        /// <see cref="FracturedCityRegion.TryAttackEnemy"/> 唯一结算入口（与战略 Attack 命令共用同一
        /// 装配伤害出口，AC-REA-003"AI/玩家同装配"同一结构在破碎都市同样成立）。瞄准落空/没有装配
        /// 输出都是合法负向路径，只记日志不抛错。</summary>
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

            RegionEnemyRecord target = FracturedCityRegion.TryFindEnemyInAim(state, origin, aimDir);
            if (target == null)
            {
                Log.Info("[FracturedCityController] 直控攻击：瞄准方向/射程内没有可命中的敌人。");
                return;
            }

            FracturedCityRegion.TryAttackEnemy(state, _possessed.LogicId, target.EnemyInstanceId, state.RandomSeed,
                isAiSource: false);
        }

        // ── ER5-SILENT-01：敌人 AI 传感器数据 / 视线判定（复用交互系统同款障碍物遮挡算法）───


        /// <summary>CameraDirector 请求"给我一个直控目标"的钩子（M 键触发）。ER5-CTL-01 起校验/提交都
        /// 经 <see cref="Control"/>（含干扰场 SignalJammed 判定，见 <see cref="SetupControlSystem"/> 绑定的
        /// <see cref="FracturedCityRegion.IsPositionJammed"/>），本方法只负责"没有显式选中直接拒绝"这条
        /// 前置门槛，同 <see cref="HomeValleyController.EnsureDirectTarget"/> 先例。</summary>
        private bool EnsureDirectTarget()
        {
            if (_selected == null)
            {
                return false;
            }
            RegionControlSwitchResult result = Control.TrySwitchControlledUnit(_selected.LogicId);
            if (!result.Success)
            {
                Log.Info($"[FracturedCityController] 接管请求被拒绝：{result.PlayerText}");
            }
            return result.Success;
        }

        /// <summary>ER5-CTL-01：<see cref="Control"/> 接管成功提交后的收尾——同 HomeValleyController
        /// 镜像先例（本区域没有工作单概念，不需要 OnMachinePossessed）。</summary>
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

        /// <summary>把破碎都市的具体绑定接进共享的 <see cref="RegionControlSystem"/>——干扰场判定复用
        /// ER5-REGION-01 既有的 <see cref="FracturedCityRegion.IsPositionJammed"/>（不新造第二个判据），
        /// 2 秒宽限沿用 <see cref="FracturedCityLayout.ControlJamGraceSeconds"/>。</summary>
        private void SetupControlSystem()
        {
            Control.Bind(new RegionControlContext
            {
                RegionId = FracturedCityLayout.RegionId,
                Markers = _machineMarkers,
                GetPossessed = () => _possessed,
                SetPossessed = SetPossessedMarker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = pos => FracturedCityRegion.IsPositionJammed(CampaignSession.Current, pos),
                SquadCommands = SquadCommands,
                OnPossessCommitted = OnControlPossessCommitted,
                OnReleased = null,
                FallbackAnchor = () => FracturedCityLayout.EntryEvac.Position,
                JamGraceSeconds = FracturedCityLayout.ControlJamGraceSeconds,
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

        // ── ER5-INT-01：E 交互（残骸拆解=监听节点/战利品装载/终端读取/撤离确认占位）─────

        /// <summary>可达性判定复用 <see cref="SetupSquadCommands"/> 同一份锚点净空障碍列表。</summary>
        private void SetupInteraction()
        {
            Interact.Bind(new RegionInteractContext
            {
                GetPossessed = () => _possessed,
                GateCheck = () =>
                {
                    // DEBT-ER5INT01-03：RegionInteractFailure.MachineLostControl 枚举值已定义，但
                    // "控制彻底丢失"这一刻 RegionControlSystem 已经把 Controller._possessed 写回 null
                    // （SignalLost→SetPossessed(null)），本类 Tick() 顶部的 possessed==null 判定先一步
                    // 短路返回 NoControlledUnit，GateCheck 这里永远轮不到——两者对玩家的可见效果完全一致
                    // （交互整块消失），只是失败码归类不同，不是漏做。干扰宽限期内（Suspended）刻意不拦截：
                    // 直控移动本来就不检查 Availability，监听节点/终端等目标恰好全部落在节点自己的干扰
                    // 半径内，若在这里拦，"按住 E 摧毁节点解除干扰"这条唯一解法会在 2 秒宽限内被自己先
                    // 拦掉，变成打不开的死锁。
                    return InputRouter.ModalUiOpen ? RegionInteractFailure.ModalBlocked : RegionInteractFailure.None;
                },
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
            RegionRecord region = FracturedCityRegion.Find(state);

            bool nodeDestroyed = region?.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId);
            if (!nodeDestroyed)
            {
                list.Add(new RegionInteractCandidate
                {
                    Id = "node:" + FracturedCityLayout.ListeningNodeId,
                    Category = RegionInteractCategory.WreckageSalvage,
                    Position = FracturedCityLayout.ListeningNode.Position,
                    // 摧毁节点必须站在它自己的干扰半径内（InteractRange 3米 < JammerRadius 12米，无法
                    // 从场外够到）——按住时长必须留在 FracturedCityLayout.ControlJamGraceSeconds（2秒）
                    // 宽限之内，否则直控在拆到一半时先一步被判 Suspended，永远够不到"摧毁后干扰解除"
                    // 这个唯一解法，等于把自己的干扰机制锁死成无解。
                    HoldSeconds = 1.5f,
                    Priority = 20,
                    ActionVerb = "拆解",
                    Validate = () =>
                    {
                        RegionRecord r = FracturedCityRegion.Find(CampaignSession.Current);
                        return r?.DestroyedNodeIds != null && r.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId)
                            ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "监听节点已被摧毁。")
                            : RegionInteractResult.Ok();
                    },
                    Complete = () => ToInteractResult(TryInteractListeningNode(), "监听节点已摧毁：干扰区消失，标记器模块已掉落。"),
                });
            }

            bool terminalLooted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(FracturedCityLayout.TerminalId);
            if (!terminalLooted)
            {
                list.Add(new RegionInteractCandidate
                {
                    Id = "terminal:" + FracturedCityLayout.TerminalId,
                    Category = RegionInteractCategory.TerminalRead,
                    Position = FracturedCityLayout.Terminal.Position,
                    // 终端（0,10）与监听节点（0,4）相距6米，节点摧毁前同样落在12米干扰半径内——
                    // 同上一条注释理由，按住时长必须留在2秒宽限之内。
                    HoldSeconds = 1.5f,
                    Priority = 15,
                    ActionVerb = "读取",
                    Validate = () =>
                    {
                        RegionRecord r = FracturedCityRegion.Find(CampaignSession.Current);
                        return r?.LootedContainerIds != null && r.LootedContainerIds.Contains(FracturedCityLayout.TerminalId)
                            ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "终端已读取过。")
                            : RegionInteractResult.Ok();
                    },
                    Complete = () => ToInteractResult(TryInteractTerminal(), "协议终端已读取：协议数据盒已产出。"),
                });
            }

            AddCrateCandidate(list, region, FracturedCityLayout.Crate1Id, FracturedCityLayout.Crate1.Position);
            AddCrateCandidate(list, region, FracturedCityLayout.Crate2Id, FracturedCityLayout.Crate2.Position);
            AddCrateCandidate(list, region, FracturedCityLayout.Crate3Id, FracturedCityLayout.Crate3.Position);

            if (state.RegionQuestItems != null)
            {
                foreach (RegionQuestItemRecord item in state.RegionQuestItems)
                {
                    if (item.RegionId != FracturedCityLayout.RegionId || item.State != RegionQuestItemState.OnGround)
                    {
                        continue;
                    }
                    string salvageInstanceId = item.SalvageInstanceId;
                    list.Add(new RegionInteractCandidate
                    {
                        Id = "quest:" + salvageInstanceId,
                        Category = RegionInteractCategory.LootLoad,
                        Position = item.Position,
                        HoldSeconds = 0.5f,
                        Priority = 25, // 关键物优先于普通废料装载。
                        ActionVerb = "装载",
                        Validate = () =>
                        {
                            RegionQuestItemRecord q = FracturedCityRegion.FindQuestItem(CampaignSession.Current, salvageInstanceId);
                            return q == null || q.State != RegionQuestItemState.OnGround
                                ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "地面上没有该关键物。")
                                : RegionInteractResult.Ok();
                        },
                        Complete = () => ToInteractResult(TryCollectNearestQuestItem(salvageInstanceId), "关键物已装入货舱。"),
                    });
                }
            }

            if (state.GroundItems != null)
            {
                foreach (GroundItemRecord item in state.GroundItems)
                {
                    if (item.RegionId != FracturedCityLayout.RegionId)
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

            // ER5-RETURN-01：撤离确认——按住 E 只打开确认面板（已上车/遗留货物/幸存阵亡/未完成目标/
            // 关键技术是否仍不可解析，见 ExpeditionReturnPanelUIToolkit），真正的回城结算只在玩家在
            // 面板内点击"确认撤离"后由 ExpeditionReturnService.TryConfirmEvacuation 执行——"确认后才
            // 进入回城事务"（验收卡第1条字面要求），按住 E 本身不产生任何不可逆效果，可随时取消。
            if (!IsEvacPanelOpen)
            {
                list.Add(new RegionInteractCandidate
                {
                    Id = "evac:" + FracturedCityLayout.EntryEvacId,
                    Category = RegionInteractCategory.EvacConfirm,
                    Position = FracturedCityLayout.EntryEvac.Position,
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
                    RegionRecord r = FracturedCityRegion.Find(CampaignSession.Current);
                    return r?.LootedContainerIds != null && r.LootedContainerIds.Contains(crateId)
                        ? RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "该箱子已打开过。")
                        : RegionInteractResult.Ok();
                },
                Complete = () => ToInteractResult(TryInteractCrate(crateId),
                    $"箱子已打开：{FracturedCityLayout.CrateScrapAmount} 废料已落地。"),
            });
        }

        /// <summary>ER5-INT-01 战利品装载——通用地面物两阶段搬运票据一次性走完（同
        /// <see cref="HomeValleyController.TryCollectWreckageDrop"/> 先例，泛化到任意本区域地面物）。</summary>
        private RegionInteractResult CompleteLootLoad(string groundItemId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return RegionInteractResult.Fail(RegionInteractFailure.NoControlledUnit, "没有活动的破碎都市会话。");
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

        private static RegionInteractResult ToInteractResult(FracturedCityRegion.ActionResult result, string successText)
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
                ArenaHalfExtent = FracturedCityLayout.CameraBoundsHalfExtentX,
                FollowOffset = new Vector3(0f, 40f, 0f),
                InitialStrategyOrthographicSize = FracturedCityLayout.CameraBoundsHalfExtentZ,
                InitialDirectOrthographicSize = 14f,
                StartFocus = FracturedCityLayout.CameraFocusStart,
                Background = new Color(0.08f, 0.06f, 0.07f),
            };
        }

        // ── ER5-CMD-01：战略命令（多选/编组/Move·Attack·Guard·Retreat）────────

        private void SetupSquadCommands()
        {
            _squadCtx = new RegionSquadCommandContext
            {
                Camera = _camera,
                Markers = _machineMarkers,
                VisualRoot = _root,
                IsDirectControlled = IsMachineDirectControlled,
                IsEligible = logicId => MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) &&
                    rec.IsAlive && rec.RegionId == FracturedCityLayout.RegionId,
                CancelWorkIfAny = null, // 破碎都市没有"工作单"这一层概念。
                SafePoint = FracturedCityLayout.EntryEvac.Position,
                Obstacles = _obstacles,
                FindHostileNear = FindEnemyHostileNear,
                ResolveHostile = ResolveEnemyHostile,
                Site = _combat,
                HostileUnit = id => _combat != null && _combat.TryGetEnemyUnit(id, out int u) ? u : 0,
                AttackRange = AttackRange,
                AttackCooldownSeconds = 1.2f,
            };
            SquadCommands.Bind(_squadCtx);
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

        // ── 可视化（占位几何体，同 HomeValleyController 手法）─────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[FracturedCityRoot]");
            _enemyBadges.Clear();

            BuildAnchorVisual(FracturedCityLayout.EntryEvac, PrimitiveType.Cylinder, new Color(0.2f, 0.7f, 0.9f, 0.5f), 0.05f, true);
            BuildAnchorVisual(FracturedCityLayout.RecoveryLocker, PrimitiveType.Cube, new Color(0.6f, 0.5f, 0.2f), 0.8f, false);
            BuildAnchorVisual(FracturedCityLayout.Terminal, PrimitiveType.Cube, new Color(0.2f, 0.5f, 0.8f), 0.9f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate1, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate1Id), 0.5f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate2, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate2Id), 0.5f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate3, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate3Id), 0.5f, false);
            BuildNodeVisual(state);
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

        private void BuildAnchorVisual(FracturedCityLayout.Anchor anchor, PrimitiveType shape, Color color, float height, bool isMarkerRing)
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
            RegionRecord region = FracturedCityRegion.Find(state);
            bool looted = region?.LootedContainerIds != null && region.LootedContainerIds.Contains(crateId);
            return looted ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.85f, 0.75f, 0.35f);
        }

        private void BuildNodeVisual(CampaignState state)
        {
            RegionRecord region = FracturedCityRegion.Find(state);
            bool destroyed = region?.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId);
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Poi_" + FracturedCityLayout.ListeningNodeId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(FracturedCityLayout.ListeningNode.Position.x, 1f, FracturedCityLayout.ListeningNode.Position.y);
            go.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ViewMaterials.Standard(destroyed ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.8f, 0.2f, 0.6f));

            // 干扰场半径可视化环（半透明，随节点摧毁一起消失于 SyncWorldVisuals）。
            GameObject field = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            field.name = "JamField";
            field.transform.SetParent(_root.transform, false);
            field.transform.position = new Vector3(FracturedCityLayout.ListeningNode.Position.x, 0.02f, FracturedCityLayout.ListeningNode.Position.y);
            field.transform.localScale = new Vector3(FracturedCityLayout.JammerRadius * 2f, 0.02f, FracturedCityLayout.JammerRadius * 2f);
            GameLogic.View.UnityObjects.Release(field.GetComponent<Collider>());
            Renderer fieldRenderer = field.GetComponent<Renderer>();
            fieldRenderer.sharedMaterial = ViewMaterials.Standard(new Color(0.6f, 0.1f, 0.5f, 0.15f));
            fieldRenderer.enabled = !destroyed;
        }

        private void BuildEnemyVisuals(CampaignState state)
        {
            if (state.RegionEnemies == null)
            {
                return;
            }
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != FracturedCityLayout.RegionId)
                {
                    continue;
                }
                Vector2 at = _combat != null && _combat.TryGetEnemyPosition(enemy.EnemyInstanceId, out Vector2 live) ? live : enemy.Position;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Enemy_" + enemy.EnemyInstanceId;
                go.transform.SetParent(_root.transform, false);
                go.transform.position = new Vector3(at.x, 1f, at.y);
                _enemyViews[enemy.EnemyInstanceId] = go;
                if (_combat != null && _combat.TryGetEnemyUnit(enemy.EnemyInstanceId, out int enemyUnit))
                {
                    _combat.BindView(enemyUnit, go.transform, 1f); // 位置每帧由内核插值写入（FrameRender）。
                }
                Renderer renderer = go.GetComponent<Renderer>();
                Color baseColor = enemy.EnemyTypeId == EnemyCatalog.JammerId
                    ? new Color(0.6f, 0.15f, 0.55f)
                    : new Color(0.75f, 0.35f, 0.15f);
                renderer.sharedMaterial = ViewMaterials.Standard(enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f));
                // AC-THEME-002：静默阵营细高“天线”剪影（胶囊只留作命中盒）。
                PlaceholderSilhouette.ApplyEnemy(go, renderer, enemy.EnemyTypeId);

                // ER8-CONTENT-01 AC-ACC-002：敌人头顶的类型标记（倒三角＝敌方，角标圆点/方块＝静默/铸造阵营），
                // 敌我不再只靠胶囊体颜色区分。胶囊体等比缩放，标记直接挂在它下面随之移动。
                WorldBadge enemyBadge = WorldBadge.Create(go.transform, "Badge", go.transform.position + Vector3.up * 1.9f, 1.1f);
                enemyBadge.SetIcon(EnemyBadgeIcon(enemy.EnemyTypeId));
                enemyBadge.SetVisible(enemy.IsAlive);
                _enemyBadges[enemy.EnemyInstanceId] = enemyBadge;

                // ER5-SILENT-01：侦察机的"扫描线"非听觉反馈——初始禁用，命中标记那一帧短暂显示
                // （见 TriggerScanPulse），满足 AC-ACC-002"失去听觉时有扫描线/边界/图标"。
                if (enemy.EnemyTypeId == EnemyCatalog.ScoutId)
                {
                    GameObject pulse = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    pulse.name = "ScanPulse_" + enemy.EnemyInstanceId;
                    pulse.transform.SetParent(_root.transform, false);
                    pulse.transform.position = new Vector3(enemy.Position.x, 0.03f, enemy.Position.y);
                    pulse.transform.localScale = new Vector3(FracturedCityLayout.ScoutMarkRange * 2f, 0.02f, FracturedCityLayout.ScoutMarkRange * 2f);
                    GameLogic.View.UnityObjects.Release(pulse.GetComponent<Collider>());
                    Renderer pulseRenderer = pulse.GetComponent<Renderer>();
                    pulseRenderer.sharedMaterial = ViewMaterials.Standard(new Color(0.95f, 0.85f, 0.2f, 0.35f));
                    pulseRenderer.enabled = false;
                }
            }
        }

        /// <summary>点亮一次侦察机扫描脉冲的可视反馈，<see cref="_scanPulseExpireRealtime"/> 记录到期时间，
        /// 由 <see cref="SyncWorldVisuals"/> 每帧检查并熄灭——纯表现层动画，不驱动任何游戏状态。</summary>
        private void TriggerScanPulse(string enemyInstanceId)
        {
            if (_root == null)
            {
                return;
            }
            Transform pulse = _root.transform.Find("ScanPulse_" + enemyInstanceId);
            if (pulse == null)
            {
                return;
            }
            Renderer renderer = pulse.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
            }
            _scanPulseExpireRealtime[enemyInstanceId] = Time.time + 0.6f;
        }

        private void BuildMachineView(HomeValleyMachineMarker marker, MachineRecord machine)
        {
            Vector2 at = marker.Position;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId + "_" + machine.LogicId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(at.x, 1f, at.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.7f, 0.85f, 0.75f); // 与归还谷地机器配色区分（略带绿，"出征中"）。
            renderer.sharedMaterial = ViewMaterials.Standard(baseColor);
            PlaceholderSilhouette.ApplyMachine(go, renderer, machine.ChassisId); // AC-THEME-002 车辆剪影。

            MachineView view = go.AddComponent<MachineView>();
            view.Initialize(renderer, baseColor);
            marker.AttachView(view);
            _combat?.BindView(marker.UnitId, go.transform, 1f);
        }

        /// <summary>FG0-ARCH-03：进场 / 读档时建立本地点的战斗内核：障碍、兴趣点、机器、敌人、标记。</summary>
        private void OpenCombat(CampaignState state, bool resume)
        {
            _combatRules = new FracturedCityCombatRules { OnScanPulse = TriggerScanPulse, OnEnemyVisualChanged = RefreshEnemyView };
            _combat = CombatSites.Open(SiteId, state, resume, _combatRules, out bool restored);
            _combat.Squad = SquadCommands;
            _combat.SetObstacles(CombatDemoContent.Obstacles(_obstacles));
            RegionRecord region = FracturedCityRegion.Find(state);
            if (!restored)
            {
                var pois = new List<float3>();
                var reached = new List<bool>();
                foreach (FracturedCityLayout.Anchor a in _combatRules.Pois)
                {
                    pois.Add(new float3(a.Position.x, a.Position.y, FracturedCityLayout.PoiDiscoveryRadius));
                    reached.Add(region?.DiscoveredNodes != null && region.DiscoveredNodes.Contains(a.Id));
                }
                _combat.SetPois(pois, reached);
            }
            SyncCombatMachines(state);
            CombatDemoContent.ReconcileEnemies(_combat, state, FracturedCityLayout.RegionId);
            if (!restored)
            {
                _combat.ImportMarks(region);
            }
            _combat.RefreshAllMachineWeapons(state);
        }

        /// <summary>本区域存活机器 ↔ 内核单位对账（进场、读档）；逻辑句柄按记录顺序排列。</summary>
        private void SyncCombatMachines(CampaignState state)
        {
            _machineMarkers.Clear();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != FracturedCityLayout.RegionId)
                {
                    continue;
                }
                HomeValleyMachineMarker marker = _combat.SpawnMachine(state, m, m.WorldPosition, autoEngage: false);
                if (marker != null)
                {
                    _machineMarkers.Add(marker);
                }
            }
            foreach (int stale in _combat.MachinesNotIn(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive && r.RegionId == FracturedCityLayout.RegionId))
            {
                _combat.RemoveMachine(stale, export: false);
            }
        }

        /// <summary>敌人阵亡等状态变化时刷新它的表现（事件驱动，不每帧扫全部敌人）。</summary>
        private void RefreshEnemyView(string enemyInstanceId)
        {
            CampaignState state = CampaignSession.Current;
            RegionEnemyRecord enemy = FracturedCityRegion.FindEnemy(state, enemyInstanceId);
            if (_root == null || enemy == null)
            {
                return;
            }
            Color baseColor = enemy.EnemyTypeId == EnemyCatalog.JammerId
                ? new Color(0.6f, 0.15f, 0.55f)
                : new Color(0.75f, 0.35f, 0.15f);
            RefreshColor("Enemy_" + enemy.EnemyInstanceId, enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f));
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
            RegionRecord region = FracturedCityRegion.Find(state);
            bool nodeDestroyed = region?.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId);

            RefreshColor("Poi_" + FracturedCityLayout.ListeningNodeId, nodeDestroyed ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.8f, 0.2f, 0.6f));
            Transform field = _root.transform.Find("JamField");
            if (field != null)
            {
                Renderer r = field.GetComponent<Renderer>();
                if (r != null)
                {
                    r.enabled = !nodeDestroyed;
                }
            }
            RefreshColor("Poi_" + FracturedCityLayout.Crate1Id, CrateColor(state, FracturedCityLayout.Crate1Id));
            RefreshColor("Poi_" + FracturedCityLayout.Crate2Id, CrateColor(state, FracturedCityLayout.Crate2Id));
            RefreshColor("Poi_" + FracturedCityLayout.Crate3Id, CrateColor(state, FracturedCityLayout.Crate3Id));
            RefreshColor("Poi_" + FracturedCityLayout.TerminalId,
                region?.LootedContainerIds != null && region.LootedContainerIds.Contains(FracturedCityLayout.TerminalId)
                    ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.2f, 0.5f, 0.8f));

            // FG0-ARCH-03：敌人位置由内核每帧插值写入（FrameRender）；颜色 / 头顶标记在阵亡事件时刷新（RefreshEnemyView），不每帧扫全部敌人。
            if (_scanPulseExpireRealtime.Count > 0)
            {
                List<string> expired = null;
                foreach (KeyValuePair<string, float> kv in _scanPulseExpireRealtime)
                {
                    Transform pulse = _root.transform.Find("ScanPulse_" + kv.Key);
                    if (Time.time < kv.Value)
                    {
                        // 脉冲显示期间跟随侦察机（O(显示中的脉冲)；位置真相在内核）。
                        if (pulse != null && _combat != null && _combat.TryGetEnemyPosition(kv.Key, out Vector2 pulseAt))
                        {
                            pulse.position = new Vector3(pulseAt.x, 0.03f, pulseAt.y);
                        }
                        continue;
                    }
                    if (pulse != null)
                    {
                        Renderer renderer = pulse.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                        }
                    }
                    (expired ??= new List<string>()).Add(kv.Key);
                }
                if (expired != null)
                {
                    foreach (string key in expired)
                    {
                        _scanPulseExpireRealtime.Remove(key);
                    }
                }
            }
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
            _enemyViews.Clear();
            _enemyBadges.Clear();
            _scanPulseExpireRealtime.Clear();
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
