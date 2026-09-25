using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>一次确认请求。所有文字由调用方按文本键取好（已本地化）。</summary>
    public sealed class ConfirmRequest
    {
        public string Title;
        /// <summary>后果清单（FGR-UX-002：“将丢失：未装载的战利品 ×7”）。显示在“将丢失：”标题下，逐行。</summary>
        public readonly List<string> Consequences = new List<string>();
        /// <summary>补充说明（不属于“将丢失”的正文行，如冲突说明）。</summary>
        public readonly List<string> Lines = new List<string>();
        /// <summary>true：显示“此操作无法撤销。”并用危险样式。</summary>
        public bool Irreversible;
        public string ConfirmText;
        public string CancelText;
        public Action OnConfirm;
        public Action OnCancel;
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 确认框、FGR-UX-002、FG00 B04）：全局唯一的确认框。
    /// - 只给不可逆、代价大的操作用（可逆操作不弹，靠撤销兜底）；写清后果。
    /// - 模态：打开时 <see cref="InputRouter.PushModal"/>（输入上下文切到“界面”），并压一层 Esc 栈；
    ///   Esc / 点遮罩 / 取消按钮 = 取消，回车（<see cref="GameActionId.UiConfirm"/>）/ 确认按钮 = 确认。
    /// - 已有一个在显示时，新的请求排队，前一个结束后再显示（不会互相覆盖、丢失回调）。
    /// 视图元素来自 UiKitOverlay.uxml，由 <c>UiKitOverlayUIToolkit</c> 绑定；自检可以把同一份 UXML 实例化后直接绑定测试。
    /// </summary>
    public static class UiConfirmDialog
    {
        private static readonly Queue<ConfirmRequest> Pending = new Queue<ConfirmRequest>();
        private static readonly object ModalOwner = new object();

        private static VisualElement _scrim;
        private static VisualElement _dialog;
        private static Label _title;
        private static Label _losePrefix;
        private static VisualElement _consequences;
        private static VisualElement _lines;
        private static Label _irreversible;
        private static Button _ok;
        private static Button _cancel;

        public static ConfirmRequest Current { get; private set; }
        public static bool IsOpen => Current != null;
        public static int PendingCount => Pending.Count;

        public static void BindView(VisualElement root)
        {
            _scrim = root.Q<VisualElement>("ConfirmScrim");
            _dialog = root.Q<VisualElement>("ConfirmDialog");
            _title = root.Q<Label>("ConfirmTitle");
            _losePrefix = root.Q<Label>("ConfirmLosePrefix");
            _consequences = root.Q<VisualElement>("ConfirmConsequences");
            _lines = root.Q<VisualElement>("ConfirmLines");
            _irreversible = root.Q<Label>("ConfirmIrreversible");
            _ok = root.Q<Button>("ConfirmOk");
            _cancel = root.Q<Button>("ConfirmCancel");
            if (_ok != null)
            {
                _ok.clicked -= Confirm;
                _ok.clicked += Confirm;
            }
            if (_cancel != null)
            {
                _cancel.clicked -= Cancel;
                _cancel.clicked += Cancel;
            }
            _scrim?.RegisterCallback<PointerDownEvent>(OnScrimPointerDown);
            Render();
        }

        public static void UnbindView()
        {
            _scrim?.UnregisterCallback<PointerDownEvent>(OnScrimPointerDown);
            if (_ok != null) _ok.clicked -= Confirm;
            if (_cancel != null) _cancel.clicked -= Cancel;
            _scrim = _dialog = _consequences = _lines = null;
            _title = _losePrefix = _irreversible = null;
            _ok = _cancel = null;
        }

        public static void Show(ConfirmRequest request)
        {
            if (request == null)
            {
                return;
            }
            if (Current != null)
            {
                Pending.Enqueue(request);
                return;
            }
            Open(request);
        }

        private static void Open(ConfirmRequest request)
        {
            Current = request;
            InputRouter.PushModal(ModalOwner);
            UiEscapeStack.Push(ModalOwner, Cancel);
            Render();
        }

        public static void Confirm() => Finish(true);

        public static void Cancel() => Finish(false);

        private static void Finish(bool confirmed)
        {
            ConfirmRequest done = Current;
            if (done == null)
            {
                return;
            }
            Current = null;
            UiEscapeStack.Remove(ModalOwner);
            InputRouter.PopModal(ModalOwner);
            Render();
            try
            {
                if (confirmed)
                {
                    done.OnConfirm?.Invoke();
                }
                else
                {
                    done.OnCancel?.Invoke();
                }
            }
            finally
            {
                if (Current == null && Pending.Count > 0)
                {
                    Open(Pending.Dequeue());
                }
            }
        }

        /// <summary>每帧由浮层宿主调用：回车确认（取消由 Esc 栈统一处理）。</summary>
        public static void Tick()
        {
            if (Current != null && InputRouter.ConsumeContextAction(GameActionId.UiConfirm))
            {
                Confirm();
            }
        }

        private static void OnScrimPointerDown(PointerDownEvent evt)
        {
            // 只有点在遮罩空白处才算取消；点在对话框里的事件目标是对话框的子元素。
            if (evt.target == _scrim)
            {
                Cancel();
                evt.StopPropagation();
            }
        }

        private static void Render()
        {
            if (_scrim == null)
            {
                return;
            }
            ConfirmRequest r = Current;
            _scrim.EnableInClassList("uk-hidden", r == null);
            if (r == null)
            {
                return;
            }
            _dialog?.EnableInClassList("uk-confirm-danger", r.Irreversible);
            if (_title != null) _title.text = r.Title ?? string.Empty;
            if (_losePrefix != null)
            {
                _losePrefix.text = GameText.Get("ui.confirm.lose_prefix");
                _losePrefix.EnableInClassList("uk-hidden", r.Consequences.Count == 0);
            }
            FillLines(_consequences, r.Consequences);
            FillLines(_lines, r.Lines);
            if (_irreversible != null)
            {
                _irreversible.text = GameText.Get("ui.confirm.irreversible");
                _irreversible.EnableInClassList("uk-hidden", !r.Irreversible);
            }
            if (_ok != null) _ok.text = string.IsNullOrEmpty(r.ConfirmText) ? GameText.Get("ui.common.confirm") : r.ConfirmText;
            if (_cancel != null) _cancel.text = string.IsNullOrEmpty(r.CancelText) ? GameText.Get("ui.common.cancel") : r.CancelText;
        }

        /// <summary>可变条数的正文行（列表内容，不是页面结构）：每行一个带类的 Label。</summary>
        private static void FillLines(VisualElement container, List<string> lines)
        {
            if (container == null)
            {
                return;
            }
            container.Clear();
            foreach (string line in lines)
            {
                var label = new Label(line);
                label.AddToClassList("uk-confirm-line");
                container.Add(label);
            }
        }

        /// <summary>自检用：丢弃队列与当前请求（不触发回调），释放模态占用。</summary>
        public static void ResetForTests() => DiscardAll();

        /// <summary>离开世界（回主菜单）时丢弃当前与排队中的确认（不触发回调——对应的世界已经不在了），释放模态占用。</summary>
        public static void DiscardAll()
        {
            Pending.Clear();
            Current = null;
            UiEscapeStack.Remove(ModalOwner);
            InputRouter.PopModal(ModalOwner);
            Render();
        }
    }
}
