using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002 镜头 / FGR-ARC-009 统一时钟；FG07 FGR-ENV-001；FG00 B01/B02/B05/B09/B13/B15/B16/B22/B24）：世界时间条。
    /// - 第一行：“第 N 日 HH:MM”、0.5x / 1x / 2x / 3x、暂停 / 继续、状态（运行中 / 已暂停 / 接入锁 1x）——全部读写统一时钟，整个世界同时生效。
    /// - 第二行：关注点（家园、在外的远征、每支行进中的突袭及预计到达），点一下镜头飞过去；当前关注点有“▸”前缀（不只靠颜色）。
    /// - 每个按钮都有悬停提示并写明当前快捷键（改键后自动跟着变）；突袭 / 地貌是占位表现，提示里写明（B22）。
    /// - 刷新键（语言、日 / 分、速度 / 暂停版本、关注点标签）不变的帧只做 O(1) 比较（B18）。
    /// </summary>
    public sealed class WorldBarHudUIToolkit : UiKitPanelHost
    {
        /// <summary>右上角常驻条，与旧速度 HUD 同层（UI_WORKFLOW_GUIDE 分层表：HUD 3）。</summary>
        public const int Order = 3;

        public static WorldBarHudUIToolkit Instance { get; private set; }

        private VisualElement _bar;
        private Label _dayTime;
        private readonly Button[] _speeds = new Button[4];
        private Button _pause;
        private Label _status;
        private Button _phase;
        private Label _calExtra;
        private Label _silentBanner;
        private Label _focusTitle;
        // FG8-EXP-02（FGR-EXP-011 / 012）：远征行军条——队伍色块、名字 · 状态 · 预计到达时间、远程命令、跟随 / 前往、停下与被拒的原因。
        private VisualElement _marchRow;
        private VisualElement _marchSwatch;
        private Label _marchStatus;
        private Button _marchAdvance;
        private Button _marchHold;
        private Button _marchReturn;
        private Button _marchFollow;
        private Label _marchReason;
        private string _marchFailure;

        /// <summary>FG7-ENV-02：静默夜横幅（自检 / 冒烟读）。</summary>
        public string SilentBannerText => _silentBanner != null && !_silentBanner.ClassListContains("uk-hidden") ? _silentBanner.text : string.Empty;
        private Button _labor;
        private ScrollView _focusList;
        private readonly List<Button> _focusButtons = new List<Button>(4);
        private readonly List<string> _focusIds = new List<string>(4);
        private readonly StringBuilder _keyBuilder = new StringBuilder(128);
        private string _lastKey;

        protected override string UxmlLocation => "WorldBar";
        protected override int SortingOrder => Order;

        /// <summary>自检 / 冒烟可读。</summary>
        public bool BarVisible => _bar != null && !_bar.ClassListContains("uk-hidden");
        public string DayTimeText => _dayTime?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public int FocusButtonCount => _focusButtons.Count;
        public IReadOnlyList<Button> FocusButtons => _focusButtons;
        public Button SpeedButton(int index) => index >= 0 && index < _speeds.Length ? _speeds[index] : null;

        /// <summary>FG8-EXP-02：远征行军条（自检 / 冒烟读）。</summary>
        public bool MarchRowShown => _marchRow != null && !_marchRow.ClassListContains("uk-hidden") && BarVisible;
        public string MarchStatusText => _marchStatus?.text ?? string.Empty;
        public string MarchReasonText => _marchReason != null && !_marchReason.ClassListContains("uk-hidden") ? _marchReason.text : string.Empty;
        public Color MarchSwatchColor => _marchSwatch != null ? _marchSwatch.resolvedStyle.backgroundColor : default;
        public Color MarchSwatchStyleColor => _marchSwatch != null ? _marchSwatch.style.backgroundColor.value : default;
        public Button MarchAdvanceButton => _marchAdvance;
        public Button MarchHoldButton => _marchHold;
        public Button MarchReturnButton => _marchReturn;
        public Button MarchFollowButton => _marchFollow;
        public Button PauseButton => _pause;
        public Button LaborButton => _labor;
        /// <summary>FG7-ENV-01：日历行（昼夜阶段 + 倒计时；点开日历面板）与静默夜 / 天气摘要。</summary>
        public Button PhaseButton => _phase;
        public string PhaseText => _phase?.text ?? string.Empty;
        public string CalendarExtraText => _calExtra?.text ?? string.Empty;
        public string LaborText => _labor?.text ?? string.Empty;

        // ── FG4-ECO-08 资源顶栏（自检读点）──
        public int ResourceChipCount { get; private set; }
        public Button ResourceChip(int i) => i >= 0 && i < ResourceChipCount ? _resChips[i] : null;
        public string ResourceChipText(int i) => ResourceChip(i)?.text ?? string.Empty;
        public string ResourceChipTip(int i) => i >= 0 && i < ResourceChipCount ? _resTipBody[i] : string.Empty;
        public int FindResourceChip(string contains)
        {
            for (int i = 0; i < ResourceChipCount; i++)
            {
                if (_resChips[i].text.Contains(contains))
                {
                    return i;
                }
            }
            return -1;
        }
        /// <summary>自检：模拟右键点格子（固定物品 = 取消固定）。</summary>
        public void RightClickResourceChip(int i)
        {
            if (i >= 0 && i < ResourceChipCount)
            {
                _resRight[i]?.Invoke();
            }
        }
        /// <summary>资源顶栏真正重建的次数（自检：节流内不重建、不每帧分配）。</summary>
        public int ResourceRebuilds { get; private set; }
        public static Func<double> ResourceClock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _resRow;
        private readonly List<Button> _resChips = new List<Button>(12);
        private readonly List<Action> _resLeft = new List<Action>(12);
        private readonly List<Action> _resRight = new List<Action>(12);
        private readonly List<string> _resTipTitle = new List<string>(12);
        private readonly List<string> _resTipBody = new List<string>(12);
        private readonly List<int> _subnets = new List<int>(4);
        private double _resNext;
        private int _resKey;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            _bar = root.Q<VisualElement>("WorldBar");
            _dayTime = root.Q<Label>("WorldDayTime");
            _pause = root.Q<Button>("WorldPause");
            _status = root.Q<Label>("WorldStatus");
            _focusTitle = root.Q<Label>("WorldFocusTitle");
            _focusList = root.Q<ScrollView>("WorldFocusList");
            _labor = root.Q<Button>("WorldLabor");
            _phase = root.Q<Button>("WorldDayPhase");
            _calExtra = root.Q<Label>("WorldCalendarExtra");
            _silentBanner = root.Q<Label>("WorldSilentBanner");
            if (_silentBanner != null)
            {
                UiTooltip.Attach(_silentBanner, () => new TooltipContent
                {
                    Title = GameText.Get("env.silent.banner_tip_title"),
                    Body = GameText.Get("env.silent.banner_tip"),
                });
            }
            _resRow = root.Q<VisualElement>("WorldResourceRow");
            BindMarch(root);
            if (_phase != null)
            {
                // FG7-ENV-01（FGR-ENV-002“点开是日历面板”）：日历行点一下打开日历（与日历键同一入口）。
                _phase.clicked += CalendarPanelUIToolkit.Open;
                UiTooltip.Attach(_phase, PhaseTooltip);
            }
            _resKey = 0;
            _resNext = 0;
            if (_labor != null)
            {
                // FG4-ECO-07（FGR-ECO-042）：顶栏劳动力，点一下打开机器名册。
                _labor.clicked += RosterPanelUIToolkit.Open;
                UiTooltip.Attach(_labor, () =>
                {
                    MachineRoster.LaborCount c = MachineRoster.Labor(CampaignSession.Current);
                    return new TooltipContent
                    {
                        Title = MachineRoster.LaborBarText(c),
                        Body = GameText.Format("roster.labor.tip", c.Labor, c.Busy, InputDisplay.ForAction(GameActionId.OpenRoster)),
                        Shortcut = GameActionId.OpenRoster,
                    };
                });
            }
            GameActionId[] speedActions = { GameActionId.SpeedHalf, GameActionId.SpeedNormal, GameActionId.SpeedDouble, GameActionId.SpeedTriple };
            for (int i = 0; i < _speeds.Length; i++)
            {
                _speeds[i] = root.Q<Button>("WorldSpeed" + i);
                int index = i;
                GameActionId action = speedActions[i];
                _speeds[i].clicked += () => GameClock.SetSpeed(GameClock.Speeds[index]);
                UiTooltip.Attach(_speeds[i], () => new TooltipContent
                {
                    Title = SpeedLabel(GameClock.Speeds[index]),
                    Body = GameText.Format("ui.world.tip.speed", SpeedLabel(GameClock.Speeds[index]), InputDisplay.ForAction(action)),
                    Shortcut = action,
                });
            }
            _pause.clicked += () => GameRoot.ToggleWorldPause();
            UiTooltip.Attach(_pause, () => new TooltipContent
            {
                Title = GameText.Get(GameClock.Paused ? "ui.world.resume" : "ui.world.pause"),
                Body = GameText.Format("ui.world.tip.pause", InputDisplay.ForAction(GameActionId.TogglePause)),
                Shortcut = GameActionId.TogglePause,
            });
            UiTooltip.Attach(_dayTime, () => new TooltipContent
            {
                Title = GameClock.FormatDayTime(GameClock.GameSeconds),
                Body = GameText.Format("ui.world.tip.time", GameClock.DayOf(GameClock.GameSeconds).ToString(CultureInfo.InvariantCulture),
                    GameClock.FormatHhMm(GameClock.GameSeconds),
                    (GameClock.DaySeconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture)),
            });
            UiTooltip.Attach(_status, () => new TooltipContent
            {
                Title = _status.text,
                Body = GameClock.DirectLocked ? GameText.Get("ui.world.tip.direct_locked") : GameText.Get("ui.world.placeholder"),
            });
            _lastKey = null;
        }

        private float _refreshTimer;

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            // 关注点标签含每支队伍的预计到达时间（逐支格式化）：每 0.2 秒真实时间刷新一次，不每帧逐队伍分配字符串（B18）。
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f && GameClock.Revision == _lastRevision)
            {
                return;
            }
            _refreshTimer = 0.2f;
            _lastRevision = GameClock.Revision;
            Refresh();
        }

        private int _lastRevision = -1;

        /// <summary>按统一时钟与世界状态刷新（自检可直接调用）。</summary>
        public void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            bool inWorld = state != null && GameRoot.AnyRegionActive;
            SetVisible(_bar, inWorld);
            if (!inWorld)
            {
                _lastKey = null;
                return;
            }

            // FG4-ECO-07：劳动力读数（MachineRoster.Labor 自带节流：名册 / 岗位变化立刻重算，否则最多每 roster.labor_refresh_seconds 真实秒一次）。
            RefreshLabor(state);
            // FG4-ECO-08：资源顶栏（固定 / 语言变化立刻重建，否则每 eco.topbar.refresh_seconds 真实秒一次；O(固定物品 + 电网数)）。
            RefreshResources(state);

            // FG8-EXP-02：远征行军条（状态 / 预计到达时间每次刷新都可能变，不走下面的刷新键；O(成员数)）。
            RefreshMarch(state);
            // FG8-LOOT-02：待分拣数据包计数（只在数量 / 语言变化时改文字，O(1)）。
            RefreshPackets(state);
            RefreshCargo(state); // FG8-LOOT-03

            IReadOnlyList<WorldView.FocusTarget> targets = WorldView.FocusTargets(state);
            _keyBuilder.Clear();
            _keyBuilder.Append(GameText.Language).Append('|').Append(GameClock.MinuteOfDay(GameClock.GameSeconds)).Append('|')
                .Append(GameClock.DayOf(GameClock.GameSeconds)).Append('|').Append(GameClock.Revision).Append('|')
                .Append(WorldView.ObservedSiteId).Append('|').Append(WorldView.LastFocusTargetId).Append('|').Append(Campaign.Env.SilentNightService.Revision).Append('|').Append(Campaign.Env.WeatherService.Revision);
            for (int i = 0; i < targets.Count; i++)
            {
                _keyBuilder.Append('|').Append(targets[i].Id).Append(':').Append(targets[i].Label);
            }
            string key = _keyBuilder.ToString();
            if (key == _lastKey)
            {
                return;
            }
            _lastKey = key;

            _dayTime.text = GameClock.FormatDayTime(GameClock.GameSeconds);
            for (int i = 0; i < _speeds.Length; i++)
            {
                bool current = Mathf.Approximately(GameClock.Speed, GameClock.Speeds[i]);
                string label = SpeedLabel(GameClock.Speeds[i]);
                _speeds[i].text = current ? GameText.Format("ui.world.focus.current", label) : label;
                SetClass(_speeds[i], "wb-speed-current", current);
            }
            _pause.text = GameText.Get(GameClock.Paused ? "ui.world.resume" : "ui.world.pause");
            SetClass(_pause, "wb-paused", GameClock.Paused);
            _status.text = GameClock.Paused
                ? GameText.Get("ui.world.status_paused")
                : GameClock.DirectLocked
                    ? GameText.Get("ui.world.status_direct_locked")
                    : GameText.Format("ui.world.status_running", SpeedLabel(GameClock.Speed));
            IWorldSite exp = WorldSimulation.ActiveExpedition;
            if (exp != null && exp.IsWiped && WorldView.IsObserved(exp.SiteId))
            {
                _status.text = GameText.Get("ui.world.expedition_wiped_here");
            }

            _focusTitle.text = GameText.Get("ui.world.focus.title");
            RebuildFocus(targets);
            RefreshCalendarRow(state);
        }

        // ── FG8-EXP-02 远征行军条（FGR-EXP-011 / 012）──

        private void BindMarch(VisualElement root)
        {
            _marchRow = root.Q<VisualElement>("WorldMarchRow");
            _marchSwatch = root.Q<VisualElement>("WorldMarchSwatch");
            _marchStatus = root.Q<Label>("WorldMarchStatus");
            _marchAdvance = root.Q<Button>("WorldMarchAdvance");
            _marchHold = root.Q<Button>("WorldMarchHold");
            _marchReturn = root.Q<Button>("WorldMarchReturn");
            _marchFollow = root.Q<Button>("WorldMarchFollow");
            _marchReason = root.Q<Label>("WorldMarchReason");
            // FG8-LOOT-02：待分拣数据包计数（点开待分拣面板；悬停写现在能不能分拣）。
            _packets = root.Q<Button>("WorldPacketCount");
            if (_packets != null)
            {
                _packets.clicked += LootPickPanelUIToolkit.Open;
                UiTooltip.Attach(_packets, PacketTip);
            }
            // FG8-LOOT-03：全队货舱读数（点开货舱面板；快满 / 已满写在文字里）。
            _cargo = root.Q<Button>("WorldCargo");
            if (_cargo != null)
            {
                _cargo.clicked += CargoPanelUIToolkit.Open;
                UiTooltip.Attach(_cargo, () => new TooltipContent { Title = GameText.Get("cargo.title"), Body = GameText.Get("cargo.hud_tip") });
            }
            if (_marchAdvance == null || _marchHold == null || _marchReturn == null || _marchFollow == null)
            {
                return;
            }
            _marchAdvance.clicked += () => IssueMarch(ExpeditionMarchService.Command.Advance);
            _marchHold.clicked += () => IssueMarch(ExpeditionMarchService.Command.Hold);
            _marchReturn.clicked += () => IssueMarch(ExpeditionMarchService.Command.Return);
            _marchFollow.clicked += () =>
            {
                WorldView.ToggleFollowExpedition();
                RefreshMarch(CampaignSession.Current);
            };
            UiTooltip.Attach(_marchAdvance, () => MarchTip("exp.cmd.advance", "exp.cmd.tip.advance"));
            UiTooltip.Attach(_marchHold, () => MarchTip("exp.cmd.hold", "exp.cmd.tip.hold"));
            UiTooltip.Attach(_marchReturn, () => MarchTip("exp.cmd.return", "exp.cmd.tip.return"));
            UiTooltip.Attach(_marchFollow, () => new TooltipContent { Title = _marchFollow.text, Body = GameText.Get("exp.follow.started") });
        }

        private Button _packets;
        private int _packetKey = -1;
        private Button _cargo;
        private int _cargoKey = -1;
        private readonly List<int> _cargoMachines = new List<int>();

        /// <summary>自检读点：顶栏“货舱 N / M”。</summary>
        public string CargoText => _cargo != null && !_cargo.ClassListContains("uk-hidden") ? _cargo.text : string.Empty;
        public Button CargoButton => _cargo;

        /// <summary>FG8-LOOT-03：顶栏全队货舱——远征队在外或有机器装着货时显示“货舱 已用 / 容量（· 快满了 / · 已满）”。只在货舱版本 / 游戏秒 / 语言变了时重算（O(列出的机器 × 货舱行)）。</summary>
        private void RefreshCargo(CampaignState state)
        {
            if (_cargo == null)
            {
                return;
            }
            int key = HashCode.Combine(CargoService.Revision, GameClock.Ticks / Math.Max(1, GameClock.StepHz), (int)GameText.Language, state != null ? state.GetHashCode() : 0);
            if (key == _cargoKey)
            {
                return;
            }
            _cargoKey = key;
            CargoService.PanelMachines(state, _cargoMachines);
            bool show = state != null && (CargoService.TeamOut(state) || _cargoMachines.Count > 0);
            SetVisible(_cargo, show);
            if (!show)
            {
                return;
            }
            int level = CargoService.Totals(state, _cargoMachines, out int used, out int cap);
            _cargo.text = GameText.Format("cargo.hud", used, cap, CargoService.LevelSuffix(level));
            _cargo.EnableInClassList("wb-cargo-warn", level > 0);
        }

        /// <summary>自检读点：顶栏“待分拣 ×N”。</summary>
        public string PacketCountText => _packets != null && !_packets.ClassListContains("uk-hidden") ? _packets.text : string.Empty;
        public Button PacketButton => _packets;

        /// <summary>顶栏文字：“待分拣 ×N”、“待分拣 ×N · 货舱 ×M”或只剩货舱物品时“货舱里 ×M”。</summary>
        public static string PacketLabel(int packets, int held) =>
            packets > 0 ? (held > 0 ? GameText.Format("loot.pick.hud.both", packets, held) : GameText.Format("loot.pick.hud", packets)) : GameText.Format("loot.pick.hud.held", held);

        private static TooltipContent PacketTip()
        {
            CampaignState s = CampaignSession.Current;
            if (LootPickService.Count(s) == 0)
            {
                return new TooltipContent { Title = GameText.Get("loot.pick.title"), Body = GameText.Format("loot.pick.hud.held_tip", LootPickService.HeldCount(s)) };
            }
            int ready = LootPickService.ReadyCount(s);
            string why = null;
            if (ready == 0)
            {
                foreach (LootPackageRecord p in LootPickService.Packets(s))
                {
                    LootPickService.CanProcess(s, p, out why, out _);
                    break;
                }
            }
            return new TooltipContent
            {
                Title = GameText.Get("loot.pick.title"),
                Body = ready > 0 ? GameText.Format("loot.pick.hud.ready", ready) : GameText.Format("loot.pick.hud.wait", GameText.Get(why ?? "loot.pick.reason.none")),
            };
        }

        /// <summary>顶栏数据包计数：只在数量 / 语言变化时改文字。O(1)。</summary>
        private void RefreshPackets(CampaignState state)
        {
            if (_packets == null)
            {
                return;
            }
            int n = LootPickService.Count(state);
            int h = LootPickService.HeldCount(state);
            int key = HashCode.Combine(n, h, (int)GameText.Language);
            if (key == _packetKey)
            {
                return;
            }
            _packetKey = key;
            // 有待分拣的数据包，或货舱里还有分拣得到的物品（没有数据包时也要能点开面板查看 / 入库 / 丢弃）。
            SetVisible(_packets, n > 0 || h > 0);
            _packets.text = PacketLabel(n, h);
        }

        private static TooltipContent MarchTip(string titleKey, string bodyKey) => new TooltipContent
        {
            Title = GameText.Get(titleKey),
            Body = GameText.Get(bodyKey) + "\n" + GameText.Get("exp.cmd.tip.coverage"),
        };

        /// <summary>FG8-EXP-05：“返回家园”的二次确认（附近地上还有没捡的东西时，第一次点只列清单）。自检读。</summary>
        public string ReturnConfirmText => _returnConfirm;
        private string _returnConfirm;
        private float _returnConfirmUntil;

        /// <summary>二次确认的有效时间（真实秒；表 expedition.leave.confirm_seconds，常数只作兜底）。</summary>
        public static float ReturnConfirmSeconds => Campaign.Grid.GridContent.TryGetTuning("expedition.leave.confirm_seconds", out float v) && v > 0f ? v : 8f;

        /// <summary>远征命令按钮（FGR-EXP-011）：覆盖内生效；覆盖外 / 静默夜被拒并写明原因（拒绝音 + 字幕 + 行军条原因行），队伍继续执行最后的命令。</summary>
        public void IssueMarch(ExpeditionMarchService.Command cmd)
        {
            CampaignState state = CampaignSession.Current;
            // FG8-EXP-05（FGR-EXP-032“离开一片区域前，界面会列出地上还没捡的战利品”）：去程中下“返回家园”而附近地上还有没捡的东西时，
            // 第一次点只在行军条上列出清单与回收倒计时，8 秒内再点一次才真的返回（玩家知道要丢下什么）。命令被拒（覆盖外等）不走这里。
            if (cmd == ExpeditionMarchService.Command.Return)
            {
                bool armed = _returnConfirm != null && Time.unscaledTime <= _returnConfirmUntil;
                ExpeditionMarchState cur = ExpeditionMarchService.Current(state);
                if (!armed && cur != null && cur.Phase == ExpeditionMarchService.PhaseOutbound && ExpeditionMarchService.CanCommand(state, out _)
                    && Campaign.Economy.ExpeditionSettlementService.LeaveWarning(state, out string warn))
                {
                    _returnConfirm = warn;
                    _returnConfirmUntil = Time.unscaledTime + ReturnConfirmSeconds;
                    RefreshMarch(state);
                    return;
                }
            }
            _returnConfirm = null;
            if (ExpeditionMarchService.IssueCommand(state, cmd, out string failure))
            {
                _marchFailure = null;
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck);
            }
            else
            {
                _marchFailure = failure ?? "unknown";
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, GameText.Get(ExpeditionTextKeys.ReasonKey(_marchFailure)));
            }
            RefreshMarch(state);
        }

        /// <summary>
        /// 远征行军条（FGR-EXP-012 五态常显）：“名字 · 状态 · 预计到达时间 / 集结进度 / 所在地点”；星球上行军时显示三个远程命令，覆盖外 / 静默夜时按钮禁用、原因行写明并附最后的命令；
        /// 路线截断 / 到达失败写明怎么办；跟随按钮在地点里变成“前往远征地点”。O(成员数)，只在刷新时调用。
        /// </summary>
        private void RefreshMarch(CampaignState state)
        {
            if (_marchRow == null || _marchStatus == null || state == null)
            {
                return;
            }
            ExpeditionMarchState m = ExpeditionMarchService.Current(state);
            int phase = m?.Phase ?? ExpeditionMarchService.PhaseNone;
            ExpeditionMarchService.TeamStatus status = ExpeditionMarchService.StatusOf(state);
            string name = ExpeditionTeamService.DisplayName(state);
            string statusText = GameText.Get(ExpeditionMarchService.StatusTextKey(status));
            bool onPlanet = phase == ExpeditionMarchService.PhaseOutbound || phase == ExpeditionMarchService.PhaseReturning;
            string text;
            if (onPlanet)
            {
                int near = ExpeditionMarchService.ArrivedCount(state, out int alive);
                double eta = ExpeditionMarchService.EtaSeconds(state);
                text = near > 0 && near < alive && m.Command != (int)ExpeditionMarchService.Command.Hold
                    ? GameText.Format("exp.march.bar_rally", name, statusText, near, alive)
                    : eta >= 0
                        ? GameText.Format("exp.march.bar_eta", name, statusText, GameClock.FormatGameDuration(eta))
                        : GameText.Format("exp.march.bar", name, statusText);
            }
            else if (phase == ExpeditionMarchService.PhaseAtSite)
            {
                text = GameText.Format("exp.march.bar_site", name, statusText, GameText.Get(ExpeditionTextKeys.SiteNameKey(m.SiteId)));
            }
            else
            {
                text = GameText.Format("exp.march.bar", name, statusText);
            }
            if (_marchStatus.text != text)
            {
                _marchStatus.text = text;
            }
            if (_marchSwatch != null)
            {
                _marchSwatch.style.backgroundColor = new StyleColor((Color)ExpeditionTeamService.ColorOf(ExpeditionTeamService.ColorIndexOf(state)));
            }
            SetVisible(_marchAdvance, onPlanet);
            SetVisible(_marchHold, onPlanet);
            SetVisible(_marchReturn, onPlanet);
            bool canCommand = ExpeditionMarchService.CanCommand(state, out string why);
            _marchAdvance?.SetEnabled(canCommand);
            _marchHold?.SetEnabled(canCommand);
            _marchReturn?.SetEnabled(canCommand);
            if (_marchAdvance != null)
            {
                _marchAdvance.text = GameText.Get("exp.cmd.advance");
                _marchHold.text = GameText.Get("exp.cmd.hold");
                _marchReturn.text = GameText.Get("exp.cmd.return");
            }
            bool atSite = phase == ExpeditionMarchService.PhaseAtSite;
            SetVisible(_marchFollow, onPlanet || atSite);
            if (_marchFollow != null)
            {
                _marchFollow.text = atSite ? GameText.Get("exp.cmd.goto_site") : GameText.Get("exp.cmd.follow");
                SetClass(_marchFollow, "wb-focus-current", WorldView.FollowingExpedition);
            }
            if (canCommand)
            {
                _marchFailure = null; // 能下令了：上一次被拒的原因已经过时。
            }
            string reason = null;
            if (_returnConfirm != null && Time.unscaledTime > _returnConfirmUntil)
            {
                _returnConfirm = null; // 确认过期
            }
            if (onPlanet && _returnConfirm != null)
            {
                reason = _returnConfirm;
            }
            else if (onPlanet)
            {
                string code = _marchFailure ?? (canCommand ? null : why);
                if (code != null)
                {
                    reason = GameText.Get(ExpeditionTextKeys.ReasonKey(code)) + "  " + GameText.Format("exp.march.last_order", GameText.Get(CommandKey((ExpeditionMarchService.Command)m.Command)));
                }
                else if (m.Halted == ExpeditionMarchService.HaltRouteTruncated)
                {
                    reason = GameText.Get("exp.march.halted.route");
                }
                else if (m.Halted == ExpeditionMarchService.HaltArrivalFailed)
                {
                    reason = GameText.Format("exp.march.halted.arrival", GameText.Get(ExpeditionTextKeys.ReasonKey(m.ArrivalFailReason)));
                }
            }
            SetVisible(_marchReason, reason != null);
            if (reason != null && _marchReason != null && _marchReason.text != reason)
            {
                _marchReason.text = reason;
            }
        }

        private static string CommandKey(ExpeditionMarchService.Command cmd) =>
            cmd == ExpeditionMarchService.Command.Hold ? "exp.cmd.hold" : cmd == ExpeditionMarchService.Command.Return ? "exp.cmd.return" : "exp.cmd.advance";

        // ── FG7-ENV-01 日历行（FGR-ENV-002 / 003）──

        /// <summary>“☀ 白天 · 2时06分后入夜”；静默夜 / 天气有来源时右侧写“静默夜 X 后”“当前天气”。随刷新键（游戏分钟、语言）更新，O(日历事件数)。</summary>
        private void RefreshCalendarRow(CampaignState state)
        {
            if (_phase == null)
            {
                return;
            }
            double now = GameClock.GameSeconds;
            Campaign.Env.DayPhase p = Campaign.Env.DayNightCycle.PhaseAt(now);
            double left = Campaign.Env.DayNightCycle.NextChange(now, out bool toNight);
            _phase.text = GameText.Format(toNight ? "env.hud.phase_to_night" : "env.hud.phase_to_day",
                Campaign.Env.DayNightCycle.PhaseGlyph(p), Campaign.Env.DayNightCycle.PhaseName(p), Campaign.Env.CalendarService.ShortDuration(left));
            SetClass(_phase, "wb-phase-night", p == Campaign.Env.DayPhase.Night);
            // FG7-ENV-02（FGR-ENV-012）：静默夜期间顶部横幅“信号中断 · 剩余 X时Y分”（随游戏分钟刷新）。
            if (_silentBanner != null)
            {
                bool silentNow = Campaign.Env.SilentNightService.IsActive(state);
                if (silentNow)
                {
                    _silentBanner.text = GameText.Format("env.silent.banner", Campaign.Env.CalendarService.ShortDuration(Campaign.Env.SilentNightService.RemainingSeconds(state)));
                }
                SetVisible(_silentBanner, silentNow);
            }
            if (_calExtra == null)
            {
                return;
            }
            double horizon = Campaign.Env.CalendarService.Days * GameClock.DaySeconds;
            var sb = new StringBuilder();
            if (Campaign.Env.CalendarService.TryNext(state, Campaign.Env.CalendarKind.SilentNight, now, horizon, out Campaign.Env.CalendarEntry silent))
            {
                sb.Append(silent.StartSeconds <= now
                    ? GameText.Format("env.hud.silent_now", silent.Title)
                    : GameText.Format("env.hud.silent_in", Campaign.Env.CalendarService.ShortDuration(silent.StartSeconds - now)));
            }
            if (Campaign.Env.CalendarService.HasSource(Campaign.Env.CalendarKind.Weather))
            {
                // FG7-ENV-03（FGR-ENV-002 当前天气和预报图标）：始终写当前天气（晴也写），后面接看得到的下一段（预报远近按监听站 T2 的天气预报）。
                if (sb.Length > 0)
                {
                    sb.Append("  ");
                }
                sb.Append(Campaign.Env.WeatherService.HudText(state, now));
            }
            _calExtra.text = sb.ToString();
            SetVisible(_calExtra, sb.Length > 0);
        }

        private TooltipContent PhaseTooltip()
        {
            double now = GameClock.GameSeconds;
            Campaign.Env.DayPhase p = Campaign.Env.DayNightCycle.PhaseAt(now);
            int light = Mathf.RoundToInt(Campaign.Env.DayNightCycle.Daylight(now) * 100f);
            return new TooltipContent
            {
                Title = GameText.Get("env.hud.tip_title"),
                Body = GameText.Format("env.hud.tip", Campaign.Env.DayNightCycle.PhaseName(p), light,
                           GameClock.FormatHour(Campaign.Env.DayNightCycle.NightEndHour), GameClock.FormatHour(Campaign.Env.DayNightCycle.NightStartHour),
                           Mathf.RoundToInt(Settings.GameSettings.NightBrightnessFloor * 100f), InputDisplay.ForAction(GameActionId.OpenCalendar))
                       + Campaign.Env.CalendarService.PendingKindsSuffix(),
                Shortcut = GameActionId.OpenCalendar,
            };
        }

        /// <summary>FG4-ECO-08：重建资源顶栏（自检可直接调用：<paramref name="force"/> = 忽略节流）。</summary>
        public void RefreshResources(CampaignState state, bool force = false)
        {
            if (_resRow == null || state == null)
            {
                return;
            }
            // 电网汇总版本号在有储能时每步都变，不进键（否则每帧重建）：电力读数随节流刷新。
            int key = HashCode.Combine(ResourcePins.Revision, (int)GameText.Language, state.GetHashCode());
            double now = ResourceClock();
            if (!force && key == _resKey && now < _resNext)
            {
                return;
            }
            _resKey = key;
            _resNext = now + Math.Max(0.1f, Campaign.Grid.GridContent.Tuning("eco.topbar.refresh_seconds"));
            ResourceRebuilds++;
            int n = 0;
            foreach (PinnedItemRecord pin in ResourcePins.Pins(state))
            {
                ResourcePins.PinView v = ResourcePins.View(state, pin);
                if (v.Item == null)
                {
                    continue;
                }
                string id = v.Item.Id;
                // FG4-ECO-10（DEBT-FG4ECO08-03）：流体显示储罐存量合计（升），管线服务没运行时照旧写“管线”。
                string stock = v.Stock >= 0 ? v.Stock.ToString(CultureInfo.InvariantCulture)
                    : v.FluidLiters >= 0 ? GameText.Format("topbar.fluid_stock", v.FluidLiters.ToString("N0", CultureInfo.InvariantCulture)) : GameText.Get("topbar.fluid");
                string rate = ProductionStats.Rate(v.Item, v.ProducedPerMinute);
                string text = v.HasTarget
                    ? GameText.Format(v.Reached ? "topbar.pin_target_ok" : "topbar.pin_target_miss", v.Item.Name, stock, rate, ProductionStats.Rate(v.Item, v.Target))
                    : GameText.Format("topbar.pin", v.Item.Name, stock, rate);
                string body = GameText.Format("topbar.pin_tip", v.Item.Name, stock, rate, ProductionStats.Rate(v.Item, v.NetPerMinute),
                                  ProductionStats.WindowName(ResourcePins.RateWindow))
                              + (v.FluidLiters >= 0 ? "\n" + GameText.Format("topbar.fluid_stock_tip", v.Item.Name, v.FluidLiters.ToString("N0", CultureInfo.InvariantCulture), v.FluidNetworks) : string.Empty)
                              + (v.HasTarget ? "\n" + GameText.Format(v.Reached ? "topbar.target_ok_tip" : "topbar.target_miss_tip", ProductionStats.Rate(v.Item, v.Target)) : string.Empty)
                              + "\n" + GameText.Format("topbar.pin_hint", InputDisplay.ForAction(GameActionId.OpenStats));
                Chip(n++, text, v.Item.Name, body, "wb-res-pin", v.HasTarget ? (v.Reached ? "wb-res-reached" : "wb-res-missed") : null,
                    () => StatsPanelUIToolkit.OpenTab(StatsTab.Production, id), () => ResourcePins.TryUnpin(CampaignSession.Current, id, out _));
            }
            if (ResourcePins.Pins(state).Count < ResourcePins.MaxPins)
            {
                Chip(n++, GameText.Get("topbar.add"), GameText.Get("topbar.add"), GameText.Format("topbar.add_tip", InputDisplay.ForAction(GameActionId.OpenStats)),
                    "wb-res-add", null, StatsPanelUIToolkit.OpenAllItems, null);
            }
            // 电力（各电网）：每个电网“电网 1 发电 / 需要”，供不上时写明“缺电”（不只靠颜色）。
            _subnets.Clear();
            if (ReferenceEquals(HomeValleyPowerGrid.BoundState, state))
            {
                HomeValleyPowerGrid.SubnetsBySerial(_subnets);
            }
            int maxNets = Math.Max(1, Campaign.Grid.GridContent.TuningInt("eco.topbar.max_subnets"));
            int shown = 0;
            foreach (int sIdx in _subnets)
            {
                if (shown >= maxNets)
                {
                    break;
                }
                if (!HomeValleyPowerGrid.TryGetSubnetInfo(state, sIdx, out PowerSubnetInfo info))
                {
                    continue;
                }
                shown++;
                bool lacking = info.Delivered + 0.5f < info.Demand;
                string name = HomeValleyPowerGrid.SubnetName(info.Serial);
                string text = GameText.Format(lacking ? "topbar.power_short" : "topbar.power", name, HomeValleyPowerGrid.Num(info.Supply), HomeValleyPowerGrid.Num(info.Demand));
                Chip(n++, text, name, HomeValleyPowerGrid.DescribeSubnetLine(sIdx) + "\n" + GameText.Get("topbar.power_tip"), null, lacking ? "wb-res-missed" : null,
                    PowerPanelUIToolkit.Open, null);
            }
            if (_subnets.Count > shown)
            {
                Chip(n++, GameText.Format("topbar.power_more", _subnets.Count - shown), GameText.Get("topbar.power_title"), GameText.Get("topbar.power_tip"), null, null,
                    PowerPanelUIToolkit.Open, null);
            }
            else if (_subnets.Count == 0)
            {
                Chip(n++, GameText.Get("topbar.power_none"), GameText.Get("topbar.power_title"), GameText.Get("topbar.power_none_tip"), null, null, PowerPanelUIToolkit.Open, null);
            }
            Chip(n++, GameText.Format("topbar.tech", state.TechData), ItemCatalog.NameOf(ItemCatalog.TechDataId), GameText.Get("topbar.tech_tip"), null, null,
                ItemsPanelUIToolkit.Open, null);
            // FG5-RND-01：研究点芯片写研究在做什么（为什么停了），点一下打开研发树。
            Chip(n++, GameText.Format("topbar.research", state.Research?.Points ?? 0), ItemCatalog.NameOf("research_points"),
                GameText.Format("research.topbar.tip", ResearchService.StatusText(state), InputDisplay.ForAction(GameActionId.OpenResearch)), null, null,
                ResearchTreePanelUIToolkit.Open, null);
            // FG7-ENV-04（FGR-ENV-042）：“已归还 N 格”（世界无限没有百分比）；悬停写口径与下一个里程碑，点一下打开日历（里程碑与已达成的奖励在日历页脚）。
            Chip(n++, GameText.Format("topbar.returned", Campaign.Env.PurificationService.ReturnedArea(state).ToString("N0", CultureInfo.InvariantCulture)),
                GameText.Get("topbar.returned_title"),
                // 三行（口径 / 下一个里程碑 / 操作）由代码按行拼接：文本表里不放换行（fgdata.py --dump 快照按行比对）。
                GameText.Get("topbar.returned_tip") + "\n" + Campaign.Env.PurificationService.NextMilestoneText(state) + "\n"
                + GameText.Format("topbar.returned_tip_hint", InputDisplay.ForAction(GameActionId.OpenCalendar), InputDisplay.ForAction(GameActionId.OverlayPollution)),
                null, null, CalendarPanelUIToolkit.Open, null);
            Chip(n++, GameText.Format("topbar.exposure", Mathf.RoundToInt(state.SignalExposure), Mathf.RoundToInt(CampaignExposureLedger.MaxExposure)),
                GameText.Get("topbar.exposure_title"), GameText.Format("topbar.exposure_tip", InputDisplay.ForAction(GameActionId.OpenExposure)), null, null,
                SignalCore.SignalCoreHudUIToolkit.OpenExposure, null);
            for (int i = n; i < _resChips.Count; i++)
            {
                SetVisible(_resChips[i], false);
            }
            ResourceChipCount = n;
        }

        private void Chip(int i, string text, string title, string body, string cls, string stateCls, Action left, Action right)
        {
            while (_resChips.Count <= i)
            {
                var b = new Button { name = "WorldRes" + _resChips.Count };
                b.AddToClassList("mw-btn");
                b.AddToClassList("wb-res-chip");
                int index = _resChips.Count;
                b.clicked += () => _resLeft[index]?.Invoke();
                b.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button == 1)
                    {
                        _resRight[index]?.Invoke();
                    }
                });
                UiTooltip.Attach(b, () => new TooltipContent { Title = _resTipTitle[index], Body = _resTipBody[index] });
                _resChips.Add(b);
                _resLeft.Add(null);
                _resRight.Add(null);
                _resTipTitle.Add(string.Empty);
                _resTipBody.Add(string.Empty);
                _resRow.Add(b);
            }
            Button c = _resChips[i];
            SetVisible(c, true);
            c.text = text;
            c.EnableInClassList("wb-res-pin", cls == "wb-res-pin");
            c.EnableInClassList("wb-res-add", cls == "wb-res-add");
            c.EnableInClassList("wb-res-reached", stateCls == "wb-res-reached");
            c.EnableInClassList("wb-res-missed", stateCls == "wb-res-missed");
            _resLeft[i] = left;
            _resRight[i] = right;
            _resTipTitle[i] = title ?? string.Empty;
            _resTipBody[i] = body ?? string.Empty;
        }

        private void RefreshLabor(CampaignState state)
        {
            if (_labor == null)
            {
                return;
            }
            // 审查修复（P2）：只在台数 / 忙碌数 / 语言变化时重新格式化（每帧调用，不每帧分配字符串）。
            MachineRoster.LaborCount c = MachineRoster.Labor(state);
            int language = (int)GameText.Language;
            if (_laborShown && c.Labor == _laborShownCount && c.Busy == _laborShownBusy && language == _laborShownLanguage)
            {
                return;
            }
            _laborShown = true;
            _laborShownCount = c.Labor;
            _laborShownBusy = c.Busy;
            _laborShownLanguage = language;
            _labor.text = MachineRoster.LaborBarText(c);
        }

        private bool _laborShown;
        private int _laborShownCount;
        private int _laborShownBusy;
        private int _laborShownLanguage;

        private void RebuildFocus(IReadOnlyList<WorldView.FocusTarget> targets)
        {
            while (_focusButtons.Count < targets.Count)
            {
                var b = new Button { name = "WorldFocus" + _focusButtons.Count };
                b.AddToClassList("mw-btn");
                b.AddToClassList("wb-focus-btn");
                int index = _focusButtons.Count;
                b.clicked += () =>
                {
                    if (index < _focusIds.Count)
                    {
                        WorldView.FocusOn(_focusIds[index]);
                    }
                };
                UiTooltip.Attach(b, () => new TooltipContent
                {
                    Title = b.text,
                    Body = GameText.Format("ui.world.focus.tip", InputDisplay.ForAction(GameActionId.CycleWorldFocus), InputDisplay.ForAction(GameActionId.FocusHomeCore))
                           + "\n" + GameText.Get("ui.world.placeholder"),
                    Shortcut = GameActionId.CycleWorldFocus,
                });
                _focusButtons.Add(b);
                _focusList.Add(b);
            }
            _focusIds.Clear();
            for (int i = 0; i < _focusButtons.Count; i++)
            {
                Button b = _focusButtons[i];
                bool used = i < targets.Count;
                SetVisible(b, used);
                if (!used)
                {
                    continue;
                }
                WorldView.FocusTarget t = targets[i];
                _focusIds.Add(t.Id);
                bool current = t.Id == WorldView.LastFocusTargetId
                               || (WorldView.LastFocusTargetId == null && !t.IsRaid && t.SiteId == WorldView.ObservedSiteId);
                b.text = current ? GameText.Format("ui.world.focus.current", t.Label) : t.Label;
                SetClass(b, "wb-focus-current", current);
                SetClass(b, "wb-focus-raid", t.IsRaid);
            }
        }

        private static string SpeedLabel(float speed) =>
            GameText.Format("ui.world.speed", speed.ToString("0.#", CultureInfo.InvariantCulture));

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e == null)
            {
                return;
            }
            if (visible)
            {
                e.RemoveFromClassList("uk-hidden");
            }
            else
            {
                e.AddToClassList("uk-hidden");
            }
        }

        private static void SetClass(VisualElement e, string cls, bool on)
        {
            if (on)
            {
                e.AddToClassList(cls);
            }
            else
            {
                e.RemoveFromClassList(cls);
            }
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
