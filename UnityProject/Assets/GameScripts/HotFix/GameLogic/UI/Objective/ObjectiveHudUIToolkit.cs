using System;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Settings;
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
        }

        internal static bool AnyRegionActive() =>
            (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive) ||
            (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive) ||
            (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive);

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
            string here = state.CurrentRegionId;
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
