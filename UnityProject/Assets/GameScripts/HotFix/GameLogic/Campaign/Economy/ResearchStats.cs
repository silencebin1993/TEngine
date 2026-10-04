using System.Collections.Generic;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG5-E2E-01（FG00 B19“本系统的关键产出进入统计面板”；FG05 FGR-RND-070“统计面板里可以看到技术数据的收入和支出”）：统计面板“研发”页——
    /// DEBT-FG5RND06-04（技术数据收入 / 支出按来源分项）、DEBT-FG5RND04-07（熔合统计）、DEBT-FG5RND05-06（情报统计）、研究与黑匣子的累计。
    /// 全部读存档里已经在记的累计（<see cref="TechDataFlow"/>、<see cref="ResearchState"/>、<see cref="FusionState"/>、<see cref="IntelState"/>、<see cref="BlackBoxState"/>），
    /// 不新增逐帧统计；只在面板刷新时组装文本，O(来源数 + 情报种类数)。
    /// </summary>
    public static class ResearchStats
    {
        public static void Build(CampaignState state, List<(string Text, string Cls)> lines)
        {
            lines.Clear();
            if (state?.Research == null)
            {
                return;
            }
            AppendTech(state, lines);
            AppendResearch(state, lines);
            AppendFusion(state, lines);
            AppendIntel(state, lines);
            AppendBlackBoxes(state, lines);
        }

        private static void AppendTech(CampaignState state, List<(string Text, string Cls)> lines)
        {
            lines.Add((GameText.Get("stats.research.tech_title"), "st-row-title"));
            lines.Add((GameText.Format("stats.research.tech_stock", N(state.TechData), N(state.Research.TechStart), N(TechDataFlow.TotalIncome(state)), N(TechDataFlow.TotalExpense(state))), null));
            foreach (string src in TechDataFlow.IncomeSources)
            {
                long v = TechDataFlow.IncomeOf(state, src);
                lines.Add((GameText.Format("stats.research.income", GameText.Get("stats.research.src." + src), N(v)), v > 0 ? null : "st-row-dim"));
            }
            foreach (string sink in TechDataFlow.ExpenseSinks)
            {
                long v = TechDataFlow.ExpenseOf(state, sink);
                lines.Add((GameText.Format("stats.research.expense", GameText.Get("stats.research.sink." + sink), N(v)), v > 0 ? null : "st-row-dim"));
            }
        }

        private static void AppendResearch(CampaignState state, List<(string Text, string Cls)> lines)
        {
            ResearchState r = state.Research;
            int ready = 0;
            foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
            {
                ready += n.IsReady ? 1 : 0;
            }
            lines.Add((GameText.Get("stats.research.research_title"), "st-row-title"));
            lines.Add((GameText.Format("stats.research.research_row", N(r.Points), N(r.PointsProduced), N(r.PointsInvested), N(r.CompletedNodes?.Length ?? 0), N(ready),
                N(r.Queue?.Length ?? 0), N(ResearchService.BuiltLabCount(state))), null));
        }

        private static void AppendFusion(CampaignState state, List<(string Text, string Cls)> lines)
        {
            FusionState f = FusionService.StateOf(state);
            lines.Add((GameText.Get("stats.research.fusion_title"), "st-row-title"));
            if (f == null)
            {
                lines.Add((GameText.Get("stats.research.none"), "st-row-dim"));
                return;
            }
            int total = FusionCatalog.Recipes.Count;
            lines.Add((GameText.Format("stats.research.fusion_row", N(f.Simulations), N(f.SimulationMisses), N(f.Fused), N(f.RolledBack), N(f.TechSpent),
                N(f.Discovered?.Length ?? 0), N(total), N(f.Clues?.Length ?? 0)), null));
        }

        private static void AppendIntel(CampaignState state, List<(string Text, string Cls)> lines)
        {
            IntelState f = IntelService.StateOf(state);
            lines.Add((GameText.Get("stats.research.intel_title"), "st-row-title"));
            if (f == null)
            {
                lines.Add((GameText.Get("stats.research.none"), "st-row-dim"));
                return;
            }
            lines.Add((GameText.Format("stats.research.intel_row", N(f.Produced), N(f.Interruptions), N(IntelService.ValidCount(state, GameClock.Ticks))), null));
            foreach (IntelKindDef k in IntelCatalog.All)
            {
                int produced = 0;
                foreach (IntelProgressRecord p in f.Progress ?? System.Array.Empty<IntelProgressRecord>())
                {
                    if (p != null && p.Kind == k.Id)
                    {
                        produced += p.Produced;
                    }
                }
                int fromRecords = 0;
                foreach (IntelRecord rec in f.Records ?? System.Array.Empty<IntelRecord>())
                {
                    fromRecords += rec != null && rec.Kind == k.Id ? 1 : 0;
                }
                lines.Add((GameText.Format("stats.research.intel_kind", k.Name, N(System.Math.Max(produced, fromRecords))), produced > 0 || fromRecords > 0 ? "st-row-sub" : "st-row-dim"));
            }
        }

        private static void AppendBlackBoxes(CampaignState state, List<(string Text, string Cls)> lines)
        {
            BlackBoxState b = BlackBoxService.StateOf(state);
            lines.Add((GameText.Get("stats.research.blackbox_title"), "st-row-title"));
            lines.Add((GameText.Format("stats.research.blackbox_row", N(b?.Boxes?.Length ?? 0), N(b?.Analyzed ?? 0), N(b?.PointsProduced ?? 0)), null));
        }

        private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
