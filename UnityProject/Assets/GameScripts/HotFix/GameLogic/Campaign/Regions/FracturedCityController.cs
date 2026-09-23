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
    public sealed class FracturedCityController
    {
        public bool IsActive { get; private set; }
        public bool IsPaused => _paused;

        private GameObject _root;
        private Camera _camera;
        private CameraDirector _cameraDirector;
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(5);
        private HomeValleyMachineMarker _selected;
        private HomeValleyMachineMarker _possessed;
        private bool _paused;
        private bool _wipeResolved;

        /// <summary>玩家/机器与固定物件的最小交互距离（"距离≤3米"，同 ER5-INT-01 卡片点名的数字，
        /// 本 Story 先用它做交互挂载点的距离判定，不等该 Story 落地才补）。</summary>
        public const float InteractRange = 3f;

        public void SetPaused(bool paused) => _paused = paused;

        /// <summary>ER5-CMD-01：与 <see cref="HomeValleyController.SquadCommands"/> 同一套引擎、
        /// 各自一份实例（两区域互斥运行，但各自的选择集/编组/命令状态不该跨区域串）。</summary>
        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();
        /// <summary>ER5-CTL-01：任意接管正式化，与 <see cref="HomeValleyController.Control"/> 同一
        /// 共享引擎的各自一份实例（干扰场/宽限恢复绑定见 <see cref="SetupControlSystem"/>）。</summary>
        public readonly RegionControlSystem Control = new RegionControlSystem();
        /// <summary>Attack 命令的接战距离——破碎都市锚点间距比归还谷地紧凑（10～15 量级），
        /// 沿用 <see cref="InteractRange"/> 的同一空间尺度加倍，不直接照抄归还谷地的 12 米。</summary>
        private const float AttackRange = 6f;

        /// <summary>最小可用切场入口：把 <paramref name="expeditionLogicIds"/> 指定的家园存活机器
        /// 转移进破碎都市（RegionId 改写+定位到入口安全区），要求区域已经 <see cref="RegionState.Available"/>
        /// 或以上（由 ER5-SIG-01 的 <see cref="HomeValleySignal.RecomputeUnlock"/> 真实判定，本类不重复
        /// 校验解锁条件，只读结果）。</summary>
        public void Enter(IEnumerable<int> expeditionLogicIds, bool resume)
        {
            if (IsActive)
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

            BuildVisuals(state);
            SetupCameraDirector();
            SetupSquadCommands();
            SetupControlSystem();

            IsActive = true;
            _wipeResolved = false;

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
                record.RegionId = FracturedCityLayout.RegionId;
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

        public void Update(float dt)
        {
            if (!IsActive)
            {
                return;
            }

            InputRouter.SetGameplayPaused(_paused, strategic: true);
            _cameraDirector?.Tick(_paused);

            // ER5-CTL-01：经 Control.ReleaseToStrategy 统一处理（取消 Move/Attack 残留命令、发布
            // RegionControlledUnitChangedSignal），不再直接写 _possessed。
            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                Control.ReleaseToStrategy();
            }

            bool directLocked = _cameraDirector != null && _cameraDirector.Mode == ViewMode.Direct;
            float scaledDt = _paused ? 0f : StrategyClock.GetScaledDt(dt, directLocked);

            // ER5-CMD-01：同 HomeValleyController 先例——必须在 HandleSelectionClick 之前跑，
            // 战略暂停下仍要能选人/排队命令。
            SquadCommands.Tick(_paused, scaledDt);
            HandleSelectionClick();

            HandleDirectControl(scaledDt);

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

            FracturedCityRegion.TickEnemies(state, scaledDt);
            // ER5-CTL-01：干扰宽限（Suspended→恢复/None）与受控机死亡回弹统一由 Control.Tick 处理——
            // 取代原来本类专属的 TickJamGrace，同一套状态机现在归还谷地/破碎都市共用。
            Control.Tick(scaledDt);
            TickDiscovery(state);
            TickWipeDetection(state);
            SyncWorldVisuals(state);
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
                foreach (FracturedCityLayout.Anchor anchor in FracturedCityLayout.AllAnchors())
                {
                    FracturedCityRegion.TryDiscover(state, anchor.Id, pos2, anchor.Position);
                }
            }
        }

        /// <summary>全灭检测：本区域曾经有过机器、当前全部阵亡时，自动结算 Lost（不自动弹出结算 UI/
        /// 强制退出场景——完整的失败回城结算属于 ER5-RETURN-01，本 Story 只保证数据层真相正确，
        /// 幂等，同一次全灭只结算一次。</summary>
        private void TickWipeDetection(CampaignState state)
        {
            if (_wipeResolved || _machineMarkers.Count == 0)
            {
                return;
            }
            bool anyAlive = state.MachineRecords != null && state.MachineRecords
                .Any(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive);
            if (anyAlive)
            {
                return;
            }
            FracturedCityRegion.ResolveExtraction(state, Array.Empty<int>());
            _wipeResolved = true;
            Log.Info("[FracturedCityController] 全灭：区域内所有机器阵亡，已结算携带中的关键物为 Lost。");
        }

        /// <summary>撤离结算：把当前存活于本区域的机器货物按 Recovered 处理并送回归还谷地。
        /// <paramref name="evacuateSuccess"/> 为 false 时只是"暂离/调试退出"，不结算、不移动机器——
        /// 供玩家中途保存退出后下次继续，不强行判定成功或失败。</summary>
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
                    List<int> survivors = (state.MachineRecords ?? Array.Empty<MachineRecord>())
                        .Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive)
                        .Select(m => m.LogicId).ToList();
                    FracturedCityRegion.ResolveExtraction(state, survivors);
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
            IsActive = false;
            Log.Info($"[FracturedCityController] 已退出破碎都市（evacuateSuccess={evacuateSuccess}）。");
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

        // ── 交互挂载点（供 ER5-INT-01 的正式 E 输入调用，本 Story 先暴露真实底层动作）───

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
            _possessed.DirectMove(new Vector3(x, 0f, z), dt);
        }

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
                SetPossessed = marker => _possessed = marker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = pos => FracturedCityRegion.IsPositionJammed(CampaignSession.Current, pos),
                SquadCommands = SquadCommands,
                OnPossessCommitted = OnControlPossessCommitted,
                OnReleased = null,
                FallbackAnchor = () => FracturedCityLayout.EntryEvac.Position,
                JamGraceSeconds = FracturedCityLayout.ControlJamGraceSeconds,
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
            _camera.orthographicSize = FracturedCityLayout.CameraBoundsHalfExtentZ;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.08f, 0.06f, 0.07f); // 破碎都市：偏冷灰红，与归还谷地区分。
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _cameraDirector = new CameraDirector();
            var followOffset = new Vector3(0f, 40f, 0f);
            _cameraDirector.Bind(_camera, TryGetPossessedAnchor, followOffset,
                FracturedCityLayout.CameraBoundsHalfExtentX, startInStrategy: true, initialDirectOrthographicSize: 14f);
            _cameraDirector.FocusStrategyOn(new float2(FracturedCityLayout.CameraFocusStart.x, FracturedCityLayout.CameraFocusStart.y));
            _cameraDirector.EnsureDirectTarget = EnsureDirectTarget;
        }

        // ── ER5-CMD-01：战略命令（多选/编组/Move·Attack·Guard·Retreat）────────

        private void SetupSquadCommands()
        {
            var obstacles = new List<(Vector2 Position, float Radius)>();
            foreach (FracturedCityLayout.Anchor anchor in FracturedCityLayout.AllAnchors())
            {
                obstacles.Add((anchor.Position, anchor.ClearanceRadius));
            }

            SquadCommands.Bind(new RegionSquadCommandContext
            {
                Camera = _camera,
                Markers = _machineMarkers,
                VisualRoot = _root,
                IsDirectControlled = IsMachineDirectControlled,
                IsEligible = logicId => MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) &&
                    rec.IsAlive && rec.RegionId == FracturedCityLayout.RegionId,
                CancelWorkIfAny = null, // 破碎都市没有"工作单"这一层概念。
                SafePoint = FracturedCityLayout.EntryEvac.Position,
                Obstacles = obstacles,
                FindHostileNear = FindEnemyHostileNear,
                ResolveHostile = ResolveEnemyHostile,
                TryAttack = TrySquadAttackEnemy,
                AttackRange = AttackRange,
                AttackCooldownSeconds = 1.2f,
            });
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
                if (enemy.RegionId != FracturedCityLayout.RegionId || !enemy.IsAlive)
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
            RegionEnemyRecord enemy = FracturedCityRegion.FindEnemy(CampaignSession.Current, hostileId);
            return enemy == null ? (RegionHostileInfo?)null : new RegionHostileInfo(enemy.EnemyInstanceId, enemy.Position, enemy.IsAlive);
        }

        /// <summary>Attack 命令的伤害结算——唯一入口 <see cref="FracturedCityRegion.TryAttackEnemy"/>，
        /// 本方法只负责把 <see cref="FracturedCityRegion.ActionResult"/> 翻译成
        /// <see cref="RegionAttackOutcome"/>（附带"目标是否已被击毁"，靠结算后重新查一次记录判断，
        /// 不在 ActionResult 里重复加字段）。</summary>
        private RegionAttackOutcome TrySquadAttackEnemy(int attackerLogicId, string hostileId)
        {
            CampaignState state = CampaignSession.Current;
            FracturedCityRegion.ActionResult result =
                FracturedCityRegion.TryAttackEnemy(state, attackerLogicId, hostileId, state?.RandomSeed ?? 0, isAiSource: false);
            if (!result.Success)
            {
                return RegionAttackOutcome.Fail(result.FailureReason);
            }
            RegionEnemyRecord enemy = FracturedCityRegion.FindEnemy(state, hostileId);
            return RegionAttackOutcome.Ok(targetDestroyed: enemy == null || !enemy.IsAlive);
        }

        // ── 可视化（占位几何体，同 HomeValleyController 手法）─────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[FracturedCityRoot]");

            BuildAnchorVisual(FracturedCityLayout.EntryEvac, PrimitiveType.Cylinder, new Color(0.2f, 0.7f, 0.9f, 0.5f), 0.05f, true);
            BuildAnchorVisual(FracturedCityLayout.RecoveryLocker, PrimitiveType.Cube, new Color(0.6f, 0.5f, 0.2f), 0.8f, false);
            BuildAnchorVisual(FracturedCityLayout.Terminal, PrimitiveType.Cube, new Color(0.2f, 0.5f, 0.8f), 0.9f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate1, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate1Id), 0.5f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate2, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate2Id), 0.5f, false);
            BuildAnchorVisual(FracturedCityLayout.Crate3, PrimitiveType.Cube, CrateColor(state, FracturedCityLayout.Crate3Id), 0.5f, false);
            BuildNodeVisual(state);
            BuildEnemyVisuals(state);

            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m.IsAlive && m.RegionId == FracturedCityLayout.RegionId)
                {
                    BuildMachineVisual(m);
                }
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
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            }
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = color };
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
            renderer.material = new Material(Shader.Find("Standard"))
            {
                color = destroyed ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.8f, 0.2f, 0.6f),
            };

            // 干扰场半径可视化环（半透明，随节点摧毁一起消失于 SyncWorldVisuals）。
            GameObject field = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            field.name = "JamField";
            field.transform.SetParent(_root.transform, false);
            field.transform.position = new Vector3(FracturedCityLayout.ListeningNode.Position.x, 0.02f, FracturedCityLayout.ListeningNode.Position.y);
            field.transform.localScale = new Vector3(FracturedCityLayout.JammerRadius * 2f, 0.02f, FracturedCityLayout.JammerRadius * 2f);
            UnityEngine.Object.Destroy(field.GetComponent<Collider>());
            Renderer fieldRenderer = field.GetComponent<Renderer>();
            fieldRenderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.6f, 0.1f, 0.5f, 0.15f) };
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
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Enemy_" + enemy.EnemyInstanceId;
                go.transform.SetParent(_root.transform, false);
                go.transform.position = new Vector3(enemy.Position.x, 1f, enemy.Position.y);
                Renderer renderer = go.GetComponent<Renderer>();
                Color baseColor = enemy.EnemyTypeId == EnemyCatalog.JammerId
                    ? new Color(0.6f, 0.15f, 0.55f)
                    : new Color(0.75f, 0.35f, 0.15f);
                renderer.material = new Material(Shader.Find("Standard")) { color = enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f) };
            }
        }

        private void BuildMachineVisual(MachineRecord machine)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId + "_" + machine.LogicId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(machine.WorldPosition.x, 1f, machine.WorldPosition.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.7f, 0.85f, 0.75f); // 与归还谷地机器配色区分（略带绿，"出征中"）。
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

            if (state.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord enemy in state.RegionEnemies)
                {
                    if (enemy.RegionId != FracturedCityLayout.RegionId)
                    {
                        continue;
                    }
                    Color baseColor = enemy.EnemyTypeId == EnemyCatalog.JammerId
                        ? new Color(0.6f, 0.15f, 0.55f)
                        : new Color(0.75f, 0.35f, 0.15f);
                    RefreshColor("Enemy_" + enemy.EnemyInstanceId, enemy.IsAlive ? baseColor : new Color(0.25f, 0.25f, 0.25f));
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
