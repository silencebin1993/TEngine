using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-SYS-01（FGR-SYS-001 / 003 / 006 / 007，FG-GAP-013，DEBT-FG3GEN01-02）：主菜单存档卡“管理”与云存档冲突窗口（30086，新游戏设置之上）。
    /// - 管理：缩略图 + 完整卡片文字；复制种子 / 分享短码、打开存档目录；3 份轮换自动存档与快速存档各一行，可读取（读副本时战役槽里更新的主档先另存）；
    ///   删除存档、清理保留文件先二次确认（永不自动删除：删除只由玩家在这里做；删除不碰保留文件）。
    /// - 冲突：<see cref="CloudSaveService.Conflicts"/> 逐个显示本机 / 云端的存档时间与游戏进度，玩家选“保留本机” / “保留云端”，落选的一份另存为保留文件。
    /// 模态：打开时输入上下文为“界面”；Esc = 取消确认 / 关闭。
    /// </summary>
    public sealed class SavePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30086;

        public static SavePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static bool InConflictMode { get; private set; }

        protected override string UxmlLocation => "SavePanel";
        protected override int SortingOrder => Order;

        private VisualElement _root;
        private Label _title;
        private VisualElement _manage;
        private VisualElement _thumb;
        private Label _info;
        private Button _copySeed;
        private Button _copyCode;
        private Button _openDir;
        private Label _copiesTitle;
        private readonly Label[] _copyText = new Label[CampaignSaveService.AutoRotationCount + 1];
        private readonly Button[] _copyLoad = new Button[CampaignSaveService.AutoRotationCount + 1];
        private Button _delete;
        private Button _cleanKeep;
        private VisualElement _conflict;
        private Label _conflictText;
        private Button _keepLocal;
        private Button _keepCloud;
        private Label _conflictMore;
        private VisualElement _confirm;
        private Label _confirmText;
        private Button _confirmYes;
        private Button _confirmNo;
        private Label _feedback;
        private Button _close;
        private Texture2D _thumbTex;

        private int _home = -1;
        private Action<int> _onLoad;
        private Action _onChanged;
        private Action _pendingConfirm;

        private static (int Home, Action<int> Load, Action Changed)? _pendingManage;
        private static Action _pendingConflictDone;
        private static bool _pendingConflict;
        private Action _conflictDone;

        /// <summary>打开某个战役槽的存档管理。<paramref name="onLoad"/> = 读取某份副本（槽位号）；<paramref name="onChanged"/> = 删除 / 清理后让主菜单刷新列表。</summary>
        public static void OpenManage(int home, Action<int> onLoad, Action onChanged)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingManage = (home, onLoad, onChanged);
                return;
            }
            Instance.ShowManage(home, onLoad, onChanged);
        }

        /// <summary>有云存档冲突时打开（主菜单对账后调）。全部选完后调 <paramref name="onDone"/>。</summary>
        public static void OpenConflicts(Action onDone)
        {
            if (CloudSaveService.Conflicts.Count == 0)
            {
                onDone?.Invoke();
                return;
            }
            if (Instance == null || Instance._root == null)
            {
                _pendingConflict = true;
                _pendingConflictDone = onDone;
                return;
            }
            Instance.ShowConflict(onDone);
        }

        public static void Close()
        {
            _pendingManage = null;
            _pendingConflict = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingManage.HasValue)
            {
                var p = _pendingManage.Value;
                _pendingManage = null;
                ShowManage(p.Home, p.Load, p.Changed);
            }
            else if (_pendingConflict)
            {
                _pendingConflict = false;
                ShowConflict(_pendingConflictDone);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("SaveRoot");
            _title = root.Q<Label>("SaveTitle");
            _manage = root.Q<VisualElement>("SaveManage");
            _thumb = root.Q<VisualElement>("SaveThumb");
            _info = root.Q<Label>("SaveInfo");
            _copySeed = root.Q<Button>("SaveCopySeed");
            _copyCode = root.Q<Button>("SaveCopyCode");
            _openDir = root.Q<Button>("SaveOpenDir");
            _copiesTitle = root.Q<Label>("SaveCopiesTitle");
            for (int k = 0; k < _copyText.Length; k++)
            {
                int kk = k;
                _copyText[k] = root.Q<Label>("SaveCopyText" + k);
                _copyLoad[k] = root.Q<Button>("SaveCopyLoad" + k);
                _copyLoad[k].clicked += () => LoadCopy(kk);
            }
            _delete = root.Q<Button>("SaveDelete");
            _cleanKeep = root.Q<Button>("SaveCleanKeep");
            _conflict = root.Q<VisualElement>("SaveConflict");
            _conflictText = root.Q<Label>("SaveConflictText");
            _keepLocal = root.Q<Button>("SaveKeepLocal");
            _keepCloud = root.Q<Button>("SaveKeepCloud");
            _conflictMore = root.Q<Label>("SaveConflictMore");
            _confirm = root.Q<VisualElement>("SaveConfirm");
            _confirmText = root.Q<Label>("SaveConfirmText");
            _confirmYes = root.Q<Button>("SaveConfirmYes");
            _confirmNo = root.Q<Button>("SaveConfirmNo");
            _feedback = root.Q<Label>("SaveFeedback");
            _close = root.Q<Button>("SaveClose");

            _copySeed.clicked += () => Copy(CampaignSlotText.SeedText(CampaignSaveService.GetSlotMetadata(_home)));
            _copyCode.clicked += () =>
            {
                string code = CampaignSlotText.ShareCode(CampaignSaveService.GetSlotMetadata(_home));
                if (code == null)
                {
                    _feedback.text = GameText.Get("save.manage.no_code");
                    return;
                }
                Copy(code);
            };
            _openDir.clicked += () => CampaignSaveService.OpenSaveDirectory();
            _delete.clicked += AskDelete;
            _cleanKeep.clicked += AskCleanKeep;
            _keepLocal.clicked += () => PickConflict(true);
            _keepCloud.clicked += () => PickConflict(false);
            _confirmYes.clicked += ConfirmYes;
            _confirmNo.clicked += CancelConfirm;
            _close.clicked += Back;
        }

        // ── 管理 ──────────────────────────────────────────────────────────

        private void ShowManage(int home, Action<int> onLoad, Action onChanged)
        {
            _home = CampaignSaveService.HomeOf(home);
            _onLoad = onLoad;
            _onChanged = onChanged;
            InConflictMode = false;
            SetOpen(true);
            _manage.EnableInClassList("uk-hidden", false);
            _conflict.EnableInClassList("uk-hidden", true);
            _feedback.text = string.Empty;
            CancelConfirm();
            RefreshManage();
        }

        private void RefreshManage()
        {
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(_home);
            _title.text = GameText.Format("save.manage.title", CampaignSlotText.SlotTitle(_home));
            _info.text = CampaignSlotText.CardText(meta);
            SetThumb(meta.ThumbnailJpg);
            bool ready = meta.State == CampaignSlotState.Ready;
            _copySeed.text = GameText.Get("save.manage.copy_seed");
            _copyCode.text = GameText.Get("save.manage.copy_code");
            _openDir.text = GameText.Get("save.manage.open_dir");
            _copySeed.SetEnabled(ready);
            _copyCode.SetEnabled(ready && CampaignSlotText.ShareCode(meta) != null);
            _copiesTitle.text = GameText.Get("save.manage.copies_title");
            CampaignSlotMetadata[] copies = CampaignSaveService.CopyMetadata(_home);
            for (int k = 0; k < copies.Length; k++)
            {
                CampaignSlotMetadata c = copies[k];
                string name = CampaignSlotText.SlotTitle(c.SlotIndex);
                _copyText[k].text = c.State == CampaignSlotState.Empty
                    ? GameText.Format("save.manage.copy_empty", name)
                    : c.State == CampaignSlotState.Ready
                        ? GameText.Format("save.manage.copy_line", name, CampaignSlotText.SavedAt(c.WrittenAtUtc), CampaignSlotText.FgFields(c))
                        : GameText.Format("save.manage.copy_line", name, CampaignSlotText.SavedAt(c.WrittenAtUtc), CampaignSlotText.ReasonText(c.FailureReason, c.SchemaVersion));
                _copyLoad[k].text = GameText.Get("save.manage.load");
                _copyLoad[k].SetEnabled(c.State == CampaignSlotState.Ready);
            }
            int keeps = CampaignSaveService.PreservedFiles(_home).Length;
            bool any = meta.State != CampaignSlotState.Empty || Array.Exists(copies, c => c.State != CampaignSlotState.Empty) || meta.HasBackup;
            _delete.text = GameText.Get("save.manage.delete");
            _delete.SetEnabled(any);
            _cleanKeep.text = GameText.Format("save.manage.clean_keep", keeps);
            _cleanKeep.SetEnabled(keeps > 0);
            _close.text = GameText.Get("save.manage.close");
        }

        private void SetThumb(byte[] jpg)
        {
            if (_thumbTex != null)
            {
                Release(_thumbTex);
                _thumbTex = null;
            }
            if (jpg != null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (tex.LoadImage(jpg))
                {
                    _thumbTex = tex;
                }
                else
                {
                    Release(tex);
                }
            }
            _thumb.style.backgroundImage = _thumbTex != null ? new StyleBackground(_thumbTex) : new StyleBackground(StyleKeyword.None);
        }

        private static void Release(UnityEngine.Object o)
        {
            if (Application.isPlaying)
            {
                Destroy(o);
            }
            else
            {
                DestroyImmediate(o);
            }
        }

        /// <summary>当前缩略图（自检读）。</summary>
        public Texture2D ThumbnailTexture => _thumbTex;

        private void LoadCopy(int k)
        {
            int slot = CampaignSaveService.CopySlot(_home, k);
            Action<int> load = _onLoad;
            SetOpen(false);
            load?.Invoke(slot);
        }

        private void Copy(string text)
        {
            if (CampaignSlotText.Copy(text))
            {
                _feedback.text = GameText.Format("save.manage.copied", text);
            }
        }

        private void AskDelete()
        {
            Ask(GameText.Format("save.manage.delete_confirm", CampaignSlotText.SlotTitle(_home)), () =>
            {
                int n = CampaignSaveService.DeleteSlot(_home);
                _feedback.text = GameText.Format("save.manage.deleted", n);
            });
        }

        private void AskCleanKeep()
        {
            int keeps = CampaignSaveService.PreservedFiles(_home).Length;
            Ask(GameText.Format("save.manage.clean_confirm", CampaignSlotText.SlotTitle(_home), keeps), () =>
            {
                int n = CampaignSaveService.DeletePreservedFiles(_home);
                _feedback.text = GameText.Format("save.manage.cleaned", n);
            });
        }

        private void Ask(string text, Action onYes)
        {
            _pendingConfirm = onYes;
            _confirmText.text = text;
            _confirmYes.text = GameText.Get("save.manage.confirm_yes");
            _confirmNo.text = GameText.Get("save.manage.confirm_no");
            _confirm.EnableInClassList("uk-hidden", false);
        }

        private void ConfirmYes()
        {
            Action act = _pendingConfirm;
            CancelConfirm();
            act?.Invoke();
            RefreshManage();
            _onChanged?.Invoke();
        }

        private void CancelConfirm()
        {
            _pendingConfirm = null;
            _confirm?.EnableInClassList("uk-hidden", true);
        }

        /// <summary>确认框是否开着（自检读）。</summary>
        public bool ConfirmPending => _pendingConfirm != null;

        // ── 冲突 ──────────────────────────────────────────────────────────

        private void ShowConflict(Action onDone)
        {
            _conflictDone = onDone;
            InConflictMode = true;
            SetOpen(true);
            _manage.EnableInClassList("uk-hidden", true);
            _conflict.EnableInClassList("uk-hidden", false);
            CancelConfirm();
            _feedback.text = string.Empty;
            RefreshConflict();
        }

        private void RefreshConflict()
        {
            List<CloudSaveConflict> list = CloudSaveService.Conflicts;
            if (list.Count == 0)
            {
                Action done = _conflictDone;
                _conflictDone = null;
                SetOpen(false);
                done?.Invoke();
                return;
            }
            CloudSaveConflict c = list[0];
            _title.text = GameText.Get("save.cloud.title");
            _conflictText.text = CloudConflictText.Body(c);
            _keepLocal.text = GameText.Get("save.cloud.keep_local");
            _keepCloud.text = GameText.Get("save.cloud.keep_cloud");
            _conflictMore.text = list.Count > 1 ? GameText.Format("save.cloud.more", list.Count - 1) : string.Empty;
            _close.text = GameText.Get("save.manage.close");
        }

        private void PickConflict(bool keepLocal)
        {
            if (CloudSaveService.Conflicts.Count == 0)
            {
                RefreshConflict();
                return;
            }
            CloudSaveConflict c = CloudSaveService.Conflicts[0];
            if (!CloudSaveService.Resolve(c, keepLocal))
            {
                CloudSaveService.Conflicts.Remove(c); // 处理失败（离线等）：两份都原样留着，下次对账再问
            }
            RefreshConflict();
        }

        // ── 开关 ──────────────────────────────────────────────────────────

        private void Back()
        {
            if (_pendingConfirm != null)
            {
                CancelConfirm();
                return;
            }
            if (InConflictMode)
            {
                // 冲突没选就关：什么都不覆盖，两份原样留着，下次打开主菜单再问。
                Action done = _conflictDone;
                _conflictDone = null;
                SetOpen(false);
                done?.Invoke();
                return;
            }
            SetOpen(false);
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
                UiEscapeStack.Push(this, Back);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                CancelConfirm();
            }
        }

        protected override void OnDestroy()
        {
            if (IsOpen)
            {
                SetOpen(false);
            }
            if (_thumbTex != null)
            {
                Release(_thumbTex);
                _thumbTex = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
