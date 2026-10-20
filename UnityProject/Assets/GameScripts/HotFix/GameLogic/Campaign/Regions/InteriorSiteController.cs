using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG8-GEN-02（FG17 FGR-GEN-010 / 042；FG14 FGR-ARC-002 / 014）：室内场地（地下中继大厅、数据中心）作为世界里的一个地点。
    ///
    /// - 表面：室内表面由世界生成器按种子生成（墙、入口、立柱、废墟；FG0-ARCH-05 已交付），独立的区块存储与差异；这里只读它的地形建立
    ///   立柱 / 墙的障碍与占位表现（几何体，B22 美术占位）。局部坐标：x ∈ [0, 宽)，y ∈ [0, 高)，入口在 y = 0 的中间。
    /// - 模拟：机器是本地点战斗内核里的真实单位（移动、编队命令、接入、热量冷却随机器走）；<see cref="SimStep"/> 由统一时钟的固定步推进，
    ///   **与是否被观察无关**（室内与地表同时运行，FGR-ARC-002 / FGR-BASE-021）。首领内容（寂听主脑 / 余烬主机）由 FG9-FAC-01 / FG11-BOSS-01 填进来。
    /// - 进出：只经 <see cref="InteriorSiteService"/>（入口面板 / 出口面板；进入前自动存档 = 首领战前存档）。全部机器离开或阵亡后地点卸载。
    /// - 表现：被观察时才建表现对象（地板、墙、立柱、出口标记、机器），不被观察时销毁（只保留内核）。
    /// </summary>
    public sealed class InteriorSiteController : IWorldSite
    {
        public string SiteId { get; }
        public WorldSurfaceKind SurfaceKind => WorldSurfaceKind.Interior;
        public bool IsLoaded { get; private set; }
        public bool IsActive => IsLoaded && WorldView.IsObserved(SiteId);
        public bool IsWiped => false;
        public int LiveMachineCount => _combat != null ? _combat.CountAliveMachines() : 0;
        public WorldCameraProfile CameraProfile => _cameraProfile;
        public CombatSite Combat => _combat;

        /// <summary>室内宽高（格）与出口格（入口在 y = 0 的中间）。</summary>
        public int Width { get; }
        public int Height { get; }
        public Vector2 ExitPoint => new Vector2(Width / 2f, 1.5f);

        public readonly RegionSquadCommandSystem SquadCommands = new RegionSquadCommandSystem();
        public readonly RegionControlSystem Control = new RegionControlSystem();

        private CombatSite _combat;
        private WorldCameraProfile _cameraProfile;
        private RegionSquadCommandContext _squadCtx;
        private Camera _camera;
        private GameObject _root;
        private readonly List<HomeValleyMachineMarker> _machineMarkers = new List<HomeValleyMachineMarker>(8);
        private HomeValleyMachineMarker _selected;
        private HomeValleyMachineMarker _possessed;
        private readonly List<(Vector2 Position, float Radius)> _obstacles = new List<(Vector2, float)>(256);
        private readonly List<Vector2> _pillars = new List<Vector2>(64);
        private CameraDirector _cameraDirector => IsActive && WorldView.Director.IsBound ? WorldView.Director : null;
        private const float AttackRange = 8f;

        /// <summary>FG9-FAC-01：有手工点位的室内（地下中继大厅）的敌人与首领内容；没有点位的室内（数据中心，FG11-BOSS-01）为 null。</summary>
        private readonly RelayHallContent _content;
        public RelayHallContent Content => _content;

        public InteriorSiteController(string surfaceId)
        {
            SiteId = surfaceId;
            _content = RelayHallLayout.IsInteriorWithPoints(surfaceId) ? new RelayHallContent(surfaceId) : null;
            GameConfig.fg.Surface s = WorldGenContent.Surface(surfaceId);
            int chunk = GridContent.TuningInt("grid.chunk_size");
            Width = Math.Max(1, s.WidthChunks) * chunk;
            Height = Math.Max(1, s.HeightChunks) * chunk;
        }

        /// <summary>自检读：本地点的立柱数、障碍数、机器句柄。</summary>
        public int PillarCount => _pillars.Count;
        public int ObstacleCount => _obstacles.Count;
        public IReadOnlyList<HomeValleyMachineMarker> MachineMarkers => _machineMarkers;

        // ── 载入 / 卸载 ─────────────────────────────────────────────────────────

        /// <summary>把 <paramref name="logicIds"/> 指定的机器转进室内（<paramref name="resume"/> = 读档恢复：机器已经在室内，从战斗快照恢复）。</summary>
        public void Enter(IEnumerable<int> logicIds, bool resume)
        {
            _unloadQueued = false;
            if (IsLoaded)
            {
                TransportIn(logicIds);
                SyncCombatMachines(CampaignSession.Current);
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            BuildObstacles(state);
            TransportIn(logicIds);
            RegisterLoadouts(state);
            _combat = CombatSites.Open(SiteId, state, resume, _content != null ? _content.Rules : new InteriorCombatRules(), out bool restored);
            _combat.Squad = SquadCommands;
            _combat.SetObstacles(CombatDemoContent.Obstacles(_obstacles));
            SyncCombatMachines(state);
            _combat.RefreshAllMachineWeapons(state);
            _content?.OnOpened(state, _combat, resume, restored); // FG9-FAC-01：大厅的敌人、首领开战（新进入时）、伏笔地点
            SetupCameraProfile();
            SetupSquadCommands();
            SetupControl();
            IsLoaded = true;
            Log.Info($"[InteriorSiteController] 载入室内 {SiteId}（{Width}×{Height} 格，立柱 {_pillars.Count} 根，resume={resume}，恢复快照={restored}），机器 {_machineMarkers.Count} 台。");
        }

        /// <summary>机器转进室内：从别的地点导出实时状态（热量 / 冷却随机器走），换地点，在入口内侧排开。已在室内的不动。</summary>
        private void TransportIn(IEnumerable<int> logicIds)
        {
            if (logicIds == null)
            {
                return;
            }
            float spacing = Mathf.Max(1.5f, GridContent.TryGetTuning("interior.spawn_spacing", out float sp) ? sp : 2.5f);
            int i = 0;
            foreach (int logicId in logicIds)
            {
                if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord record) || record == null || !record.IsAlive)
                {
                    continue;
                }
                if (record.RegionId == SiteId)
                {
                    i++;
                    continue;
                }
                CombatSites.ExportMachine(logicId);
                MachineRegistry.MoveToRegion(record, SiteId);
                record.WorldPosition = SpawnPoint(i, spacing);
                i++;
            }
        }

        /// <summary>入口内侧第 <paramref name="i"/> 台的落点（每排 5 台，从入口往里排）。</summary>
        public Vector2 SpawnPoint(int i, float spacing)
        {
            int col = i % 5;
            int row = i / 5;
            float x = Width / 2f + (col - 2) * spacing;
            float y = 4f + row * spacing;
            return new Vector2(Mathf.Clamp(x, 2f, Width - 2f), Mathf.Clamp(y, 2f, Height - 2f));
        }

        /// <summary>
        /// 机器离开室内回到地表（<see cref="InteriorSiteService.TryExit"/> 调用）：写回实时状态、移出本地点内核。表面上的落点由调用方写进记录。
        /// 室内一台机器都不剩时卸载本地点（战斗快照删除，记录已写回）。
        /// </summary>
        public void RemoveMachines(IEnumerable<int> logicIds)
        {
            foreach (int id in logicIds)
            {
                if (_combat != null && _combat.TryGetMachineUnit(id, out _))
                {
                    _combat.ExportMachine(id, includePosition: true);
                    _combat.RemoveMachine(id, export: false);
                }
                HomeValleyMachineMarker m = _machineMarkers.FirstOrDefault(x => x != null && x.LogicId == id);
                if (m != null)
                {
                    if (_possessed == m)
                    {
                        Control.ReleaseToStrategy();
                        _possessed = null;
                    }
                    if (_selected == m)
                    {
                        _selected = null;
                    }
                    if (m.View != null)
                    {
                        UnityObjects.Release(m.View.gameObject);
                    }
                    m.DetachView();
                    _machineMarkers.Remove(m);
                }
            }
        }

        /// <summary>卸载（全部离开 / 全灭 / 世界卸载）。<paramref name="dropRecord"/> = 删掉存档里的战斗快照（地点里已没有机器）。</summary>
        public void Exit(bool dropRecord)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            _combat?.FlushPendingEvents();
            SyncLiveStateBackToRecords();
            // FG9-FAC-01：全灭（_unloadQueued）或全员撤出（删快照）时首领战没打完就复位；读档 / 世界卸载（不删快照）不复位。
            _content?.OnUnloaded(state, dropRecord, _unloadQueued);
            DestroyVisuals();
            SquadCommands.ReleaseVisuals();
            CombatSites.Close(SiteId, state, dropRecord);
            _combat = null;
            _machineMarkers.Clear();
            _possessed = null;
            _selected = null;
            SquadCommands.Unbind();
            Control.Unbind();
            IsLoaded = false;
            Log.Info($"[InteriorSiteController] 卸载室内 {SiteId}（dropRecord={dropRecord}）。");
        }

        // ── IWorldSite ─────────────────────────────────────────────────────────

        public Vector2? LivePosition(int logicId) =>
            _combat != null && _combat.TryGetMachinePosition(logicId, out Vector2 p) ? p : (Vector2?)null;

        public Vector2 DefaultFocus => _combat != null && _combat.TryMachineCentroid(out Vector2 c) ? c : ExitPoint;

        public void SetObserved(bool observed)
        {
            if (!IsLoaded)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state?.World != null)
            {
                // 读档后镜头回到哪里：存档时正在看的室内（只是表现层的“上次看的地方”，不影响模拟）。
                if (observed)
                {
                    state.World.InteriorFocusId = SiteId;
                }
                else if (state.World.InteriorFocusId == SiteId)
                {
                    state.World.InteriorFocusId = string.Empty;
                }
            }
            if (!observed)
            {
                if (_possessed != null)
                {
                    Control.ReleaseToStrategy();
                }
                DestroyVisuals();
                SquadCommands.ReleaseVisuals();
                _combat?.ReleaseRender();
                return;
            }
            if (_root == null && state != null)
            {
                BuildVisuals(state);
            }
        }

        public void FrameUpdate(float realDt, float frameScaledDt)
        {
            if (!IsLoaded)
            {
                return;
            }
            bool paused = GameClock.Paused;
            if (_cameraDirector != null && _cameraDirector.Mode == ViewMode.Strategy && _possessed != null)
            {
                Control.ReleaseToStrategy();
            }
            SquadCommands.TickInput(paused);
            HandleSelectionClick();
            HandleDirectControl();
            if (!paused)
            {
                Control.Tick(GameClock.FrameStepSeconds);
            }
            _combat?.FrameRender(_camera, GameClock.StepAlpha);
            _content?.FrameUpdate(CampaignSession.Current);
        }

        /// <summary>固定模拟步——无论镜头在不在这里都执行（FGR-BASE-021），不读取观察状态。</summary>
        public void SimStep(float dt)
        {
            if (!IsLoaded)
            {
                return;
            }
            SquadCommands.TickSim(dt);
            _combat?.Step(dt, GameClock.GameSeconds);
            _content?.SimStep(CampaignSession.Current); // FG9-FAC-01：首领状态机（与观察无关）
            // 室内一台存活机器都不剩（全部阵亡）：本步提交后卸载（战斗快照删除），镜头若在这里就回到地表。内核计数 O(1)；记录侧只在名册变了才数。
            if (_combat != null && !_unloadQueued && _combat.CountAliveMachines() == 0)
            {
                if (_rosterChecked != MachineRegistry.RosterRevision)
                {
                    _rosterChecked = MachineRegistry.RosterRevision;
                    _recordAlive = InteriorSiteService.CountIn(SiteId) > 0;
                }
                if (!_recordAlive)
                {
                    _unloadQueued = true;
                    string name = InteriorSiteService.SurfaceName(SiteId);
                    string site = SiteId;
                    GameLogic.Notifications.NotificationCenter.Post("interior_transit", Localization.GameText.Format("interior.notify.wiped", name), new Vector3(ExitPoint.x, 0f, ExitPoint.y));
                    WorldSimulation.RunAfterStep(() => WorldSimulation.UnloadInterior(site, dropRecord: true));
                }
            }
        }

        private bool _unloadQueued;
        private int _rosterChecked = int.MinValue;
        private bool _recordAlive = true;

        public void SyncLiveStateForSave()
        {
            if (IsLoaded)
            {
                SyncLiveStateBackToRecords();
            }
        }

        private void SyncLiveStateBackToRecords()
        {
            if (_combat == null || _combat.IsDisposed)
            {
                return;
            }
            foreach (HomeValleyMachineMarker m in _machineMarkers)
            {
                if (m != null)
                {
                    _combat.ExportMachine(m.LogicId, includePosition: true);
                }
            }
        }

        // ── 内核对账 ───────────────────────────────────────────────────────────

        private void RegisterLoadouts(CampaignState state)
        {
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != SiteId || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                CircuitOpResult r = MachineLoadoutRegistry.Register(state, m.LogicId, m.BlueprintId, m.BlueprintVersion);
                if (!r.Success)
                {
                    Log.Warning($"[InteriorSiteController] 机器 {m.LogicId} 装配登记失败：{r.Message}");
                }
            }
        }

        /// <summary>本地点存活机器 ↔ 内核单位对账（进场、追加进入、读档）。</summary>
        private void SyncCombatMachines(CampaignState state)
        {
            if (_combat == null || state == null)
            {
                return;
            }
            var have = new HashSet<int>(_machineMarkers.Where(m => m != null).Select(m => m.LogicId));
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != SiteId || have.Contains(m.LogicId))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(m.BlueprintId))
                {
                    MachineLoadoutRegistry.Register(state, m.LogicId, m.BlueprintId, m.BlueprintVersion);
                }
                HomeValleyMachineMarker marker = _combat.SpawnMachine(state, m, m.WorldPosition, autoEngage: false);
                if (marker != null)
                {
                    _machineMarkers.Add(marker);
                    if (_root != null)
                    {
                        BuildMachineView(marker, m);
                    }
                }
            }
            foreach (int stale in _combat.MachinesNotIn(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive && r.RegionId == SiteId).ToList())
            {
                _combat.RemoveMachine(stale, export: false);
            }
            _machineMarkers.RemoveAll(m => m == null || !MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord r) || !r.IsAlive || r.RegionId != SiteId);
        }

        /// <summary>立柱与墙的障碍：读室内表面的地形（悬崖格 = 墙 / 立柱）。立柱按 2×2 块合成一个圆；墙沿边每 2 格一个圆，入口留空。</summary>
        private void BuildObstacles(CampaignState state)
        {
            _obstacles.Clear();
            _pillars.Clear();
            HomeGridMap map = WorldGenService.MapFor(state, SiteId);
            byte cliff = GridContent.TerrainCode("cliff");
            for (int y = 1; y < Height - 1; y++)
            {
                for (int x = 1; x < Width - 1; x++)
                {
                    if (map.GetTerrain(new GridCell(x, y)) != cliff)
                    {
                        continue;
                    }
                    // 一根立柱的左下角：左边、下边都不是悬崖。
                    if (map.GetTerrain(new GridCell(x - 1, y)) == cliff || map.GetTerrain(new GridCell(x, y - 1)) == cliff)
                    {
                        continue;
                    }
                    var center = new Vector2(x + 1f, y + 1f);
                    _pillars.Add(center);
                    _obstacles.Add((center, 1.6f));
                }
            }
            int half = Width / 2;
            for (int x = 0; x <= Width; x += 2)
            {
                if (Math.Abs(x - half) > 2)
                {
                    _obstacles.Add((new Vector2(x, 0f), 1.2f)); // 入口所在的那面墙，入口留空
                }
                _obstacles.Add((new Vector2(x, Height), 1.2f));
            }
            for (int y = 2; y < Height; y += 2)
            {
                _obstacles.Add((new Vector2(0f, y), 1.2f));
                _obstacles.Add((new Vector2(Width, y), 1.2f));
            }
        }

        private Vector2 ClampInside(Vector2 p) => new Vector2(Mathf.Clamp(p.x, 2f, Width - 2f), Mathf.Clamp(p.y, 2f, Height - 2f));

        // ── 镜头、命令、接入 ───────────────────────────────────────────────────

        private void SetupCameraProfile()
        {
            _camera = WorldView.EnsureCamera();
            _cameraProfile = new WorldCameraProfile
            {
                DirectAnchor = TryGetPossessedAnchor,
                EnsureDirectTarget = EnsureDirectTarget,
                ArenaHalfExtent = Mathf.Max(Width, Height) * 0.5f + 6f,
                FollowOffset = new Vector3(0f, 40f, 0f),
                InitialStrategyOrthographicSize = Mathf.Max(18f, Height * 0.45f),
                InitialDirectOrthographicSize = 14f,
                StartFocus = new Vector2(Width / 2f, 12f),
                Background = new Color(0.03f, 0.03f, 0.05f),
            };
        }

        private void SetupSquadCommands()
        {
            _squadCtx = new RegionSquadCommandContext
            {
                Camera = _camera,
                Markers = _machineMarkers,
                VisualRoot = _root,
                IsDirectControlled = id => _possessed != null && _possessed.LogicId == id,
                IsEligible = id => MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec.IsAlive && rec.RegionId == SiteId,
                CancelWorkIfAny = null,
                SafePoint = ExitPoint + new Vector2(0f, 3f),
                Obstacles = _obstacles,
                ClampDestination = ClampInside,
                // FG9-FAC-01：右键敌人 = 攻击（地下中继大厅）；没有内容的室内（数据中心）没有敌人，FG11-BOSS-01 接上。
                FindHostileNear = (p, r) => _content?.FindHostileNear(p, r),
                ResolveHostile = id => _content?.ResolveHostile(id),
                Site = _combat,
                FlyCameraTo = p => { _cameraDirector?.FlyStrategyTo(new float2(p.x, p.y), CameraDirector.TransitionSeconds); },
                HostileUnit = id => _content != null ? _content.HostileUnit(id) : 0,
                AttackRange = AttackRange,
                AttackCooldownSeconds = CombatSite.MachineAttackInterval,
            };
            SquadCommands.Bind(_squadCtx);
        }

        private void SetupControl()
        {
            Control.Bind(new RegionControlContext
            {
                RegionId = SiteId,
                Markers = _machineMarkers,
                GetPossessed = () => _possessed,
                SetPossessed = SetPossessedMarker,
                IsCameraTransitioning = () => _cameraDirector != null && _cameraDirector.Mode == ViewMode.Transition,
                IsPositionJammed = pos => Signal.SignalLinkService.IsJammedAt(CampaignSession.Current, SiteId, pos), // FG9-FAC-01：干扰机 / 全频静默
                SquadCommands = SquadCommands,
                OnPossessCommitted = id =>
                {
                    if (!RegionControlSystem.IsRestoringUplink)
                    {
                        MachineRegistry.RecordControlled(id);
                        MachineRegistry.TryMarkExperience(id, MachineExperienceFlags.Controlled);
                    }
                },
                OnReleased = null,
                FallbackAnchor = () => ExitPoint,
                JamGraceSeconds = 2f,
            });
        }

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
                Vector3 v = _possessed.View != null ? _possessed.View.transform.position : _possessed.Position3;
                anchor = new float2(v.x, v.z);
                return true;
            }
            anchor = float2.zero;
            return false;
        }

        private bool EnsureDirectTarget()
        {
            int id = _selected != null ? _selected.LogicId : SquadCommands.Selection.Count > 0 ? SquadCommands.Selection.Min() : 0;
            return Signal.SignalUplinkService.RequestFromSelection(id);
        }

        /// <summary>左键：点机器 = 选中（Shift 加选），点出口附近 = 打开出口面板，点空地 = 取消选中。</summary>
        private void HandleSelectionClick()
        {
            if (_camera == null || SquadCommands.ConsumedClickThisFrame || !InputRouter.GetMouseButtonUp(0, InputScope.Strategy)
                || !InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer))
            {
                return;
            }
            bool additive = InputRouter.Reader.GetKey(KeyCode.LeftShift) || InputRouter.Reader.GetKey(KeyCode.RightShift);
            Ray ray = _camera.ScreenPointToRay(pointer);
            HomeValleyMachineMarker marker = Physics.Raycast(ray, out RaycastHit hit, 500f) ? hit.collider.GetComponent<MachineView>()?.Marker : null;
            if (marker != null)
            {
                _selected?.SetSelected(false);
                _selected = marker;
                _selected.SetSelected(true);
                SquadCommands.SelectSingle(marker.LogicId, additive);
                return;
            }
            if (Mathf.Abs(ray.direction.y) > 1e-4f)
            {
                float t = -ray.origin.y / ray.direction.y;
                Vector3 g = ray.origin + ray.direction * t;
                float pick = GridContent.TryGetTuning("interior.pick_radius", out float pr) ? pr : 5f;
                if (t > 0f && (new Vector2(g.x, g.z) - ExitPoint).sqrMagnitude <= pick * pick)
                {
                    InteriorSiteService.RequestExitPanel(SiteId);
                    return;
                }
            }
            if (!additive)
            {
                _selected?.SetSelected(false);
                _selected = null;
                SquadCommands.ClearSelection();
            }
        }

        private void HandleDirectControl()
        {
            if (_possessed == null)
            {
                return;
            }
            if (InputRouter.ConsumeAction(GameActionId.CycleControlTarget, InputScope.Direct))
            {
                Signal.SignalUplinkService.RequestCycle();
            }
            if (Signal.SignalUplinkService.IsPending)
            {
                _possessed.SetDirectInput(Vector2.zero);
                return;
            }
            float x = 0f, z = 0f;
            if (InputRouter.GetActionKey(GameActionId.MoveLeft, InputScope.Direct)) { x -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveRight, InputScope.Direct)) { x += 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Direct)) { z -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Direct)) { z += 1f; }
            _possessed.SetDirectInput(new Vector2(x, z));
        }

        // ── 表现（占位几何体，B22）──────────────────────────────────────────────

        private void BuildVisuals(CampaignState state)
        {
            _root = new GameObject("[InteriorRoot_" + SiteId + "]");
            Color floor = new Color(0.16f, 0.17f, 0.2f);
            Color wall = new Color(0.32f, 0.34f, 0.38f);
            Color pillar = new Color(0.42f, 0.44f, 0.5f);
            Box("Floor", new Vector3(Width / 2f, -0.05f, Height / 2f), new Vector3(Width, 0.1f, Height), floor, collider: true);
            int half = Width / 2;
            Box("WallSouthL", new Vector3((half - 2) / 2f, 1f, 0f), new Vector3(half - 2, 2f, 1f), wall, collider: false);
            Box("WallSouthR", new Vector3((Width + half + 2) / 2f, 1f, 0f), new Vector3(Width - half - 2, 2f, 1f), wall, collider: false);
            Box("WallNorth", new Vector3(Width / 2f, 1f, Height), new Vector3(Width, 2f, 1f), wall, collider: false);
            Box("WallWest", new Vector3(0f, 1f, Height / 2f), new Vector3(1f, 2f, Height), wall, collider: false);
            Box("WallEast", new Vector3(Width, 1f, Height / 2f), new Vector3(1f, 2f, Height), wall, collider: false);
            foreach (Vector2 p in _pillars)
            {
                Box("Pillar", new Vector3(p.x, 1.5f, p.y), new Vector3(2f, 3f, 2f), pillar, collider: false);
            }
            // 出口：入口格上的蓝色光环 + 门框（形状 + 颜色，B15）。
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "InteriorExitMarker";
            ring.transform.SetParent(_root.transform, false);
            ring.transform.position = new Vector3(ExitPoint.x, 0.03f, ExitPoint.y);
            ring.transform.localScale = new Vector3(6f, 0.03f, 6f);
            UnityObjects.Release(ring.GetComponent<Collider>());
            ring.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(new Color(0.3f, 0.75f, 0.95f, 0.6f));
            Box("ExitFrameL", new Vector3(ExitPoint.x - 2.5f, 1.5f, 0.2f), new Vector3(0.6f, 3f, 0.6f), new Color(0.3f, 0.75f, 0.95f), collider: false);
            Box("ExitFrameR", new Vector3(ExitPoint.x + 2.5f, 1.5f, 0.2f), new Vector3(0.6f, 3f, 0.6f), new Color(0.3f, 0.75f, 0.95f), collider: false);
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
            _content?.BuildVisuals(state, _root.transform);
        }

        private void Box(string name, Vector3 at, Vector3 size, Color color, bool collider)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = at;
            go.transform.localScale = size;
            if (!collider)
            {
                UnityObjects.Release(go.GetComponent<Collider>());
            }
            go.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(color);
        }

        private void BuildMachineView(HomeValleyMachineMarker marker, MachineRecord machine)
        {
            if (_root == null)
            {
                return;
            }
            Vector2 at = marker.Position;
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Machine_" + machine.ChassisId + "_" + machine.LogicId;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(at.x, 1f, at.y);
            Renderer renderer = go.GetComponent<Renderer>();
            Color baseColor = new Color(0.72f, 0.82f, 0.9f);
            renderer.sharedMaterial = ViewMaterials.Standard(baseColor);
            PlaceholderSilhouette.ApplyMachine(go, renderer, machine.ChassisId);
            MachineView view = go.AddComponent<MachineView>();
            view.Initialize(renderer, baseColor);
            marker.AttachView(view);
            _combat?.BindView(marker.UnitId, go.transform, 1f);
        }

        private void DestroyVisuals()
        {
            _content?.DestroyVisuals();
            foreach (HomeValleyMachineMarker m in _machineMarkers)
            {
                m?.DetachView();
            }
            if (_root != null)
            {
                UnityObjects.Release(_root);
                _root = null;
            }
        }
    }

    /// <summary>室内场地的战斗结算挂点：首领内容到来之前没有具名敌人，全部用默认行为。</summary>
    public sealed class InteriorCombatRules : CombatSiteRules
    {
    }
}
