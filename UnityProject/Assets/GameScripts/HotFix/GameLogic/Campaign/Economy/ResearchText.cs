using System;
using System.Collections.Generic;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>一条“解锁什么”的预览（研发树悬停 / 详情、图鉴）：文字 + 占地（建筑才有）。</summary>
    public readonly struct ResearchUnlockLine
    {
        public readonly string Text;
        /// <summary>建筑占地（宽 × 高格）；不是建筑为 0。</summary>
        public readonly int FootprintW;
        public readonly int FootprintH;

        public ResearchUnlockLine(string text, int w = 0, int h = 0)
        {
            Text = text;
            FootprintW = w;
            FootprintH = h;
        }
    }

    /// <summary>
    /// FG5-RND-01（FGR-RND-013“悬停节点显示它会解锁什么，并附预览（图标、说明，建筑的话还有占地）”；FG00 B06 / B13）：研究节点的说明文字——
    /// 研发树面板的悬停详情、图鉴“研究”页签、建造菜单的门槛说明共用这一份。全部走文本键；只在打开 / 悬停时调用，O(解锁数)。
    /// </summary>
    public static class ResearchText
    {
        public static string KindName(ResearchNodeDef n) => n == null ? string.Empty : GameText.Get("research.detail.kind." + n.Kind);

        /// <summary>“解锁：”下面的逐条预览。</summary>
        public static List<ResearchUnlockLine> UnlockLines(CampaignState state, ResearchNodeDef n)
        {
            var lines = new List<ResearchUnlockLine>(4);
            if (n == null)
            {
                return lines;
            }
            if (!n.IsReady)
            {
                lines.Add(new ResearchUnlockLine(GameText.Get("research.detail.later")));
                return lines;
            }
            foreach (string u in n.Unlocks)
            {
                if (u.StartsWith("build:", StringComparison.Ordinal) && BuildCatalog.TryGet(u.Substring(6), out BuildEntry e))
                {
                    if (e.IsTool)
                    {
                        lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.unlock_tool", e.Name, e.Tool.ScrapPerCell)));
                    }
                    else
                    {
                        int scrap = HomeValleyLayout.BuildProfile.TryGetValue(e.Id, out (int ScrapCost, float Seconds) p) ? p.ScrapCost : 0;
                        lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.unlock_building", e.Name, e.Building.FootprintW, e.Building.FootprintH, scrap),
                            e.Building.FootprintW, e.Building.FootprintH));
                    }
                }
                else if (ResearchCatalog.TryParseTier(u, out string typeId, out int tier))
                {
                    lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.unlock_tier", HomeGridService.DisplayName(typeId), tier,
                        BuildingOps.TierEffect(typeId, tier) ?? string.Empty)));
                }
                else if (u.StartsWith("rule:", StringComparison.Ordinal))
                {
                    // FG6-DEF-03：解锁一类常驻规则（自动重建）：写明在哪里用。
                    lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.unlock_rule", ResearchService.TargetName(u))));
                }
            }
            double capPct = ResearchService.EfficiencyCap * 100.0;
            switch (n.EffectKind)
            {
                case ResearchCatalog.EffectSpeed:
                    lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.effect_speed", GameText.Get("research.category." + n.EffectTarget),
                        Pct(n.EffectValue), SpeedTargets(n.EffectTarget), Pct(ResearchService.EffectTotal(state, n.EffectKind, n.EffectTarget)), ResearchService.Num(capPct))));
                    break;
                case ResearchCatalog.EffectLab:
                    lines.Add(new ResearchUnlockLine(GameText.Format("research.detail.effect_lab", Pct(n.EffectValue),
                        Pct(ResearchService.EffectTotal(state, n.EffectKind, n.EffectTarget)), ResearchService.Num(capPct))));
                    break;
                case ResearchCatalog.EffectCapacity:
                    lines.Add(new ResearchUnlockLine(GameText.Format(n.EffectTarget == "warehouse" ? "research.detail.effect_capacity_warehouse" : "research.detail.effect_capacity_storage",
                        Pct(n.EffectValue), Pct(ResearchService.EffectTotal(state, n.EffectKind, n.EffectTarget)))));
                    break;
            }
            return lines;
        }

        private static string Pct(double v) => ResearchService.Num(v * 100.0);

        /// <summary>效率节点作用到哪些建筑（这一分类里按周期生产的：回收站、提取钻、精炼炉……）。</summary>
        public static string SpeedTargets(string category)
        {
            var names = new List<string>(6);
            foreach (BuildingGrid g in GridContent.Buildings)
            {
                if (g.Placeable == 1 && g.Category == category && ProducerCatalog.TryGet(g.TypeId, out ProducerDef def)
                    && (def.Mode == ProducerMode.Recipe || def.Mode == ProducerMode.Drill || def.Mode == ProducerMode.Recycler))
                {
                    names.Add(HomeGridService.DisplayName(g.TypeId));
                }
            }
            return string.Join(GameText.Get("build.cost.extra_sep"), names);
        }

        /// <summary>前置的一行：“前置：物流 · 分流与过滤（✓）、……”；没有前置返回 null。</summary>
        public static string PrereqLine(CampaignState state, ResearchNodeDef n)
        {
            if (n == null || n.Prereqs.Length == 0)
            {
                return null;
            }
            var parts = new List<string>(n.Prereqs.Length);
            foreach (string p in n.Prereqs)
            {
                string name = ResearchCatalog.Find(p)?.Name ?? p;
                parts.Add(GameText.Format(ResearchService.IsCompleted(state, p) ? "research.detail.prereq_done" : "research.detail.prereq_todo", name));
            }
            return GameText.Format("research.detail.prereqs", string.Join(GameText.Get("build.cost.extra_sep"), parts));
        }

        /// <summary>关键材料的一行：“关键材料（不消耗）：监听阵列核 ×1（核心保管库 0）”；不要关键材料返回 null。</summary>
        public static string KeyLine(CampaignState state, ResearchNodeDef n)
        {
            if (n == null || n.KeyItems.Count == 0)
            {
                return null;
            }
            var parts = new List<string>(n.KeyItems.Count);
            foreach (BuildMaterialNeed k in n.KeyItems)
            {
                parts.Add(GameText.Format("research.detail.key_item", k.Item.Name, k.Amount, HomeInventory.Stock(state, k.Item)));
            }
            return GameText.Format("research.detail.key", string.Join(GameText.Get("build.cost.extra_sep"), parts));
        }

        /// <summary>这个节点现在不能推进 / 不能加入队列的原因（B06）；能推进返回 null。</summary>
        public static string BlockReason(CampaignState state, ResearchNodeDef n)
        {
            switch (ResearchService.StateOf(state, n))
            {
                case ResearchNodeState.Later:
                    return GameText.Format("research.reason.later", n.Name);
                case ResearchNodeState.Closed:
                    return GameText.Format("research.reason.closed", n.Branch.Name);
                case ResearchNodeState.Locked:
                    var missing = new List<string>(2);
                    foreach (string p in n.Prereqs)
                    {
                        if (!ResearchService.IsCompleted(state, p))
                        {
                            missing.Add(ResearchCatalog.Find(p)?.Name ?? p);
                        }
                    }
                    return GameText.Format("research.reason.prereq", string.Join(GameText.Get("build.cost.extra_sep"), missing));
                case ResearchNodeState.WaitingKey:
                case ResearchNodeState.Available:
                case ResearchNodeState.Queued:
                    return ResearchService.KeyShortfall(state, n);
                default:
                    return null;
            }
        }

        /// <summary>整段详情（悬停 / 详情栏 / 图鉴正文）。</summary>
        public static string Detail(CampaignState state, ResearchNodeDef n)
        {
            if (n == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(256);
            sb.Append(GameText.Format("research.detail.header", n.Branch.Name, KindName(n), n.Cost)).Append('\n');
            sb.Append(n.Desc).Append('\n');
            sb.Append(GameText.Format("research.detail.status", ResearchService.StateText(state, n)));
            int inv = ResearchService.Invested(state, n.Id);
            if (inv > 0 && !ResearchService.IsCompleted(state, n.Id))
            {
                sb.Append('\n').Append(GameText.Format("research.detail.progress", inv, n.Cost));
            }
            string reason = BlockReason(state, n);
            if (!string.IsNullOrEmpty(reason))
            {
                sb.Append('\n').Append(GameText.Format("research.detail.reason", reason));
            }
            string pre = PrereqLine(state, n);
            if (pre != null)
            {
                sb.Append('\n').Append(pre);
            }
            string key = KeyLine(state, n);
            if (key != null)
            {
                sb.Append('\n').Append(key);
            }
            sb.Append('\n').Append(GameText.Get("research.detail.unlocks"));
            foreach (ResearchUnlockLine l in UnlockLines(state, n))
            {
                sb.Append('\n').Append(l.Text);
            }
            return sb.ToString();
        }

        /// <summary>占位图标的颜色（分支色；美术阶段换正式图标，DEBT-FG5RND01-03）。</summary>
        public static Color BranchColor(ResearchBranchDef b) =>
            b != null && ColorUtility.TryParseHtmlString(b.Color, out Color c) ? c : new Color(0.6f, 0.6f, 0.6f);
    }
}
