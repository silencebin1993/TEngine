using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-005 布局库；FG13 FGU-11“保存、缩略图、放置、删除”；FGT-LOG-013；FG00 B01 / B04 / B05 / B06 / B12）：布局库面板。
    /// - 顶部：名字输入框 + “保存剪贴板”（把最近一次复制的内容存成布局，名字空着叫“布局 N”）+ “导入”（从系统剪贴板里的导出文本）。
    /// - 每行一个布局：缩略图、名字、件数与尺寸、本局还没解锁 / 本版本不认识的件数（放置时这些件标红叉不放）；行内“放置”（关掉面板进入粘贴）、
    ///   “改名”（用输入框里的名字）、“导出”（一行文本复制到系统剪贴板）、“删除”（不可逆，先确认，B04）。
    /// - 空库有说明（怎么往里存）；读文件出问题时写明原因（坏文件已改名备份）。
    /// 数据全部来自 <see cref="LayoutLibrary"/>（跨存档，玩家配置目录）。入口：快捷键（默认 Ctrl+B）、建造栏“布局库”按钮。
    /// 模态（盖在世界上，Esc / 关闭 / 点遮罩关闭）；只在布局库变化或语言变化时重建行（O(布局数)），关着时每帧 O(1)。缩略图贴图随行成对创建 / 释放。
    /// </summary>
    public sealed class LayoutLibraryPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>通知 30040 之上、施工队列 30045 之下（同时打开时施工队列在上），字幕 30050 之下。</summary>
        public const int Order = 30044;

        public static LayoutLibraryPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Button _save;
        private Button _import;
        private Label _message;
        private Label _empty;
        private ScrollView _list;
        private Label _footer;
        private UiSearchBox _nameBox;
        private string _nameText = string.Empty;
        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<Texture2D> _thumbs = new List<Texture2D>();
        private string _lastKey;
        private bool _messageError;
        private string _messageText = string.Empty;

        protected override string UxmlLocation => "LayoutLibraryPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowName(int i) => Row(i)?.Q<Label>("LlName")?.text ?? string.Empty;
        public string RowInfo(int i) => Row(i)?.Q<Label>("LlInfo")?.text ?? string.Empty;
        public Button RowButton(int i, string name) => Row(i)?.Q<Button>(name);
        public Texture2D RowThumbnail(int i) => i >= 0 && i < _thumbs.Count ? _thumbs[i] : null;
        public string MessageText => _message?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public Button SaveButton => _save;
        public Button ImportButton => _import;
        public Button CloseButton => _close;
        public bool PendingDeleteConfirm { get; private set; }

        private TemplateContainer Row(int i) => i >= 0 && i < VisibleRowCount && i < _rows.Count ? _rows[i] : null;

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
            ReleaseThumbs();
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
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
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_rowTemplate != null || _rowTemplateLoading)
            {
                return;
            }
            _rowTemplateLoading = true;
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("LayoutLibraryRow");
            _rowTemplateLoading = false;
            if (this == null)
            {
                if (asset != null)
                {
                    GameModule.Resource.UnloadAsset(asset);
                }
                return;
            }
            _rowTemplate = asset;
            if (_rowTemplate == null)
            {
                Log.Error("[LayoutLibraryPanelUIToolkit] 加载 LayoutLibraryRow 失败，布局行不可用。");
                return;
            }
            _lastKey = null;
            Refresh();
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset template)
        {
            _rowTemplate = template;
            _lastKey = null;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("LayoutLibraryRoot");
            _title = root.Q<Label>("LayoutLibraryTitle");
            _count = root.Q<Label>("LayoutLibraryCount");
            _close = root.Q<Button>("LayoutLibraryClose");
            _save = root.Q<Button>("LayoutLibrarySave");
            _import = root.Q<Button>("LayoutLibraryImport");
            _message = root.Q<Label>("LayoutLibraryMessage");
            _empty = root.Q<Label>("LayoutLibraryEmpty");
            _list = root.Q<ScrollView>("LayoutLibraryList");
            _footer = root.Q<Label>("LayoutLibraryFooter");
            _nameBox = new UiSearchBox(root.Q<TextField>("LayoutLibraryName"), root.Q<Label>("LayoutLibraryNamePlaceholder"), root.Q<Button>("LayoutLibraryNameClear"),
                "plan.library.name_placeholder", text => _nameText = text ?? string.Empty);
            _close.clicked += () => SetOpen(false);
            _save.clicked += SaveClipboard;
            _import.clicked += () => ImportText(GUIUtility.systemCopyBuffer);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            UiTooltip.Attach(_save, () => new TooltipContent { Title = GameText.Get("plan.library.save"), Body = GameText.Get("plan.library.save_tip") });
            UiTooltip.Attach(_import, () => new TooltipContent { Title = GameText.Get("plan.library.import"), Body = GameText.Get("plan.library.import_tip") });
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
                GuidanceHooks.Raise(GuidanceHooks.LayoutLibraryFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _messageText = LayoutLibrary.LastLoadProblem ?? string.Empty;
                _messageError = !string.IsNullOrEmpty(_messageText);
                _lastKey = null;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                PendingDeleteConfirm = false;
            }
        }

        /// <summary>自检：输入框的名字（与在框里打字同一回调）。</summary>
        public void SetNameText(string text)
        {
            _nameBox?.SetText(text ?? string.Empty);
            _nameText = text ?? string.Empty;
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
            Refresh();
        }

        /// <summary>按布局库重建行（只在库 / 语言 / 剪贴板 / 消息变化时重建，O(布局数)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            string key = string.Concat(LayoutLibrary.Revision.ToString(), "|", ((int)GameText.Language).ToString(), "|",
                PlanEntries.CountOf(HomeValleyBuildMode.Clipboard).ToString(), "|", _messageText, "|", _rowTemplate != null ? "1" : "0", "|",
                GameLogic.Settings.GameSettings.Revision.ToString());
            if (key == _lastKey)
            {
                return;
            }
            _lastKey = key;
            CampaignState state = CampaignSession.Current;
            IReadOnlyList<LayoutRecord> all = LayoutLibrary.All;
            _title.text = GameText.Get("plan.library.title");
            _close.text = GameText.Get("build.queue.close");
            _count.text = GameText.Format("plan.library.count", all.Count, LayoutLibrary.Max);
            int clip = PlanEntries.CountOf(HomeValleyBuildMode.Clipboard);
            _save.text = GameText.Format("plan.library.save_btn", clip);
            _save.SetEnabled(clip > 0 && all.Count < LayoutLibrary.Max);
            _import.text = GameText.Get("plan.library.import");
            _message.text = _messageText;
            _message.EnableInClassList("ll-message-error", _messageError);
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("plan.library.footer"));
            bool empty = all.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Format("plan.library.empty", InputDisplay.ForAction(GameActionId.Copy)) : string.Empty;
            ReleaseThumbs();
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < all.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                row.Q<Button>("LlPlace").clicked += () => Place(index);
                row.Q<Button>("LlRename").clicked += () => Rename(index);
                row.Q<Button>("LlExport").clicked += () => Export(index);
                row.Q<Button>("LlDelete").clicked += () => AskDelete(index);
                _list.Add(row);
                _rows.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shown = i < all.Count;
                row.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                LayoutRecord l = all[i];
                int n = PlanEntries.CountOf(l.Entries);
                int locked = PlanEntries.LockedCount(state, l.Entries, out int unknown);
                row.Q<Label>("LlName").text = l.Name;
                Label info = row.Q<Label>("LlInfo");
                info.text = GameText.Format("plan.library.row_info", n, l.Width, l.Height)
                            + (locked + unknown > 0 ? "\n" + GameText.Format("plan.paste.locked_note", locked, unknown) : string.Empty);
                info.EnableInClassList("ll-row-warn", locked + unknown > 0);
                Texture2D thumb = LayoutLibrary.BuildThumbnail(l.Entries);
                _thumbs.Add(thumb);
                row.Q<VisualElement>("LlThumb").style.backgroundImage = new StyleBackground(thumb); // 数据驱动的运行时贴图（红线 2 允许）
                row.Q<Button>("LlPlace").text = GameText.Get("plan.library.place");
                row.Q<Button>("LlRename").text = GameText.Get("plan.library.rename");
                row.Q<Button>("LlExport").text = GameText.Get("plan.library.export");
                row.Q<Button>("LlDelete").text = GameText.Get("plan.library.delete");
            }
            VisibleRowCount = all.Count;
        }

        private void ReleaseThumbs()
        {
            foreach (Texture2D t in _thumbs)
            {
                LayoutLibrary.ReleaseThumbnail(t);
            }
            _thumbs.Clear();
            foreach (TemplateContainer row in _rows)
            {
                VisualElement thumb = row.Q<VisualElement>("LlThumb");
                if (thumb != null)
                {
                    thumb.style.backgroundImage = StyleKeyword.Null;
                }
            }
        }

        private void SetMessage(string text, bool error)
        {
            _messageText = text ?? string.Empty;
            _messageError = error;
            _lastKey = null;
            Refresh();
            Campaign.Feedback.FeedbackCues.Raise(error ? Campaign.Feedback.FeedbackCueId.Denied : Campaign.Feedback.FeedbackCueId.CommandAck, _messageText);
        }

        /// <summary>“保存剪贴板”：把最近一次复制的内容存成布局（名字用输入框，空着叫“布局 N”）。</summary>
        public void SaveClipboard()
        {
            if (HomeValleyBuildMode.SaveClipboardToLibrary(_nameText, out LayoutRecord saved, out string reason))
            {
                SetNameText(string.Empty);
                SetMessage(GameText.Format("plan.library.saved", saved.Name, PlanEntries.CountOf(saved.Entries)), false);
            }
            else
            {
                SetMessage(reason, true);
            }
        }

        /// <summary>“导入”：从文本（系统剪贴板）导入一个布局。</summary>
        public void ImportText(string text)
        {
            if (LayoutLibrary.TryImport(text, out LayoutRecord saved, out string reason))
            {
                SetMessage(GameText.Format("plan.library.imported", saved.Name, PlanEntries.CountOf(saved.Entries)), false);
            }
            else
            {
                SetMessage(reason, true);
            }
        }

        /// <summary>“放置”：关掉面板，建造模式进入粘贴（布局跟着鼠标，旋转键转向；本局没解锁的件标红叉不放）。</summary>
        public void Place(int index)
        {
            IReadOnlyList<LayoutRecord> all = LayoutLibrary.All;
            if (index < 0 || index >= all.Count)
            {
                return;
            }
            LayoutRecord l = all[index];
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (mode == null)
            {
                SetMessage(GameText.Get("plan.library.no_home"), true);
                return;
            }
            SetOpen(false);
            mode.StartPaste(CampaignSession.Current, l.Entries, l.Name);
        }

        public void Rename(int index)
        {
            if (string.IsNullOrWhiteSpace(_nameText))
            {
                SetMessage(GameText.Get("plan.library.rename_need_name"), true);
                return;
            }
            if (LayoutLibrary.TryRename(index, _nameText, out string reason))
            {
                SetNameText(string.Empty);
                SetMessage(GameText.Format("plan.library.renamed", LayoutLibrary.All[index].Name), false);
            }
            else
            {
                SetMessage(reason, true);
            }
        }

        /// <summary>“导出”：一行文本复制到系统剪贴板（可以贴给别人，再用“导入”读回来）。</summary>
        public string Export(int index)
        {
            IReadOnlyList<LayoutRecord> all = LayoutLibrary.All;
            if (index < 0 || index >= all.Count)
            {
                return null;
            }
            string text = LayoutLibrary.Export(all[index]);
            GUIUtility.systemCopyBuffer = text;
            SetMessage(GameText.Format("plan.library.exported", all[index].Name, text.Length), false);
            return text;
        }

        /// <summary>“删除”：不可逆，先确认（B04）。</summary>
        public void AskDelete(int index)
        {
            IReadOnlyList<LayoutRecord> all = LayoutLibrary.All;
            if (index < 0 || index >= all.Count)
            {
                return;
            }
            LayoutRecord l = all[index];
            var req = new ConfirmRequest
            {
                Title = GameText.Format("plan.library.delete_title", l.Name),
                Irreversible = true,
                ConfirmText = GameText.Get("plan.library.delete"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    PendingDeleteConfirm = false;
                    int at = IndexOf(l);
                    if (at >= 0 && LayoutLibrary.TryDelete(at, out string reason))
                    {
                        SetMessage(GameText.Format("plan.library.deleted", l.Name), false);
                    }
                    else
                    {
                        SetMessage(GameText.Get("plan.library.not_found"), true);
                    }
                },
                OnCancel = () => PendingDeleteConfirm = false,
            };
            req.Consequences.Add(GameText.Get("plan.library.delete_line"));
            UiConfirmDialog.Show(req);
            PendingDeleteConfirm = true;
        }

        private static int IndexOf(LayoutRecord l)
        {
            IReadOnlyList<LayoutRecord> all = LayoutLibrary.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (ReferenceEquals(all[i], l))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
