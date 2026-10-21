using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG8-GEN-02：地点与领地的界面（UI Toolkit，一个 UIDocument）——
    /// <list type="number">
    /// <item><b>领地名称提示</b>（FG17 第 4 节）：镜头 / 接入的机器所在点进入某个阵营领地时屏幕上方显示“进入 静默·破碎都市”和一句介绍，离开时“离开 …”，
    ///   接近外圈危害带时“接近 酸沼带：效果……”，进入外延区时一句说明；停留 territory.banner_seconds 真实秒。纯表现：只读规划层（每 0.25 真实秒一次 O(领地数)），不改任何状态。</item>
    /// <item><b>室内进出的过渡</b>（FGR-GEN-042，星球上唯一不无缝的地方）：<see cref="InteriorSiteService.BeginTransition"/> 之后全屏渐暗 + 一行文字，世界照常运行。</item>
    /// <item><b>室内入口 / 出口面板</b>：左键点入口（地表）或出口（室内）打开；写名字、说明、入口旁有几台机器、进入时自动存档、入口保护范围；
    ///   “进入 / 离开室内”按钮，不能进时写原因（B06）。Esc / 关闭 / 点遮罩关闭；面板开着时每 0.25 真实秒刷新一次。</item>
    /// </list>
    /// </summary>
    public sealed class WorldPlacesHudUIToolkit : UiKitPanelHost
    {
        /// <summary>战略地图 30050 之上、暂停菜单 30070 之下（入口面板是模态，盖在 HUD 上）。</summary>
        public const int Order = 30052;

        public static WorldPlacesHudUIToolkit Instance { get; private set; }
        public static bool PanelOpen { get; private set; }

        /// <summary>自检：编辑模式下没有载入的地点时也允许打开面板。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _banner, _fade, _panel;
        private Label _bannerTitle, _bannerSub, _fadeText, _title, _desc, _near, _info, _protect, _reason;
        private Button _close, _go;
        private string _panelTarget;
        private bool _panelExit;
        private float _panelRefresh;
        private float _bannerUntil = -1f;
        private float _scanTimer;
        private string _lastTerritory;
        private bool _lastOuter;
        private string _lastBelt;
        private bool _haveFocus;

        protected override string UxmlLocation => "WorldPlacesHud";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool BannerVisible => _banner != null && !_banner.ClassListContains("uk-hidden");
        public string BannerTitleText => _bannerTitle?.text ?? string.Empty;
        public string BannerSubText => _bannerSub?.text ?? string.Empty;
        public bool FadeVisible => _fade != null && !_fade.ClassListContains("uk-hidden");
        public string FadeText => _fadeText?.text ?? string.Empty;
        public string PanelTitleText => _title?.text ?? string.Empty;
        public string PanelNearText => _near?.text ?? string.Empty;
        public string PanelReasonText => _reason != null && !_reason.ClassListContains("uk-hidden") ? _reason.text : string.Empty;
        public string PanelProtectText => _protect != null && !_protect.ClassListContains("uk-hidden") ? _protect.text : string.Empty;
        public bool GoEnabled => _go != null && _go.enabledSelf;
        public string GoText => _go?.text ?? string.Empty;
        public Button GoButton => _go;
        public Button CloseButton => _close;
        public int BannerShowCount { get; private set; }

        private void Awake()
        {
            Instance = this;
            InteriorSiteService.PanelRequested += OnPanelRequested;
        }

        protected override void OnDestroy()
        {
            InteriorSiteService.PanelRequested -= OnPanelRequested;
            if (PanelOpen && Instance == this)
            {
                SetPanel(null, false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        protected override void OnReady(VisualElement root) => BindView(root);

        public void BindView(VisualElement root)
        {
            Instance = this;
            _banner = root.Q<VisualElement>("PlaceBanner");
            _bannerTitle = root.Q<Label>("PlaceBannerTitle");
            _bannerSub = root.Q<Label>("PlaceBannerSub");
            _fade = root.Q<VisualElement>("InteriorFade");
            _fadeText = root.Q<Label>("InteriorFadeText");
            _panel = root.Q<VisualElement>("EntranceRoot");
            _title = root.Q<Label>("EntranceTitle");
            _desc = root.Q<Label>("EntranceDesc");
            _near = root.Q<Label>("EntranceNear");
            _info = root.Q<Label>("EntranceInfo");
            _protect = root.Q<Label>("EntranceProtect");
            _reason = root.Q<Label>("EntranceReason");
            _close = root.Q<Button>("EntranceClose");
            _go = root.Q<Button>("EntranceGo");
            if (_close != null)
            {
                _close.text = GameText.Get("ui.interior.close");
                _close.clicked += () => SetPanel(null, false);
            }
            if (_go != null)
            {
                _go.clicked += OnGo;
            }
            _panel?.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _panel)
                {
                    SetPanel(null, false);
                }
            });
        }

        private static bool InWorld() => InWorldOverrideForTests || (CampaignSession.Current != null && GameRoot.AnyRegionActive);

        private void OnPanelRequested(string target, bool isExit)
        {
            if (!InWorld())
            {
                return;
            }
            SetPanel(target, isExit);
        }

        /// <summary>打开（<paramref name="target"/> = 入口锚点 ID / 室内表面 ID）或关闭（null）入口 / 出口面板。</summary>
        public void SetPanel(string target, bool isExit)
        {
            if (_panel == null)
            {
                return;
            }
            bool open = !string.IsNullOrEmpty(target);
            _panelTarget = target;
            _panelExit = isExit;
            if (open != PanelOpen)
            {
                PanelOpen = open;
                _panel.EnableInClassList("uk-hidden", !open);
                if (open)
                {
                    InputRouter.PushModal(this);
                    UiEscapeStack.Push(this, () => SetPanel(null, false));
                }
                else
                {
                    InputRouter.PopModal(this);
                    UiEscapeStack.Remove(this);
                }
            }
            if (open)
            {
                Refresh(verify: true); // 点入口打开面板 = 玩家动作：失败过的入口重新校验一次
            }
        }

        private void OnGo()
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || string.IsNullOrEmpty(_panelTarget))
            {
                return;
            }
            string reason;
            bool ok = _panelExit ? InteriorSiteService.TryExit(s, _panelTarget, out reason) : InteriorSiteService.TryEnter(s, _panelTarget, out reason);
            if (ok)
            {
                SetPanel(null, false);
                return;
            }
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            ShowReason(reason);
        }

        private void ShowReason(string reason)
        {
            if (_reason == null)
            {
                return;
            }
            _reason.text = reason ?? string.Empty;
            _reason.EnableInClassList("uk-hidden", string.IsNullOrEmpty(reason));
        }

        /// <summary>按当前状态刷新面板（定时刷新与自检直接调）：只读入口可达校验的结论，不发寻路请求（失败态下不重算、不刷日志）。</summary>
        public void RefreshPanel() => Refresh(verify: false);

        private void Refresh(bool verify)
        {
            CampaignState s = CampaignSession.Current;
            if (!PanelOpen || s == null || _title == null)
            {
                return;
            }
            if (_panelExit)
            {
                string surface = _panelTarget;
                _title.text = GameText.Format("ui.interior.title", InteriorSiteService.SurfaceName(surface));
                PlannedAnchor gate = InteriorSiteService.EntranceFor(s, surface);
                _desc.text = gate != null ? GameText.Get(gate.Row.DescKey) : string.Empty;
                List<int> near = InteriorSiteService.MachinesNearExit(s, surface);
                _near.text = GameText.Format("ui.interior.exit_near", Mathf.RoundToInt(InteriorSiteService.ExitRadius), near.Count);
                _info.text = GameText.Get("ui.interior.info");
                _protect.EnableInClassList("uk-hidden", true);
                _go.text = GameText.Format("ui.interior.exit", near.Count);
                string why = near.Count == 0 ? GameText.Format("interior.reason.exit_none_near", Mathf.RoundToInt(InteriorSiteService.ExitRadius)) : null;
                _go.SetEnabled(why == null);
                ShowReason(why);
                return;
            }
            PlannedAnchor a = WorldGenService.PlanFor(s)?.FindAnchor(_panelTarget);
            if (a == null)
            {
                _title.text = GameText.Get("interior.reason.not_found");
                _go.SetEnabled(false);
                return;
            }
            _title.text = GameText.Format("anchor.hover.title", GameText.Get(a.Row.NameKey), GameText.Get(a.KindKey));
            _desc.text = GameText.Get(a.Row.DescKey) + InteriorSiteService.BossPreview(CampaignSession.Current, a.SurfaceId); // FG9-FAC-01：首领情报提前警告
            List<int> nearIn = InteriorSiteService.MachinesNearEntrance(s, a);
            _near.text = GameText.Format("ui.interior.near", a.EntryRadius, nearIn.Count);
            string info = GameText.Get("ui.interior.info");
            _protect.text = GameText.Format("ui.interior.protect", a.ProtectRadius);
            _protect.EnableInClassList("uk-hidden", a.ProtectRadius <= 0);
            string reason = InteriorSiteService.CheckEnter(s, a, out List<int> ready, verify);
            // 进入会中断几台机器手上的工作（与远征出发同一条规则），事先写明（B06）。
            int interrupting = reason == null ? InteriorSiteService.CountInterrupting(s, ready) : 0;
            _info.text = interrupting > 0 ? info + "\n" + GameText.Format("ui.interior.interrupt", interrupting) : info;
            _go.text = GameText.Format("ui.interior.enter", ready.Count);
            _go.SetEnabled(reason == null);
            ShowReason(reason);
        }

        private void Update()
        {
            if (PanelOpen)
            {
                if (!InWorld())
                {
                    SetPanel(null, false);
                }
                else
                {
                    _panelRefresh -= Time.unscaledDeltaTime;
                    if (_panelRefresh <= 0f)
                    {
                        _panelRefresh = 0.25f;
                        RefreshPanel();
                    }
                }
            }
            TickFade();
            TickBanner();
        }

        private void TickFade()
        {
            if (_fade == null)
            {
                return;
            }
            bool on = InteriorSiteService.TransitionActive && InWorld();
            if (on)
            {
                _fadeText.text = InteriorSiteService.TransitionText;
            }
            _fade.EnableInClassList("uk-hidden", !on);
        }

        private void TickBanner()
        {
            if (_banner == null)
            {
                return;
            }
            if (_bannerUntil >= 0f && Time.unscaledTime >= _bannerUntil)
            {
                _bannerUntil = -1f;
                _banner.EnableInClassList("uk-hidden", true);
            }
            _scanTimer -= Time.unscaledDeltaTime;
            if (_scanTimer > 0f)
            {
                return;
            }
            _scanTimer = 0.25f;
            CampaignState s = CampaignSession.Current;
            if (s == null || !GameRoot.AnyRegionActive || !TryFocusPoint(s, out Vector2 p))
            {
                _haveFocus = false;
                return;
            }
            Scan(s, p);
        }

        /// <summary>“玩家在哪里”：接入的机器（在星球上时）的位置，否则星球镜头的焦点。不在星球上（室内 / Demo 地点）时没有。</summary>
        private static bool TryFocusPoint(CampaignState s, out Vector2 p)
        {
            p = default;
            IWorldSite observed = WorldView.ObservedSite;
            if (observed == null || observed.SurfaceKind != WorldSurfaceKind.Planet)
            {
                return false;
            }
            int linked = Campaign.Signal.SignalUplinkService.CurrentMachine(s);
            if (linked > 0 && MachineRegistry.TryGetLivePosition(linked, out Vector2 live)
                && MachineRegistry.TryGetRecord(linked, out MachineRecord rec) && rec.RegionId == HomeValleyLayout.RegionId)
            {
                p = live;
                return true;
            }
            var c = WorldView.PlanetFocusCell;
            p = new Vector2(c.X, c.Y);
            return true;
        }

        /// <summary>按焦点判定进入 / 离开领地、接近危害带、进入外延区（自检直接调）。第一次看到焦点时只记录、不弹提示（读档 / 开局不打扰）。</summary>
        public void Scan(CampaignState s, Vector2 p)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return;
            }
            int x = Mathf.FloorToInt(p.x);
            int y = Mathf.FloorToInt(p.y);
            PlannedTerritory t = plan.TerritoryAt(x, y);
            string territory = t != null && t.IsFaction ? t.Id : null;
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            long dx = x - plan.CoreX;
            long dy = y - plan.CoreY;
            int outerStart = ctx?.Row != null && WorldGenContent.HasTerritoryContent(ctx.Row) ? ctx.Row.OuterZoneDistance : int.MaxValue;
            bool outer = territory == null && dx * dx + dy * dy >= (long)outerStart * outerStart;
            string belt = BeltNear(plan, x, y);
            if (!_haveFocus)
            {
                _haveFocus = true;
                _lastTerritory = territory;
                _lastOuter = outer;
                _lastBelt = belt;
                return;
            }
            if (territory != _lastTerritory)
            {
                if (territory != null)
                {
                    TerritoryTemplate tpl = plan.TemplateFor(territory);
                    Show(GameText.Format("ui.territory.banner.enter", GameText.Get(t.NameKey)), tpl != null ? GameText.Get(tpl.IntroKey) : string.Empty);
                    GuidanceHooks.Raise(GuidanceHooks.WorldFirstTerritoryEntered);
                    Progression.MechanicCodex.OnTerritoryEntered(territory); // FG9-UX-01：第一次进入领地解锁区域 / 阵营条目
                }
                else if (_lastTerritory != null)
                {
                    PlannedTerritory left = plan.Find(_lastTerritory);
                    Show(GameText.Format("ui.territory.banner.leave", left != null ? GameText.Get(left.NameKey) : _lastTerritory), string.Empty);
                }
                _lastTerritory = territory;
            }
            else if (belt != null && belt != _lastBelt)
            {
                PlannedTerritory bt = plan.Find(belt);
                Hazard h = HomeExplorationService.HazardForBelt(bt?.HazardKind);
                Show(GameText.Format("ui.territory.banner.belt", bt != null ? GameText.Get(bt.HazardKey) : belt, h != null ? GameText.Get(h.EffectKey) : string.Empty),
                    h != null ? GameText.Get(h.CounterKey) : string.Empty);
            }
            else if (outer && !_lastOuter)
            {
                Show(GameText.Get("ui.territory.outer"), GameText.Get("ui.territory.banner.outer"));
            }
            _lastBelt = belt;
            _lastOuter = outer;
        }

        /// <summary>离哪条外圈危害带不到 territory.belt_warn_cells 格（含已经在带里）；没有返回 null。O(领地数)。</summary>
        private static string BeltNear(WorldPlan plan, int x, int y)
        {
            float warn = Campaign.Grid.GridContent.TryGetTuning("territory.belt_warn_cells", out float w) ? w : 40f;
            foreach (PlannedTerritory t in plan.Territories)
            {
                if (t == null || !t.IsFaction || t.HazardWidth <= 0)
                {
                    continue;
                }
                long dx = x - t.CenterX;
                long dy = y - t.CenterY;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > t.Radius && d <= t.OuterRadius + warn)
                {
                    return t.Id;
                }
            }
            return null;
        }

        private void Show(string title, string sub)
        {
            _bannerTitle.text = title ?? string.Empty;
            _bannerSub.text = sub ?? string.Empty;
            _bannerSub.EnableInClassList("uk-hidden", string.IsNullOrEmpty(sub));
            _banner.EnableInClassList("uk-hidden", false);
            float seconds = Campaign.Grid.GridContent.TryGetTuning("territory.banner_seconds", out float b) ? b : 4f;
            _bannerUntil = Time.unscaledTime + Mathf.Max(0.5f, seconds);
            BannerShowCount++;
        }

        /// <summary>自检：清掉“第一次看到焦点”的记录（下一次 <see cref="Scan"/> 重新建立基线）。</summary>
        public void ResetBannerForTests()
        {
            _haveFocus = false;
            _bannerUntil = -1f;
            BannerShowCount = 0;
            _banner?.EnableInClassList("uk-hidden", true);
        }
    }
}
