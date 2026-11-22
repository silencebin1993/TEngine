using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using TEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-UX-04（FG13 FGR-UX-040“可以在帮助里重看”、FGR-UX-041“设置里关闭全部引导，也可以随时从暂停菜单重新打开”）：引导与帮助。
    /// 暂停菜单“引导与帮助”打开（盖在暂停菜单上面，关掉回到暂停菜单）：
    /// - 总开关（与设置 › 游戏性“引导”同一个开关）；
    /// - 看过的引导列表，点一条重看（<see cref="GuidanceQueue.Replay"/>：卡片盖在本面板之上，不改“只出现一次”的记录）；
    /// - 重新显示全部引导（确认后清空已看记录，之后每个系统第一次遇到时重新提示）。
    /// Esc / 关闭按钮 / 点遮罩关闭；模态。刷新只在设置 / 语言变化时重建，O(看过的条数)。
    /// </summary>
    public sealed class GuidanceHelpPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30085; // 暂停菜单 30070、设置 30079 之上；引导卡片 30195 盖在它上面（重看时看得见）

        public static GuidanceHelpPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Label _hint;
        private Label _empty;
        private ScrollView _list;
        private Button _toggle;
        private Button _reset;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<string> _rowHooks = new List<string>();
        private int _key;

        protected override string UxmlLocation => "GuidanceHelpPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowHook(int i) => i >= 0 && i < VisibleRowCount ? _rowHooks[i] : null;
        public Button Row(int i) => i >= 0 && i < VisibleRowCount ? _rows[i] : null;
        public string CountText => _count?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public Button ToggleButton => _toggle;
        public Button ResetButton => _reset;
        public Button CloseButton => _close;

        private void Awake() => Instance = this;

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

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("GuideHelpRoot");
            _title = root.Q<Label>("GuideHelpTitle");
            _count = root.Q<Label>("GuideHelpCount");
            _close = root.Q<Button>("GuideHelpClose");
            _hint = root.Q<Label>("GuideHelpHint");
            _empty = root.Q<Label>("GuideHelpEmpty");
            _list = root.Q<ScrollView>("GuideHelpList");
            _toggle = root.Q<Button>("GuideHelpToggle");
            _reset = root.Q<Button>("GuideHelpReset");
            _close.clicked += () => SetOpen(false);
            _toggle.clicked += () => GameSettings.SetGuidanceEnabled(!GameSettings.GuidanceEnabled);
            _reset.clicked += AskReset;
            UiTooltip.Attach(_toggle, () => new TooltipContent { Title = GameText.Get("guide.help.title"), Body = GameText.Get("guide.help.toggle_tip") });
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        /// <summary>“重新显示全部引导”：确认后清空已看记录。</summary>
        public void AskReset()
        {
            int seen = GameSettings.SeenGuidanceHooks.Count;
            var request = new ConfirmRequest
            {
                Title = GameText.Get("guide.help.reset_title"),
                OnConfirm = () =>
                {
                    GameSettings.ResetSeenGuidanceHooks();
                    Log.Info(GameText.Get("guide.help.reset_done"));
                },
            };
            request.Lines.Add(GameText.Format("guide.help.reset_line", seen));
            UiConfirmDialog.Show(request);
        }

        private void Update()
        {
            if (IsOpen)
            {
                Refresh();
            }
        }

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            int key = System.HashCode.Combine(GameSettings.Revision, (int)GameText.Language, GameText.Revision);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("guide.help.title");
            _close.text = GameText.Get("codex.panel.close");
            _hint.text = GameText.Get("guide.help.hint");
            _toggle.text = GameText.Get(GameSettings.GuidanceEnabled ? "guide.help.toggle_on" : "guide.help.toggle_off");
            _reset.text = GameText.Get("guide.help.reset");
            GuidanceQueue.History(_rowHooks);
            _rowHooks.Reverse(); // 最近看到的在前
            _count.text = GameText.Format("guide.help.count", _rowHooks.Count, GuidanceQueue.ContentHookCount());
            while (_rows.Count < _rowHooks.Count)
            {
                var b = new Button();
                b.AddToClassList("mw-btn");
                b.AddToClassList("gd-help-row");
                int index = _rows.Count;
                b.clicked += () => ReplayRow(index);
                _list.Add(b);
                _rows.Add(b);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < _rowHooks.Count;
                _rows[i].EnableInClassList("uk-hidden", !shown);
                if (shown)
                {
                    _rows[i].text = GameText.Get(GuidanceQueue.TitleKey(_rowHooks[i]));
                }
            }
            VisibleRowCount = _rowHooks.Count;
            _empty.EnableInClassList("uk-hidden", _rowHooks.Count > 0);
            _empty.text = _rowHooks.Count == 0 ? GameText.Get("guide.help.empty") : string.Empty;
        }

        public void ReplayRow(int index)
        {
            if (index >= 0 && index < VisibleRowCount)
            {
                GuidanceQueue.Replay(_rowHooks[index]);
            }
        }
    }
}
