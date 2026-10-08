using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-001～005；FG06 第 4 节“批量设置目标模式、炮塔可以改名、记录击杀数”）：炮塔面板。
    /// - 蓝图：同一种炮塔座能装的固定底盘蓝图（下拉，选中即换、立即生效；没有可用蓝图时写明怎么配一张）。
    /// - 目标模式：五个按钮（选中的描边 + “●”前缀，不只靠颜色），悬停 / 下方写明怎么选目标；批量：全部炮塔 / 同蓝图的炮塔（可逆操作，不弹确认，B04）。
    /// - 状态：状态与原因（缺电 / 缺流体 / 过热 / 没装蓝图……）、击毁数、热量、射程 / 转速 / 每发 / 间隔 / 投送、补给。
    /// - 接入：有接入口的蓝图可以接入（FGR-DEF-005），没有时写明原因。
    /// 入口：炮塔的建筑面板“炮塔…”、机器名册“炮塔”分类（<see cref="Open"/>）。模态；Esc / 关闭 / 点遮罩关闭。改名在建筑面板（同一座建筑，BuildingOps.TryRename）。
    /// 刷新：结构（下拉选项、按钮文字）在 <see cref="TurretService.Revision"/> / 语言 / 建筑变化时重建；读数节流 0.25 秒。O(炮塔数)（下拉选项）+ O(1)。
    /// </summary>
    public sealed class TurretPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30063;
        private const float ReadingsInterval = 0.25f;

        public static TurretPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _state;
        private Button _help;
        private Button _close;
        private Label _message;
        private Label _secBlueprint;
        private DropdownField _blueprint;
        private Label _blueprintHint;
        private Label _secMode;
        private readonly Button[] _modes = new Button[5];
        private readonly int[] _modeCodes = new int[5];
        private Label _modeDesc;
        private Button _modeAll;
        private Button _modeSame;
        private Label _secStats;
        private Label _status;
        private Label _kills;
        private Label _heat;
        private Label _weapon;
        private Label _supply;
        private Button _uplink;
        private Label _uplinkHint;
        private Label _footer;
        private Button _overview;
        public Button OverviewButton => _overview;

        private readonly List<string> _blueprintIds = new List<string>();
        /// <summary>FG6-E2E-01（FG-GAP-115）：下拉每一行代表的版本（现在装的那张旧版本一行 + 同一张蓝图的最新版本另起一行，选它 = 换到最新版本）。</summary>
        private readonly List<int> _blueprintRowVersions = new List<int>();
        private int _key;
        private float _nextReadings;
        private bool _suppress;

        protected override string UxmlLocation => "TurretPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string KillsText => _kills?.text ?? string.Empty;
        public string HeatText => _heat?.text ?? string.Empty;
        public string WeaponText => _weapon?.text ?? string.Empty;
        public string SupplyText => _supply?.text ?? string.Empty;
        public string UplinkHintText => _uplinkHint?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public IReadOnlyList<string> BlueprintChoices => _blueprint?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public DropdownField BlueprintField => _blueprint;
        public Button ModeButton(int i) => i >= 0 && i < _modes.Length ? _modes[i] : null;
        public int ModeCodeAt(int i) => i >= 0 && i < _modeCodes.Length ? _modeCodes[i] : -1;
        public Button ModeAllButton => _modeAll;
        public Button ModeSameButton => _modeSame;
        public Button UplinkButton => _uplink;
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public VisualElement RootElement => _root;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开某座炮塔的面板（已开着就切到这座）。</summary>
        public static void Open(string buildingId)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingId = buildingId;
                BuildingId = buildingId;
                return;
            }
            BuildingId = buildingId;
            Instance._key = 0;
            if (IsOpen)
            {
                Instance.Refresh(force: true);
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingId = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingId != null)
            {
                string id = _pendingId;
                _pendingId = null;
                Open(id);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("TurretRoot");
            _title = root.Q<Label>("TurretTitle");
            _state = root.Q<Label>("TurretState");
            _help = root.Q<Button>("TurretHelp");
            _close = root.Q<Button>("TurretClose");
            _message = root.Q<Label>("TurretMessage");
            _secBlueprint = root.Q<Label>("TurretSecBlueprint");
            _blueprint = root.Q<DropdownField>("TurretBlueprint");
            _blueprintHint = root.Q<Label>("TurretBlueprintHint");
            _secMode = root.Q<Label>("TurretSecMode");
            for (int i = 0; i < _modes.Length; i++)
            {
                int slot = i;
                _modes[i] = root.Q<Button>("TurretMode" + i);
                _modes[i].clicked += () => ClickMode(slot);
                UiTooltip.Attach(_modes[i], () => ModeTooltip(slot));
            }
            _modeDesc = root.Q<Label>("TurretModeDesc");
            _modeAll = root.Q<Button>("TurretModeAll");
            _modeSame = root.Q<Button>("TurretModeSame");
            _secStats = root.Q<Label>("TurretSecStats");
            _status = root.Q<Label>("TurretStatus");
            _kills = root.Q<Label>("TurretKills");
            _heat = root.Q<Label>("TurretHeat");
            _weapon = root.Q<Label>("TurretWeapon");
            _supply = root.Q<Label>("TurretSupply");
            _uplink = root.Q<Button>("TurretUplink");
            _uplinkHint = root.Q<Label>("TurretUplinkHint");
            _footer = root.Q<Label>("TurretFooter");
            _overview = root.Q<Button>("TurretOverview");
            if (_overview != null)
            {
                _overview.clicked += ClickOverview; // FG6-DEF-06：防御总览入口
                UiTooltip.Attach(_overview, () => new TooltipContent
                {
                    Title = GameText.Get("defov.title"),
                    Body = GameText.Format("raid.spec.overview_tip", InputDisplay.ForAction(GameActionId.OpenDefense)),
                    Shortcut = GameActionId.OpenDefense,
                });
            }

            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _modeAll.clicked += ClickModeAll;
            _modeSame.clicked += ClickModeSame;
            _uplink.clicked += ClickUplink;
            _blueprint.RegisterValueChangedCallback(_ => OnBlueprintChanged());
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.TurretFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                SetMessage(string.Empty, false);
                Refresh(force: true);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh(force: false);
        }

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        public void ClickMode(int slot)
        {
            if (slot < 0 || slot >= _modeCodes.Length)
            {
                return;
            }
            Show(TurretService.TrySetMode(CampaignSession.Current, BuildingId, _modeCodes[slot]));
        }

        public void ClickModeAll()
        {
            TurretRecord r = TurretService.Find(CampaignSession.Current, BuildingId);
            Show(r != null ? TurretService.TrySetModeBatch(CampaignSession.Current, r.TargetMode)
                : TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found")));
        }

        public void ClickModeSame()
        {
            TurretRecord r = TurretService.Find(CampaignSession.Current, BuildingId);
            Show(r != null ? TurretService.TrySetModeBatch(CampaignSession.Current, r.TargetMode, BuildingId)
                : TurretOpResult.Fail(TurretFailure.NotFound, GameText.Get("turret.reason.not_found")));
        }

        /// <summary>蓝图下拉选第 <paramref name="index"/> 项（与玩家点选同一回调：选中即生效）。</summary>
        public void SelectBlueprint(int index)
        {
            if (index >= 0 && index < _blueprint.choices.Count)
            {
                _blueprint.index = index;
            }
        }

        private void OnBlueprintChanged()
        {
            if (_suppress)
            {
                return;
            }
            int i = _blueprint.index;
            if (i < 0 || i >= _blueprintIds.Count)
            {
                return;
            }
            TurretRecord r = TurretService.Find(CampaignSession.Current, BuildingId);
            int rowVersion = i < _blueprintRowVersions.Count ? _blueprintRowVersions[i] : 0;
            if (r != null && r.BlueprintId == _blueprintIds[i] && (rowVersion == r.BlueprintVersion
                || TurretService.TryGetReadout(CampaignSession.Current, BuildingId, out TurretReadout ro) && ro.BlueprintVersion == ro.LatestVersion))
            {
                return; // 选的就是现在装的（同一版本）：不算一次操作
            }
            Show(TurretService.TryAssignBlueprint(CampaignSession.Current, BuildingId, _blueprintIds[i]));
        }

        public void ClickUplink()
        {
            CampaignState s = CampaignSession.Current;
            Show(TurretUplink.IsUplinkedTo(s, BuildingId) ? TurretUplink.Leave(s) : TurretUplink.Request(s, BuildingId));
        }

        public void OpenCodex() => MechanicCodex.Open("codex.defense.turret");

        /// <summary>FG6-DEF-06：“防御总览…”——关掉炮塔面板、打开防御总览（总览在炮塔面板之下，不先关会被盖住）。</summary>
        public void ClickOverview()
        {
            SetOpen(false);
            DefenseOverviewPanelUIToolkit.Open();
        }

        private TooltipContent ModeTooltip(int slot)
        {
            int code = slot >= 0 && slot < _modeCodes.Length ? _modeCodes[slot] : 0;
            return TurretCatalog.TryGetMode(code, out TurretModeDef d)
                ? new TooltipContent { Title = d.Name, Body = d.Description }
                : new TooltipContent { Title = string.Empty, Body = string.Empty };
        }

        private void Show(TurretOpResult r)
        {
            SetMessage(r.Message, !r.Ok);
            if (!r.Ok)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _key = 0;
            Refresh(force: true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("tu-message-error", error);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            BuildingRecord b = HomeGridService.FindBuilding(s, BuildingId);
            int key = HashCode.Combine(TurretService.Revision, TurretUplink.Revision, (int)GameText.Language, GameSettings.Revision, BuildingId,
                b?.ConstructionState ?? 0, b?.PowerState ?? 0, s != null ? s.GetHashCode() : 0);
            if (force || key != _key)
            {
                _key = key;
                RebuildStructure(s, b);
                _nextReadings = 0f;
            }
            float now = Time.unscaledTime;
            if (force || now >= _nextReadings || !Application.isPlaying)
            {
                _nextReadings = now + ReadingsInterval;
                RefreshReadings(s);
            }
        }

        private void RebuildStructure(CampaignState s, BuildingRecord b)
        {
            _suppress = true;
            try
            {
                bool found = TurretService.TryGetReadout(s, BuildingId, out TurretReadout ro);
                _title.text = GameText.Format("turret.panel.title", found ? ro.Name : string.Empty);
                _close.text = GameText.Get("turret.panel.close");
                _help.text = GameText.Get("prod.panel.help");
                _secBlueprint.text = GameText.Get("turret.panel.sec.blueprint");
                _secMode.text = GameText.Get("turret.panel.sec.mode");
                _secStats.text = GameText.Get("turret.panel.sec.stats");
                _modeAll.text = GameText.Get("turret.panel.mode_all");
                _modeSame.text = GameText.Get("turret.panel.mode_same");
                _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("turret.panel.footer"));
                _blueprint.label = GameText.Get("turret.panel.sec.blueprint");
                _blueprintHint.text = GameText.Get("turret.panel.blueprint_hint");

                // 蓝图下拉：同一种炮塔座能装的（选中项 = 现在装的；现在装的不能用了也列出来，选别的就换掉）。
                _blueprintIds.Clear();
                _blueprintRowVersions.Clear();
                var choices = new List<string>();
                if (found)
                {
                    var usable = new List<string>();
                    TurretService.BlueprintChoices(s, ro.Size, usable);
                    var ids = new List<string>(usable);
                    if (!string.IsNullOrEmpty(ro.BlueprintId) && !ids.Contains(ro.BlueprintId))
                    {
                        ids.Insert(0, ro.BlueprintId);
                    }
                    foreach (string id in ids)
                    {
                        BlueprintRecord rec = Campaign.Blueprint.BlueprintEditorService.Find(s, id);
                        string name = TurretService.BlueprintName(rec);
                        int ver = id == ro.BlueprintId ? ro.BlueprintVersion : rec?.ActiveVersion ?? 0;
                        _blueprintIds.Add(id);
                        _blueprintRowVersions.Add(ver);
                        choices.Add(GameText.Format("turret.panel.blueprint_row", name, ver.ToString(CultureInfo.InvariantCulture))); // 同名由 Apply 去重
                        // FG6-E2E-01（FG-GAP-115）：已建炮塔钉住建造时的版本；这张蓝图后来改出了新版本时，紧跟着列一行“同一张蓝图 · 最新版本”，选它就换到最新版本
                        // （原来下拉里同一张蓝图只有一行、显示的是旧版本，重新选它不触发变化——已建的炮塔没有任何入口换到新版本）。
                        if (id == ro.BlueprintId && rec != null && rec.ActiveVersion > ro.BlueprintVersion && usable.Contains(id))
                        {
                            _blueprintIds.Add(id);
                            _blueprintRowVersions.Add(rec.ActiveVersion);
                            choices.Add(GameText.Format("turret.panel.blueprint_row", name, rec.ActiveVersion.ToString(CultureInfo.InvariantCulture)));
                        }
                    }
                }
                DropdownChoices.Apply(_blueprint, choices, GameText.Format("turret.panel.blueprint_none", found ? TurretCatalog.SizeName(ro.Size) : string.Empty));
                int cur = found ? _blueprintIds.IndexOf(ro.BlueprintId) : -1;
                if (choices.Count > 0)
                {
                    _blueprint.SetValueWithoutNotify(_blueprint.choices[Math.Max(0, cur)]);
                }

                // 目标模式按钮（按表的排序；选中的描边 + “●”）。
                IReadOnlyList<TurretModeDef> modes = TurretCatalog.Modes;
                for (int i = 0; i < _modes.Length; i++)
                {
                    bool has = i < modes.Count;
                    _modeCodes[i] = has ? modes[i].Code : -1;
                    _modes[i].EnableInClassList("uk-hidden", !has);
                    if (!has)
                    {
                        continue;
                    }
                    bool selected = found && ro.TargetMode == modes[i].Code;
                    _modes[i].text = (selected ? "● " : string.Empty) + modes[i].Name;
                    _modes[i].EnableInClassList("tu-mode-selected", selected);
                    _modes[i].SetEnabled(found);
                }
                _modeDesc.text = found && TurretCatalog.TryGetMode(ro.TargetMode, out TurretModeDef md) ? md.Description : string.Empty;
                _modeAll.SetEnabled(found);
                _modeSame.SetEnabled(found && !string.IsNullOrEmpty(ro.BlueprintId));

                // 接入
                if (_overview != null)
                {
                    _overview.text = GameText.Get("defov.open_from_turret");
                }
                _uplink.text = GameText.Get(found && ro.Uplinked ? "turret.panel.leave" : "turret.panel.uplink");
                _uplink.SetEnabled(found && (ro.Uplinked || ro.HasPort));
                _uplinkHint.text = !found ? string.Empty
                    : ro.HasPort ? GameText.Format("turret.panel.uplink_hint", InputDisplay.ForAction(GameActionId.ToggleCameraView))
                    : GameText.Get("turret.panel.no_port");
            }
            finally
            {
                _suppress = false;
            }
        }

        private void RefreshReadings(CampaignState s)
        {
            if (!TurretService.TryGetReadout(s, BuildingId, out TurretReadout ro))
            {
                _state.text = GameText.Get("turret.reason.not_found");
                _status.text = string.Empty;
                _kills.text = string.Empty;
                _heat.text = string.Empty;
                _weapon.text = string.Empty;
                _supply.text = string.Empty;
                return;
            }
            string statusName = GameText.Get(BuildingOps.UiStatusNameKey(ro.Status.Kind));
            _state.text = statusName;
            _status.text = ro.Status.Reason;
            _kills.text = GameText.Format("turret.panel.kills", ro.Kills, ro.EliteKills);
            _heat.text = ro.HasUnit ? GameText.Format("turret.panel.heat", Mathf.RoundToInt(ro.Heat), Mathf.RoundToInt(ro.OverheatAt), Mathf.RoundToInt(ro.RecoverBelow)) : string.Empty;
            _heat.EnableInClassList("tu-readings-warn", ro.Overheated);
            if (ro.Valid)
            {
                string turn = ro.TurnRate > 0f ? GameText.Format("turret.panel.turn_rate", Mathf.RoundToInt(ro.TurnRate)) : GameText.Get("turret.panel.turn_free");
                string delivery = ro.Projectile ? GameText.Get("turret.panel.delivery_projectile") : GameText.Format("turret.panel.delivery_instant", ro.Carrier);
                _weapon.text = GameText.Format("turret.panel.weapon", ro.Range.ToString("0.#", CultureInfo.InvariantCulture), turn,
                    ro.DamagePerShot.ToString("0.#", CultureInfo.InvariantCulture), ro.Cooldown.ToString("0.##", CultureInfo.InvariantCulture), delivery)
                    + "\n" + GameText.Get("turret.panel.range_ring");
            }
            else
            {
                _weapon.text = ro.InvalidReason;
            }
            _supply.text = GameText.Format("turret.panel.supply", ro.SupplyLine);
            _supply.EnableInClassList("tu-readings-warn", ro.SupplyShort);
        }
    }
}
