using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Core;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

namespace GameLogic.UI.Objective
{
    /// <summary>任务日志一行（纯数据，自检直接断言）。</summary>
    public struct QuestRowView
    {
        public string Id;
        public string Text;
        public bool Tracked;
        public bool Main;
        public bool Warn;
        /// <summary>false = 空栏提示行（不可点选）。</summary>
        public bool Selectable;
    }

    /// <summary>任务日志右侧的详情（纯数据，自检直接断言）。</summary>
    public sealed class QuestDetailView
    {
        public string Id;
        public string Title = string.Empty;
        public string Source = string.Empty;
        public string Goal = string.Empty;
        public string Progress = string.Empty;
        public string Where = string.Empty;
        public string Reward = string.Empty;
        public string Time = string.Empty;
        public string Note = string.Empty;
        public bool CanAccept;
        public bool CanTrack;
        public bool TrackOn;
        public bool CanFly;
        public bool CanAbandon;
        public bool CanRestart;
        public bool CanClaim;
        public string TrackRefusal = string.Empty;
    }

    /// <summary>
    /// 任务日志（ER8 收尾 UI-14 起；FG8-QST-01 改为 FGU-36 三栏任务面板）：进行中 / 已完成 / 已失败三栏（主线、支线、家园指引与 Demo 目标链 OBJ-01～10 一起列出），
    /// 右侧详情写明目标、位置、奖励预览、有无时限，可以追踪（最多 3 个）、让镜头飞过去、接取支线、放弃支线（二次确认）、重新开始失败的主线、领取家园指引的奖励；
    /// 下方是关键材料清单（FGR-QST-020）与 Demo 的三个旧地点 / 核心门三灯。任务日志键（默认 L，可重绑）开关，取消键或关闭按钮收起。
    /// 只做展示与转发：判定与状态全在 <see cref="CampaignObjectiveTracker"/>。
    /// </summary>
    public sealed class MissionLogUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.5f;
        private const float AbandonConfirmSeconds = 4f;

        /// <summary>战役地图的三行，顺序固定（与 UXML 的 RegionRow0～2 对应）。</summary>
        public static readonly string[] MapRegionIds =
        {
            HomeValleyLayout.RegionId,
            FracturedCityLayout.RegionId,
            FoundryOutpostLayout.RegionId,
        };

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;
        private bool _ownsRowTemplate;

        private VisualElement _window;
        private Label _title;
        private Label _status;
        private Label _gate;
        private Label _footer;
        private Label _keyTitle;
        private Label _campaignTitle;
        private VisualElement _keyList;
        private readonly ScrollView[] _cols = new ScrollView[3];
        private readonly Label[] _colTitles = new Label[3];
        private readonly List<VisualElement>[] _colRows = { new List<VisualElement>(), new List<VisualElement>(), new List<VisualElement>() };
        private readonly List<QuestRowView>[] _colViews = { new List<QuestRowView>(), new List<QuestRowView>(), new List<QuestRowView>() };
        private readonly List<VisualElement> _keyRows = new List<VisualElement>();
        private Label _dTitle, _dSource, _dGoal, _dProgress, _dWhere, _dReward, _dTime, _dNote, _message;
        private Button _accept, _track, _fly, _claim, _restart, _abandon;
        private readonly Label[] _regionNames = new Label[3];
        private readonly Label[] _regionStates = new Label[3];
        private readonly Label[] _regionDetails = new Label[3];
        private readonly QuestDetailView _detail = new QuestDetailView();
        private bool _open;
        private float _refreshTimer;
        private int _renderedRevision = int.MinValue;
        private string _selected;
        private string _abandonArmed;
        private float _abandonArmedAt;

        public static bool IsOpen { get; private set; }

        /// <summary>自检：当前实例（运行时只有一个）。</summary>
        public static MissionLogUIToolkit Instance { get; private set; }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("MissionLog");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("QuestRow");
            _ownsRowTemplate = true;
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：覆盖面板(10)。
            _document.sortingOrder = 10;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null)
            {
                Log.Error("[MissionLogUIToolkit] rootVisualElement 等待超时，任务日志未初始化。");
                return;
            }
            BindView(root);
            // 拖动、点击置顶、窗口下方世界输入拦截统一走 UiWindowFocus（UI 红线第 9 条）。
            UiWindowFocus.Attach(_document, _window, root.Q<VisualElement>("MissionLogHeader"), "mission-log");
        }

        /// <summary>自检：没有资源系统时直接给行模板。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row) => _rowTemplate = row;

        /// <summary>绑定界面元素（运行时 Start 调用；自检挂上 UXML 后直接调用）。</summary>
        public void BindView(VisualElement root)
        {
            Instance = this;
            _window = root.Q<VisualElement>("MissionLogRoot");
            _title = root.Q<Label>("MissionLogTitle");
            _status = root.Q<Label>("MissionLogStatus");
            _gate = root.Q<Label>("CoreGateLabel");
            _footer = root.Q<Label>("MissionLogFooter");
            _keyTitle = root.Q<Label>("KeyMatTitle");
            _campaignTitle = root.Q<Label>("CampaignTitle");
            _keyList = root.Q<VisualElement>("KeyMatList");
            string[] cols = { "QuestColActive", "QuestColDone", "QuestColFailed" };
            for (int i = 0; i < 3; i++)
            {
                _cols[i] = root.Q<ScrollView>(cols[i]);
                _colTitles[i] = root.Q<Label>(cols[i] + "Title");
            }
            _dTitle = root.Q<Label>("QuestDetailTitle");
            _dSource = root.Q<Label>("QuestDetailSource");
            _dGoal = root.Q<Label>("QuestDetailGoal");
            _dProgress = root.Q<Label>("QuestDetailProgress");
            _dWhere = root.Q<Label>("QuestDetailWhere");
            _dReward = root.Q<Label>("QuestDetailReward");
            _dTime = root.Q<Label>("QuestDetailTime");
            _dNote = root.Q<Label>("QuestDetailNote");
            _message = root.Q<Label>("QuestMessage");
            _accept = BindButton(root, "QuestBtnAccept", OnAccept);
            _track = BindButton(root, "QuestBtnTrack", OnTrack);
            _fly = BindButton(root, "QuestBtnFly", OnFly);
            _claim = BindButton(root, "QuestBtnClaim", OnClaim);
            _restart = BindButton(root, "QuestBtnRestart", OnRestart);
            _abandon = BindButton(root, "QuestBtnAbandon", OnAbandon);
            for (int i = 0; i < MapRegionIds.Length; i++)
            {
                _regionNames[i] = root.Q<Label>("RegionRowName" + i);
                _regionStates[i] = root.Q<Label>("RegionRowState" + i);
                _regionDetails[i] = root.Q<Label>("RegionRowDetail" + i);
            }
            Button close = root.Q<Button>("MissionLogClose");
            if (close != null)
            {
                close.clicked += () => SetOpen(false);
            }
        }

        private static Button BindButton(VisualElement root, string name, Action onClick)
        {
            Button b = root.Q<Button>(name);
            if (b != null)
            {
                b.clicked += onClick;
            }
            return b;
        }

        private void Update()
        {
            if (_window == null || _document == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null || !ObjectiveHudUIToolkit.AnyRegionActive())
            {
                if (_open)
                {
                    SetOpen(false);
                }
                return;
            }

            // FG0-UX-01：FG13 第 5 节把“任务日志”（L）与“地图”（M）分成两个键。FG3-GEN-01 起地图键打开战略地图（StrategicMapUIToolkit），
            // 这里只认任务日志键；例外：旧版本原型地形的存档（生成器版本 0）没有战略地图，地图键照旧打开任务日志（兼战役地图），
            // 不让按键静默无反应（FG00 B06）。战略地图那边对这类存档不读地图键，两边不会抢。
            if (InputRouter.ConsumeGlobalAction(GameActionId.ToggleMissionLog)
                || (!StrategicMapUIToolkit.HasWorldMap(state) && InputRouter.ConsumeGlobalAction(GameActionId.OpenMap)))
            {
                SetOpen(!_open);
            }
            // FG0-UX-01：Esc 不在这里抢先读——任务日志在 Esc 栈里占一层，由 UiKitInputPump 统一“先关最上层”
            // （例如任务日志上面又开了通知中心，Esc 先关通知中心）。

            if (!_open)
            {
                return;
            }
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f && _renderedRevision == CampaignObjectiveTracker.QuestRevision)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;
            Render(state);
        }

        private System.Action _escClose;

        public void SetOpen(bool open)
        {
            _open = open;
            IsOpen = open;
            _window.EnableInClassList("mw-hidden", !open);
            if (open)
            {
                _refreshTimer = 0f;
                _renderedRevision = int.MinValue;
                if (_document != null)
                {
                    UiWindowFocus.BringToFront(_document, _window);
                }
                UiEscapeStack.Push(this, _escClose ??= () => SetOpen(false));
                GuidanceHooks.Raise(GuidanceHooks.QuestFirstLogOpen);
                CampaignState s = CampaignSession.Current;
                if (s != null)
                {
                    Render(s);
                }
            }
            else
            {
                UiEscapeStack.Remove(this);
                _abandonArmed = null;
            }
        }

        /// <summary>按当前战役状态重画（运行时每 0.5 秒 / 任务有变化时；自检直接调用）。</summary>
        public void Render(CampaignState state)
        {
            if (_window == null || state == null)
            {
                return;
            }
            _renderedRevision = CampaignObjectiveTracker.QuestRevision;
            if (_abandonArmed != null && Time.realtimeSinceStartup - _abandonArmedAt > AbandonConfirmSeconds)
            {
                _abandonArmed = null;
            }
            _title.text = GameText.Get("quest.log.title");
            Button close = _window.Q<Button>("MissionLogClose");
            if (close != null)
            {
                close.text = GameText.Get("quest.btn.close");
            }
            ComposeColumns(state, _colViews[0], _colViews[1], _colViews[2]);
            _status.text = ComposeQuestStatus(state);
            string[] titleKeys = { "quest.log.col.active", "quest.log.col.done", "quest.log.col.failed" };
            for (int c = 0; c < 3; c++)
            {
                int real = _colViews[c].Count(v => v.Selectable);
                _colTitles[c].text = GameText.Format(titleKeys[c], real);
                FillColumn(c);
            }
            if (_selected == null || !AnyRow(_selected))
            {
                _selected = FirstSelectable();
            }
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < _colViews[c].Count; i++)
                {
                    _colRows[c][i].EnableInClassList("ml-q-selected", _colViews[c][i].Selectable && _colViews[c][i].Id == _selected);
                }
            }
            ComposeDetail(state, _selected, _detail);
            RenderDetail();
            RenderKeyMaterials(state);
            _campaignTitle.text = GameText.Format("quest.log.row_suffix", GameText.Get("quest.log.campaign"), ComposeStatus(state));
            for (int i = 0; i < MapRegionIds.Length; i++)
            {
                ComposeRegionRow(state, MapRegionIds[i], out string name, out string stateText, out string detail);
                _regionNames[i].text = name;
                _regionStates[i].text = stateText;
                _regionDetails[i].text = detail;
            }
            _gate.text = ComposeGateText(state);
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("quest.footer"));
            _message.text = CampaignObjectiveTracker.LastQuestMessage ?? string.Empty;
        }

        private bool AnyRow(string id)
        {
            for (int c = 0; c < 3; c++)
            {
                foreach (QuestRowView v in _colViews[c])
                {
                    if (v.Selectable && v.Id == id)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private string FirstSelectable()
        {
            for (int c = 0; c < 3; c++)
            {
                foreach (QuestRowView v in _colViews[c])
                {
                    if (v.Selectable)
                    {
                        return v.Id;
                    }
                }
            }
            return null;
        }

        private VisualElement NewRow(VisualElement parent)
        {
            VisualElement row;
            if (_rowTemplate != null)
            {
                TemplateContainer t = _rowTemplate.Instantiate();
                row = t.Q<VisualElement>("QuestRowRoot") ?? t;
                parent.Add(t);
            }
            else
            {
                // 行模板没加载到（资源缺失）：退化为一个纯文字行，至少能看能点（B06：不静默空白）。
                row = new VisualElement();
                row.AddToClassList("ml-q-row");
                var l = new Label { name = "QuestRowText" };
                l.AddToClassList("ml-q-text");
                row.Add(l);
                parent.Add(row);
            }
            return row;
        }

        private void FillColumn(int c)
        {
            ScrollView col = _cols[c];
            List<VisualElement> rows = _colRows[c];
            List<QuestRowView> views = _colViews[c];
            while (rows.Count < views.Count)
            {
                VisualElement row = NewRow(col.contentContainer);
                row.RegisterCallback<ClickEvent>(evt =>
                {
                    if (row.userData is string id)
                    {
                        Select(id);
                    }
                });
                rows.Add(row);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                VisualElement row = rows[i];
                bool used = i < views.Count;
                (row.parent is TemplateContainer tc ? tc : row).style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
                if (!used)
                {
                    row.userData = null;
                    continue;
                }
                QuestRowView v = views[i];
                row.userData = v.Selectable ? v.Id : null;
                row.Q<Label>("QuestRowText").text = v.Text;
                row.EnableInClassList("ml-q-main", v.Main);
                row.EnableInClassList("ml-q-tracked", v.Tracked);
                row.EnableInClassList("ml-q-warn", v.Warn);
                row.EnableInClassList("ml-q-empty", !v.Selectable);
            }
        }

        private void RenderDetail()
        {
            QuestDetailView d = _detail;
            _dTitle.text = d.Title;
            _dSource.text = d.Source;
            _dGoal.text = d.Goal;
            _dProgress.text = d.Progress;
            _dWhere.text = d.Where;
            _dReward.text = d.Reward;
            _dTime.text = d.Time;
            _dNote.text = d.Note;
            ShowButton(_accept, d.CanAccept, "quest.btn.accept");
            ShowButton(_track, d.Id != null && (d.CanTrack || d.TrackOn || !string.IsNullOrEmpty(d.TrackRefusal)), d.TrackOn ? "quest.btn.untrack" : "quest.btn.track");
            _track?.SetEnabled(d.CanTrack || d.TrackOn);
            if (_track != null)
            {
                _track.tooltip = d.CanTrack || d.TrackOn ? string.Empty : d.TrackRefusal;
            }
            ShowButton(_fly, d.Id != null, "quest.btn.fly");
            _fly?.SetEnabled(d.CanFly);
            ShowButton(_claim, d.CanClaim, "quest.btn.claim");
            ShowButton(_restart, d.CanRestart, "quest.btn.restart");
            ShowButton(_abandon, d.CanAbandon, _abandonArmed != null && _abandonArmed == d.Id ? "quest.btn.abandon_confirm" : "quest.btn.abandon");
        }

        private static void ShowButton(Button b, bool show, string key)
        {
            if (b == null)
            {
                return;
            }
            b.EnableInClassList("ml-btn-hidden", !show);
            b.text = GameText.Get(key);
        }

        private void RenderKeyMaterials(CampaignState state)
        {
            _keyTitle.text = GameText.Get("quest.keymat.title");
            List<string> lines = ComposeKeyMaterials(state);
            while (_keyRows.Count < lines.Count)
            {
                _keyRows.Add(NewRow(_keyList));
            }
            for (int i = 0; i < _keyRows.Count; i++)
            {
                VisualElement row = _keyRows[i];
                bool used = i < lines.Count;
                (row.parent is TemplateContainer tc ? tc : row).style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
                if (used)
                {
                    row.Q<Label>("QuestRowText").text = lines[i];
                }
            }
        }

        // ── 操作（全部转发给追踪器；结果文字写在详情下方）─────────────────────

        public void Select(string id)
        {
            if (_selected != id)
            {
                _abandonArmed = null;
            }
            _selected = id;
            CampaignState s = CampaignSession.Current;
            if (s != null)
            {
                Render(s);
            }
        }

        private void OnAccept() => Do((s, id) => CampaignObjectiveTracker.TryAccept(s, id, out _));

        private void OnTrack() => Do((s, id) =>
        {
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(s, id);
            CampaignObjectiveTracker.TrySetTracked(s, id, r == null || r.TrackOrder <= 0, out _);
        });

        private void OnFly() => Do((s, id) => CampaignObjectiveTracker.TryFlyTo(s, id, out _));

        private void OnClaim() => Do((s, id) => CampaignObjectiveTracker.TryClaim(s, id, out _));

        private void OnRestart() => Do((s, id) => CampaignObjectiveTracker.TryRestart(s, id, out _));

        /// <summary>放弃是不可逆的（B04）：第一次点只是“确认放弃？再点一次”，4 秒内再点才放弃；换选别的任务即取消。</summary>
        private void OnAbandon() => Do((s, id) =>
        {
            if (_abandonArmed != id)
            {
                _abandonArmed = id;
                _abandonArmedAt = Time.realtimeSinceStartup;
                return;
            }
            _abandonArmed = null;
            CampaignObjectiveTracker.TryAbandon(s, id, out _);
        });

        private void Do(Action<CampaignState, string> action)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || _selected == null)
            {
                return;
            }
            action(s, _selected);
            Render(s);
        }

        // ── 自检读取 ──────────────────────────────────────────────────────

        public int RowCount(int column) => column >= 0 && column < 3 ? _colViews[column].Count : 0;
        public QuestRowView RowView(int column, int index) => _colViews[column][index];
        public string RowText(int column, int index) => _colRows[column][index].Q<Label>("QuestRowText").text;
        public VisualElement RowElement(int column, int index) => _colRows[column][index];
        public string ColumnTitle(int column) => _colTitles[column].text;
        public string Selected => _selected;
        public QuestDetailView Detail => _detail;
        public Button AcceptButton => _accept;
        public Button TrackButton => _track;
        public Button FlyButton => _fly;
        public Button ClaimButton => _claim;
        public Button RestartButton => _restart;
        public Button AbandonButton => _abandon;
        public string MessageText => _message?.text;
        public string StatusText => _status?.text;
        public int KeyRowCount => _keyRows.Count(r => (r.parent is TemplateContainer tc ? tc : r).style.display != DisplayStyle.None);
        public string KeyRowText(int i) => _keyRows[i].Q<Label>("QuestRowText").text;
        public static bool ButtonShown(Button b) => b != null && !b.ClassListContains("ml-btn-hidden");

        // ── 纯文本合成（自检直接断言，不经 UIDocument）──────────────────────

        /// <summary>
        /// 三栏内容：进行中 = 当前 Demo 目标（OBJ，主线，常驻目标条）+ 进行中的任务（主线 → 支线 → 家园指引）+ 可接取的支线；
        /// 已完成 = 完成的任务与 OBJ 目标（新完成的在上）；已失败 = 失败与放弃的任务（写原因）。家园指引在设置里关掉时不列出。空栏有一行提示。
        /// </summary>
        public static void ComposeColumns(CampaignState state, List<QuestRowView> active, List<QuestRowView> done, List<QuestRowView> failed)
        {
            active.Clear();
            done.Clear();
            failed.Clear();
            var objDone = new List<QuestRowView>();
            if (state != null)
            {
                string current = CampaignObjectiveTracker.CurrentObjectiveId(state);
                for (int i = 0; i < CampaignObjectiveCatalog.All.Length; i++)
                {
                    ObjectiveDef def = CampaignObjectiveCatalog.All[i];
                    ObjectiveState st = CampaignObjectiveTracker.StateOf(state, def.Id);
                    string text = GameText.Format("quest.log.row", QuestCatalog.CategoryName(QuestCatalog.CatMain), $"{i + 1}. {def.Title}");
                    if (def.Id == current)
                    {
                        active.Add(new QuestRowView { Id = def.Id, Text = text, Main = true, Selectable = true });
                    }
                    else if (st == ObjectiveState.Completed)
                    {
                        objDone.Add(new QuestRowView { Id = def.Id, Text = text, Main = true, Selectable = true });
                    }
                }
                var quests = new List<ObjectiveRecord>();
                foreach (ObjectiveRecord r in state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                {
                    if (CampaignObjectiveTracker.IsQuestRecord(r) && !CampaignObjectiveTracker.IsHidden(r))
                    {
                        quests.Add(r);
                    }
                }
                quests.Sort((a, b) => Order(a).CompareTo(Order(b)) != 0 ? Order(a).CompareTo(Order(b)) : string.CompareOrdinal(a.ObjectiveId, b.ObjectiveId));
                foreach (ObjectiveRecord r in quests.Where(r => r.State == ObjectiveState.Active))
                {
                    active.Add(Row(state, r));
                }
                foreach (ObjectiveRecord r in quests.Where(r => r.State == ObjectiveState.Available))
                {
                    active.Add(Row(state, r));
                }
                // 已完成：任务在前（新完成的在上），Demo 目标链按顺序在后。
                foreach (ObjectiveRecord r in quests.Where(r => r.State == ObjectiveState.Completed).OrderByDescending(r => r.EndedTick).ThenBy(r => r.ObjectiveId, StringComparer.Ordinal))
                {
                    done.Add(Row(state, r));
                }
                done.AddRange(objDone);
                foreach (ObjectiveRecord r in quests.Where(r => r.State == ObjectiveState.Failed || r.State == ObjectiveState.Abandoned).OrderByDescending(r => r.EndedTick)
                             .ThenBy(r => r.ObjectiveId, StringComparer.Ordinal))
                {
                    failed.Add(Row(state, r));
                }
            }
            if (active.Count == 0)
            {
                active.Add(new QuestRowView { Text = GameText.Get("quest.log.empty.active") });
            }
            if (done.Count == 0)
            {
                done.Add(new QuestRowView { Text = GameText.Get("quest.log.empty.done") });
            }
            if (failed.Count == 0)
            {
                failed.Add(new QuestRowView { Text = GameText.Get("quest.log.empty.failed") });
            }
        }

        private static int Order(ObjectiveRecord r)
        {
            Quest q = CampaignObjectiveTracker.DefOf(r);
            return q?.SortOrder ?? int.MaxValue;
        }

        private static QuestRowView Row(CampaignState state, ObjectiveRecord r)
        {
            Quest q = CampaignObjectiveTracker.DefOf(r);
            string title = CampaignObjectiveTracker.QuestTitle(r);
            string suffix = r.State == ObjectiveState.Available ? GameText.Get("quest.state.available")
                : r.State == ObjectiveState.Abandoned ? GameText.Get("quest.state.abandoned")
                : r.State == ObjectiveState.Failed ? GameText.Get(string.IsNullOrEmpty(r.FailReasonKey) ? "quest.state.failed" : r.FailReasonKey)
                : CampaignObjectiveTracker.CanClaim(r) ? GameText.Get("quest.state.claimable")
                : null;
            if (r.State == ObjectiveState.Active && CampaignObjectiveTracker.RemainingSeconds(r) is double left)
            {
                suffix = GameText.Format("quest.hud.time", QuestCatalog.FormatRemaining(left));
            }
            if (!string.IsNullOrEmpty(suffix))
            {
                title = GameText.Format("quest.log.row_suffix", title, suffix);
            }
            bool tracked = r.TrackOrder > 0 && r.State == ObjectiveState.Active;
            return new QuestRowView
            {
                Id = r.ObjectiveId,
                Text = GameText.Format(tracked ? "quest.log.row.tracked" : "quest.log.row", QuestCatalog.CategoryName(q?.Category), title),
                Tracked = tracked,
                Main = q?.Category == QuestCatalog.CatMain,
                Warn = CampaignObjectiveTracker.IsWarningTime(r),
                Selectable = true,
            };
        }

        /// <summary>详情：目标、进度、位置、奖励预览、有无时限（FGR-QST-003），以及可用的按钮（不可用的写原因）。</summary>
        public static void ComposeDetail(CampaignState state, string id, QuestDetailView d)
        {
            d.Id = null;
            d.Title = GameText.Get("quest.log.select");
            d.Source = d.Goal = d.Progress = d.Where = d.Reward = d.Time = d.Note = d.TrackRefusal = string.Empty;
            d.CanAccept = d.CanTrack = d.TrackOn = d.CanFly = d.CanAbandon = d.CanRestart = d.CanClaim = false;
            if (state == null || string.IsNullOrEmpty(id))
            {
                return;
            }
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(state, id);
            ObjectiveDef obj = CampaignObjectiveCatalog.Get(id);
            if (obj != null)
            {
                ObjectiveState st = CampaignObjectiveTracker.StateOf(state, id);
                int index = Array.IndexOf(CampaignObjectiveCatalog.All, obj);
                d.Id = id;
                d.Title = GameText.Format("quest.detail.title", $"{index + 1}. {obj.Title}", QuestCatalog.CategoryName(QuestCatalog.CatMain));
                d.Source = GameText.Format("quest.detail.source", QuestCatalog.SourceName("core_memo"));
                var items = new List<string>(obj.Items.Length);
                foreach (ObjectiveItemDef it in obj.Items)
                {
                    bool ok = it.IsDone(state);
                    string status = ok ? null : it.Status?.Invoke(state);
                    string label = string.IsNullOrEmpty(status) ? it.Label : GameText.Format("quest.log.item_status", it.Label, status);
                    items.Add((ok ? "√ " : "○ ") + label);
                }
                string goal = items.Count > 0 ? items[0] : string.Empty;
                for (int i = 1; i < items.Count; i++)
                {
                    goal = GameText.Format("quest.log.item_join", goal, items[i]);
                }
                d.Goal = GameText.Format("quest.detail.goal", goal);
                d.Progress = GameText.Format("quest.detail.progress", GameText.Get(st == ObjectiveState.Completed ? "quest.state.done" : "quest.state.active"));
                d.Where = GameText.Format("quest.detail.where", CampaignObjectiveCatalog.RegionDisplayName(obj.RegionId));
                d.Reward = GameText.Get("quest.detail.reward_none");
                d.Time = GameText.Get("quest.detail.time_none");
                d.Note = GameText.Get("quest.detail.main_hint");
                d.CanFly = CampaignObjectiveCatalog.LocationOf(state, id).HasValue;
                d.TrackRefusal = GameText.Get("quest.reason.track_chain");
                return;
            }
            Quest q = CampaignObjectiveTracker.DefOf(r);
            if (q == null)
            {
                return;
            }
            d.Id = id;
            string stateText = GameText.Get(r.State == ObjectiveState.Completed ? "quest.state.done"
                : r.State == ObjectiveState.Failed ? "quest.state.failed"
                : r.State == ObjectiveState.Abandoned ? "quest.state.abandoned"
                : r.State == ObjectiveState.Available ? "quest.state.available" : "quest.state.active");
            d.Title = GameText.Format("quest.detail.title", QuestCatalog.Title(q), QuestCatalog.CategoryName(q.Category) + " · " + stateText);
            d.Source = GameText.Format("quest.detail.source", QuestCatalog.SourceName(q.Source));
            d.Goal = GameText.Format("quest.detail.goal", QuestCatalog.Goal(q));
            d.Progress = r.State == ObjectiveState.Active ? GameText.Format("quest.detail.progress", CampaignObjectiveTracker.ProgressText(state, r)) : string.Empty;
            d.Where = r.HasAnchor ? GameText.Format("quest.detail.where", CampaignObjectiveTracker.WhereText(state, r)) : GameText.Get("quest.detail.where_none");
            string reward = QuestCatalog.RewardText(q);
            d.Reward = string.IsNullOrEmpty(reward) ? GameText.Get("quest.detail.reward_none") : GameText.Format("quest.detail.reward", reward);
            double? left = CampaignObjectiveTracker.RemainingSeconds(r);
            if (left.HasValue)
            {
                d.Time = GameText.Format(CampaignObjectiveTracker.IsWarningTime(r) ? "quest.detail.time_warn" : "quest.detail.time", QuestCatalog.FormatRemaining(left.Value));
            }
            else if (QuestCatalog.HasTimeLimit(q))
            {
                d.Time = GameText.Format("quest.detail.time_total", QuestCatalog.FormatRemaining(q.TimeHours * GameClock.HourSeconds),
                    GameText.Get(q.TimerFrom == "stage" ? "quest.detail.time_from_stage" : "quest.detail.time_from_accept"));
            }
            else
            {
                d.Time = GameText.Get("quest.detail.time_none");
            }
            var notes = new List<string>(3);
            if (r.Attempt > 1)
            {
                notes.Add(GameText.Format("quest.detail.attempt", r.Attempt));
            }
            if (r.EndedTick >= 0 && r.State != ObjectiveState.Active)
            {
                double at = r.EndedTick / (double)Math.Max(1, GameClock.StepHz);
                notes.Add(GameText.Format("quest.detail.ended", stateText, GameClock.DayOf(at), GameClock.FormatHhMm(at)));
            }
            if (!string.IsNullOrEmpty(r.FailReasonKey))
            {
                notes.Add(GameText.Format("quest.detail.reason", GameText.Get(r.FailReasonKey)));
            }
            if (q.Category == QuestCatalog.CatMain)
            {
                notes.Add(GameText.Get("quest.detail.main_hint"));
            }
            d.Note = string.Join("\n", notes);
            d.CanAccept = r.State == ObjectiveState.Available;
            d.TrackOn = r.TrackOrder > 0 && r.State == ObjectiveState.Active;
            int tracked = CampaignObjectiveTracker.TrackedCount(state);
            d.CanTrack = r.State == ObjectiveState.Active && !d.TrackOn && tracked < CampaignObjectiveTracker.TrackMax;
            d.TrackRefusal = r.State != ObjectiveState.Active ? GameText.Get("quest.reason.track_not_active")
                : tracked >= CampaignObjectiveTracker.TrackMax ? GameText.Format("quest.reason.track_full", CampaignObjectiveTracker.TrackMax) : string.Empty;
            d.CanFly = CampaignObjectiveTracker.QuestLocation(state, r).HasValue;
            d.CanAbandon = QuestCatalog.CanAbandon(q) && CampaignObjectiveTracker.IsOpen(r);
            d.CanRestart = r.State == ObjectiveState.Failed && QuestCatalog.IsRestartable(q);
            d.CanClaim = CampaignObjectiveTracker.CanClaim(r);
        }

        /// <summary>页眉：进行中 / 已完成 / 已失败 / 追踪 n/3。</summary>
        public static string ComposeQuestStatus(CampaignState state)
        {
            int active = 0, done = 0, failed = 0;
            foreach (ObjectiveRecord r in state?.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (r == null || (CampaignObjectiveTracker.IsQuestRecord(r) && CampaignObjectiveTracker.IsHidden(r)))
                {
                    continue;
                }
                if (!CampaignObjectiveTracker.IsQuestRecord(r) && CampaignObjectiveCatalog.Get(r.ObjectiveId) == null)
                {
                    continue;
                }
                switch (r.State)
                {
                    case ObjectiveState.Active:
                    case ObjectiveState.Available:
                        active++;
                        break;
                    case ObjectiveState.Completed:
                        done++;
                        break;
                    case ObjectiveState.Failed:
                    case ObjectiveState.Abandoned:
                        failed++;
                        break;
                }
            }
            return GameText.Format("quest.log.status", active, done, failed, CampaignObjectiveTracker.TrackedCount(state), CampaignObjectiveTracker.TrackMax);
        }

        /// <summary>关键材料清单（FGR-QST-020）：名称｜已获得 / 未获得｜来源｜用途。</summary>
        public static List<string> ComposeKeyMaterials(CampaignState state)
        {
            var lines = new List<string>();
            foreach (KeyMaterial k in QuestCatalog.KeyMaterials)
            {
                // “已获得”按来源判断（用掉也算拿到过）；物品已全部投入建筑时另注“已投入使用”，免得玩家以为还要再拿一次。
                bool have = CampaignObjectiveTracker.HasKeyMaterial(state, k);
                string status = !have ? "quest.keymat.missing" : CampaignObjectiveTracker.KeyMaterialStock(state, k) == 0 ? "quest.keymat.have_used" : "quest.keymat.have";
                lines.Add(GameText.Format("quest.keymat.row", GameText.Get(k.NameKey), GameText.Get(status),
                    GameText.Get(k.SourceKey), GameText.Get(k.UsageKey)));
            }
            return lines;
        }

        /// <summary>第 <paramref name="index"/> 个目标的一行。未解锁的后续目标不剧透内容，只给编号。</summary>
        public static void ComposeObjectiveRow(CampaignState state, int index, out ObjectiveState objState,
            out string stateText, out string title, out string region)
        {
            ObjectiveDef def = CampaignObjectiveCatalog.All[index];
            objState = CampaignObjectiveTracker.StateOf(state, def.Id);
            switch (objState)
            {
                case ObjectiveState.Completed:
                    ObjectiveRecord record = state?.ObjectiveRecords?.FirstOrDefault(r => r != null && r.ObjectiveId == def.Id);
                    stateText = "已完成 " + FormatClock(record?.CompletedAtPlaySeconds ?? 0f);
                    break;
                case ObjectiveState.Active:
                    stateText = "进行中";
                    break;
                default:
                    stateText = "未解锁";
                    break;
            }
            bool locked = objState == ObjectiveState.Locked;
            title = locked ? $"{index + 1}. ？？？" : $"{index + 1}. {def.Title}";
            region = locked ? string.Empty : CampaignObjectiveCatalog.RegionDisplayName(def.RegionId);
        }

        public static string ComposeStatus(CampaignState state)
        {
            int completed = 0;
            foreach (ObjectiveDef def in CampaignObjectiveCatalog.All)
            {
                if (CampaignObjectiveTracker.IsCompleted(state, def.Id))
                {
                    completed++;
                }
            }
            return $"已完成 {completed}/{CampaignObjectiveCatalog.All.Length}｜战役时间 {FormatClock(state?.PlaySeconds ?? 0f)}";
        }

        /// <summary>战役地图的一行（UI-09）：区域名（当前所在加 ◆）、状态、当前目标是否在这里、未解锁时的
        /// 解锁条件、出征次数与带回内容、主核心状态。</summary>
        public static void ComposeRegionRow(CampaignState state, string regionId, out string name, out string stateText, out string detail)
        {
            bool here = state != null && (GameLogic.Campaign.WorldSim.WorldView.ObservedSiteId ?? state.CurrentRegionId) == regionId; // FG0-ARCH-01：镜头所在的地点。
            name = CampaignObjectiveCatalog.RegionDisplayName(regionId) + (here ? " ◆" : string.Empty);
            var parts = new List<string>(5);
            ObjectiveDef current = CampaignObjectiveCatalog.Get(CampaignObjectiveTracker.CurrentObjectiveId(state));
            if (current != null && current.RegionId == regionId)
            {
                parts.Add("当前目标在这里：" + current.Title);
            }
            if (here)
            {
                parts.Add("你现在在这里（◆）");
            }

            if (regionId == HomeValleyLayout.RegionId)
            {
                stateText = "家园";
                if (!here)
                {
                    parts.Add("远征队的出发与撤离地");
                }
                detail = string.Join("｜", parts);
                return;
            }

            RegionRecord record = regionId == FracturedCityLayout.RegionId ? FracturedCityRegion.Find(state)
                : regionId == FoundryOutpostLayout.RegionId ? FoundryOutpostRegion.Find(state)
                : null;
            RegionState regionState = record?.State ?? RegionState.Locked;
            bool bossInitialized = regionId == FoundryOutpostLayout.RegionId && record != null && FoundryOutpostCoreBoss.IsInitialized(record);
            bool bossDestroyed = bossInitialized && FoundryOutpostCoreBoss.GetState(record) == CoreBossState.Destroyed;
            stateText = RegionStateText(regionId, regionState, here, bossDestroyed);

            if (regionState == RegionState.Locked)
            {
                // UI-09：破碎都市需信号塔，铸造外围需完成目标 6。
                parts.Add(regionId == FracturedCityLayout.RegionId
                    ? "解锁条件：修复归还谷地的信号塔"
                    : $"解锁条件：完成目标 6「{CampaignObjectiveCatalog.TitleOf(CampaignObjectiveTracker.Obj06)}」");
            }
            else
            {
                int departures = record?.ExpeditionCount ?? 0;
                parts.Add(departures > 0 ? $"已出征 {departures} 次" : "尚未出征");
            }
            string recovered = RecoveredItems(state, regionId);
            if (!string.IsNullOrEmpty(recovered))
            {
                parts.Add("已带回：" + recovered);
            }
            if (bossInitialized)
            {
                parts.Add("主核心：" + FoundryOutpostCoreBoss.DisplayPhaseText(record));
            }
            detail = string.Join("｜", parts);
        }

        /// <summary>UI-14：三灯分别写明缺什么，完成的打勾（形状 + 文字，不只靠颜色）。FG1-SIG-05：灯 2 / 3 改为“接入就绪”（过载是核心固件，
        /// 熔穿过载只在你接入重炮机、信号核带上过载时打得出），文案走文本键。</summary>
        public static string ComposeGateText(CampaignState state)
        {
            FoundryOutpostRegion.CoreGateLights lights = FoundryOutpostRegion.ComputeCoreGateLights(state);
            string Lamp(bool on, string key) => GameText.Format(on ? "foundry.gate.log.done" : "foundry.gate.log.todo", GameText.Get(key));
            return GameText.Get("foundry.gate.log.title") +
                   Lamp(lights.CannonAnalyzed, "foundry.gate.lamp.cannon") + "｜" +
                   Lamp(lights.OverloadBlueprintSaved, "foundry.gate.lamp.overload_saved") + "｜" +
                   Lamp(lights.MachineEquipped, "foundry.gate.lamp.machine_ready") +
                   GameText.Get(lights.AllReady ? "foundry.gate.log.all_ready" : "foundry.gate.log.need_all");
        }

        /// <summary>区域状态。Active 只表示“出征过、还没肃清”，人不在那里时不写“远征中”；铸造前哨外围肃清的
        /// 只是外围（带回重炮），主核心还在时不能写成整片已肃清。</summary>
        private static string RegionStateText(string regionId, RegionState state, bool here, bool bossDestroyed)
        {
            switch (state)
            {
                case RegionState.Available:
                    return here ? "远征中" : "可出征";
                case RegionState.Active:
                    return here ? "远征中" : "未肃清，可再出征";
                case RegionState.Cleared:
                    if (regionId != FoundryOutpostLayout.RegionId)
                    {
                        return "已肃清";
                    }
                    return bossDestroyed ? "主核心已摧毁" : here ? "核心进攻中" : "外围已肃清";
                default:
                    return "未解锁";
            }
        }

        private static string RecoveredItems(CampaignState state, string regionId)
        {
            if (state?.RegionQuestItems == null)
            {
                return string.Empty;
            }
            IEnumerable<string> names = state.RegionQuestItems
                .Where(q => q != null && q.RegionId == regionId && q.State == RegionQuestItemState.Recovered
                            && !Campaign.Economy.BlackBoxService.IsBlackBox(q)) // FG5-RND-06：黑匣子不是区域任务物（在陈列馆面板里看）
                .Select(q => Campaign.Feedback.FeedbackCues.QuestItemName(q.ContentId))
                .Distinct();
            return string.Join("、", names);
        }

        private static string FormatClock(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }

        private void OnDestroy()
        {
            IsOpen = false;
            if (Instance == this)
            {
                Instance = null;
            }
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_rowTemplate != null && _ownsRowTemplate)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
