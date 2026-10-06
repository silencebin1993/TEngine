using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-06：常驻规则的文字（全部走文本键，B16）——规则名“R3 · 库存维持”、条件 / 动作描述、原因、触发日志、实体上的“由规则 R3 触发”。
    /// 日志与原因存的是文本键 + 参数；参数里的 @item: / @b: / @m: / @recipe: / @key: / @raw: 显示时按当前语言换成名字（切换语言后旧日志也跟着变）。
    /// </summary>
    public static partial class StandingRuleService
    {
        public static string Label(StandingRuleRecord r) => r == null ? string.Empty : GameText.Format("rules.label", r.Serial);

        public static string LabelWithKind(StandingRuleRecord r) =>
            r == null ? string.Empty : GameText.Format("rules.label_kind", r.Serial, KindName(r.Kind));

        public static string KindName(string kind)
        {
            RuleKind k = KindRow(kind);
            return k != null ? GameText.Get(k.NameKey) : kind;
        }

        private static string LabelArg(int serial) => "@rule:" + serial.ToString(CultureInfo.InvariantCulture);

        /// <summary>实体键 → 显示参数（b:… → @b:…；m:… → @m:…；o:工单 → 它的目标建筑）。</summary>
        private static string EntityArg(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 3)
            {
                return string.Empty;
            }
            return key[0] == 's' ? "@b:" + key.Substring(key.IndexOf(':', 2) + 1) : "@" + key;
        }

        // ── 名字 ─────────────────────────────────────────────────────────────────

        /// <summary>建筑的显示名：玩家起的名字，没有就是“类型名 #编号”。建筑已经没了按 ID 里的类型与编号写。</summary>
        public static string BuildingLabel(CampaignState state, string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId))
            {
                return GameText.Get("rules.none");
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b != null)
            {
                return !string.IsNullOrEmpty(b.CustomName)
                    ? b.CustomName
                    : GameText.Format("rules.building_numbered", HomeGridService.DisplayName(b.BuildingTypeId), BuildingOps.NumberOf(b).ToString(CultureInfo.InvariantCulture));
            }
            int colon = buildingId.IndexOf(':');
            int hash = buildingId.LastIndexOf('#');
            string type = buildingId.Substring(colon + 1, (hash > colon ? hash : buildingId.Length) - colon - 1);
            string number = hash >= 0 ? buildingId.Substring(hash + 1) : "1";
            return GameText.Format("rules.building_numbered", HomeGridService.DisplayName(type), number);
        }

        /// <summary>列表分隔（中文“、”，英文“, ”；B16）。</summary>
        public static string ListSeparator => GameText.Get("rules.sep_list");

        /// <summary>FG4-ECO-07（承接 DEBT-FG4ECO06-06）：规则里的机器称呼与名册同源——起了名的写“名字 #编号”。</summary>
        public static string MachineLabel(int logicId) => MachineNaming.Short(logicId);

        private static string RecipeLabel(string recipeId) =>
            string.IsNullOrEmpty(recipeId) ? GameText.Get("rules.recipe_none") : ItemCatalog.TryGetRecipe(recipeId, out RecipeDef rd) ? rd.Name : recipeId;

        private static string ItemLabel(string itemId) =>
            string.IsNullOrEmpty(itemId) ? GameText.Get("rules.none") : ItemCatalog.NameOf(itemId);

        /// <summary>参数 → 文字（@ 开头的按当前语言换成名字；其余原样）。</summary>
        public static string ResolveArg(CampaignState state, string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg[0] != '@')
            {
                return arg ?? string.Empty;
            }
            int colon = arg.IndexOf(':');
            if (colon < 0)
            {
                return arg;
            }
            string tag = arg.Substring(1, colon - 1);
            string v = arg.Substring(colon + 1);
            switch (tag)
            {
                case "b":
                    return BuildingLabel(state, v);
                case "m":
                    return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? MachineLabel(id) : v;
                case "item":
                    return ItemLabel(v);
                case "recipe":
                    return RecipeLabel(v);
                case "rule":
                    return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int serial) ? GameText.Format("rules.label", serial) : v;
                case "key":
                    return GameText.Get(v);
                case "raw":
                    return v;
                default:
                    return v;
            }
        }

        /// <summary>文本键 + 参数 → 文字。</summary>
        public static string Expand(CampaignState state, string key, IReadOnlyList<string> args)
        {
            var objs = new object[args?.Count ?? 0];
            for (int i = 0; i < objs.Length; i++)
            {
                objs[i] = ResolveArg(state, args[i]);
            }
            return GameText.Format(key, objs);
        }

        /// <summary>原因（文本键 + “|”分隔的参数）→ 文字。</summary>
        public static string FormatArgs(string key, string arg)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            string[] parts = string.IsNullOrEmpty(arg) ? Array.Empty<string>() : arg.Split('|');
            return Expand(CampaignSession.Current, key, parts);
        }

        /// <summary>规则这一刻没能执行的原因（没有 = null）。</summary>
        public static string IssueText(StandingRuleRecord r) => r == null || string.IsNullOrEmpty(r.IssueKey) ? null : FormatArgs(r.IssueKey, r.IssueArg);

        /// <summary>日志一条 → 文字（前面带游戏时间）。</summary>
        public static string LogText(CampaignState state, RuleLogRecord e) =>
            e == null ? string.Empty : GameClock.FormatDayTime(e.Tick / (double)Math.Max(1, GameClock.StepHz)) + "  " + Expand(state, e.Key, e.Args);

        // ── 条件 / 动作描述 ───────────────────────────────────────────────────────

        private static string ListOf(IReadOnlyList<string> names, string emptyKey)
        {
            if (names.Count == 0)
            {
                return GameText.Get(emptyKey);
            }
            if (names.Count <= 3)
            {
                return string.Join(ListSeparator, names);
            }
            return GameText.Format("rules.list_more", string.Join(ListSeparator, new[] { names[0], names[1], names[2] }), names.Count - 3);
        }

        private static string BuildingList(CampaignState state, string[] ids, string emptyKey)
        {
            var names = new List<string>(ids.Length);
            foreach (string id in ids)
            {
                names.Add(BuildingLabel(state, id));
            }
            return ListOf(names, emptyKey);
        }

        private static string MachineList(int[] ids, string emptyKey)
        {
            var names = new List<string>(ids.Length);
            foreach (int id in ids)
            {
                names.Add(MachineLabel(id));
            }
            return ListOf(names, emptyKey);
        }

        private static string TypeList(string[] ids)
        {
            var names = new List<string>(ids.Length);
            foreach (string id in ids)
            {
                names.Add(id == BeltTarget ? GameText.Get("rules.target.belts") : HomeGridService.DisplayName(id)); // FG6-DEF-03：传送带与物流节点
            }
            return ListOf(names, "rules.all_buildings");
        }

        public static string ConditionText(CampaignState state, StandingRuleRecord r)
        {
            switch (r.Kind)
            {
                case KindStock:
                    return GameText.Format("rules.cond.stock_keep", ItemLabel(r.ItemId), r.Threshold);
                case KindSupply:
                    return GameText.Format("rules.cond.supply", BuildingList(state, r.Targets, "rules.none"), ItemLabel(r.ItemId), r.Threshold);
                case KindWar:
                    return GameText.Get("rules.cond.war_plan");
                case KindSilent:
                    return GameText.Format("rules.cond.silent_night", r.Threshold);
                case KindRepair:
                    return GameText.Format("rules.cond.machine_repair", MachineList(r.Machines, "rules.all_machines"), r.Threshold);
                case KindUnload:
                    return GameText.Get("rules.cond.expedition_unload");
                case KindRebuild:
                    // FG6-DEF-03：圈了重建区域时写明“在开着的重建区域里”（区域列表在编辑区）。
                    return GameText.Format(r.Zones != null && r.Zones.Length > 0 ? "rules.cond.auto_rebuild_zones" : "rules.cond.auto_rebuild", TypeList(r.Targets));
                default:
                    return r.Kind;
            }
        }

        public static string ActionText(CampaignState state, StandingRuleRecord r)
        {
            switch (r.Kind)
            {
                case KindStock:
                    return GameText.Format("rules.act.stock_keep", r.Targets.Length > 0 ? BuildingLabel(state, r.Targets[0]) : GameText.Get("rules.none"), RecipeLabel(r.RecipeId));
                case KindSupply:
                    return GameText.Format("rules.act.supply", r.Batch);
                case KindWar:
                    return GameText.Format("rules.act.war_plan", GameText.Get(r.BoostRepair ? "rules.act.war_boost" : "rules.act.war_noboost")
                            + (r.BoostDefenseSupply ? GameText.Get("rules.act.war_supply") : string.Empty), // FG6-DEF-06
                        BuildingList(state, r.Targets, "rules.nothing"));
                case KindSilent:
                    return GameText.Format("rules.act.silent_night", MachineList(r.Machines, "rules.nothing"),
                        string.IsNullOrEmpty(r.PointId) ? HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeCore) : BuildingLabel(state, r.PointId),
                        BuildingList(state, r.Targets, "rules.nothing"));
                case KindRepair:
                    return GameText.Get("rules.act.machine_repair");
                case KindUnload:
                    return GameText.Format("rules.act.expedition_unload", string.IsNullOrEmpty(r.PointId) ? GameText.Get("rules.shared_storage") : BuildingLabel(state, r.PointId));
                case KindRebuild:
                    return GameText.Get("rules.act.auto_rebuild");
                default:
                    return string.Empty;
            }
        }

        /// <summary>冲突警告（没有 = null）。</summary>
        public static string ConflictText(CampaignState state, StandingRuleRecord r) =>
            r == null || r.ConflictWith == 0 ? null
                : GameText.Format("rules.row.conflict", GameText.Format("rules.label", r.ConflictWith), ResolveArg(state, EntityArg(r.ConflictEntity)),
                    GameText.Format("rules.label", r.ConflictWith));

        /// <summary>
        /// 配置层面的冲突（两条启用的规则要控制同一座建筑 / 同一台机器——面板一直显示，不等真触发；FGR-ECO-031 冲突检测）。
        /// 返回和它争的那条规则与实体；没有返回 false。O(规则数 × 目标数)，只在面板刷新时调用。
        /// </summary>
        public static bool TryStaticConflict(CampaignState state, StandingRuleRecord r, out StandingRuleRecord other, out string entityKey)
        {
            other = null;
            entityKey = null;
            if (r == null || !r.Enabled)
            {
                return false;
            }
            var mine = new List<string>();
            ClaimKeys(r, mine);
            if (mine.Count == 0)
            {
                return false;
            }
            var theirs = new List<string>();
            foreach (StandingRuleRecord x in Ordered(state))
            {
                if (x == r || !x.Enabled)
                {
                    continue;
                }
                theirs.Clear();
                ClaimKeys(x, theirs);
                foreach (string k in mine)
                {
                    if (theirs.Contains(k))
                    {
                        other = x;
                        entityKey = k;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>这条规则可能要控制的实体（配置层面）：库存维持 = 工厂；战时 = 暂停的建筑；静默夜 = 储能站 + 机器；机器维修 = 指定的机器（空 = 不列，任何机器都可能）。</summary>
        private static void ClaimKeys(StandingRuleRecord r, List<string> into)
        {
            switch (r.Kind)
            {
                case KindStock:
                case KindWar:
                case KindSilent:
                    foreach (string t in r.Targets)
                    {
                        into.Add(BuildingKey(t));
                    }
                    if (r.Kind == KindSilent)
                    {
                        foreach (int m in r.Machines)
                        {
                            into.Add(MachineKey(m));
                        }
                    }
                    break;
                case KindRepair:
                    foreach (int m in r.Machines)
                    {
                        into.Add(MachineKey(m));
                    }
                    break;
            }
        }

        public static string StaticConflictText(CampaignState state, StandingRuleRecord r)
        {
            if (!TryStaticConflict(state, r, out StandingRuleRecord other, out string key))
            {
                return null;
            }
            StandingRuleRecord winner = Compare(r, other) <= 0 ? r : other;
            return GameText.Format("rules.row.conflict", Label(other), ResolveArg(state, EntityArg(key)), Label(winner));
        }

        // ── 实体上的追溯（建筑面板 / 工单 / 机器）───────────────────────────────────

        /// <summary>“由规则 R3 触发：排产「维修件」”——这座建筑正被规则控制 / 有规则派的单子在做；没有返回 null。</summary>
        public static string DescribeBuilding(CampaignState state, string buildingId)
        {
            if (state == null || string.IsNullOrEmpty(buildingId))
            {
                return null;
            }
            RuleHoldRecord h = FindHold(state, BuildingKey(buildingId));
            if (h != null)
            {
                string label = GameText.Format("rules.label", h.Rule);
                if (h.Overridden)
                {
                    return GameText.Format("rules.trace.overridden", label);
                }
                switch (h.Kind)
                {
                    case HoldRecipe:
                        return GameText.Format("rules.trace.recipe", label, RecipeLabel(Find(state, h.Rule)?.RecipeId));
                    case HoldDisabled:
                        return GameText.Format("rules.trace.paused", label);
                    case HoldStorage:
                        return GameText.Format("rules.trace.discharge", label);
                }
            }
            foreach (WorkOrderRecord o in state.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.RuleSerial > 0 && o.TargetId == buildingId && HomeValleyWorkOrders.IsActive(o) && o.Kind != WorkOrderKind.Garrison
                    && o.Kind != WorkOrderKind.MachineRepair)
                {
                    return DescribeOrder(state, o);
                }
            }
            return null;
        }

        /// <summary>规则派出 / 改动过的工单：“由规则 R3 触发” / “优先级由规则 R2 提高”；不是规则的返回 null。</summary>
        public static string DescribeOrder(CampaignState state, WorkOrderRecord o)
        {
            if (o != null && o.IssuerId == HomeValleyWorkOrders.RosterIssuer)
            {
                // FG4-ECO-07：名册派的送修 / 驻防也写来源（FGR-ECO-031 可追溯同一做法）。
                return o.Kind == WorkOrderKind.MachineRepair
                    ? GameText.Format("roster.trace.repair", BuildingLabel(state, o.TargetId), o.Duration > 0f ? Math.Min(100, (int)Math.Round(o.Progress / o.Duration * 100f)) : 0)
                    : GameText.Format("roster.trace.garrison", BuildingLabel(state, o.TargetId));
            }
            if (o != null && o.IssuerId == Defense.RaidResultService.DeliverIssuer)
            {
                // FG6-DEF-08（FGR-BASE-020）：按“残骸去向”派的送货单写明来源。
                return GameText.Format("raid.result.trace.wreck", BuildingLabel(state, o.TargetId));
            }
            if (o == null || o.RuleSerial <= 0)
            {
                return null;
            }
            string label = GameText.Format("rules.label", o.RuleSerial);
            switch (o.Kind)
            {
                case WorkOrderKind.Repair:
                {
                    RuleHoldRecord boost = FindHold(state, OrderKey(o.WorkOrderId));
                    if (boost == null)
                    {
                        return GameText.Format("rules.trace.order", label);
                    }
                    string boostText = GameText.Format("rules.trace.boost", GameText.Format("rules.label", boost.Rule));
                    // 自动重建派的单子又被战时预案提了优先级：两个来源都写。
                    return boost.Rule == o.RuleSerial ? boostText : GameText.Format("rules.trace.order", label) + GameText.Get("rules.sep_dot") + boostText;
                }
                case WorkOrderKind.Garrison:
                    return GameText.Format("rules.trace.garrison", label, BuildingLabel(state, o.TargetId));
                case WorkOrderKind.MachineRepair:
                    return GameText.Format("rules.trace.repairing", label, BuildingLabel(state, o.TargetId),
                        o.Duration > 0f ? Math.Min(100, (int)Math.Round(o.Progress / o.Duration * 100f)) : 0);
                default:
                    return GameText.Format("rules.trace.order", label);
            }
        }

        /// <summary>这台机器正在做规则派的事：“由规则 R4 触发：在 维修台 #1 修理（60%）”；没有返回 null。</summary>
        public static string DescribeMachine(CampaignState state, int logicId)
        {
            WorkOrderRecord o = state == null ? null : HomeValleyWorkOrders.FindActiveOrderForMachine(state, logicId);
            return o != null ? DescribeOrder(state, o) : null;
        }

        /// <summary>规则派的三类工单的目标文字（工单面板 / 施工队列用）。</summary>
        public static string DescribeRuleOrderTarget(CampaignState state, WorkOrderRecord o)
        {
            switch (o.Kind)
            {
                case WorkOrderKind.Deliver:
                    return GameText.Format("work.deliver.target", o.ReservedItemAmount, ItemLabel(o.ReservedItemId), BuildingLabel(state, o.TargetId));
                case WorkOrderKind.MachineRepair:
                    return o.FailureReason == "no-power" ? GameText.Get("work.machine_repair.no_power") : GameText.Format("work.machine_repair.target", BuildingLabel(state, o.TargetId));
                case WorkOrderKind.Garrison:
                    return GameText.Format("work.garrison.target", BuildingLabel(state, o.TargetId));
                default:
                    return null;
            }
        }

        public static string KindTextOf(WorkOrderKind kind)
        {
            switch (kind)
            {
                case WorkOrderKind.Deliver: return GameText.Get("work.kind.deliver");
                case WorkOrderKind.MachineRepair: return GameText.Get("work.kind.machine_repair");
                case WorkOrderKind.Garrison: return GameText.Get("work.kind.garrison");
                default: return null;
            }
        }
    }
}
