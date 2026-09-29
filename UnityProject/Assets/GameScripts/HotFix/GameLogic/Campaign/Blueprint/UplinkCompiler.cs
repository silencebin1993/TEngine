using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.Graph;
using TEngine;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>FG1-SIG-02：信号核里某枚固件没有插进接入口的原因（稳定码，测试与界面共用）。</summary>
    public enum UplinkSkipReason
    {
        /// <summary>这张蓝图没有接入口（FGR-SIG-031 第 3 条：照样接入，但不插入固件）。</summary>
        NoUplink = 0,
        /// <summary>超出接入口负载配额（FGR-SIG-023，初值 2 个）。</summary>
        OverQuota = 1,
        /// <summary>插入后路径数超过上限，按槽位倒序撤下（FGR-SIG-023）。</summary>
        PathLimit = 2,
        /// <summary>固件目录里查不到这个 ID（旧档残留 / 篡改）——插进去也不会有效果，直接说明。
        /// 目录里有、但没有 gene 模块的固件（标记跳转等）照常插入：反应与热量按固件 ID 判定。</summary>
        Unknown = 3,
    }

    public readonly struct UplinkFirmwareEntry
    {
        /// <summary>信号核槽位（0 起；界面显示时 +1）。</summary>
        public readonly int CoreSlot;
        public readonly string FirmwareId;
        public readonly UplinkSkipReason Skip;

        public UplinkFirmwareEntry(int coreSlot, string firmwareId, UplinkSkipReason skip = UplinkSkipReason.NoUplink)
        {
            CoreSlot = coreSlot;
            FirmwareId = firmwareId;
            Skip = skip;
        }
    }

    /// <summary>FG1-SIG-02（FGR-SIG-023）：信号核的固件怎样插进接入口——纯计算，不改任何状态。
    /// 双态预览与 FG1-SIG-03 的真实接入都只经 <see cref="UplinkCompiler.Plan"/> 得到它，保证“预览与实际一致”。</summary>
    public sealed class UplinkInsertionPlan
    {
        public bool HasUplink;
        public int UplinkSlot;
        public int Quota;
        public int PathLimit;
        /// <summary>信号核里一枚固件都没有。</summary>
        public bool CoreEmpty;
        /// <summary>按信号核槽位顺序插入的固件（数量 ≤ <see cref="Quota"/>）。</summary>
        public readonly List<UplinkFirmwareEntry> Inserted = new List<UplinkFirmwareEntry>();
        /// <summary>没插入的固件与原因，按信号核槽位排序。</summary>
        public readonly List<UplinkFirmwareEntry> Skipped = new List<UplinkFirmwareEntry>();
        /// <summary>插入之后的路径数（与 <see cref="PathLimit"/> 比较）。</summary>
        public int PathCountAfter;

        public string[] InsertedIds => Inserted.Select(e => e.FirmwareId).ToArray();
    }

    /// <summary>双态预览的一行：文字 + 是否与另一栏不同（高亮）。</summary>
    public readonly struct UplinkPreviewLine
    {
        public readonly string Text;
        public readonly bool Highlight;

        public UplinkPreviewLine(string text, bool highlight)
        {
            Text = text;
            Highlight = highlight;
        }
    }

    public enum UplinkDiffKind
    {
        PathChanged,
        PathAdded,
        ReactionAdded,
        ReactionChanged,
        HeatChanged,
        DamageChanged,
        /// <summary>FG2-FW-01（FG-GAP-028）：每发耗电变化。</summary>
        PowerChanged,
    }

    public readonly struct UplinkDiffLine
    {
        public readonly UplinkDiffKind Kind;
        public readonly string Text;

        public UplinkDiffLine(UplinkDiffKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>FG1-SIG-02（FGR-SIG-022）：电路编辑器里同时显示的两种编译结果与它们的差异。</summary>
    public sealed class UplinkDualPreview
    {
        /// <summary>AI 驾驶时：接入口按空槽处理（FGR-SIG-021），AI 永远不填接入口（FGR-SIG-090）。</summary>
        public BlueprintCircuitPreview Ai;
        /// <summary>你接入时：用玩家当前的信号核配置计算（FGR-SIG-022）。</summary>
        public BlueprintCircuitPreview Uplinked;
        public UplinkInsertionPlan Plan;
        public readonly List<UplinkPreviewLine> AiLines = new List<UplinkPreviewLine>();
        public readonly List<UplinkPreviewLine> UplinkedLines = new List<UplinkPreviewLine>();
        /// <summary>差异：新增 / 改变的路径、新增的反应、热量与伤害的变化。没有差异时为空。</summary>
        public readonly List<UplinkDiffLine> Diff = new List<UplinkDiffLine>();
        /// <summary>说明：没有接入口、信号核为空、接入口没接通、每枚未插入固件的原因。</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>FG1-SIG-02：接入口的插入规则与双态编译预览（FG01 FGR-SIG-020～023，FGU-18）。
    ///
    /// 规则（全部是纯计算，O(信号核槽位 + 路径)，与机器总数无关）：
    /// 1. 信号核的固件按槽位顺序（1 号槽优先）插入，超出接入口配额（fg.TbHomeTuning signal.uplink.quota，初值 2）的不插；
    ///    接入口的配额独立，不占电路原有的负载上限。
    /// 2. 插入后路径数超过上限（<see cref="BlueprintCircuitLayout.MaxPaths"/>，现有规则 4 条）时按槽位**倒序**撤下固件，直到合法——
    ///    “1 号槽优先”，所以先撤优先级最低的；插入永远不会让电路进入非法状态。电路自己（AI 驾驶时）已经超限的草稿不归插入负责，不为它撤固件。
    /// 3. 插入的固件只作用于经过接入口的路径（<see cref="BlueprintCircuitCompiler.Compile"/>）；接入口不在任何源→汇路径上时不生效并说明。
    /// 4. 预览与正式结算同一套计算（IC-REQ-010）：<see cref="MachineLoadoutRegistry.ResolveForUplink"/> 也只调这里。</summary>
    public static class UplinkCompiler
    {
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>接入口负载配额（固件个数）。</summary>
        public static int Quota
        {
            get
            {
                if (GridContent.TryGetTuning("signal.uplink.quota", out float v))
                {
                    return Math.Max(0, (int)Math.Round(v));
                }
                if (WarnedTuning.Add("signal.uplink.quota"))
                {
                    Log.Error("[UplinkCompiler] fg.TbHomeTuning 缺少 signal.uplink.quota，暂用规格初值 2（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
                }
                return 2;
            }
        }

        /// <summary>路径上限（现有规则，与保存校验同一个常量）。</summary>
        public static int PathLimit => BlueprintCircuitLayout.MaxPaths;

        /// <summary>自检用：替换“插入这些固件之后有几条路径”的计算。现有 Demo 固件都不改变电路拓扑（插入前后路径数相同），
        /// 路径上限截断规则要靠它注入一个“会增加路径”的固件来验证；FG2 全量固件若有改变拓扑的，在 <see cref="PathCountAfter"/> 里接真实规则。</summary>
        public static Func<BlueprintCircuitBoard, IReadOnlyList<string>, int> PathCountOverrideForTests;

        public static void ResetForTests()
        {
            PathCountOverrideForTests = null;
        }

        /// <summary>插入 <paramref name="inserted"/> 之后电路的路径数。</summary>
        public static int PathCountAfter(BlueprintCircuitBoard board, IReadOnlyList<string> inserted)
        {
            if (PathCountOverrideForTests != null)
            {
                return PathCountOverrideForTests(board, inserted);
            }
            return PathCompiler.Compile(board.ToSlotGrid()).Count;
        }

        /// <summary>算出信号核（按槽位顺序的内容 ID，空槽为空串 / null）怎样插进 <paramref name="board"/> 的接入口。</summary>
        public static UplinkInsertionPlan Plan(BlueprintCircuitBoard board, IReadOnlyList<string> signalCoreContentIds)
        {
            board.SyncFixedSlots();
            var plan = new UplinkInsertionPlan
            {
                HasUplink = board.HasUplink,
                UplinkSlot = board.HasUplink ? board.UplinkSlot : BlueprintCircuitLayout.NoUplink,
                Quota = Quota,
                PathLimit = PathLimit,
            };
            var candidates = new List<UplinkFirmwareEntry>();
            for (int i = 0; signalCoreContentIds != null && i < signalCoreContentIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(signalCoreContentIds[i]))
                {
                    candidates.Add(new UplinkFirmwareEntry(i, signalCoreContentIds[i]));
                }
            }
            plan.CoreEmpty = candidates.Count == 0;
            int basePaths = PathCountAfter(board, Array.Empty<string>());
            plan.PathCountAfter = basePaths;
            if (!plan.HasUplink)
            {
                foreach (UplinkFirmwareEntry c in candidates)
                {
                    plan.Skipped.Add(new UplinkFirmwareEntry(c.CoreSlot, c.FirmwareId, UplinkSkipReason.NoUplink));
                }
                return plan;
            }

            foreach (UplinkFirmwareEntry c in candidates)
            {
                // 与机器自己的固件槽同一口径：目录里有的固件都能插入、占配额。没有 gene 实现的（标记跳转、装甲击穿）
                // 由 ResolveFirmware 跳过模块与契约，反应（按固件 ID 判定）与热量照样算——否则接入永远打不出标记跳转。
                // Unknown 只留给目录里查不到的 ID（旧档残留 / 篡改）。
                if (!FirmwareCatalog.TryGet(c.FirmwareId, out _))
                {
                    plan.Skipped.Add(new UplinkFirmwareEntry(c.CoreSlot, c.FirmwareId, UplinkSkipReason.Unknown));
                }
                else if (plan.Inserted.Count >= plan.Quota)
                {
                    plan.Skipped.Add(new UplinkFirmwareEntry(c.CoreSlot, c.FirmwareId, UplinkSkipReason.OverQuota));
                }
                else
                {
                    plan.Inserted.Add(c);
                }
            }

            // 路径上限：插入不能让电路变得非法。电路自己已经超限（未保存的非法草稿）时，那不是插入造成的，不为它撤固件。
            int limit = Math.Max(plan.PathLimit, basePaths);
            int after = plan.Inserted.Count > 0 ? PathCountAfter(board, plan.InsertedIds) : basePaths;
            while (after > limit && plan.Inserted.Count > 0)
            {
                UplinkFirmwareEntry last = plan.Inserted[plan.Inserted.Count - 1];
                plan.Inserted.RemoveAt(plan.Inserted.Count - 1);
                plan.Skipped.Add(new UplinkFirmwareEntry(last.CoreSlot, last.FirmwareId, UplinkSkipReason.PathLimit));
                after = plan.Inserted.Count > 0 ? PathCountAfter(board, plan.InsertedIds) : basePaths;
            }
            plan.PathCountAfter = after;
            plan.Skipped.Sort((a, b) => a.CoreSlot.CompareTo(b.CoreSlot));
            return plan;
        }

        /// <summary>“你接入时”的编译结果（与 FG1-SIG-03 真实接入后的结算同一个入口）。</summary>
        public static BlueprintCircuitPreview CompileUplinked(BlueprintCircuitBoard board, IReadOnlyList<string> signalCoreContentIds, int seed = 1) =>
            CompileUplinked(board, Plan(board, signalCoreContentIds), seed);

        public static BlueprintCircuitPreview CompileUplinked(BlueprintCircuitBoard board, UplinkInsertionPlan plan, int seed = 1) =>
            BlueprintCircuitCompiler.Compile(board, seed, plan != null && plan.HasUplink ? plan.InsertedIds : null);

        /// <summary>双态编译预览：同一张电路、同一个种子，分别按 AI 驾驶与你接入编译，再逐项比较。</summary>
        public static UplinkDualPreview CompileDual(BlueprintCircuitBoard board, IReadOnlyList<string> signalCoreContentIds, int seed = 1)
        {
            UplinkInsertionPlan plan = Plan(board, signalCoreContentIds);
            var dual = new UplinkDualPreview
            {
                Plan = plan,
                Ai = BlueprintCircuitCompiler.Compile(board, seed, null),
                Uplinked = CompileUplinked(board, plan, seed),
            };
            BuildLines(dual);
            BuildDiff(dual);
            BuildNotes(dual);
            return dual;
        }

        // ── 文字（全部走文本键）──────────────────────────────────────────────────

        private static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        public static string FirmwareName(string id) => FirmwareKinds.DisplayName(id) ?? id;

        private static string Names(IEnumerable<string> ids)
        {
            string[] names = (ids ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Select(FirmwareName).ToArray();
            return names.Length == 0 ? GameText.Get("circuit.uplink.none_word") : string.Join(GameText.Get("signal.core.summary_sep"), names);
        }

        private static string ReactionName(string reactionId) =>
            reactionId != null && MechanicalReactionCatalog.TryGet(reactionId, out MechanicalContentDef def) ? def.DisplayName : reactionId;

        private static string PathText(int[] slotPath) => string.Join("→", slotPath ?? Array.Empty<int>());

        private static List<string> ColumnTexts(BlueprintCircuitPreview p, UplinkInsertionPlan plan, bool uplinked)
        {
            var lines = new List<string>();
            if (!plan.HasUplink)
            {
                lines.Add(GameText.Get("circuit.uplink.line.no_uplink"));
            }
            else if (!uplinked || p.UplinkFirmwareIds.Length == 0)
            {
                lines.Add(GameText.Format("circuit.uplink.line.uplink_empty", plan.UplinkSlot));
            }
            else
            {
                lines.Add(GameText.Format("circuit.uplink.line.uplink_filled", plan.UplinkSlot, Names(p.UplinkFirmwareIds), p.UplinkFirmwareIds.Length, plan.Quota));
            }
            lines.Add(p.HasCombatOutput
                ? GameText.Format("circuit.uplink.line.paths", p.PathCount)
                : GameText.Get("circuit.uplink.line.no_output"));
            lines.Add(GameText.Format("circuit.uplink.line.damage", F1(p.TotalNormalizedDamage)));
            // FG2-FW-01（FG-GAP-028，FGR-SIG-022“热量和能耗的变化”）：热量行带上固件的每发耗电（同一行，栏的行数与界面标签数不变）。
            lines.Add(GameText.Format("circuit.uplink.line.heat_power", F1(p.HeatBudget), p.PowerCost.ToString(CultureInfo.InvariantCulture)));
            lines.Add(p.ReactionId != null
                ? GameText.Format("circuit.uplink.line.reaction", ReactionName(p.ReactionId))
                : GameText.Get("circuit.uplink.line.reaction_none"));
            lines.Add(GameText.Format("circuit.uplink.line.firmware", Names(p.FirmwareIds)));
            // FG1-SIG-06（FGR-SIG-060、061）：接入时裸跑的未破解固件单独一行，写明代价。
            if (uplinked && p.RawFirmwareIds.Length > 0)
            {
                lines.Add(GameText.Format("circuit.uplink.line.raw", Names(p.RawFirmwareIds), RawFirmwareService.ExposurePerFire.ToString("0.#", CultureInfo.InvariantCulture),
                    p.RawHeatMultiplier.ToString("0.#", CultureInfo.InvariantCulture)));
            }
            return lines;
        }

        private static void BuildLines(UplinkDualPreview dual)
        {
            List<string> ai = ColumnTexts(dual.Ai, dual.Plan, uplinked: false);
            List<string> up = ColumnTexts(dual.Uplinked, dual.Plan, uplinked: true);
            for (int i = 0; i < ai.Count; i++)
            {
                dual.AiLines.Add(new UplinkPreviewLine(ai[i], false));
            }
            for (int i = 0; i < up.Count; i++)
            {
                bool differs = i >= ai.Count || !string.Equals(ai[i], up[i], StringComparison.Ordinal);
                dual.UplinkedLines.Add(new UplinkPreviewLine(up[i], differs));
            }
        }

        private static void BuildDiff(UplinkDualPreview dual)
        {
            BlueprintCircuitPreview a = dual.Ai;
            BlueprintCircuitPreview u = dual.Uplinked;
            var aiPaths = a.Paths.ToDictionary(p => PathText(p.SlotPath), p => p);
            foreach (BlueprintCircuitPathPreview p in u.Paths)
            {
                string key = PathText(p.SlotPath);
                if (!aiPaths.TryGetValue(key, out BlueprintCircuitPathPreview before))
                {
                    dual.Diff.Add(new UplinkDiffLine(UplinkDiffKind.PathAdded, GameText.Format("circuit.uplink.diff.path_added", key, F1(p.Damage))));
                }
                else if (p.ThroughUplink || Math.Abs(p.Damage - before.Damage) > 0.0001f || !p.Tags.SetEquals(before.Tags))
                {
                    string inserted = Names(p.ThroughUplink ? u.UplinkFirmwareIds : null);
                    dual.Diff.Add(new UplinkDiffLine(UplinkDiffKind.PathChanged, Math.Abs(p.Damage - before.Damage) > 0.0001f
                        ? GameText.Format("circuit.uplink.diff.path", key, inserted, F1(before.Damage), F1(p.Damage))
                        : GameText.Format("circuit.uplink.diff.path_same", key, inserted, F1(p.Damage))));
                }
            }
            if (u.ReactionId != a.ReactionId)
            {
                dual.Diff.Add(a.ReactionId == null
                    ? new UplinkDiffLine(UplinkDiffKind.ReactionAdded, GameText.Format("circuit.uplink.diff.reaction_added", ReactionName(u.ReactionId)))
                    : new UplinkDiffLine(UplinkDiffKind.ReactionChanged, GameText.Format("circuit.uplink.diff.reaction_changed",
                        ReactionName(a.ReactionId), u.ReactionId != null ? ReactionName(u.ReactionId) : GameText.Get("circuit.uplink.none_word"))));
            }
            if (Math.Abs(u.HeatBudget - a.HeatBudget) > 0.0001f)
            {
                dual.Diff.Add(new UplinkDiffLine(UplinkDiffKind.HeatChanged, GameText.Format("circuit.uplink.diff.heat", F1(a.HeatBudget), F1(u.HeatBudget))));
            }
            if (u.PowerCost != a.PowerCost)
            {
                dual.Diff.Add(new UplinkDiffLine(UplinkDiffKind.PowerChanged, GameText.Format("circuit.uplink.diff.power",
                    a.PowerCost.ToString(CultureInfo.InvariantCulture), u.PowerCost.ToString(CultureInfo.InvariantCulture))));
            }
            if (Math.Abs(u.TotalNormalizedDamage - a.TotalNormalizedDamage) > 0.0001f)
            {
                dual.Diff.Add(new UplinkDiffLine(UplinkDiffKind.DamageChanged,
                    GameText.Format("circuit.uplink.diff.damage", F1(a.TotalNormalizedDamage), F1(u.TotalNormalizedDamage))));
            }
        }

        private static void BuildNotes(UplinkDualPreview dual)
        {
            UplinkInsertionPlan plan = dual.Plan;
            // FG1-SIG-05（FGR-SIG-090）：机器电路里残留的核心固件（旧草稿 / 旧档）AI 不用——说明它为什么没出现在左栏、该怎么处理。
            foreach (string inert in dual.Ai.InertCoreFirmwareIds)
            {
                dual.Notes.Add(GameText.Format("circuit.uplink.note.core_inert", FirmwareName(inert)));
            }
            // FG1-SIG-06：未破解固件接入时照样生效，但有暴露与积热代价（破解后没有）。
            foreach (string raw in dual.Uplinked.RawFirmwareIds)
            {
                dual.Notes.Add(GameText.Format("circuit.uplink.note.raw", FirmwareName(raw), RawFirmwareService.ExposurePerFire.ToString("0.#", CultureInfo.InvariantCulture),
                    dual.Uplinked.RawHeatMultiplier.ToString("0.#", CultureInfo.InvariantCulture)));
            }
            if (!plan.HasUplink)
            {
                dual.Notes.Add(GameText.Get("circuit.uplink.note.no_uplink"));
            }
            else if (plan.CoreEmpty)
            {
                dual.Notes.Add(GameText.Format("circuit.uplink.note.core_empty", GameLogic.Core.InputDisplay.ForAction(GameLogic.Core.GameActionId.OpenSignalCore)));
            }
            else if (!dual.Uplinked.UplinkOnPath && plan.Inserted.Count > 0)
            {
                dual.Notes.Add(GameText.Format("circuit.uplink.note.off_path", plan.UplinkSlot));
            }
            foreach (UplinkFirmwareEntry e in plan.Skipped)
            {
                string name = FirmwareName(e.FirmwareId);
                switch (e.Skip)
                {
                    case UplinkSkipReason.OverQuota:
                        dual.Notes.Add(GameText.Format("circuit.uplink.skip.over_quota", name, plan.Quota));
                        break;
                    case UplinkSkipReason.PathLimit:
                        dual.Notes.Add(GameText.Format("circuit.uplink.skip.path_limit", name, plan.PathLimit));
                        break;
                    case UplinkSkipReason.Unknown:
                        dual.Notes.Add(GameText.Format("circuit.uplink.skip.unknown", name));
                        break;
                    default:
                        dual.Notes.Add(GameText.Format("circuit.uplink.skip.no_uplink", name));
                        break;
                }
            }
        }
    }
}
