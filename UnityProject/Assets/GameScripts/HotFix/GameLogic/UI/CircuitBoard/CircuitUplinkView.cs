using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEngine.UIElements;

namespace GameLogic.UI.CircuitBoard
{
    /// <summary>FG1-SIG-02（FGR-SIG-020、022；FGU-18）：电路编辑器里“接入口”与“双态编译预览”的界面绑定。
    /// 只做数据 → 元素（结构在 CircuitBoardPanel.uxml，样式在 CircuitBoardUI.uss）；计算全部来自
    /// <see cref="UplinkCompiler.CompileDual"/>。电路编辑器面板与自检共用本类，自检挂真 UXML 断言显示。</summary>
    public sealed class CircuitUplinkView
    {
        public const int LineCount = 6;

        private readonly Label[] _aiLines = new Label[LineCount];
        private readonly Label[] _upLines = new Label[LineCount];
        private readonly Button[] _slots = new Button[BlueprintCircuitLayout.SlotCount];
        private readonly VisualElement[] _slotIcons = new VisualElement[BlueprintCircuitLayout.SlotCount];
        private readonly Label[] _slotTags = new Label[BlueprintCircuitLayout.SlotCount];
        private Label _title;
        private Label _coreLine;
        private Label _aiTitle;
        private Label _upTitle;
        private Label _diffTitle;
        private Label _diff;
        private Label _notes;
        private Button _toggle;

        /// <summary>最近一次渲染的双态结果（自检 / 冒烟读，与界面显示同一份）。</summary>
        public UplinkDualPreview Last { get; private set; }

        public Button ToggleButton => _toggle;
        public string DiffText => _diff?.text ?? string.Empty;
        public string NotesText => _notes?.text ?? string.Empty;
        public string CoreLineText => _coreLine?.text ?? string.Empty;
        public bool DiffHighlighted => _diff != null && _diff.ClassListContains("cb-dual-diff-on");

        public string AiLine(int i) => i >= 0 && i < LineCount ? _aiLines[i]?.text ?? string.Empty : string.Empty;
        public string UplinkedLine(int i) => i >= 0 && i < LineCount ? _upLines[i]?.text ?? string.Empty : string.Empty;
        public bool UplinkedLineHighlighted(int i) => i >= 0 && i < LineCount && _upLines[i] != null && _upLines[i].ClassListContains("cb-dual-line-diff");

        /// <summary>格子上的接入口标记是否可见（图标、文字都显示、格子有接入口样式）。</summary>
        public bool SlotShowsUplink(int slot) =>
            slot >= 0 && slot < _slots.Length && _slots[slot] != null && _slots[slot].ClassListContains("cb-slot-uplink")
            && _slotIcons[slot] != null && !_slotIcons[slot].ClassListContains("cb-hidden")
            && _slotTags[slot] != null && !_slotTags[slot].ClassListContains("cb-hidden");

        public string SlotTagText(int slot) => slot >= 0 && slot < _slotTags.Length ? _slotTags[slot]?.text ?? string.Empty : string.Empty;

        /// <summary>绑定 UXML 元素，返回缺失的元素名（空 = 全部找到）。</summary>
        public List<string> Bind(VisualElement root)
        {
            var missing = new List<string>();
            T Q<T>(string name) where T : VisualElement
            {
                T e = root?.Q<T>(name);
                if (e == null)
                {
                    missing.Add(name);
                }
                return e;
            }

            _title = Q<Label>("UplinkTitle");
            _coreLine = Q<Label>("UplinkCoreLine");
            _aiTitle = Q<Label>("UplinkAiTitle");
            _upTitle = Q<Label>("UplinkUpTitle");
            _diffTitle = Q<Label>("UplinkDiffTitle");
            _diff = Q<Label>("UplinkDiffLabel");
            _notes = Q<Label>("UplinkNotesLabel");
            _toggle = Q<Button>("UplinkToggleButton");
            for (int i = 0; i < LineCount; i++)
            {
                _aiLines[i] = Q<Label>("UplinkAiLine" + i);
                _upLines[i] = Q<Label>("UplinkUpLine" + i);
            }
            for (int s = 0; s < BlueprintCircuitLayout.SlotCount; s++)
            {
                _slots[s] = root?.Q<Button>("Slot" + s);
                if (BlueprintCircuitLayout.IsUplinkCandidate(s))
                {
                    _slotIcons[s] = Q<VisualElement>("Slot" + s + "UplinkIcon");
                    _slotTags[s] = Q<Label>("Slot" + s + "UplinkTag");
                }
            }
            ApplyStaticTexts();
            return missing;
        }

        /// <summary>固定文字（标题、栏名、格子标注）走文本键；换语言后再调一次即可。</summary>
        public void ApplyStaticTexts()
        {
            if (_title != null) _title.text = GameText.Get("circuit.uplink.title");
            if (_aiTitle != null) _aiTitle.text = GameText.Get("circuit.uplink.col_ai");
            if (_upTitle != null) _upTitle.text = GameText.Get("circuit.uplink.col_uplinked");
            if (_diffTitle != null) _diffTitle.text = GameText.Get("circuit.uplink.diff_title");
            string tag = GameText.Get("circuit.uplink.tag");
            foreach (Label l in _slotTags)
            {
                if (l != null)
                {
                    l.text = tag;
                }
            }
        }

        /// <summary>格子上的接入口标记（每次刷新网格后调）。</summary>
        public void ApplySlots(BlueprintCircuitBoard board)
        {
            for (int s = 0; s < BlueprintCircuitLayout.SlotCount; s++)
            {
                bool on = board != null && board.HasUplink && board.UplinkSlot == s;
                _slots[s]?.EnableInClassList("cb-slot-uplink", on);
                _slotIcons[s]?.EnableInClassList("cb-hidden", !on);
                _slotTags[s]?.EnableInClassList("cb-hidden", !on);
            }
        }

        /// <summary>检查器里的“标为接入口 / 取消接入口”按钮文字（选中的正是接入口时显示“取消”）。</summary>
        public void ApplyToggle(BlueprintCircuitBoard board, int? selectedSlot)
        {
            if (_toggle == null)
            {
                return;
            }
            bool isUplink = board != null && selectedSlot.HasValue && board.HasUplink && board.UplinkSlot == selectedSlot.Value;
            _toggle.text = GameText.Get(isUplink ? "circuit.uplink.unmark" : "circuit.uplink.mark");
            _toggle.SetEnabled(board != null);
            _toggle.tooltip = GameText.Format("circuit.uplink.slot_detail", UplinkCompiler.Quota);
        }

        /// <summary>按当前信号核重算并显示双态预览。</summary>
        public UplinkDualPreview Render(BlueprintCircuitBoard board, CampaignState state)
        {
            if (board == null)
            {
                Last = null;
                for (int i = 0; i < LineCount; i++)
                {
                    SetLine(_aiLines[i], string.Empty, false);
                    SetLine(_upLines[i], string.Empty, false);
                }
                if (_diff != null) _diff.text = string.Empty;
                if (_notes != null) _notes.text = string.Empty;
                if (_coreLine != null) _coreLine.text = string.Empty;
                return null;
            }
            string[] core = state != null ? SignalCoreService.CurrentContentIds(state) : System.Array.Empty<string>();
            UplinkDualPreview dual = UplinkCompiler.CompileDual(board, core);
            Last = dual;
            if (_coreLine != null)
            {
                _coreLine.text = GameText.Format("circuit.uplink.core_line", SignalCoreService.DescribeLoadout(core));
            }
            for (int i = 0; i < LineCount; i++)
            {
                SetLine(_aiLines[i], i < dual.AiLines.Count ? dual.AiLines[i].Text : string.Empty, false);
                SetLine(_upLines[i], i < dual.UplinkedLines.Count ? dual.UplinkedLines[i].Text : string.Empty,
                    i < dual.UplinkedLines.Count && dual.UplinkedLines[i].Highlight);
            }
            if (_diff != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (UplinkDiffLine d in dual.Diff)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(d.Text);
                }
                _diff.text = dual.Diff.Count == 0 ? GameText.Get("circuit.uplink.diff.none") : sb.ToString();
                _diff.EnableInClassList("cb-dual-diff-on", dual.Diff.Count > 0);
            }
            if (_notes != null)
            {
                _notes.text = string.Join("\n", dual.Notes);
                _notes.EnableInClassList("cb-hidden", dual.Notes.Count == 0);
            }
            return dual;
        }

        private static void SetLine(Label l, string text, bool highlight)
        {
            if (l == null)
            {
                return;
            }
            l.text = text;
            l.EnableInClassList("cb-dual-line-diff", highlight);
        }
    }
}
