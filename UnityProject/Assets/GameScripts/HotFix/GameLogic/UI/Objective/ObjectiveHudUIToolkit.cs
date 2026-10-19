using System;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Objective
{
    /// <summary>目标条要显示的内容（纯数据，由 <see cref="ObjectiveHudUIToolkit.Compose"/> 填写，自检直接断言）。</summary>
    public sealed class ObjectiveHudView
    {
        public const int ItemSlots = 4;

        public bool Visible;
        public string Title = string.Empty;
        public string Region = string.Empty;
        public int ItemCount;
        public readonly string[] ItemLabels = new string[ItemSlots];
        public readonly bool[] ItemDone = new bool[ItemSlots];
        /// <summary>未完成项的现状（UI-10“在地面/已装车/已带回”），null＝没有。</summary>
        public readonly string[] ItemStatus = new string[ItemSlots];
        /// <summary>人在该目标区域时的附加一行（可选物资进度），空串＝不显示。</summary>
        public string Note = string.Empty;

        /// <summary>一行清单的完整显示文字。</summary>
        public string ItemText(int index) =>
            string.IsNullOrEmpty(ItemStatus[index]) ? ItemLabels[index] : ItemLabels[index] + "：" + ItemStatus[index];
    }

    /// <summary>FG8-QST-01（FGU-36）：追踪栏要显示的内容（纯数据，<see cref="ObjectiveHudUIToolkit.ComposeTracker"/> 填写，自检直接断言）。</summary>
    public sealed class QuestTrackerView
    {
        public const int Slots = 3;

        public bool Visible;
        public bool Collapsed;
        public string Title = string.Empty;
        public string ToggleText = string.Empty;
        public int Count;
        public readonly string[] Ids = new string[Slots];
        public readonly string[] Texts = new string[Slots];
        public readonly bool[] Warn = new bool[Slots];
        public readonly bool[] CanFly = new bool[Slots];
    }

    /// <summary>ER8 收尾（DEBT-ER6LOOP01-01 / AC-CAM-001 / ERD-UI-002）：常驻“当前目标”条。
    /// 只读 <see cref="CampaignObjectiveTracker.CurrentObjectiveId"/> 与 <see cref="CampaignObjectiveCatalog"/>，
    /// 不做任何判定；归还谷地与两个远征区域都显示，主菜单不显示。</summary>
    public sealed class ObjectiveHudUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.25f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _panel;
        private Label _title;
        private Label _region;
        private Label _hint;
        private Label _note;
        private readonly VisualElement[] _items = new VisualElement[ObjectiveHudView.ItemSlots];
        private readonly Label[] _marks = new Label[ObjectiveHudView.ItemSlots];
        private readonly Label[] _texts = new Label[ObjectiveHudView.ItemSlots];
        private readonly ObjectiveHudView _view = new ObjectiveHudView();
        private float _refreshTimer;

        // FG8-QST-01：追踪栏
        private VisualElement _tracker;
        private Label _trackerTitle;
        private Button _trackerToggle;
        private readonly VisualElement[] _trackRows = new VisualElement[QuestTrackerView.Slots];
        private readonly Label[] _trackTexts = new Label[QuestTrackerView.Slots];
        private readonly Button[] _trackFly = new Button[QuestTrackerView.Slots];
        private readonly QuestTrackerView _trackerView = new QuestTrackerView();

        /// <summary>自检：追踪栏视图与按钮。</summary>
        public QuestTrackerView TrackerView => _trackerView;
        public Button TrackerToggleButton => _trackerToggle;
        public Button TrackFlyButton(int i) => i >= 0 && i < _trackFly.Length ? _trackFly[i] : null;
        public string TrackRowText(int i) => i >= 0 && i < _trackTexts.Length ? _trackTexts[i]?.text : null;
        public bool TrackRowShown(int i) => i >= 0 && i < _trackRows.Length && _trackRows[i] != null && !_trackRows[i].ClassListContains("obj-item-hidden");
        public bool TrackerShown => _tracker != null && !_tracker.ClassListContains("obj-hidden");

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ObjectiveHud");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：运行中枢(2)与装配(4)之间——常驻 HUD，不盖住任何玩法窗口。
            _document.sortingOrder = 3;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }
            VisualElement root = _document.rootVisualElement;
            if (root == null)
            {
                Log.Error("[ObjectiveHudUIToolkit] rootVisualElement 等待超时，目标条未初始化。");
                return;
            }
            root.pickingMode = PickingMode.Ignore;
            BindView(root);
        }

        /// <summary>绑定界面元素（运行时由 Start 调用；自检挂上 UXML 后直接调用）。</summary>
        public void BindView(VisualElement root)
        {
            _panel = root.Q<VisualElement>("ObjectiveHudRoot");
            _title = root.Q<Label>("ObjectiveTitle");
            _region = root.Q<Label>("ObjectiveRegion");
            _hint = root.Q<Label>("ObjectiveHint");
            _note = root.Q<Label>("ObjectiveNote");
            for (int i = 0; i < ObjectiveHudView.ItemSlots; i++)
            {
                _items[i] = root.Q<VisualElement>("ObjectiveItem" + i);
                _marks[i] = root.Q<Label>("ObjectiveItemMark" + i);
                _texts[i] = root.Q<Label>("ObjectiveItemText" + i);
            }
            _tracker = root.Q<VisualElement>("QuestTracker");
            _trackerTitle = root.Q<Label>("QuestTrackerTitle");
            _trackerToggle = root.Q<Button>("QuestTrackerToggle");
            if (_trackerToggle != null)
            {
                _trackerToggle.clicked += () =>
                {
                    CampaignState s = CampaignSession.Current;
                    CampaignObjectiveTracker.SetTrackerCollapsed(s, !(s?.Quests?.TrackerCollapsed ?? false));
                    RefreshTracker(s);
                };
            }
            for (int i = 0; i < QuestTrackerView.Slots; i++)
            {
                _trackRows[i] = root.Q<VisualElement>("QuestTrackRow" + i);
                _trackTexts[i] = root.Q<Label>("QuestTrackText" + i);
                _trackFly[i] = root.Q<Button>("QuestTrackFly" + i);
                int slot = i;
                if (_trackFly[i] != null)
                {
                    _trackFly[i].clicked += () => FlySlot(slot);
                    UiTooltip.Attach(_trackFly[i], () => new TooltipContent
                    {
                        Title = GameText.Get("quest.btn.fly"),
                        Body = InputDisplay.ExpandActionTokens(GameText.Get("quest.hud.fly_tip")),
                    });
                }
            }
        }

        private void FlySlot(int slot)
        {
            CampaignState s = CampaignSession.Current;
            string id = slot < _trackerView.Count ? _trackerView.Ids[slot] : null;
            if (s != null && id != null)
            {
                CampaignObjectiveTracker.TryFlyTo(s, id, out string msg);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck, msg);
            }
        }

        /// <summary>FG8-QST-01：按当前战役状态刷新追踪栏（运行时每 0.25 秒；自检直接调用）。</summary>
        public void RefreshTracker(CampaignState state)
        {
            ComposeTracker(state, _trackerView);
            if (_tracker == null)
            {
                return;
            }
            _tracker.EnableInClassList("obj-hidden", !_trackerView.Visible);
            if (!_trackerView.Visible)
            {
                return;
            }
            _trackerTitle.text = _trackerView.Title;
            _trackerToggle.text = _trackerView.ToggleText;
            for (int i = 0; i < QuestTrackerView.Slots; i++)
            {
                bool used = !_trackerView.Collapsed && i < _trackerView.Count;
                _trackRows[i].EnableInClassList("obj-item-hidden", !used);
                if (!used)
                {
                    continue;
                }
                _trackTexts[i].text = _trackerView.Texts[i];
                _trackRows[i].EnableInClassList("qt-warn", _trackerView.Warn[i]);
                _trackFly[i].text = GameText.Get("quest.hud.fly");
                _trackFly[i].SetEnabled(_trackerView.CanFly[i]);
            }
        }

        /// <summary>追踪栏内容：最多 3 个追踪中的任务（按追踪先后），每行“标题：进度 · 距离 · 剩余时限”；没有追踪任何任务时不显示；收起时只留标题行。</summary>
        public static void ComposeTracker(CampaignState state, QuestTrackerView view)
        {
            view.Visible = false;
            view.Count = 0;
            if (state == null)
            {
                return;
            }
            var list = new System.Collections.Generic.List<ObjectiveRecord>(4);
            CampaignObjectiveTracker.TrackedQuests(state, list);
            if (list.Count == 0)
            {
                return;
            }
            view.Visible = true;
            view.Collapsed = state.Quests?.TrackerCollapsed ?? false;
            int max = Mathf.Min(QuestTrackerView.Slots, CampaignObjectiveTracker.TrackMax);
            view.Title = GameText.Format("quest.hud.title", list.Count, max);
            view.ToggleText = view.Collapsed ? GameText.Format("quest.hud.expand", list.Count) : GameText.Get("quest.hud.collapse");
            Vector2 focus = DistanceOrigin(state);
            for (int i = 0; i < list.Count && i < QuestTrackerView.Slots; i++)
            {
                ObjectiveRecord r = list[i];
                var parts = new System.Collections.Generic.List<string>(3) { CampaignObjectiveTracker.ProgressText(state, r) };
                Vector2? at = CampaignObjectiveTracker.QuestLocation(state, r);
                if (at.HasValue)
                {
                    parts.Add(GameText.Format("quest.hud.dist", Mathf.RoundToInt((at.Value - focus).magnitude)));
                }
                double? left = CampaignObjectiveTracker.RemainingSeconds(r);
                bool warn = CampaignObjectiveTracker.IsWarningTime(r);
                if (left.HasValue)
                {
                    parts.Add(GameText.Format(warn ? "quest.hud.time_warn" : "quest.hud.time", QuestCatalog.FormatRemaining(left.Value)));
                }
                view.Ids[i] = r.ObjectiveId;
                view.Texts[i] = GameText.Format("quest.hud.row", CampaignObjectiveTracker.QuestTitle(r), string.Join(" · ", parts));
                view.Warn[i] = warn;
                view.CanFly[i] = at.HasValue;
                view.Count = i + 1;
            }
        }

        /// <summary>距离从哪里量：镜头在星球上时量镜头焦点，否则量归还核心。</summary>
        private static Vector2 DistanceOrigin(CampaignState state)
        {
            Campaign.Grid.GridCell c = Campaign.WorldSim.WorldView.ObservedSite != null
                ? Campaign.WorldSim.WorldView.PlanetFocusCell
                : Campaign.Grid.HomeGridService.CorePivot(state);
            return new Vector2(c.X, c.Y);
        }

        /// <summary>FG0-ARCH-01：在游戏世界里（任一地点已载入）就显示——镜头在哪个地点都一样。</summary>
        internal static bool AnyRegionActive() => GameRoot.AnyRegionActive;

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null || !AnyRegionActive())
            {
                _panel.AddToClassList("obj-hidden");
                _tracker?.AddToClassList("obj-hidden");
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;
            Compose(state, _view);
            Render();
            RefreshTracker(state);
        }

        private void Render()
        {
            _panel.EnableInClassList("obj-hidden", !_view.Visible);
            if (!_view.Visible)
            {
                return;
            }
            _title.text = _view.Title;
            _region.text = _view.Region;
            _region.EnableInClassList("obj-item-hidden", string.IsNullOrEmpty(_view.Region));
            for (int i = 0; i < ObjectiveHudView.ItemSlots; i++)
            {
                bool used = i < _view.ItemCount;
                _items[i].EnableInClassList("obj-item-hidden", !used);
                if (!used)
                {
                    continue;
                }
                _items[i].EnableInClassList("obj-item-done", _view.ItemDone[i]);
                _marks[i].text = _view.ItemDone[i] ? "√" : "○";
                _texts[i].text = _view.ItemText(i);
            }
            _note.text = _view.Note;
            _note.EnableInClassList("obj-item-hidden", string.IsNullOrEmpty(_view.Note));
            _hint.text = HintText();
        }

        /// <summary>按当前战役状态填写目标条内容。还没有进行中的目标（进场后第一次重算之前）时不显示；
        /// 全部完成后显示一行完成语。</summary>
        public static void Compose(CampaignState state, ObjectiveHudView view)
        {
            view.Visible = false;
            view.ItemCount = 0;
            view.Title = string.Empty;
            view.Region = string.Empty;
            view.Note = string.Empty;
            if (state == null)
            {
                return;
            }

            string currentId = CampaignObjectiveTracker.CurrentObjectiveId(state);
            if (currentId == null)
            {
                if (CampaignObjectiveTracker.IsCompleted(state, CampaignObjectiveTracker.Obj10))
                {
                    view.Visible = true;
                    view.Title = "全部目标完成：返航信标已启动";
                }
                return;
            }

            ObjectiveDef def = CampaignObjectiveCatalog.Get(currentId);
            if (def == null)
            {
                return;
            }
            view.Visible = true;
            view.ItemCount = Mathf.Min(def.Items.Length, ObjectiveHudView.ItemSlots);
            int done = 0;
            for (int i = 0; i < view.ItemCount; i++)
            {
                view.ItemLabels[i] = def.Items[i].Label;
                view.ItemDone[i] = def.Items[i].IsDone(state);
                view.ItemStatus[i] = view.ItemDone[i] ? null : def.Items[i].Status?.Invoke(state);
                if (view.ItemDone[i])
                {
                    done++;
                }
            }

            int index = Array.IndexOf(CampaignObjectiveCatalog.All, def);
            view.Title = $"目标 {index + 1}/{CampaignObjectiveCatalog.All.Length}：{def.Title}";
            string where = CampaignObjectiveCatalog.RegionDisplayName(def.RegionId);
            string here = GameLogic.Campaign.WorldSim.WorldView.ObservedSiteId ?? state.CurrentRegionId; // FG0-ARCH-01：镜头所在的地点。
            if (here == def.RegionId && def.RegionNote != null)
            {
                view.Note = def.RegionNote(state) ?? string.Empty;
            }
            view.Region = !string.IsNullOrEmpty(here) && here != def.RegionId
                ? $"地点：{where}（你现在在{CampaignObjectiveCatalog.RegionDisplayName(here)}）｜进度 {done}/{def.Items.Length}"
                : $"地点：{where}｜进度 {done}/{def.Items.Length}";
        }

        public static string HintText() =>
            $"按 {InputDisplay.ForAction(GameActionId.ToggleMissionLog)} 查看任务日志与战役地图";

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
