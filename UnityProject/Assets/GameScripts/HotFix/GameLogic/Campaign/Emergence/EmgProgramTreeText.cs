using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>程序树预览的一行（EMG1-UX-01，FGU-48）。</summary>
    public enum EmgTreeRowKind : byte
    {
        Node = 0,
        Own = 1,
        Inherited = 2,
        OverflowTitle = 3,
        Overflow = 4,
        Heat = 5,
        ReasonsTitle = 6,
        Reason = 7,
        Empty = 8,
        Legacy = 9,
        OffPath = 10,
    }

    public struct EmgTreeRow
    {
        public EmgTreeRowKind Kind;
        public string Text;
        /// <summary>所属节点（节点行 / 指令行；原因行 = 原因所在节点，没有为 −1）。</summary>
        public int Node;
        /// <summary>指令行 / 溢出行 / 原因行对应的程序序号（其余 −1）。</summary>
        public int Ordinal;
        /// <summary>缩进层级（= 节点代号；节点内的指令再缩一层）。</summary>
        public int Depth;
        public bool Promoted, Duplicate, GenFull;
        /// <summary>原因行的文本键（emg.compile.*）。</summary>
        public string NoteKey;
    }

    /// <summary>
    /// EMG1-UX-01（FG19 FGR-EMG-016、038；FG19A A3；FGU-48）：把编译器产出（<see cref="EmgProgramResult"/>）排成程序树预览的行。
    /// 只读编译结果，不另算树、容量、继承、原因或热量（IC-REQ-010）；形状符号在行首（自有 ■、继承 ◇、溢出 ×、重复 ○、提升 ▲、节点 ◆），颜色只是辅助（B15）。
    /// 只认端口码与声明，不出现任何内容 ID 判断（FGR-EMG-013，FgEmergenceRuleAuditSelfCheck 扫本目录）。只在面板刷新 / 悬停时运行，O(程序长度)。
    /// </summary>
    public static class EmgProgramTreeText
    {
        public const string GlyphNode = "◆", GlyphOwn = "■", GlyphInherited = "◇", GlyphOverflow = "×", GlyphDuplicate = "○", GlyphPromoted = "▲";

        public const string NoteFlashTruncated = "emg.compile.flash_truncated";
        public const string NoteFlashRange = "emg.compile.flash_range";

        /// <summary>预览要能解释的全部编译原因（FGR-EMG-038 六个 + 表满 + 旧内容版本）；每个都有 <c>&lt;键&gt;.fix</c> 解决办法。</summary>
        public static readonly string[] ReasonKeys =
        {
            EmgProgramCompiler.NoteOverflow, EmgProgramCompiler.NoteGenFull, EmgProgramCompiler.NoteNotFlashed, EmgProgramCompiler.NoteDuplicate,
            NoteFlashTruncated, NoteFlashRange, EmgProgramCompiler.NoteTableFull, EmgProgramCompiler.StaleContentKey,
        };

        public static string FixKey(string noteKey) => noteKey + ".fix";

        /// <summary>原因 + 解决办法的一行文本（{0} = 指令名）。</summary>
        public static string ReasonText(string noteKey, string instructionName) =>
            GameText.Format(noteKey, instructionName ?? string.Empty) + "  " + GameText.Get(FixKey(noteKey));

        private static string F1(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>指令在预览里的名字：父固件名；混合固件展开的写“父名（混合名 展开）”。</summary>
        public static string StepName(EmgProgramStep s)
        {
            string owner = FirmwareName(s.OwnerId);
            return s.FirmwareId != s.OwnerId ? GameText.Format("emg.tree.entry.mixed", owner, FirmwareName(s.FirmwareId)) : owner;
        }

        public const string UnknownKey = "emg.tree.unknown";

        /// <summary>固件显示名；表里没登记显示名时写“未登记名称”，不把内容 ID 露给玩家（B16）。</summary>
        public static string FirmwareName(string firmwareId) => FirmwareKinds.DisplayName(firmwareId) ?? GameText.Get(UnknownKey);

        /// <summary>事件的玩家名（端口名文本 emg.port.&lt;id&gt;.name）。</summary>
        public static string EventName(EmgEventKind e) =>
            EmgCatalog.Current.PortIdByCode.TryGetValue((ushort)(EmgConst.EventPortBase + (int)e), out string id) ? GameText.Get("emg.port." + id + ".name") : GameText.Get(UnknownKey);

        /// <summary>
        /// 排出一个程序的预览行。<paramref name="offPathFirmware"/> 非空 = 接入口不在任何路径上、这些接入固件没进程序；
        /// <paramref name="tableFull"/> = 本程序登记进地点程序表时被拒（<see cref="EmgProgramCompiler.NoteTableFull"/>，由战斗地点给出）。
        /// </summary>
        public static void BuildRows(EmgProgramResult r, List<EmgTreeRow> into, string offPathFirmware = null, bool tableFull = false)
        {
            into.Clear();
            if (r == null || r.IsEmpty)
            {
                Add(into, EmgTreeRowKind.Empty, GameText.Get("emg.tree.empty"), -1, -1, 0);
                AddLegacy(r, into);
                AddOffPath(into, offPathFirmware);
                return;
            }
            AddNode(r, r.Nodes[0], into);
            bool anyOverflow = false;
            foreach (EmgProgramStep s in r.Steps)
            {
                if (!s.Overflow)
                {
                    continue;
                }
                if (!anyOverflow)
                {
                    anyOverflow = true;
                    Add(into, EmgTreeRowKind.OverflowTitle, GameText.Get("emg.tree.overflow_title"), -1, -1, 0);
                }
                string name = StepName(s);
                var row = new EmgTreeRow
                {
                    Kind = EmgTreeRowKind.Overflow, Node = -1, Ordinal = s.Ordinal, Depth = 1, Promoted = s.Promoted,
                    Text = GlyphOverflow + " " + (s.Promoted ? GameText.Format("emg.tree.entry.overflow_promoted", name) : name),
                };
                into.Add(row);
            }
            Add(into, EmgTreeRowKind.Heat, GameText.Format("emg.tree.heat", F1(r.HeatInstr), F1(r.HeatDecl), F1(r.HeatTrigger), r.ChildPayloads.ToString(CultureInfo.InvariantCulture),
                F1(r.HeatChildren), F1(r.HeatTotal)), -1, -1, 0);
            bool title = false;
            for (int i = 0; i < r.Notes.Count; i++)
            {
                EmgCompileNote n = r.Notes[i];
                if (IsDuplicateNote(r, i))
                {
                    continue; // 同一条指令在同一节点的同一原因只列一次（混合固件两条声明可能各记一次）
                }
                AddReason(into, ref title, n.Key, n.Ordinal >= 0 && n.Ordinal < r.Steps.Count ? StepName(r.Steps[n.Ordinal]) : string.Empty, n.Node, n.Ordinal);
            }
            if (tableFull)
            {
                AddReason(into, ref title, EmgProgramCompiler.NoteTableFull, string.Empty, -1, -1);
            }
            AddLegacy(r, into);
            AddOffPath(into, offPathFirmware);
        }

        private static bool IsDuplicateNote(EmgProgramResult r, int i)
        {
            EmgCompileNote n = r.Notes[i];
            for (int j = 0; j < i; j++)
            {
                EmgCompileNote m = r.Notes[j];
                if (m.Key == n.Key && m.Ordinal == n.Ordinal && m.Node == n.Node)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddReason(List<EmgTreeRow> into, ref bool title, string key, string name, int node, int ordinal)
        {
            if (!title)
            {
                title = true;
                Add(into, EmgTreeRowKind.ReasonsTitle, GameText.Get("emg.tree.reasons_title"), -1, -1, 0);
            }
            string text = ReasonText(key, name);
            into.Add(new EmgTreeRow
            {
                Kind = EmgTreeRowKind.Reason, Node = node, Ordinal = ordinal, Depth = 1, NoteKey = key,
                Text = node >= 0 ? GameText.Format("emg.tree.reason_at", node, text) : text,
            });
        }

        private static void AddNode(EmgProgramResult r, EmgTreeNode n, List<EmgTreeRow> into)
        {
            string head = n.Parent < 0
                ? GameText.Format("emg.tree.node.root", n.Used, n.Capacity)
                : GameText.Format("emg.tree.node.child", n.Gen, n.Index, EventName(n.TrigEvent), n.TrigCount, n.Used, n.Capacity);
            Add(into, EmgTreeRowKind.Node, GlyphNode + " " + head, n.Index, -1, n.Gen);
            foreach (EmgTreeEntry e in n.Entries)
            {
                EmgProgramStep s = r.Steps[e.Ordinal];
                var sb = new StringBuilder(32);
                sb.Append(e.Inherited ? GlyphInherited : GlyphOwn).Append(' ');
                string name = StepName(s);
                sb.Append(e.Inherited ? GameText.Format("emg.tree.entry.inherited", name, e.FromNode) : name);
                if (s.Kind == EmgStepKind.Trigger)
                {
                    sb.Append(" · ").Append(GameText.Format("emg.tree.tag.trigger", EventName(s.TrigEvent)));
                }
                bool promoted = s.Promoted && n.Parent < 0;
                bool dup = HasNote(r, EmgProgramCompiler.NoteDuplicate, e.Ordinal, n.Index);
                bool genFull = HasNote(r, EmgProgramCompiler.NoteGenFull, e.Ordinal, n.Index);
                if (promoted)
                {
                    sb.Append(" · ").Append(GlyphPromoted).Append(' ').Append(GameText.Get("emg.tree.tag.promoted"));
                }
                if (dup)
                {
                    sb.Append(" · ").Append(GlyphDuplicate).Append(' ').Append(GameText.Get("emg.tree.tag.duplicate"));
                }
                if (genFull)
                {
                    sb.Append(" · ").Append(GlyphOverflow).Append(' ').Append(GameText.Get("emg.tree.tag.gen_full"));
                }
                into.Add(new EmgTreeRow
                {
                    Kind = e.Inherited ? EmgTreeRowKind.Inherited : EmgTreeRowKind.Own, Text = sb.ToString(), Node = n.Index, Ordinal = e.Ordinal,
                    Depth = n.Gen + 1, Promoted = promoted, Duplicate = dup, GenFull = genFull,
                });
            }
            foreach (EmgTreeNode c in r.Nodes)
            {
                if (c.Parent == n.Index)
                {
                    AddNode(r, c, into);
                }
            }
        }

        private static bool HasNote(EmgProgramResult r, string key, int ordinal, int node)
        {
            foreach (EmgCompileNote n in r.Notes)
            {
                if (n.Key == key && n.Ordinal == ordinal && n.Node == node)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddLegacy(EmgProgramResult r, List<EmgTreeRow> into)
        {
            if (r == null || r.LegacyOwners.Count == 0)
            {
                return;
            }
            var names = new List<string>(r.LegacyOwners.Count);
            foreach (string o in r.LegacyOwners)
            {
                string n = FirmwareName(o);
                if (!names.Contains(n))
                {
                    names.Add(n);
                }
            }
            Add(into, EmgTreeRowKind.Legacy, GameText.Format("emg.tree.legacy", string.Join(GameText.Get("signal.core.summary_sep"), names)), -1, -1, 0);
        }

        private static void AddOffPath(List<EmgTreeRow> into, string offPathFirmware)
        {
            if (!string.IsNullOrEmpty(offPathFirmware))
            {
                Add(into, EmgTreeRowKind.OffPath, GameText.Format("emg.tree.off_path", offPathFirmware), -1, -1, 0);
            }
        }

        private static void Add(List<EmgTreeRow> into, EmgTreeRowKind kind, string text, int node, int ordinal, int depth) =>
            into.Add(new EmgTreeRow { Kind = kind, Text = text, Node = node, Ordinal = ordinal, Depth = depth });

        /// <summary>
        /// 固件悬停 / 详情页的端口行（FG19 第 13 节 B01“固件详情显示读 / 写端口”）：“端口：读 命中、热信号；写 触发器”。
        /// 只读声明表（混合固件取两父的声明，父 A 在前），不看阶段门槛；没有声明时返回 null。
        /// </summary>
        public static string PortsLine(string firmwareId)
        {
            if (string.IsNullOrEmpty(firmwareId))
            {
                return null;
            }
            EmgCompiled c = EmgCatalog.Current;
            var reads = new List<string>(4);
            var writes = new List<string>(4);
            if (FirmwareKinds.TryGetParents(firmwareId, out string a, out string b))
            {
                CollectPorts(c, a, reads, writes);
                CollectPorts(c, b, reads, writes);
            }
            else
            {
                CollectPorts(c, firmwareId, reads, writes);
            }
            if (reads.Count == 0 && writes.Count == 0)
            {
                return null;
            }
            string sep = GameText.Get("signal.core.summary_sep");
            string none = GameText.Get("emg.ports.none");
            return GameText.Format("emg.ports.line", reads.Count > 0 ? string.Join(sep, reads) : none, writes.Count > 0 ? string.Join(sep, writes) : none);
        }

        private static void CollectPorts(EmgCompiled c, string owner, List<string> reads, List<string> writes)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi))
            {
                return;
            }
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Owner != oi || !c.PortIdByCode.TryGetValue(d.Port, out string id))
                {
                    continue;
                }
                string name = GameText.Get("emg.port." + id + ".name");
                List<string> list = d.Dir == EmgDirs.Read ? reads : writes;
                if (!list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }
    }
}
