using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine.UIElements;

namespace GameLogic.UI.CircuitBoard
{
    /// <summary>
    /// EMG1-UX-01（FG19 FGR-EMG-016、038；FG19A A3；FGU-48）：电路编辑器双态预览里的“程序树”视图。
    /// 两栏数据都直接取编译器产出（<see cref="EmgProgramCompiler.Compile(BlueprintCircuitPreview)"/>，与 CombatSite.WithProgram 同一入口），
    /// 不另算（IC-REQ-010）；行文本由 <see cref="EmgProgramTreeText.BuildRows"/> 排出。结构在 CircuitBoardPanel.uxml，样式在 CircuitBoardUI.uss。
    /// 面板每 0.2 秒 / 每次操作后刷新一次（与双态预览同节奏，O(程序长度)），行标签按需增长、复用，不每次重建。
    /// </summary>
    public sealed class ProgramTreeView
    {
        public const int ColumnAi = 0, ColumnUplinked = 1;

        /// <summary>缩进层级（根 0、根内指令 1、1 代节点内 2、2 代节点内 3）走 USS 类，不在代码里写像素。</summary>
        private static readonly string[] DepthClasses = { "cb-pt-d0", "cb-pt-d1", "cb-pt-d2", "cb-pt-d3" };

        private readonly VisualElement[] _rows = new VisualElement[2];
        private readonly Label[] _titles = new Label[2];
        private readonly List<Label>[] _pool = { new List<Label>(16), new List<Label>(16) };
        private readonly List<EmgTreeRow>[] _data = { new List<EmgTreeRow>(16), new List<EmgTreeRow>(16) };
        private readonly EmgProgramResult[] _results = new EmgProgramResult[2];
        private Label _title;
        private Label _summary;
        private bool _hookRaised;

        public string SummaryText => _summary?.text ?? string.Empty;
        public EmgProgramResult Result(int col) => _results[col];
        public IReadOnlyList<EmgTreeRow> Rows(int col) => _data[col];
        public bool ColumnVisible(int col) => _rows[col] != null && !_rows[col].ClassListContains("cb-hidden");

        /// <summary>界面上第 <paramref name="i"/> 行显示的文字（自检读界面，不读数据）。</summary>
        public string LabelText(int col, int i) => i < _pool[col].Count && _pool[col][i].style.display != DisplayStyle.None ? _pool[col][i].text : null;

        public bool LabelHasClass(int col, int i, string cls) => i < _pool[col].Count && _pool[col][i].ClassListContains(cls);

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
            _title = Q<Label>("ProgramTreeTitle");
            _summary = Q<Label>("ProgramTreeSummary");
            _titles[ColumnAi] = Q<Label>("ProgramTreeAiTitle");
            _titles[ColumnUplinked] = Q<Label>("ProgramTreeUpTitle");
            _rows[ColumnAi] = Q<VisualElement>("ProgramTreeAiRows");
            _rows[ColumnUplinked] = Q<VisualElement>("ProgramTreeUpRows");
            ApplyStaticTexts();
            return missing;
        }

        public void ApplyStaticTexts()
        {
            if (_title != null) _title.text = GameText.Get("emg.tree.title");
            if (_titles[ColumnAi] != null) _titles[ColumnAi].text = GameText.Get("circuit.uplink.col_ai");
            if (_titles[ColumnUplinked] != null) _titles[ColumnUplinked].text = GameText.Get("circuit.uplink.col_uplinked");
        }

        /// <summary>按双态预览的两次编译结果显示程序树；<paramref name="activeVersion"/> = 正在编辑的蓝图已保存的那一版（旧内容版本编译时摘要给出重新编译提示）。</summary>
        public void Render(UplinkDualPreview dual, BlueprintVersionRecord activeVersion)
        {
            ApplyStaticTexts(); // 换语言后下一次刷新即更新标题
            string stale = BlueprintEditorService.StaleContentNotice(activeVersion);
            if (_summary != null)
            {
                _summary.text = stale != null ? stale + "  " + GameText.Get(EmgProgramTreeText.FixKey(EmgProgramCompiler.StaleContentKey)) : string.Empty;
                _summary.EnableInClassList("cb-hidden", stale == null);
            }
            if (dual == null)
            {
                RenderColumn(ColumnAi, null, null, false, visible: false);
                RenderColumn(ColumnUplinked, null, null, false, visible: false);
                return;
            }
            EmgProgramResult ai = CombatSite.WithFlashForecast(dual.Ai, EmgProgramCompiler.Compile(dual.Ai)); // EMG1-FLASH-01：刷写截断预判
            RenderColumn(ColumnAi, ai, null, IsRejected(ai), visible: true);
            bool hasUplink = dual.Plan != null && dual.Plan.HasUplink;
            EmgProgramResult up = hasUplink ? CombatSite.WithFlashForecast(dual.Uplinked, EmgProgramCompiler.Compile(dual.Uplinked)) : null;
            string offPath = hasUplink && dual.Uplinked != null && !dual.Uplinked.UplinkOnPath && dual.Plan.Inserted.Count > 0 ? Names(dual.Plan.InsertedIds) : null;
            RenderColumn(ColumnUplinked, up, offPath, IsRejected(up), visible: hasUplink);
            if (!_hookRaised && (!ai.IsEmpty || (up != null && !up.IsEmpty)))
            {
                _hookRaised = true;
                GuidanceHooks.Raise(GuidanceHooks.EmgFirstProgramTree); // 第一次看到非空程序树（引导内容归 FG15-UX-04）
            }
        }

        /// <summary>渲染一栏（面板与自检共用；自检可直接给编译结果，例如注入刷写原因的夹具）。</summary>
        public void RenderColumn(int col, EmgProgramResult r, string offPathFirmware, bool tableFull, bool visible = true)
        {
            _results[col] = visible ? r : null;
            List<EmgTreeRow> rows = _data[col];
            if (visible)
            {
                EmgProgramTreeText.BuildRows(r, rows, offPathFirmware, tableFull);
            }
            else
            {
                rows.Clear();
            }
            _rows[col]?.EnableInClassList("cb-hidden", !visible);
            _titles[col]?.EnableInClassList("cb-hidden", !visible);
            if (_rows[col] == null)
            {
                return;
            }
            List<Label> pool = _pool[col];
            while (pool.Count < rows.Count)
            {
                var l = new Label();
                l.AddToClassList("cb-pt-row");
                _rows[col].Add(l);
                pool.Add(l);
            }
            for (int i = 0; i < pool.Count; i++)
            {
                Label l = pool[i];
                if (i >= rows.Count)
                {
                    l.style.display = DisplayStyle.None;
                    continue;
                }
                EmgTreeRow row = rows[i];
                l.style.display = DisplayStyle.Flex;
                l.text = row.Text;
                for (int d = 0; d < DepthClasses.Length; d++)
                {
                    l.EnableInClassList(DepthClasses[d], d == System.Math.Min(row.Depth, DepthClasses.Length - 1));
                }
                l.EnableInClassList("cb-pt-node", row.Kind == EmgTreeRowKind.Node);
                l.EnableInClassList("cb-pt-own", row.Kind == EmgTreeRowKind.Own);
                l.EnableInClassList("cb-pt-inherited", row.Kind == EmgTreeRowKind.Inherited);
                l.EnableInClassList("cb-pt-overflow", row.Kind == EmgTreeRowKind.Overflow || row.Kind == EmgTreeRowKind.OverflowTitle || row.Duplicate || row.GenFull);
                l.EnableInClassList("cb-pt-reason", row.Kind == EmgTreeRowKind.Reason || row.Kind == EmgTreeRowKind.OffPath || row.Kind == EmgTreeRowKind.Legacy);
                l.EnableInClassList("cb-pt-title", row.Kind == EmgTreeRowKind.ReasonsTitle || row.Kind == EmgTreeRowKind.OverflowTitle);
                l.EnableInClassList("cb-pt-promoted", row.Promoted);
            }
        }

        /// <summary>这个程序在某个地点登记进程序表时被拒（表满）：读战斗地点的编译结果，不重算（DEBT-EMG1PRG01-01）。</summary>
        private static bool IsRejected(EmgProgramResult r)
        {
            if (r == null || r.IsEmpty)
            {
                return false;
            }
            foreach (CombatSite site in CombatSites.All)
            {
                if (site.IsProgramRejected(r.Signature))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Names(IReadOnlyList<string> ids)
        {
            var names = new List<string>(ids.Count);
            foreach (string id in ids)
            {
                names.Add(UplinkCompiler.FirmwareName(id));
            }
            return string.Join(GameText.Get("signal.core.summary_sep"), names);
        }
    }
}
