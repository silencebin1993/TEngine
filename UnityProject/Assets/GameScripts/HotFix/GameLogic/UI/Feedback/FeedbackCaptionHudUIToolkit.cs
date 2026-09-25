using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign.Feedback;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Feedback
{
    /// <summary>ER8-CONTENT-01 AC-AUD-001 / AC-ACC-002：反馈字幕条。只读渲染
    /// <see cref="FeedbackCues.ActiveCaptions"/>，不做任何业务判断，也不决定显示什么——显示策略
    /// （常显事件通知 / 仅字幕开启时的声音字幕）全部在 <see cref="FeedbackCues"/> 里定。
    ///
    /// 只在 <see cref="FeedbackCues.CaptionRevision"/> 变化时重绘，平时每帧只比较一个整数。</summary>
    public sealed class FeedbackCaptionHudUIToolkit : MonoBehaviour
    {
        private const int SlotCount = FeedbackCues.MaxVisibleCaptions;

        private static readonly string[] ToneClasses =
        {
            "fc-tone-info",
            "fc-tone-good",
            "fc-tone-warning",
            "fc-tone-danger",
        };

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private readonly VisualElement[] _rows = new VisualElement[SlotCount];
        private readonly Label[] _tags = new Label[SlotCount];
        private readonly Label[] _texts = new Label[SlotCount];
        private readonly Label[] _counts = new Label[SlotCount];
        private bool _ready;
        private int _renderedRevision = -1;
        private VisualElement _panelRoot;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("FeedbackCaptionHud");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：所有面板（含被点到前面的浮动窗口）之上、全屏模态之下——
            // 打开任何玩法面板时通知仍可见，但不会盖住阻断式失败页/胜利页。
            _document.sortingOrder = Common.UiWindowFocus.CaptionSortingOrder;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            VisualElement root = _document.rootVisualElement;
            if (root == null)
            {
                Log.Error("[FeedbackCaptionHudUIToolkit] rootVisualElement 等待超时，字幕条未初始化。");
                return;
            }
            // 模板根节点也要让鼠标穿透，否则右下角这一块会吞掉战场点击。
            root.pickingMode = PickingMode.Ignore;

            // 所有玩法面板共用 BattleHudPanelSettings＝同一个 Panel：在 Panel 根上捕获阶段挂一次
            // 点击回调，就覆盖了全部 UI Toolkit 按钮的点击音，不必逐个面板接线。只认 Button
            // （禁用的按钮收不到 ClickEvent，天然不响）。
            _panelRoot = root.panel?.visualTree;
            _panelRoot?.RegisterCallback<ClickEvent>(OnAnyPanelClick, TrickleDown.TrickleDown);

            for (int i = 0; i < SlotCount; i++)
            {
                _rows[i] = root.Q<VisualElement>("CaptionRow" + i);
                _tags[i] = root.Q<Label>("CaptionTag" + i);
                _texts[i] = root.Q<Label>("CaptionText" + i);
                _counts[i] = root.Q<Label>("CaptionCount" + i);
                if (_rows[i] == null || _tags[i] == null || _texts[i] == null || _counts[i] == null)
                {
                    Log.Error($"[FeedbackCaptionHudUIToolkit] 第 {i} 行节点缺失，字幕条未初始化。");
                    return;
                }
            }
            _ready = true;
        }

        private void Update()
        {
            if (!_ready || _renderedRevision == FeedbackCues.CaptionRevision)
            {
                return;
            }
            _renderedRevision = FeedbackCues.CaptionRevision;
            Render(FeedbackCues.ActiveCaptions);
        }

        private void Render(IReadOnlyList<FeedbackCaption> captions)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                VisualElement row = _rows[i];
                FeedbackCaption caption = i < captions.Count ? captions[i] : null;
                if (caption == null)
                {
                    row.AddToClassList("fc-row-hidden");
                    continue;
                }

                row.RemoveFromClassList("fc-row-hidden");
                for (int t = 0; t < ToneClasses.Length; t++)
                {
                    row.RemoveFromClassList(ToneClasses[t]);
                }
                int tone = (int)caption.Tone;
                row.AddToClassList(ToneClasses[tone >= 0 && tone < ToneClasses.Length ? tone : 0]);

                _tags[i].text = "【" + caption.Tag + "】";
                _texts[i].text = caption.Text ?? string.Empty;
                _counts[i].text = caption.Count > 1 ? "×" + caption.Count : string.Empty;
            }
        }

        private static void OnAnyPanelClick(ClickEvent evt)
        {
            // 按钮里如果嵌了图标/文字子节点，点击目标是子节点——往上找最近的 Button。
            for (var element = evt.target as VisualElement; element != null; element = element.parent)
            {
                if (element is Button button)
                {
                    if (button.enabledInHierarchy)
                    {
                        FeedbackCues.Raise(FeedbackCueId.UiClick);
                    }
                    return;
                }
            }
        }

        private void OnDestroy()
        {
            _panelRoot?.UnregisterCallback<ClickEvent>(OnAnyPanelClick, TrickleDown.TrickleDown);
            _panelRoot = null;
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
