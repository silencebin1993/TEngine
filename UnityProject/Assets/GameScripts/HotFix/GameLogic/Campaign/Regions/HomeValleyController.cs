using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
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
    public sealed class HomeValleyController
    {
        public bool IsActive { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(4);
        private HomeValleyMachineMarker _selected;

        /// <summary>ER2-INPUT-01：归还谷地自己的镜头状态机实例（不共享细胞阶段那个——两边场景
        /// 互斥运行，各自 Bind 自己的 Camera.main，生命周期也该各管各的，见 Exit() 的 Unbind）。</summary>
        private CameraDirector _cameraDirector;
        /// <summary>当前被直控（WASD 亲自开）的机器。null＝没有接管，处于战略选中+下令模式。</summary>
        private HomeValleyMachineMarker _possessed;
        /// <summary>ER2-INPUT-01 AC-UI-005：本区域自己的暂停态，镜像 CellStageFlow 的
        /// _paused/_strategicPause 写法（只有"战略暂停"一种，没有模态面板暂停的第二条路径——
        /// 归还谷地目前没有选卡/商店这类会自己置位暂停的模态面板）。</summary>
        private bool _paused;
        /// <summary>HUD 暂停按钮/速度显示读这个；Space 键路径见 <see cref="HandlePauseInput"/>，
        /// 两条路径写同一个 <see cref="_paused"/> 字段，互不冲突。</summary>
        public bool IsPaused => _paused;

        /// <summary>供 HUD 暂停按钮直接调用（不经过 InputRouter/Space）。</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
        }

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
            if (IsActive)
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
            EnsureMachinesSeeded(state);
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            HomeValleyPowerGrid.Recompute(state); // 幂等：新建战役刚播种、或读档恢复旧存档，都用当前数据重算一次。

            BuildVisuals(state);
            SetupCameraDirector();

            IsActive = true;

            SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            if (!saveResult.Success)
            {
                Log.Warning($"[HomeValleyController] HomeEntryComplete 自动存档未成功：" +
                    $"{saveResult.Outcome} {saveResult.Message}");
            }

            Log.Info($"[HomeValleyController] 已进入归还谷地（resume={resume}），" +
                $"建筑 {CountRegionBuildings(state)} 项，机器 {_machineMarkers.Count} 台。");
        }

        public void Update(float dt)
        {
            if (!IsActive)
            {
                return;
            }

            // 同 CellStageFlow.Update 的既定写法：暂停开关与 InputRouter 同步、镜头驱动，
            // 都必须在下面的暂停早退**之前**——ER2-INPUT-01 M2-02 同款要求：暂停下仍要能选人。
            HandlePauseInput();
            InputRouter.SetGameplayPaused(_paused, strategic: true);
            _cameraDirector?.Tick(_paused);

            // 落回战略视角才清空接管——过渡途中（Strategy→Direct 或 Direct→Strategy）都不清，
            // 否则 EnsureDirectTarget 刚给的目标会在过渡没走完时就被抹掉，接管请求白做。
            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                _possessed = null;
                // ERD-WRK-002"退出后重评估"：这台机器刚从直控释放，立即让分配引擎把它纳入下一轮候选，
                // 不必等满 0.5 秒轮询窗口。
                HomeValleyWorkOrders.MarkAssignmentDirty();
            }

            HandleSelectionClick();

            bool directLocked = _cameraDirector != null && _cameraDirector.Mode == ViewMode.Direct;
            float scaledDt = _paused ? 0f : StrategyClock.GetScaledDt(dt, directLocked);

            HandleDirectControl(scaledDt);

            if (_paused)
            {
                return;
            }

            TickMachineMovement(scaledDt);

            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
                HomeValleyWorkOrders.Tick(state, scaledDt, GetMachinePosition, ReleaseMachineMovement,
                    IsMachineDirectControlled, BeginAutoAssignedMovement);
                SyncWorldVisuals(state);
            }
        }

        /// <summary>供 <see cref="HomeValleyWorkOrders.Tick"/> 赶路阶段路径停滞看门狗查询机器实时坐标
        /// （<see cref="MachineRecord.WorldPosition"/> 只在 Exit 时才同步，赶路途中是过期值，必须读
        /// Transform 实时位置）。</summary>
        private Vector2? GetMachinePosition(int logicId)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker.LogicId == logicId)
                {
                    Vector3 p = marker.transform.position;
                    return new Vector2(p.x, p.z);
                }
            }
            return null;
        }

        /// <summary>PathBlocked 触发时的释放回调：让对应 marker 停止赶路，不留一个订单已经
        /// Waiting/释放但视觉上机器还在朝旧目标走的不一致状态。</summary>
        private void ReleaseMachineMovement(int logicId)
        {
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker.LogicId == logicId)
                {
                    marker.CancelCommandMove();
                    return;
                }
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

        /// <summary>ERD-WRK-002"直控机器暂不领取新单"的判定来源——<see cref="HomeValleyWorkOrders.Tick"/>
        /// 的分配引擎不下沉持有 <see cref="_possessed"/>，只能靠委托查询。</summary>
        private bool IsMachineDirectControlled(int logicId)
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
                // 理论上不应发生：分配引擎只从 MachineRegistry 里活着、在本区域的记录中选人，
                // 而 _machineMarkers 应与之同步；真出现就跳过这一轮，订单已是 Reserved，
                // 下一次 0.5 秒评估会因为它一直没有实际动起来而继续留在候选池里（不会丢单）。
                Log.Warning($"[HomeValleyController] 自动分配命中订单 {order.WorkOrderId}，" +
                    $"但机器 {order.AssignedMachineLogicId} 无可视化对象，本轮跳过移动。");
                return;
            }

            Vector2 pos2 = HomeValleyWorkOrders.ResolveWorkPosition(CampaignSession.Current, order);
            Vector3 destination = new Vector3(pos2.x, 1f, pos2.y);
            BeginMovementForOrder(marker, destination, order);
        }

        /// <summary>Space（Strategy 域）：与 CellStageFlow.HandleStrategicPauseInput 同款语义。
        /// 只能从战略视角触发/解除——直控下 Space 域不匹配，天然读不到。</summary>
        private void HandlePauseInput()
        {
            if (InputRouter.ConsumeAction(GameActionId.TogglePause, InputScope.Strategy))
            {
                _paused = !_paused;
            }
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
                CycleControlTarget();
            }

            float x = 0f;
            float z = 0f;
            if (InputRouter.GetActionKey(GameActionId.MoveLeft, InputScope.Direct)) { x -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveRight, InputScope.Direct)) { x += 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct)) { z -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct)) { z += 1f; }

            _possessed.DirectMove(new Vector3(x, 0f, z), dt);
        }

        /// <summary>按 LogicId 稳定顺序循环到下一台机器（同 CellPlayerController.RequestNextControlCandidate
        /// 的排序写法：不按距离，避免两台机器之间来回跳）。只有一台机器时循环到自己，行为等价于原地不动。</summary>
        private void CycleControlTarget()
        {
            if (_machineMarkers.Count == 0 || _possessed == null)
            {
                return;
            }

            int current = _possessed.LogicId;
            HomeValleyMachineMarker next = null;
            HomeValleyMachineMarker first = _machineMarkers[0];
            foreach (HomeValleyMachineMarker marker in _machineMarkers)
            {
                if (marker.LogicId < first.LogicId)
                {
                    first = marker;
                }
                if (marker.LogicId > current && (next == null || marker.LogicId < next.LogicId))
                {
                    next = marker;
                }
            }
            next ??= first;

            _possessed.CancelCommandMove();
            _possessed = next;
            _possessed.CancelCommandMove();

            // 同 EnsureDirectTarget：切换到的新机器也不能一边被玩家直控、一边还被工作单状态机推进。
            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
                HomeValleyWorkOrders.OnMachinePossessed(state, next.LogicId);
            }

            _selected?.SetSelected(false);
            _selected = next;
            _selected.SetSelected(true);
        }

        /// <summary>UI 只读查询当前选中机器（工作面板显示/编辑该机器工作偏好用），没有选中返回 null。</summary>
        public int? SelectedMachineLogicId => _selected != null ? _selected.LogicId : (int?)null;

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
        /// （与细胞阶段"没有可接管的身体"是同一失败语义）。</summary>
        private bool EnsureDirectTarget()
        {
            if (_selected == null)
            {
                return false;
            }
            _possessed = _selected;
            _possessed.CancelCommandMove();

            // ERD-WRK-003 第三条：接管中的机器不再持有在办订单（保留已搬货物/已扣资源，订单回 Ready
            // 等待重新指派），不能一边被玩家亲自开、一边又被工作单状态机继续推进。
            CampaignState state = CampaignSession.Current;
            if (state != null)
            {
                HomeValleyWorkOrders.OnMachinePossessed(state, _possessed.LogicId);
            }
            return true;
        }

        /// <summary>CameraDirector 的直控锚点来源：接管中的机器的世界 XZ 位置；没有接管返回 false。</summary>
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

        /// <summary>退出归还谷地（进远征/回主菜单）。只清理本区域的运行时表现，
        /// <see cref="MachineRegistry"/>/<see cref="CampaignState"/> 记录原样保留——
        /// 这就是"持久化"要求：回城/再次读档要看到同一批建筑与残骸状态，不是重新生成。</summary>
        public void Exit()
        {
            if (!IsActive)
            {
                return;
            }

            SyncLiveStateBackToRecords();
            DestroyVisuals();
            // ER2-INPUT-01：与 CellStageFlow 同款纪律——离场解绑镜头，InputRouter.Reset() 顺带清掉
            // 本区域可能留下的 Scope/模态残留，避免粘到下一次进场或切去细胞阶段。
            _cameraDirector?.Unbind();
            _cameraDirector = null;
            _possessed = null;
            _paused = false;
            IsActive = false;
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

            var buildings = new List<BuildingRecord>
            {
                NewBuilding(HomeValleyLayout.Core, BuildingConstructionState.Operational,
                    BuildingPowerState.Powered, health: 500f),
                NewBuilding(HomeValleyLayout.Generator, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.Warehouse, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.SignalTower, BuildingConstructionState.Damaged,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                // PowerState 先给 NotApplicable 占位——EnsureRegionSeeded 返回后 Enter() 立即调用
                // HomeValleyPowerGrid.Recompute，会按 ERD-ECO-002 的容量/优先级仲裁重新计算成
                // Powered/Brownout。
                NewBuilding(HomeValleyLayout.AssemblyStation, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.AnalysisBench, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
                NewBuilding(HomeValleyLayout.RepairBay, BuildingConstructionState.Operational,
                    BuildingPowerState.NotApplicable, health: PlaceholderStructureHealth),
            };
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

        /// <summary>建筑没有内容锁定表专属 HP 数值（只有维修材料/时间），暂用中性占位；
        /// 等建筑受损/战斗机制的 Story 落地时再替换成真实数值，不影响本 Story 的
        /// Damaged/Operational 可区分表现要求。</summary>
        private const float PlaceholderStructureHealth = 100f;

        private static BuildingRecord NewBuilding(
            HomeValleyLayout.Anchor anchor,
            BuildingConstructionState constructionState,
            BuildingPowerState powerState,
            float health)
        {
            HomeValleyLayout.PowerProfile.TryGetValue(anchor.Id, out (float PowerDemand, int PowerPriority) profile);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + anchor.Id,
                BuildingTypeId = anchor.Id,
                RegionId = HomeValleyLayout.RegionId,
                Position = anchor.Position,
                Rotation = 0f,
                Health = health,
                ConstructionState = constructionState,
                PowerPriority = profile.PowerPriority,
                PowerState = powerState,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                BlockedReason = null,
            };
        }

        /// <summary>首次进入才登记 ERC-001/002；已有记录（新战役当局已生成，或读档已恢复）时
        /// 原样复用，绝不重复 SpawnMachine——否则每次回城都会多出一台机器（AC-LIFE-001/002）。</summary>
        private static void EnsureMachinesSeeded(CampaignState state)
        {
            SpawnIfMissing(HomeValleyLayout.Erc001Spawn);
            SpawnIfMissing(HomeValleyLayout.Erc002Spawn);

            void SpawnIfMissing(HomeValleyLayout.Anchor spawn)
            {
                bool exists = MachineRegistry.AllRecords.Any(r =>
                    r.RegionId == HomeValleyLayout.RegionId && r.ChassisId == spawn.Id && r.IsAlive);
                if (exists)
                {
                    return;
                }

                MachineOpResult result = MachineRegistry.SpawnMachine(
                    chassisId: spawn.Id,
                    blueprintId: "placeholder:" + spawn.Id,
                    regionId: HomeValleyLayout.RegionId,
                    position: spawn.Position,
                    health: 100f,
                    maxHealth: 100f);

                if (!result.Success)
                {
                    Log.Error($"[HomeValleyController] 登记机器 {spawn.Id} 失败：{result.Error} {result.Message}");
                }
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
                Vector3 p = marker.transform.position;
                float health = MachineRegistry.TryGetRecord(marker.LogicId, out MachineRecord rec) ? rec.Health : 100f;
                MachineRegistry.SyncLiveState(marker.LogicId, new Vector2(p.x, p.z), health, null);
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
            if (state == null || !IsActive)
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

        private void RefreshBuildingVisual(BuildingRecord building)
        {
            if (_root == null)
            {
                return;
            }
            Transform go = _root.transform.Find("Building_" + building.BuildingTypeId);
            Renderer renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer != null)
            {
                renderer.material.color = ColorForBuilding(building);
            }
        }

        // ── 可视化（占位几何体）────────────────────────────────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[HomeValley]");
            _machineMarkers.Clear();
            _selected = null;

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

            BuildBeaconSlotVisual();

            if (!state.BuildingRecords.Any(b => b.BuildingId == HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeGenerator2))
            {
                BuildBuildSiteVisual(HomeValleyLayout.Generator2Site);
            }

            foreach (GroundItemRecord item in state.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (item.RegionId == HomeValleyLayout.RegionId)
                {
                    BuildGroundItemVisual(item);
                }
            }

            foreach (MachineRecord machine in MachineRegistry.AllRecords.Where(m =>
                m.RegionId == HomeValleyLayout.RegionId && m.IsAlive))
            {
                BuildMachineVisual(machine);
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

            foreach (BuildingRecord building in state.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId))
            {
                Transform go = _root.transform.Find("Building_" + building.BuildingTypeId);
                if (go == null)
                {
                    BuildBuildingVisual(building);
                    if (building.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2)
                    {
                        DestroyChild("BuildSite_" + HomeValleyLayout.BuildingTypeGenerator2);
                    }
                }
                else
                {
                    RefreshBuildingVisual(building);
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
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        private void DestroyChild(string childName)
        {
            Transform child = _root != null ? _root.transform.Find(childName) : null;
            if (child != null)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        private void BuildBuildingVisual(BuildingRecord building)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Building_" + building.BuildingTypeId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(building.Position.x, 1f, building.Position.y);
            go.transform.localScale = new Vector3(3f, 2f, 3f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = ColorForBuilding(building) };
        }

        private void BuildWreckageVisual(HomeValleyLayout.Anchor wreckage)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Wreckage_" + wreckage.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(wreckage.Position.x, 0.5f, wreckage.Position.y);
            go.transform.localScale = new Vector3(2.5f, 0.5f, 2.5f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.45f, 0.35f, 0.25f) };
        }

        /// <summary>ER3-WRK-01 Build：尚未建成的建造位占位（半透明，与 <see cref="BuildBeaconSlotVisual"/>
        /// 同一手法），可点选发起 <see cref="HomeValleyWorkOrders.TryCreateBuild"/>。</summary>
        private void BuildBuildSiteVisual(HomeValleyLayout.Anchor site)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "BuildSite_" + site.Id;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(site.Position.x, 0.1f, site.Position.y);
            go.transform.localScale = new Vector3(3f, 0.2f, 3f);
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.6f, 0.9f, 0.4f) };
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
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.85f, 0.75f, 0.35f) };
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
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.5f, 0.8f, 0.4f) };
        }

        private void BuildMachineVisual(MachineRecord machine)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(machine.WorldPosition.x, 1f, machine.WorldPosition.y);
            go.transform.localScale = new Vector3(1f, 1f, 1f);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.7f, 0.75f, 0.8f);
            renderer.material = new Material(Shader.Find("Standard")) { color = baseColor };

            HomeValleyMachineMarker marker = go.AddComponent<HomeValleyMachineMarker>();
            marker.Initialize(machine.LogicId, machine.ChassisId, renderer, baseColor);
            _machineMarkers.Add(marker);
        }

        private static Color ColorForBuilding(BuildingRecord building)
        {
            if (building.ConstructionState == BuildingConstructionState.Damaged)
            {
                return new Color(0.75f, 0.25f, 0.2f); // 红：Damaged
            }
            switch (building.PowerState)
            {
                case BuildingPowerState.Brownout:
                    return new Color(0.85f, 0.7f, 0.15f); // 黄：Brownout
                case BuildingPowerState.OutputBlocked:
                    return new Color(0.9f, 0.45f, 0.1f); // 橙：OutputBlocked
                case BuildingPowerState.Powered:
                    return new Color(0.25f, 0.7f, 0.3f); // 绿：Operational + Powered
                default:
                    return new Color(0.5f, 0.55f, 0.6f); // 灰：Operational 但未接电（Unpowered/NotApplicable）
            }
        }

        // ── 相机（ER2-INPUT-01：CameraDirector 驱动，Strategy 起始 + WASD 接管）───

        /// <summary>初始朝向/背景/裁剪面与改造前逐值一致，只是把"镜头怎么动"交给
        /// <see cref="CameraDirector"/>；固定俯视旋转本类自己设一次（CameraDirector 从不碰旋转，
        /// 全程只写 position/orthographicSize，见该类设计要点第 1 条）。</summary>
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
            _camera.orthographicSize = HomeValleyLayout.CameraBoundsHalfExtentZ;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.05f, 0.07f, 0.10f);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _cameraDirector = new CameraDirector();
            Vector2 focus = HomeValleyLayout.ClampToBounds(HomeValleyLayout.CameraFocusStart);
            var followOffset = new Vector3(0f, 40f, 0f);
            // 矩形边界（X 半宽 28 / Z 半宽 30，见 HomeValleyLayout）比 CameraDirector 的单标量方形
            // 钳制窄——取较小值（28）保证任何一条轴都不会真的越界，代价是 Z 轴少 2 个单位余量，
            // 可接受（CameraDirector 本就"非目标：不做电影级轨迹"，没打算为单个场景扩展成矩形钳制）。
            _cameraDirector.Bind(_camera, TryGetPossessedAnchor, followOffset,
                HomeValleyLayout.CameraBoundsHalfExtentX, startInStrategy: true, initialDirectOrthographicSize: 14f);
            _cameraDirector.FocusStrategyOn(new float2(focus.x, focus.y));
            _cameraDirector.EnsureDirectTarget = EnsureDirectTarget;
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
            if (_selected != null && InputRouter.GetMouseButtonDown(1, InputScope.Strategy))
            {
                CampaignState cancelState = CampaignSession.Current;
                WorkOrderRecord active = cancelState != null
                    ? HomeValleyWorkOrders.FindActiveOrderForMachine(cancelState, _selected.LogicId)
                    : null;
                if (active != null)
                {
                    Vector3 pos = _selected.transform.position;
                    HomeValleyWorkOrders.CancelOrder(cancelState, active.WorkOrderId, new Vector2(pos.x, pos.z));
                    _selected.CancelCommandMove();
                    Log.Info($"[HomeValleyController] 已取消 {_selected.LogicId} 的在办订单 {active.WorkOrderId}。");
                }
                return;
            }

            if (!InputRouter.GetMouseButtonDown(0, InputScope.Strategy))
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
                return;
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

            string buildingTypeId = BuildingTypeIdFromHit(hit);
            if (buildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                CommandWork(moving, destination, "recharge:" + moving.LogicId,
                    () => HomeValleyWorkOrders.TryCreateRecharge(state, moving.LogicId), "充电");
                return;
            }
            if (buildingTypeId != null)
            {
                CommandWork(moving, destination, HomeValleyLayout.RegionId + ":" + buildingTypeId,
                    () => HomeValleyWorkOrders.TryCreateRepair(state, buildingTypeId, moving.LogicId), "修复 " + buildingTypeId);
                return;
            }

            string buildSiteId = BuildSiteIdFromHit(hit);
            if (buildSiteId != null)
            {
                CommandWork(moving, destination, HomeValleyLayout.RegionId + ":" + buildSiteId,
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
            Vector3 pos = moving.transform.position;
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
                moving.CommandMoveTo(destination, () =>
                {
                    if (!HomeValleyWorkOrders.OnArrivedAtHaulSource(CampaignSession.Current, workOrderId))
                    {
                        return;
                    }
                    Vector3 corePos = new Vector3(HomeValleyLayout.Core.Position.x, 1f, HomeValleyLayout.Core.Position.y);
                    moving.CommandMoveTo(corePos, () =>
                        HomeValleyWorkOrders.OnArrivedAtHaulDestination(CampaignSession.Current, workOrderId));
                });
                return;
            }

            moving.CommandMoveTo(destination, () => HomeValleyWorkOrders.OnArrivedAtWork(CampaignSession.Current, workOrderId));
        }

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
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
            _machineMarkers.Clear();
            _selected = null;
            _possessed = null;
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
