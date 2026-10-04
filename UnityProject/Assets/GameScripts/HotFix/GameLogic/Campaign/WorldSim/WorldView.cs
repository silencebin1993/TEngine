using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002 镜头部分；FG17 FGR-GEN-080 点击 / 通知飞过去）：全局的镜头与输入管理器。
    ///
    /// - **唯一的镜头**：整个世界只有一个 <see cref="CameraDirector"/>（Demo 是每个区域控制器各建一个）。镜头观察哪个地点，
    ///   就按那个地点的 <see cref="WorldCameraProfile"/> 绑定；换地点时记住原地点的焦点与缩放，回来时恢复。
    /// - **观察 ≠ 运行**：换观察地点只切换表现对象的可见性，不载入 / 卸载任何地点（<see cref="WorldSimulation"/> 始终推进全部地点）。
    /// - **输入**：暂停（Space）、回到归还核心（Home）、切换关注点（Tab）在这里统一处理；InputRouter 的暂停态每帧按统一时钟同步。
    /// - **飞跃**：同一表面内平滑飞过去（camera.fly_seconds，FG17 初值 0.5 秒）；换表面（Demo 的远征地点是独立表面）先切表面再飞。
    /// - **只渲染附近区块**：星球表面由 <see cref="WorldPlanetView"/> 按镜头窗口画地貌与队伍标记；镜头在别的表面时整层隐藏。
    /// 每帧开销 O(关注点数 + 已探索区域数)，与实体数无关。
    /// </summary>
    public static class WorldView
    {
        private struct CameraMemory
        {
            public float2 Focus;
            public float Ortho;
        }

        /// <summary>镜头可以飞去的一个关注点（家园 / 远征地点 / 行进中的突袭）。</summary>
        public readonly struct FocusTarget
        {
            public readonly string Id;
            public readonly string SiteId;
            public readonly Vector2 Position;
            public readonly string Label;
            public readonly bool IsRaid;

            public FocusTarget(string id, string siteId, Vector2 position, string label, bool isRaid)
            {
                Id = id;
                SiteId = siteId;
                Position = position;
                Label = label;
                IsRaid = isRaid;
            }
        }

        private static readonly CameraDirector DirectorInstance = new CameraDirector();
        private static readonly Dictionary<string, CameraMemory> Memory = new Dictionary<string, CameraMemory>(StringComparer.Ordinal);
        private static readonly List<FocusTarget> TargetsScratch = new List<FocusTarget>(8);
        private static string _observedId;
        private static string _lastFocusTargetId;
        private static Camera _camera;
        /// <summary>最近一次飞去的星球表面位置：镜头范围总包含它（通知定位 / 飞到远处的己方机器时不被“已探索区域”钳回来）。</summary>
        private static Vector2? _planetFlyPin;
        /// <summary>镜头焦点已经到过飞去的位置（飞之前的边界计算不算“回到已探索范围”，否则一飞就被清掉）。</summary>
        private static bool _planetFlyPinReached;

        public static CameraDirector Director => DirectorInstance;
        public static Camera Camera => _camera;
        public static string ObservedSiteId => _observedId;
        public static IWorldSite ObservedSite => WorldSimulation.FindSite(_observedId);
        public static bool IsObserved(string siteId) => !string.IsNullOrEmpty(siteId) && siteId == _observedId;

        /// <summary>观察地点切换次数、飞跃次数、最近一次关注点（HUD 与自检用）。</summary>
        public static int ObserveSwitchCount { get; private set; }
        public static int FlyCount { get; private set; }
        public static string LastFocusTargetId => _lastFocusTargetId;
        /// <summary>全灭后镜头的处理（自检用）：最近一次因地点卸载而自动回到家园的次数。</summary>
        public static int FallbackToHomeCount { get; private set; }

        /// <summary>星球镜头焦点所在的格（活跃区块、流式加载、地貌窗口都跟它走）。</summary>
        public static GridCell PlanetFocusCell => CameraFocusCell(_camera);

        /// <summary>相机中心射线在地面上的焦点，适用于倾斜的正交与透视镜头。</summary>
        public static GridCell CameraFocusCell(Camera camera)
        {
            if (camera == null)
            {
                return new GridCell(0, 0);
            }
            Vector3 p = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            if (Mathf.Abs(forward.y) > 0.001f)
            {
                p -= forward * (p.y / forward.y);
            }
            return GridCell.FromWorld(new Vector2(p.x, p.z));
        }

        public static Camera EnsureCamera()
        {
            if (_camera != null)
            {
                return _camera;
            }
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                _camera = go.GetComponent<Camera>();
            }
            return _camera;
        }

        // ── 观察 ────────────────────────────────────────────────────────────────

        /// <summary>把镜头换到 <paramref name="siteId"/>（已载入的地点）。已经在观察它时只返回 true。</summary>
        public static bool Observe(string siteId)
        {
            IWorldSite next = WorldSimulation.FindSite(siteId);
            if (next == null || !next.IsLoaded)
            {
                return false;
            }
            if (_observedId == siteId && DirectorInstance.IsBound)
            {
                return true;
            }
            IWorldSite prev = WorldSimulation.FindSite(_observedId);
            bool hadPrev = prev != null && prev.IsLoaded;
            if (hadPrev)
            {
                Memory[_observedId] = new CameraMemory { Focus = DirectorInstance.StrategyFocus, Ortho = DirectorInstance.StrategyOrthographicSize };
                prev.SetObserved(false); // 释放接入、收起本地点面板与建造模式（在镜头解绑之前做，接入释放还要用镜头）。
            }
            DirectorInstance.Unbind(resetInput: false);
            _observedId = siteId;

            WorldCameraProfile profile = next.CameraProfile ?? new WorldCameraProfile();
            Camera cam = EnsureCamera();
            cam.orthographic = false;
            cam.fieldOfView = 35f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = profile.Background;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            // 仅偏离垂直俯视 12°，露出模型侧面，同时保留俯视操作的可读性。
            cam.transform.rotation = Quaternion.Euler(78f, 0f, 0f);
            bool remembered = Memory.TryGetValue(siteId, out CameraMemory mem);
            cam.orthographicSize = remembered ? mem.Ortho : profile.InitialStrategyOrthographicSize;
            DirectorInstance.Bind(cam, profile.DirectAnchor, profile.FollowOffset, profile.ArenaHalfExtent,
                startInStrategy: true, initialDirectOrthographicSize: profile.InitialDirectOrthographicSize);
            DirectorInstance.EnsureDirectTarget = profile.EnsureDirectTarget;
            ApplyBounds(profile);
            float2 focus = remembered ? mem.Focus : new float2(profile.StartFocus.x, profile.StartFocus.y);
            DirectorInstance.SetStrategyView(focus, remembered ? mem.Ortho : profile.InitialStrategyOrthographicSize);

            next.SetObserved(true);
            WorldPlanetView.SetVisible(next.SurfaceKind == WorldSurfaceKind.Planet);
            if (hadPrev)
            {
                ObserveSwitchCount++;
                GuidanceHooks.Raise(GuidanceHooks.WorldFirstFocusSwitch);
            }
            return true;
        }

        /// <summary>镜头飞到 <paramref name="siteId"/> 的 <paramref name="position"/>（地面 XZ）。换表面时先切表面。</summary>
        public static bool FlyTo(string siteId, Vector2 position)
        {
            if (!Observe(siteId))
            {
                return false;
            }
            FlyCount++;
            // FG1-E2E-01（FGJ-M1 旅程发现）：镜头被明确送去别处（回到核心、Tab 换关注点、点通知定位、信号跳转……）= 不再跟随选中对象，
            // 与“玩家自己平移镜头 = 停止跟随”同一手感；否则跟随会在飞到之后把镜头又拽回编队，“回到核心”键看起来失灵。
            DirectorInstance.StopFollow();
            IWorldSite site = ObservedSite;
            if (site != null && site.SurfaceKind == WorldSurfaceKind.Planet)
            {
                // FGR-GEN-080“点击任意位置或通知，镜头飞过去”：目标可能在已探索区域之外（远处的己方机器、事件），先把它放进镜头范围再飞。
                _planetFlyPin = position;
                _planetFlyPinReached = false;
                if (site.CameraProfile != null)
                {
                    ApplyBounds(site.CameraProfile);
                }
            }
            return DirectorInstance.FlyStrategyTo(new float2(position.x, position.y), GameClock.TuningOr("camera.fly_seconds", 0.5f));
        }

        /// <summary>
        /// FG1-SIG-07（FGR-SIG-051 跨地点的远距离跳转）：把镜头切到 <paramref name="siteId"/>，战略焦点直接放在 <paramref name="position"/>（不走飞行过渡）——
        /// 调用方随后让镜头进直控对准目标机器（战略飞行落地会被地点当成“玩家退出接入”）。星球表面上先把落点放进可平移范围。
        /// </summary>
        public static bool ObserveAt(string siteId, Vector2 position)
        {
            IWorldSite site = WorldSimulation.FindSite(siteId);
            if (site == null || !site.IsLoaded)
            {
                return false;
            }
            if (site.SurfaceKind == WorldSurfaceKind.Planet)
            {
                _planetFlyPin = position;
                _planetFlyPinReached = false;
            }
            if (!Observe(siteId))
            {
                return false;
            }
            DirectorInstance.StopFollow(); // 同 FlyTo：镜头被送去别处就不再跟随选中对象。
            if (site.CameraProfile != null)
            {
                ApplyBounds(site.CameraProfile);
            }
            DirectorInstance.SetStrategyView(new float2(position.x, position.y), DirectorInstance.StrategyOrthographicSize);
            FlyCount++;
            return true;
        }

        /// <summary>FG1-SIG-07：星球表面上的远距离跳转——落点先放进镜头可平移范围（离开接入回到战略时不被“已探索区域”钳回去）。</summary>
        public static void PinPlanet(Vector2 position)
        {
            _planetFlyPin = position;
            _planetFlyPinReached = false;
            IWorldSite observed = ObservedSite;
            if (observed != null && observed.SurfaceKind == WorldSurfaceKind.Planet && observed.CameraProfile != null)
            {
                ApplyBounds(observed.CameraProfile);
            }
        }

        public static bool FocusHomeCore()
        {
            CampaignState state = CampaignSession.Current;
            IWorldSite home = WorldSimulation.Home;
            if (state == null || home == null || !home.IsLoaded)
            {
                return false;
            }
            _lastFocusTargetId = "home";
            return FlyTo(home.SiteId, home.DefaultFocus);
        }

        /// <summary>通知“定位”（FGR-UX-020）：飞到事件所在地点的位置。地点已不在运行时给出原因文本键。</summary>
        public static bool Locate(string regionId, Vector3 position, out string failureKey)
        {
            failureKey = null;
            string site = string.IsNullOrEmpty(regionId) ? _observedId : regionId;
            if (!WorldSimulation.IsSiteLoaded(site))
            {
                failureKey = "ui.world.site_unloaded";
                return false;
            }
            if (!FlyTo(site, new Vector2(position.x, position.z)))
            {
                failureKey = "ui.notify.no_camera";
                return false;
            }
            return true;
        }

        // ── 关注点 ──────────────────────────────────────────────────────────────

        /// <summary>当前可飞去的关注点：家园（归还核心）、每个在外的远征地点、每支行进中 / 已到达的突袭。</summary>
        public static IReadOnlyList<FocusTarget> FocusTargets(CampaignState state)
        {
            TargetsScratch.Clear();
            IWorldSite home = WorldSimulation.Home;
            if (home != null && home.IsLoaded)
            {
                TargetsScratch.Add(new FocusTarget("home", home.SiteId, home.DefaultFocus, GameText.Get("ui.world.focus.home"), false));
            }
            IWorldSite exp = WorldSimulation.ActiveExpedition;
            if (exp != null)
            {
                string name = GameText.Get("world.site." + exp.SiteId + ".name");
                TargetsScratch.Add(new FocusTarget("site:" + exp.SiteId, exp.SiteId, exp.DefaultFocus,
                    GameText.Format("ui.world.focus.expedition", name, exp.LiveMachineCount.ToString(CultureInfo.InvariantCulture)), false));
            }
            if (home != null && home.IsLoaded)
            {
                foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
                {
                    if (g == null)
                    {
                        continue;
                    }
                    string count = g.UnitCount.ToString(CultureInfo.InvariantCulture);
                    string label = g.State == TransitGroupState.Arrived
                        ? GameText.Format("ui.world.focus.raid_arrived", count)
                        : GameText.Format("ui.world.focus.raid", count, GameClock.FormatGameDuration(WorldTransitSystem.EtaSeconds(g)));
                    TargetsScratch.Add(new FocusTarget(g.GroupId, home.SiteId, WorldTransitSystem.Position(g), label, true));
                }
            }
            return TargetsScratch;
        }

        /// <summary>镜头飞到指定关注点（按 ID，关注点条按钮用）。</summary>
        public static bool FocusOn(string targetId)
        {
            CampaignState state = CampaignSession.Current;
            foreach (FocusTarget t in FocusTargets(state))
            {
                if (t.Id == targetId)
                {
                    _lastFocusTargetId = t.Id;
                    return FlyTo(t.SiteId, t.Position);
                }
            }
            return false;
        }

        /// <summary>依次切换关注点（Tab）：家园 → 远征 → 各支突袭 → 家园……</summary>
        public static bool CycleFocus()
        {
            IReadOnlyList<FocusTarget> targets = FocusTargets(CampaignSession.Current);
            if (targets.Count == 0)
            {
                return false;
            }
            int current = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Id == _lastFocusTargetId)
                {
                    current = i;
                    break;
                }
            }
            if (current < 0)
            {
                // 还没切过：从当前观察的地点所对应的关注点开始。
                for (int i = 0; i < targets.Count; i++)
                {
                    if (!targets[i].IsRaid && targets[i].SiteId == _observedId)
                    {
                        current = i;
                        break;
                    }
                }
            }
            FocusTarget next = targets[(current + 1 + targets.Count) % targets.Count];
            _lastFocusTargetId = next.Id;
            return FlyTo(next.SiteId, next.Position);
        }

        // ── 每帧 ────────────────────────────────────────────────────────────────

        /// <summary>模拟推进之前：全局快捷键、InputRouter 暂停态、镜头驱动、接入锁 1x。</summary>
        public static void FrameBegin(CampaignState state)
        {
            IWorldSite observed = ObservedSite;
            if (observed == null || !observed.IsLoaded)
            {
                FallbackToHome();
                observed = ObservedSite;
            }
            if (InputRouter.ConsumeAction(GameActionId.TogglePause, InputScope.Strategy))
            {
                GameClock.TogglePause();
            }
            if (InputRouter.ConsumeAction(GameActionId.FocusHomeCore, InputScope.Strategy))
            {
                FocusHomeCore();
            }
            if (InputRouter.ConsumeAction(GameActionId.CycleWorldFocus, InputScope.Strategy))
            {
                CycleFocus();
            }
            if (InputRouter.ConsumeAction(GameActionId.FollowSelection, InputScope.Strategy))
            {
                ToggleFollowSelection();
            }
            InputRouter.SetGameplayPaused(GameClock.Paused, strategic: true);
            if (observed != null && observed.CameraProfile != null)
            {
                ApplyBounds(observed.CameraProfile);
            }
            DirectorInstance.Tick(GameClock.Paused);
            UpdatePlanetFlyPin(state, observed);
            GameClock.SetDirectLocked(DirectorInstance.IsBound && DirectorInstance.Mode == ViewMode.Direct);
        }

        // ── FG1-HUD-01：跟随选中对象（默认 F，战略上下文，可重绑）──

        /// <summary>自检读点：按“跟随选中对象”开始跟随的次数。</summary>
        public static int FollowStartCount { get; private set; }

        /// <summary>
        /// 按“跟随选中对象”：没在跟随 → 镜头跟随当前地点里选中的机器（多选时跟随它们的中心）；正在跟随 → 停止。
        /// 选中集合每帧现取（框选改了就跟新的），选中的机器都没了 / 取消选中 → 自动停止；玩家平移镜头也停止。没有选中时说明原因（不静默）。
        /// 每帧 O(选中数)，只在跟随中。
        /// </summary>
        public static bool ToggleFollowSelection()
        {
            if (DirectorInstance.IsFollowing)
            {
                DirectorInstance.StopFollow();
                Signal.SignalUplinkService.PushFeedback(GameText.Get("camera.follow.stopped"));
                return false;
            }
            string site = _observedId;
            if (!TrySelectionCenter(site, out _, out int count, out int first))
            {
                Signal.SignalUplinkService.PushFeedback(GameText.Format("camera.follow.nothing", InputDisplay.ForAction(GameActionId.FollowSelection)));
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, GameText.Format("camera.follow.nothing", InputDisplay.ForAction(GameActionId.FollowSelection)));
                return false;
            }
            DirectorInstance.StartFollow(() =>
            {
                Vector2 c = Vector2.zero;
                bool ok = site == _observedId && TrySelectionCenter(site, out c, out _, out _);
                return (ok, ok ? new float2(c.x, c.y) : float2.zero);
            });
            FollowStartCount++;
            string who = count > 1 ? GameText.Format("camera.follow.group", count) : Signal.SignalPresence.MachineLabel(first);
            Signal.SignalUplinkService.PushFeedback(GameText.Format("camera.follow.started", who, InputDisplay.ForAction(GameActionId.FollowSelection)));
            return true;
        }

        /// <summary>地点里选中的机器（编队框选集合优先，没有时取家园单选）的实时位置中心。</summary>
        public static bool TrySelectionCenter(string siteId, out Vector2 center, out int count, out int firstLogicId)
        {
            center = Vector2.zero;
            count = 0;
            firstLogicId = 0;
            IWorldSite site = WorldSimulation.FindSite(siteId);
            if (site == null)
            {
                return false;
            }
            IReadOnlyList<int> selection = Combat.CombatSites.Get(siteId)?.Squad?.Selection;
            Vector2 sum = Vector2.zero;
            if (selection != null)
            {
                for (int i = 0; i < selection.Count; i++)
                {
                    Vector2? p = site.LivePosition(selection[i]);
                    if (p.HasValue)
                    {
                        sum += p.Value;
                        if (count == 0)
                        {
                            firstLogicId = selection[i];
                        }
                        count++;
                    }
                }
            }
            if (count == 0 && siteId == Regions.HomeValleyLayout.RegionId && Stage.GameRoot.HomeValley?.SelectedMachineLogicId is int single)
            {
                Vector2? p = site.LivePosition(single);
                if (p.HasValue)
                {
                    sum = p.Value;
                    firstLogicId = single;
                    count = 1;
                }
            }
            if (count == 0)
            {
                return false;
            }
            center = sum / count;
            return true;
        }

        /// <summary>模拟推进之后：观察的地点若已卸载（撤离 / 放弃远征）就回到家园；星球表现层跟随镜头。</summary>
        public static void FrameEnd(CampaignState state)
        {
            IWorldSite observed = ObservedSite;
            if (observed == null || !observed.IsLoaded)
            {
                FallbackToHome();
                observed = ObservedSite;
            }
            bool planet = observed != null && observed.SurfaceKind == WorldSurfaceKind.Planet;
            WorldPlanetView.SetVisible(planet);
            if (planet && _camera != null)
            {
                WorldPlanetView.Tick(state, PlanetFocusCell);
            }
        }

        /// <summary>观察的地点已卸载（撤离 / 放弃远征 / 暂离）时立即回到家园（不等下一帧）。</summary>
        public static void EnsureObservedLoaded()
        {
            IWorldSite observed = ObservedSite;
            if (observed == null || !observed.IsLoaded)
            {
                FallbackToHome();
            }
        }

        private static void FallbackToHome()
        {
            IWorldSite home = WorldSimulation.Home;
            if (home != null && home.IsLoaded && _observedId != home.SiteId)
            {
                bool had = !string.IsNullOrEmpty(_observedId);
                Memory.Remove(_observedId ?? string.Empty);
                _observedId = null; // 原地点已卸载，没有可释放的表现对象。
                if (Observe(home.SiteId) && had)
                {
                    FallbackToHomeCount++;
                }
            }
        }

        private static void ApplyBounds(WorldCameraProfile profile)
        {
            if (profile.DynamicBounds == null || !DirectorInstance.IsBound)
            {
                return;
            }
            (Vector2 min, Vector2 max) = profile.DynamicBounds();
            DirectorInstance.SetStrategyBounds(min, max);
        }

        /// <summary>星球表面的镜头可平移范围：已探索区域（外接矩形）+ camera.explored_margin_cells，并包含行进中的队伍、归还核心与最近一次飞去的位置。
        /// 不启用浮动原点，所以同时钳在 camera.precision_safe_cells 以内（DEBT-FG0ARCH01-02）。</summary>
        public static (Vector2 Min, Vector2 Max) PlanetBounds(CampaignState state) => PlanetBoundsCore(state, includePin: true);

        private static (Vector2 Min, Vector2 Max) PlanetBoundsCore(CampaignState state, bool includePin)
        {
            GridCell core = HomeGridService.CorePivot(state);
            float minX = core.X, minY = core.Y, maxX = core.X, maxY = core.Y;
            ExploredAreaRecord[] explored = state?.Grid?.Explored ?? Array.Empty<ExploredAreaRecord>();
            foreach (ExploredAreaRecord a in explored)
            {
                if (a == null)
                {
                    continue;
                }
                minX = Mathf.Min(minX, a.CenterX - a.Radius);
                minY = Mathf.Min(minY, a.CenterY - a.Radius);
                maxX = Mathf.Max(maxX, a.CenterX + a.Radius);
                maxY = Mathf.Max(maxY, a.CenterY + a.Radius);
            }
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (g == null)
                {
                    continue;
                }
                minX = Mathf.Min(minX, (float)g.PosX);
                minY = Mathf.Min(minY, (float)g.PosY);
                maxX = Mathf.Max(maxX, (float)g.PosX);
                maxY = Mathf.Max(maxY, (float)g.PosY);
            }
            if (includePin && _planetFlyPin.HasValue)
            {
                Vector2 pin = _planetFlyPin.Value;
                minX = Mathf.Min(minX, pin.x);
                minY = Mathf.Min(minY, pin.y);
                maxX = Mathf.Max(maxX, pin.x);
                maxY = Mathf.Max(maxY, pin.y);
            }
            float margin = GameClock.TuningOr("camera.explored_margin_cells", 24f);
            float safe = GameClock.TuningOr("camera.precision_safe_cells", 16384f);
            return (new Vector2(Mathf.Max(-safe, minX - margin), Mathf.Max(-safe, minY - margin)),
                new Vector2(Mathf.Min(safe, maxX + margin), Mathf.Min(safe, maxY + margin)));
        }

        /// <summary>
        /// 飞跃落点的去留（每帧镜头推进之后判断；<see cref="PlanetBounds"/> 只做查询、不改状态）：焦点到过落点之后，
        /// 一旦回到“已探索范围 + 边距”（不算落点）就清掉——否则可平移范围一直是“核心到那一点”的大矩形，能一路平移到未探索区域上空。
        /// 换地点时 Bind 会把焦点临时清零，所以不能在边界计算里顺手判断（会把刚恢复的远处焦点钳回来）。
        /// </summary>
        private static void UpdatePlanetFlyPin(CampaignState state, IWorldSite observed)
        {
            if (!_planetFlyPin.HasValue || observed == null || observed.SurfaceKind != WorldSurfaceKind.Planet || !DirectorInstance.IsBound)
            {
                return;
            }
            Vector2 pin = _planetFlyPin.Value;
            float2 focus = DirectorInstance.StrategyFocus;
            if (!_planetFlyPinReached)
            {
                _planetFlyPinReached = math.distance(focus, new float2(pin.x, pin.y)) < 1f;
                return;
            }
            (Vector2 min, Vector2 max) = PlanetBoundsCore(state, includePin: false);
            if (focus.x >= min.x && focus.x <= max.x && focus.y >= min.y && focus.y <= max.y)
            {
                _planetFlyPin = null;
            }
        }

        /// <summary>离开世界（回主菜单 / 自检之间）：解绑镜头并复位输入，清空地点记忆，销毁星球表现层。</summary>
        public static void Reset()
        {
            if (DirectorInstance.IsBound)
            {
                DirectorInstance.Unbind(resetInput: true);
            }
            Memory.Clear();
            _observedId = null;
            _lastFocusTargetId = null;
            _planetFlyPin = null;
            _planetFlyPinReached = false;
            WorldPlanetView.Shutdown();
            GameClock.SetDirectLocked(false);
        }
    }
}
