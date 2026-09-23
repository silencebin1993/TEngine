using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
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
    public sealed class FoundryOutpostController
    {
        public bool IsActive { get; private set; }
        public bool IsPaused => _paused;
        public bool IsWiped => _wipeResolved;
        /// <summary>ER6-REGION-01：撤离确认面板开关——与 <see cref="FracturedCityController.IsEvacPanelOpen"/>
        /// 同一定位。</summary>
        public bool IsEvacPanelOpen { get; private set; }
        public void SetEvacPanelOpen(bool open) => IsEvacPanelOpen = open;

        private GameObject _root;
        private Camera _camera;
        private CameraDirector _cameraDirector;
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(5);
        private HomeValleyMachineMarker _selected;
        private HomeValleyMachineMarker _possessed;
        private bool _paused;
        private bool _wipeResolved;

        public const float InteractRange = 3f;

        public void SetPaused(bool paused) => _paused = paused;

        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();
        public readonly RegionControlSystem Control = new RegionControlSystem();
        public readonly RegionInteractionSystem Interact = new RegionInteractionSystem();
        private Vector2 _lastFacing = new Vector2(1f, 0f);
        private const float AttackRange = 7f;

        private List<(Vector2 Position, float Radius)> _obstacles;

        public void Enter(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            if (IsActive)
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

            BuildVisuals(state);
            SetupCameraDirector();
            SetupSquadCommands();
            SetupControlSystem();
            SetupInteraction();

            IsActive = true;
            _wipeResolved = false;

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
                record.RegionId = FoundryOutpostLayout.RegionId;
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

        public void Update(float dt)
        {
            if (!IsActive)
            {
                return;
            }

            InputRouter.SetGameplayPaused(_paused, strategic: true);
            _cameraDirector?.Tick(_paused);

            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                Control.ReleaseToStrategy();
            }

            bool directLocked = _cameraDirector != null && _cameraDirector.Mode == ViewMode.Direct;
            float scaledDt = _paused ? 0f : StrategyClock.GetScaledDt(dt, directLocked);

            SquadCommands.Tick(_paused, scaledDt);
            HandleSelectionClick();

            HandleDirectControl(scaledDt);
            Interact.Tick(scaledDt);

            if (_paused)
            {
                return;
            }

            TickMachineMovement(scaledDt);

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }

            FoundryOutpostRegion.RecomputeCoreGate(state);
            TickCoreZoneEntry(state);
            FoundryOutpostCoreBoss.Tick(state, scaledDt, BuildVisibleMachines(), IsLineOfSightClear);
            CannonCombat.TickHeatDissipation(state, scaledDt);
            if (_possessed != null)
            {
                RegionRecord region = FoundryOutpostRegion.Find(state);
                if (region != null)
                {
                    CampaignExposureLedger.TickDirectControlExposure(state, region, scaledDt);
                }
            }
            FoundryOutpostRegion.TickEnemies(state, scaledDt);
            FoundryOutpostEnemyAi.Tick(state, scaledDt, BuildVisibleMachines(), IsLineOfSightClear);
            Control.Tick(scaledDt);
            TickDiscovery(state);
            TickWipeDetection(state);
            SyncWorldVisuals(state);
        }

        /// <summary>ER7-CORE-01："进入前存安全档"——首次有任意区域机器越过核心分区封锁线（门已解锁，
        /// Boss 尚未初始化）时触发一次 <see cref="SaveReason.BossEngageEnter"/> 自动档，再
        /// <see cref="FoundryOutpostCoreBoss.EnsureInitialized"/>。不限直控，编队 Move 命令把机器带
        /// 进去同样触发——两条穿越路径（直控 WASD/编队 Move）都要能触发首次进场，不只认直控。</summary>
        private void TickCoreZoneEntry(CampaignState state)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !region.CoreGateUnlocked || FoundryOutpostCoreBoss.IsInitialized(region))
            {
                return;
            }
            bool anyCrossed = _machineMarkers.Any(m => m != null && m.transform.position.z > FoundryOutpostLayout.CoreGateBlockLineY);
            if (!anyCrossed)
            {
                return;
            }
            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.BossEngageEnter);
            if (!saveResult.Success)
            {
                Log.Warning($"[FoundryOutpostController] BossEngageEnter 自动存档未成功（{saveResult.Outcome} " +
                    $"{saveResult.Message}），仍继续初始化 Boss 战（不因存档失败卡住玩家，下次自然存档点会补上）。");
            }
            FoundryOutpostCoreBoss.EnsureInitialized(state, region);
        }

        private void TickDiscovery(CampaignState state)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null)
                {
                    continue;
                }
                Vector3 p = marker.transform.position;
                var pos2 = new Vector2(p.x, p.z);
                foreach (FoundryOutpostLayout.Anchor anchor in FoundryOutpostLayout.AllAnchors())
                {
                    FoundryOutpostRegion.TryDiscover(state, anchor.Id, pos2, anchor.Position);
                }
            }
        }

        private void TickWipeDetection(CampaignState state)
        {
            if (_wipeResolved || _machineMarkers.Count == 0)
            {
                return;
            }
            bool anyAlive = MachineRegistry.AllRecords
                .Any(m => m != null && m.RegionId == FoundryOutpostLayout.RegionId && m.IsAlive);
            if (anyAlive)
            {
                return;
            }
            FoundryOutpostRegion.ResolveExtraction(state, Array.Empty<int>());
            _wipeResolved = true;
            Log.Info("[FoundryOutpostController] 全灭：区域内所有机器阵亡，已结算携带中的关键物为 Lost。");
        }

        /// <summary>撤离结算数据层——同 <see cref="FracturedCityController.Exit"/> 在 ER5-RETURN-01 补 UI
        /// 之前的裸实现（ER6-REGION-01 将在此之上加撤离确认面板/封锁门三灯）。</summary>
        public void Exit(bool evacuateSuccess)
        {
            if (!IsActive)
            {
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
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
                            record.RegionId = HomeValleyLayout.RegionId;
                            record.WorldPosition = HomeValleyLayout.Core.Position + new Vector2(2f, -2f);
                        }
                    }
                }
            }

            DestroyVisuals();
            _cameraDirector?.Unbind();
            _cameraDirector = null;
            _possessed = null;
            _selected = null;
            _paused = false;
            SquadCommands.Unbind();
            Control.Unbind();
            Interact.Unbind();
            IsActive = false;
            Log.Info($"[FoundryOutpostController] 已退出铸造前哨外围（evacuateSuccess={evacuateSuccess}）。");
        }

        private void SyncLiveStateBackToRecords()
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null)
                {
                    continue;
                }
                Vector3 p = marker.transform.position;
                float health = MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord rec) ? rec.Health : 100f;
                MachineRegistry.SyncLiveState(marker.LogicId, new Vector2(p.x, p.z), health, null);
            }
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
                Vector3 p = marker.transform.position;
                if (Vector2.Distance(new Vector2(p.x, p.z), anchorPosition) <= InteractRange)
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

            HomeValleyMachineMarker marker = hit.collider.GetComponent<HomeValleyMachineMarker>();
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
            _possessed.DirectMove(new Vector3(x, 0f, z), dt);
            ClampPossessedAgainstCoreGate();
            ClampPossessedAgainstCoreLockout();

            if (InputRouter.GetMouseButtonDown(0, InputScope.Direct) &&
                InputRouter.TryGetPointer(InputScope.Direct, out Vector3 aimPointer))
            {
                TryDirectAttackEnemy(aimPointer);
            }
        }

        /// <summary>ER6-REGION-01："物理碰撞"这一重控制——直控 WASD 移动结束后，若门锁定且当前
        /// 受控机已越过核心分区封锁线，立即把它推回线内（同一帧修正，不产生可感知的"穿模一下"）。
        /// 与编队 Move 命令走 <see cref="ClampAgainstCoreGate"/> 裁剪目的地是两条独立路径——直控没有
        /// "目的地"概念（逐帧累积位移），只能在位移发生后做边界钳制，这正是与"导航阻挡"（钳制目的地）
        /// 刻意区分开的第二重控制，不是同一机制的重复实现。</summary>
        private void ClampPossessedAgainstCoreGate()
        {
            if (_possessed == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            RegionRecord region = state != null ? FoundryOutpostRegion.Find(state) : null;
            if (region == null || region.CoreGateUnlocked)
            {
                return;
            }
            Vector3 p = _possessed.transform.position;
            if (p.z > FoundryOutpostLayout.CoreGateBlockLineY)
            {
                _possessed.transform.position = new Vector3(p.x, p.y, FoundryOutpostLayout.CoreGateBlockLineY);
            }
        }

        /// <summary>ER7-CORE-01：Phase2 区域封锁的直控物理钳制——与 <see cref="ClampPossessedAgainstCoreGate"/>
        /// 方向相反的第二重控制（那条挡"进"，这条挡"出"），同一"每帧钳制"纪律。</summary>
        private void ClampPossessedAgainstCoreLockout()
        {
            if (_possessed == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            RegionRecord region = state != null ? FoundryOutpostRegion.Find(state) : null;
            if (region == null || !region.CoreLockoutActive)
            {
                return;
            }
            Vector3 p = _possessed.transform.position;
            if (p.z < FoundryOutpostLayout.CoreLockoutLineY)
            {
                _possessed.transform.position = new Vector3(p.x, p.y, FoundryOutpostLayout.CoreLockoutLineY);
            }
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

            Vector3 originV3 = _possessed.transform.position;
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
                isAiSource: false, attackerPosition: origin, isReachable: IsLineOfSightClear);
        }

        // ── 敌人 AI 传感器数据 / 视线判定 ─────────────────────────────────

        private List<FoundryOutpostEnemyAi.VisibleMachine> BuildVisibleMachines()
        {
            var list = new List<FoundryOutpostEnemyAi.VisibleMachine>(_machineMarkers.Count);
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker == null || !MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord rec) || !rec.IsAlive)
                {
                    continue;
                }
                Vector3 p = marker.transform.position;
                list.Add(new FoundryOutpostEnemyAi.VisibleMachine(marker.LogicId, new Vector2(p.x, p.z)));
            }
            return list;
        }

        private bool IsLineOfSightClear(Vector2 from, Vector2 to)
        {
            if (_obstacles == null)
            {
                return true;
            }
            foreach ((Vector2 Position, float Radius) obstacle in _obstacles)
            {
                if (Vector2.Distance(obstacle.Position, to) <= obstacle.Radius + 0.1f)
                {
                    continue;
                }
                if (Vector2.Distance(obstacle.Position, from) <= obstacle.Radius + 0.1f)
                {
                    continue;
                }
                if (SegmentIntersectsCircle(from, to, obstacle.Position, obstacle.Radius))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SegmentIntersectsCircle(Vector2 a, Vector2 b, Vector2 center, float radius)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 0.0001f)
            {
                return Vector2.Distance(a, center) <= radius;
            }
            float t = Mathf.Clamp01(Vector2.Dot(center - a, ab) / lenSq);
            Vector2 closest = a + ab * t;
            return Vector2.Distance(closest, center) <= radius;
        }

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
                SetPossessed = marker => _possessed = marker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = null, // 铸造前哨外围没有干扰机制（干扰是破碎都市专属）。
                SquadCommands = SquadCommands,
                OnPossessCommitted = OnControlPossessCommitted,
                OnReleased = null,
                FallbackAnchor = () => FoundryOutpostLayout.EntryEvac.Position,
                JamGraceSeconds = 2f,
            });
        }

        private bool TryGetPossessedAnchor(out float2 anchor)
        {
            if (_possessed != null)
            {
                Vector3 p = _possessed.transform.position;
                anchor = new float2(p.x, p.z);
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

        private void TickMachineMovement(float dt)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                marker.Tick(dt);
            }
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

        private void SetupCameraDirector()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                _camera = go.GetComponent<Camera>();
            }

            _camera.orthographic = true;
            _camera.orthographicSize = FoundryOutpostLayout.CameraBoundsHalfExtentZ;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.09f, 0.08f, 0.06f); // 铸造前哨：偏冷金属棕，与另两区域区分。
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _cameraDirector = new CameraDirector();
            var followOffset = new Vector3(0f, 40f, 0f);
            _cameraDirector.Bind(_camera, TryGetPossessedAnchor, followOffset,
                FoundryOutpostLayout.CameraBoundsHalfExtentX, startInStrategy: true, initialDirectOrthographicSize: 14f);
            _cameraDirector.FocusStrategyOn(new float2(FoundryOutpostLayout.CameraFocusStart.x, FoundryOutpostLayout.CameraFocusStart.y));
            _cameraDirector.EnsureDirectTarget = EnsureDirectTarget;
        }

        // ── 战略命令 ──────────────────────────────────────────────────────

        private void SetupSquadCommands()
        {
            SquadCommands.Bind(new RegionSquadCommandContext
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
                TryAttack = TrySquadAttackEnemy,
                AttackRange = AttackRange,
                AttackCooldownSeconds = 1.2f,
                ClampDestination = ClampAgainstCoreGate,
            });
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

        private RegionHostileInfo? FindEnemyHostileNear(Vector2 worldPoint, float pickRadius)
        {
            CampaignState state = CampaignSession.Current;
            if (state?.RegionEnemies == null)
            {
                return null;
            }
            RegionEnemyRecord best = null;
            float bestDist = pickRadius;
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != FoundryOutpostLayout.RegionId || !enemy.IsAlive)
                {
                    continue;
                }
                float dist = Vector2.Distance(worldPoint, enemy.Position);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    best = enemy;
                }
            }
            return best == null ? (RegionHostileInfo?)null : new RegionHostileInfo(best.EnemyInstanceId, best.Position, best.IsAlive);
        }

        private RegionHostileInfo? ResolveEnemyHostile(string hostileId)
        {
            RegionEnemyRecord enemy = FoundryOutpostRegion.FindEnemy(CampaignSession.Current, hostileId);
            return enemy == null ? (RegionHostileInfo?)null : new RegionHostileInfo(enemy.EnemyInstanceId, enemy.Position, enemy.IsAlive);
        }

        private RegionAttackOutcome TrySquadAttackEnemy(int attackerLogicId, string hostileId)
        {
            CampaignState state = CampaignSession.Current;
            MachineRecord attacker = MachineRegistry.TryGetRecord(attackerLogicId, out MachineRecord rec) ? rec : null;
            Vector2 attackerPos = attacker?.WorldPosition ?? Vector2.zero;
            FoundryOutpostRegion.ActionResult result =
                FoundryOutpostRegion.TryAttackEnemy(state, attackerLogicId, hostileId, state?.RandomSeed ?? 0,
                    isAiSource: false, attackerPosition: attackerPos, isReachable: IsLineOfSightClear);
            if (!result.Success)
            {
                return RegionAttackOutcome.Fail(result.FailureReason);
            }
            RegionEnemyRecord enemy = FoundryOutpostRegion.FindEnemy(state, hostileId);
            return RegionAttackOutcome.Ok(targetDestroyed: enemy == null || !enemy.IsAlive);
        }

        // ── 可视化（占位几何体，同 FracturedCityController 手法）───────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[FoundryOutpostRoot]");

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

            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m.IsAlive && m.RegionId == FoundryOutpostLayout.RegionId)
                {
                    BuildMachineVisual(m);
                }
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
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            }
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = color };
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
                if (enemy.RegionId != FoundryOutpostLayout.RegionId)
                {
                    continue;
                }
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Enemy_" + enemy.EnemyInstanceId;
                go.transform.SetParent(_root.transform, false);
                go.transform.position = new Vector3(enemy.Position.x, 1f, enemy.Position.y);
                Renderer renderer = go.GetComponent<Renderer>();
                Color baseColor = EnemyBaseColor(enemy.EnemyTypeId);
                renderer.material = new Material(Shader.Find("Standard")) { color = enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f) };
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

        private void BuildMachineVisual(MachineRecord machine)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId + "_" + machine.LogicId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(machine.WorldPosition.x, 1f, machine.WorldPosition.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.75f, 0.8f, 0.9f); // 与另两区域机器配色区分（偏冷灰蓝）。
            renderer.material = new Material(Shader.Find("Standard")) { color = baseColor };

            HomeValleyMachineMarker marker = go.AddComponent<HomeValleyMachineMarker>();
            marker.Initialize(machine.LogicId, machine.ChassisId, renderer, baseColor);
            _machineMarkers.Add(marker);
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

            if (state.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord enemy in state.RegionEnemies)
                {
                    if (enemy.RegionId != FoundryOutpostLayout.RegionId)
                    {
                        continue;
                    }
                    Color baseColor = EnemyBaseColor(enemy.EnemyTypeId);
                    RefreshColor("Enemy_" + enemy.EnemyInstanceId, enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f));

                    Transform enemyT = _root.transform.Find("Enemy_" + enemy.EnemyInstanceId);
                    if (enemyT != null)
                    {
                        enemyT.position = new Vector3(enemy.Position.x, 1f, enemy.Position.y);
                    }
                }
            }
        }

        private void RefreshColor(string childName, Color color)
        {
            Transform t = _root.transform.Find(childName);
            Renderer r = t != null ? t.GetComponent<Renderer>() : null;
            if (r != null)
            {
                r.material.color = color;
            }
        }

        private void DestroyVisuals()
        {
            _machineMarkers.Clear();
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
