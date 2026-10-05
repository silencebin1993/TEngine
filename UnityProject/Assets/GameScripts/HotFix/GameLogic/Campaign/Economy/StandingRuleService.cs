using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-06 常驻规则（FG04 FGR-ECO-030 / 031；FG13 FGU-15；FG00 FGR-BASE-020 / 021；FGT-ECO-003）。存档真相在 <see cref="StandingRuleState"/>，本类是唯一写入口。
    /// <para>七类规则：</para>
    /// - 库存维持（stock_keep）：仓库里物品 X 少于 N 件 → 指定工厂排产配方 R；X 补足（≥ N）后工厂恢复规则接管前的配方。
    /// - 阈值补给（supply）：指定建筑输入缓存里的 X 少于 N 件 → 从仓库预留一批、开补给工单让机器送过去（每座目标同时最多一张）；仓库没货时写明原因，有货后自动继续。
    /// - 战时预案（war_plan）：突袭开始（有突袭部队到达家园；FG6 接手后由 <see cref="RaidActiveProvider"/> 判定）→ 维修工作单提到最高优先级（执行期间新开的也提）、暂停指定建筑；突袭结束后恢复。
    /// - 静默夜预案（silent_night）：静默夜开始前 N 秒（FG7 经 <see cref="SilentNightWindowProvider"/> 给出时间窗）→ 指定机器去驻防点待命、指定储能站切换为只放电；静默夜结束后恢复。
    /// - 机器维修（machine_repair）：家园机器伤势超过 X% → 自己去最近一座运转、通电、有空位的维修台修理，修满回到闲置。
    /// - 远征卸货（expedition_unload，新战役默认开启）：远征队返回 → 机器货舱里的货卸到指定仓库（空 = 共用库存，仓库放不下的落地由机器搬走）。
    /// - 自动重建（auto_rebuild）：范围内的建筑被摧毁 → 派重建单（与面板“重建”同一入口、同样收重建造价）；材料不够时等着，够了自动派。
    /// <para>运行：只在世界模拟步里（<see cref="WorldStep"/>，与镜头 / 观察无关，FGR-BASE-021）。条件按固定频率（rules.check_seconds，初值每游戏分钟一次）检查，
    /// 不每帧遍历建筑（FG04 第 7 节）；事件（建筑被摧毁、远征返回）在下一个模拟步处理，玩家改动规则 / 突袭到达时下一个模拟步立刻检查一次。</para>
    /// <para>持有与冲突：规则的每一项改动都记成“持有”（<see cref="RuleHoldRecord"/>：实体、旧值、对应工单）。每次检查分三段：
    /// ① 按优先级（1 最先，同级按编号）算出每条规则这一刻要控制哪些实体，同一实体只给优先级最高的那条，其余记冲突（规则上显示警告、写日志、只在冲突变化时提醒一次）；
    /// ② 不再需要的持有恢复原样（实体已被玩家改掉的不恢复，只放手）；③ 新需要的持有执行动作并记下旧值。
    /// 玩家手动改了规则正在控制的东西 = 这次让给玩家（持有标记“你改动过”），条件解除后恢复正常——规则不和玩家来回抢（FGR-BASE-020）。</para>
    /// <para>可追溯：持有与规则派出的工单（<see cref="WorkOrderRecord.RuleSerial"/>）在建筑面板、工单与机器上显示“由规则 R3 触发”；触发日志保留最近 rules.log_max 条。</para>
    /// </summary>
    public static partial class StandingRuleService
    {
        public const string KindStock = "stock_keep";
        public const string KindSupply = "supply";
        public const string KindWar = "war_plan";
        public const string KindSilent = "silent_night";
        public const string KindRepair = "machine_repair";
        public const string KindUnload = "expedition_unload";
        public const string KindRebuild = "auto_rebuild";

        public static readonly string[] AllKinds = { KindStock, KindSupply, KindWar, KindSilent, KindRepair, KindUnload, KindRebuild };

        public const string HoldRecipe = "recipe";
        public const string HoldDisabled = "disabled";
        public const string HoldStorage = "storage";
        public const string HoldBoost = "repair_priority";
        public const string HoldGarrison = "garrison";
        public const string HoldMachineRepair = "machine_repair";
        public const string HoldSupply = "supply";

        private const string EventDestroyed = "destroyed";
        private const string EventExpedition = "expedition";
        private const string EventRebuildRetry = "rebuild_retry";

        /// <summary>规则 / 持有 / 日志有变化 +1（面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        public static void Touch() => Revision++;

        /// <summary>
        /// 突袭是否正在进行的替换判定（null = 用 <see cref="DefaultRaidActive"/>：有目标为家园的突袭部队已到达、还没撤退——FG6-DEF-04 起到达 = 突袭开始、开始撤退 = 突袭结束）。
        /// 突袭结算（FG6-DEF-05 攻城 / FG6-DEF-08 结算）接入后如需更细的判定再赋值。
        /// </summary>
        public static Func<CampaignState, bool> RaidActiveProvider;

        /// <summary>FG7-ENV-02 接入：给出当前或下一个静默夜的时间窗（统一时钟步，开始 / 结束）；没有静默夜返回 (-1, -1)。接入前为 null（静默夜预案不会触发）。</summary>
        public static Func<CampaignState, (long Start, long End)> SilentNightWindowProvider;

        // ── 统计（自检 / 性能）──
        public static int EvaluationCount { get; private set; }
        public static double LastEvaluationMs { get; private set; }
        public static double MaxEvaluationMs { get; private set; }

        public static void ResetStats()
        {
            EvaluationCount = 0;
            LastEvaluationMs = 0;
            MaxEvaluationMs = 0;
        }

        /// <summary>自检：清掉注入的判定（每次新世界前）。</summary>
        public static void ResetForTests()
        {
            RaidActiveProvider = null;
            SilentNightWindowProvider = null;
            ResetStats();
            Revision++;
        }

        // ── 调参 ─────────────────────────────────────────────────────────────────
        public static double CheckSeconds => Math.Max(1.0, GridContent.Tuning("rules.check_seconds"));
        public static int LogMax => Math.Max(1, GridContent.TuningInt("rules.log_max"));
        public static int MaxRules => Math.Max(1, GridContent.TuningInt("rules.max_count"));
        public static int SupplyBatchDefault => Math.Max(1, GridContent.TuningInt("rules.supply.batch_default"));
        public static int SupplyBatchMax => Math.Max(1, GridContent.TuningInt("rules.supply.batch_max"));
        public static float UnloadSeconds => Mathf.Max(0.1f, GridContent.Tuning("rules.supply.unload_seconds"));
        public static int BaySlots => Math.Max(1, GridContent.TuningInt("rules.repair_bay.slots"));
        public static float HealFractionPerSecond => Mathf.Max(0.0001f, GridContent.Tuning("rules.repair_bay.heal_fraction_per_second"));
        public static int WarRepairPriority => GridContent.TuningInt("rules.war.repair_priority");

        // ── 表 ───────────────────────────────────────────────────────────────────

        public static RuleKind KindRow(string kind)
        {
            try
            {
                return kind == null ? null : ConfigSystem.Instance.Tables?.TbRuleKind?.GetOrDefault(kind);
            }
            catch (Exception e)
            {
                Log.Error("[StandingRuleService] 读取 fg.TbRuleKind 失败：" + e.Message);
                return null;
            }
        }

        public static IReadOnlyList<RulePreset> Presets
        {
            get
            {
                var list = new List<RulePreset>();
                try
                {
                    TbRulePreset t = ConfigSystem.Instance.Tables?.TbRulePreset;
                    if (t != null)
                    {
                        list.AddRange(t.DataList);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[StandingRuleService] 读取 fg.TbRulePreset 失败：" + e.Message);
                }
                list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
                return list;
            }
        }

        public static bool HasThreshold(string kind)
        {
            RuleKind k = KindRow(kind);
            return k != null && k.ThresholdKey != "none";
        }

        // ── 读取 ─────────────────────────────────────────────────────────────────

        public static StandingRuleState Domain(CampaignState state)
        {
            if (state == null)
            {
                return null;
            }
            state.StandingRules ??= new StandingRuleState();
            if (state.StandingRules.Rules == null || state.StandingRules.Holds == null || state.StandingRules.Log == null || state.StandingRules.Pending == null)
            {
                CampaignFgStateDomains.EnsureRules(state.StandingRules);
            }
            return state.StandingRules;
        }

        /// <summary>按优先级排好的规则（1 最先，同级按编号）。</summary>
        public static List<StandingRuleRecord> Ordered(CampaignState state)
        {
            var list = new List<StandingRuleRecord>();
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return list;
            }
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null)
                {
                    list.Add(r);
                }
            }
            list.Sort(Compare);
            return list;
        }

        private static int Compare(StandingRuleRecord a, StandingRuleRecord b) =>
            a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Serial.CompareTo(b.Serial);

        public static StandingRuleRecord Find(CampaignState state, int serial)
        {
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return null;
            }
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        public static IReadOnlyList<RuleHoldRecord> Holds(CampaignState state) => (IReadOnlyList<RuleHoldRecord>)Domain(state)?.Holds ?? Array.Empty<RuleHoldRecord>();

        public static IReadOnlyList<RuleLogRecord> LogEntries(CampaignState state) => (IReadOnlyList<RuleLogRecord>)Domain(state)?.Log ?? Array.Empty<RuleLogRecord>();

        public static RuleHoldRecord FindHold(CampaignState state, string entityKey)
        {
            foreach (RuleHoldRecord h in Holds(state))
            {
                if (h != null && h.EntityId == entityKey)
                {
                    return h;
                }
            }
            return null;
        }

        public static string BuildingKey(string buildingId) => "b:" + buildingId;
        public static string MachineKey(int logicId) => "m:" + logicId.ToString(CultureInfo.InvariantCulture);
        public static string OrderKey(string orderId) => "o:" + orderId;
        private static string SupplyKey(int rule, string buildingId) => "s:" + rule.ToString(CultureInfo.InvariantCulture) + ":" + buildingId;

        // ── 世界模拟步 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 世界模拟的一个固定步（WorldSimulation 在生产与电网之后调用，家园载入时）：先处理排队的事件，再在到了定时检查的步、或有改动时检查一次全部规则。
        /// 只看步序号与存档状态，与观察无关；不到检查步时 O(排队事件数)。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return;
            }
            if (d.Pending.Length > 0)
            {
                ProcessEvents(state, d);
            }
            if (d.RebuildSweep)
            {
                d.RebuildSweep = false;
                SweepRebuild(state); // FG6-DEF-03：玩家刚改了自动重建（启用 / 范围 / 区域），补排已经被摧毁的东西（O(建筑数 + 虚影数)，只在改动后一次）
            }
            bool due = ticksBefore >= d.NextCheckTick;
            if (!due && !d.EvaluateNow)
            {
                return;
            }
            d.EvaluateNow = false;
            if (due)
            {
                d.NextCheckTick = ticksBefore + Math.Max(1L, (long)Math.Round(CheckSeconds * Math.Max(1, worldHz)));
            }
            Evaluate(state);
        }

        /// <summary>立刻检查一次（下一个模拟步）。</summary>
        public static void RequestEvaluation(CampaignState state)
        {
            StandingRuleState d = Domain(state);
            if (d != null)
            {
                d.EvaluateNow = true;
            }
        }

        // ── 事件钩子 ─────────────────────────────────────────────────────────────

        /// <summary>突袭部队到达家园（WorldTransitSystem）：下一个模拟步立刻检查（战时预案不用等到下一个整分钟）。</summary>
        public static void NotifyRaidArrived(CampaignState state) => RequestEvaluation(state);

        /// <summary>建筑刚被摧毁（BuildingOps.OnBuildingDestroyed）：排给下一个模拟步，自动重建在那里处理。</summary>
        public static void OnBuildingDestroyed(CampaignState state, BuildingRecord b)
        {
            if (state == null || b == null)
            {
                return;
            }
            Enqueue(state, new RuleEventRecord { Kind = EventDestroyed, EntityId = b.BuildingId, Tick = GameClock.Ticks });
            RequestEvaluation(state); // 被摧毁的建筑可能是规则的目标：立刻重新检查（目标不存在的原因、持有放手）。
        }

        /// <summary>远征队返回家园（ExpeditionReturnService 撤离确认之后）：排给下一个模拟步，远征卸货在那里处理。</summary>
        public static void OnExpeditionReturned(CampaignState state, IReadOnlyList<int> machines)
        {
            if (state == null)
            {
                return;
            }
            var ids = new int[machines?.Count ?? 0];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = machines[i];
            }
            Enqueue(state, new RuleEventRecord { Kind = EventExpedition, Machines = ids, Tick = GameClock.Ticks });
        }

        private static void Enqueue(CampaignState state, RuleEventRecord e)
        {
            StandingRuleState d = Domain(state);
            var list = new List<RuleEventRecord>(d.Pending) { e };
            d.Pending = list.ToArray();
        }

        // ── 新建 / 复制 / 删除 / 启停 / 优先级 ──────────────────────────────────────

        /// <summary>按类型新建一条规则（默认值来自 fg.TbRuleKind / 调参）；设置完整就直接启用，缺目标就先停用并写明（FGR-BASE-020：只有玩家显式设定的规则才执行）。</summary>
        public static bool TryCreate(CampaignState state, string kind, out StandingRuleRecord rule, out string message)
        {
            rule = null;
            StandingRuleState d = Domain(state);
            RuleKind k = KindRow(kind);
            if (d == null || k == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (d.Rules.Length >= MaxRules)
            {
                message = GameText.Format("rules.msg.too_many", MaxRules);
                return false;
            }
            if (KindLockedMessage(state, kind) is string locked)
            {
                message = locked; // FG6-DEF-03：研发节点解锁的规则类型（自动重建 ← 防御 · 自动重建）
                return false;
            }
            rule = new StandingRuleRecord
            {
                Serial = d.NextSerial++,
                Kind = kind,
                Priority = NextPriority(d),
                Threshold = k.ThresholdKey != "none" ? k.ThresholdDefault : 0,
                Batch = kind == KindSupply ? SupplyBatchDefault : 0,
                BoostRepair = kind == KindWar,
            };
            Append(d, rule);
            rule.Enabled = ConfigIssue(state, rule, out _, out _) == null;
            AfterEdit(state, rule);
            GuidanceHooks.Raise(GuidanceHooks.RulesFirstCreated);
            message = GameText.Format(rule.Enabled ? "rules.msg.created" : "rules.msg.created_off", Label(rule));
            return true;
        }

        /// <summary>从常用预设新建（物品 / 数值 / 配方 / 维修优先按预设；目标由玩家选）。</summary>
        public static bool TryCreateFromPreset(CampaignState state, string presetId, out StandingRuleRecord rule, out string message)
        {
            rule = null;
            RulePreset p = null;
            foreach (RulePreset x in Presets)
            {
                if (x.Id == presetId)
                {
                    p = x;
                }
            }
            if (p == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (!TryCreate(state, p.Kind, out rule, out message))
            {
                return false;
            }
            rule.ItemId = p.ItemId == "none" ? string.Empty : p.ItemId;
            if (HasThreshold(p.Kind))
            {
                rule.Threshold = p.Threshold;
            }
            if (p.Kind == KindSupply && p.Batch > 0)
            {
                rule.Batch = Math.Min(SupplyBatchMax, p.Batch);
            }
            rule.RecipeId = p.RecipeId == "none" ? string.Empty : p.RecipeId;
            if (p.Kind == KindWar)
            {
                rule.BoostRepair = p.BoostRepair == 1;
            }
            rule.Enabled = ConfigIssue(state, rule, out _, out _) == null;
            message = GameText.Format(rule.Enabled ? "rules.msg.created" : "rules.msg.created_off", Label(rule));
            AfterEdit(state, rule);
            return true;
        }

        /// <summary>复制（卡片“规则的复制”）：同样的设置，新编号，排在原规则正下方；先停用（两条一样的规则同时启用必然互相冲突），改好目标再启用。</summary>
        public static bool TryCopy(CampaignState state, int serial, out StandingRuleRecord copy, out string message)
        {
            copy = null;
            StandingRuleState d = Domain(state);
            StandingRuleRecord src = Find(state, serial);
            if (src == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (d.Rules.Length >= MaxRules)
            {
                message = GameText.Format("rules.msg.too_many", MaxRules);
                return false;
            }
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null && r.Priority > src.Priority)
                {
                    r.Priority++;
                }
            }
            copy = new StandingRuleRecord
            {
                Serial = d.NextSerial++,
                Kind = src.Kind,
                Enabled = false,
                Priority = src.Priority + 1,
                ItemId = src.ItemId,
                Threshold = src.Threshold,
                Batch = src.Batch,
                RecipeId = src.RecipeId,
                Targets = (string[])src.Targets.Clone(),
                Machines = (int[])src.Machines.Clone(),
                PointId = src.PointId,
                BoostRepair = src.BoostRepair,
                BoostDefenseSupply = src.BoostDefenseSupply, // FG6-DEF-06
                Zones = CopyZones(d, src.Zones), // FG6-DEF-03：重建区域一起复制（新编号）
            };
            Append(d, copy);
            Normalize(d);
            AfterEdit(state);
            message = GameText.Format("rules.msg.copied", Label(copy));
            return true;
        }

        /// <summary>删除（面板先确认，B04）：它持有的改动先恢复原样，排队的重建等待一并撤掉。</summary>
        public static bool TryDelete(CampaignState state, int serial, out string message)
        {
            StandingRuleState d = Domain(state);
            StandingRuleRecord r = Find(state, serial);
            if (r == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            ReleaseAllOf(state, r, log: true);
            var rules = new List<StandingRuleRecord>(d.Rules);
            rules.Remove(r);
            d.Rules = rules.ToArray();
            var pending = new List<RuleEventRecord>();
            foreach (RuleEventRecord e in d.Pending)
            {
                if (e != null && !(e.Kind == EventRebuildRetry && e.Rule == serial))
                {
                    pending.Add(e);
                }
            }
            d.Pending = pending.ToArray();
            Normalize(d);
            AfterEdit(state);
            message = GameText.Format("rules.msg.deleted", Label(r));
            return true;
        }

        /// <summary>启用 / 停用（可逆，不弹确认）。启用前设置必须完整；停用时它持有的改动恢复原样。</summary>
        public static bool TrySetEnabled(CampaignState state, int serial, bool enabled, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (r == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (enabled && KindLockedMessage(state, r.Kind) is string locked)
            {
                message = locked; // FG6-DEF-03：没研究就不能启用（已经启用着的旧规则照常运转，不替玩家停掉）
                return false;
            }
            if (enabled)
            {
                string issue = ConfigIssue(state, r, out string issueKey, out string issueArg);
                if (issue != null)
                {
                    message = GameText.Format("rules.msg.cannot_enable", Label(r), issue);
                    return false;
                }
                r.Enabled = true;
                message = GameText.Format("rules.msg.enabled", Label(r));
            }
            else
            {
                r.Enabled = false;
                ReleaseAllOf(state, r, log: true);
                r.Active = false;
                r.ConflictWith = 0;
                r.ConflictEntity = string.Empty;
                message = GameText.Format("rules.msg.disabled", Label(r));
            }
            AfterEdit(state, enabled ? r : null);
            return true;
        }

        /// <summary>上移 / 下移一位（与相邻的规则交换优先级）。</summary>
        public static bool TryMove(CampaignState state, int serial, int delta, out string message)
        {
            StandingRuleState d = Domain(state);
            List<StandingRuleRecord> list = Ordered(state);
            int i = list.FindIndex(x => x.Serial == serial);
            if (i < 0)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            int j = i + Math.Sign(delta);
            if (delta == 0 || j < 0 || j >= list.Count)
            {
                message = GameText.Format("rules.msg.moved", Label(list[i]), list[i].Priority);
                return false;
            }
            (list[i], list[j]) = (list[j], list[i]);
            for (int k = 0; k < list.Count; k++)
            {
                list[k].Priority = k + 1;
            }
            d.Rules = list.ToArray();
            AfterEdit(state);
            StandingRuleRecord moved = Find(state, serial);
            message = GameText.Format("rules.msg.moved", Label(moved), moved.Priority);
            return true;
        }

        private static int NextPriority(StandingRuleState d)
        {
            int max = 0;
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null)
                {
                    max = Math.Max(max, r.Priority);
                }
            }
            return max + 1;
        }

        private static void Append(StandingRuleState d, StandingRuleRecord r)
        {
            var list = new List<StandingRuleRecord>(d.Rules) { r };
            d.Rules = list.ToArray();
        }

        /// <summary>优先级重新编成 1..N（保持顺序）。</summary>
        private static void Normalize(StandingRuleState d)
        {
            var list = new List<StandingRuleRecord>();
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null)
                {
                    list.Add(r);
                }
            }
            list.Sort(Compare);
            for (int k = 0; k < list.Count; k++)
            {
                list[k].Priority = k + 1;
            }
            d.Rules = list.ToArray();
        }

        /// <param name="sweepFor">
        /// FG6-DEF-03 复审修复（P2 补排触发面）：只有玩家启用了自动重建规则、放宽了它的范围（加建筑类型 / 去掉最后一个类型 = 全部）、
        /// 圈了 / 打开了 / 删掉最后一个重建区域时才传这条规则，下一个模拟步补排已经被摧毁、现在落在范围里的东西。
        /// 改别的规则（库存阈值、优先级、删除、停用……）不补排，玩家取消过的重建单不会被这些无关操作重新派出。
        /// </param>
        private static void AfterEdit(CampaignState state, StandingRuleRecord sweepFor = null)
        {
            RequestEvaluation(state);
            if (sweepFor != null && sweepFor.Enabled && sweepFor.Kind == KindRebuild)
            {
                RequestRebuildSweep(state);
            }
            Revision++;
        }

        // ── 设置项（面板控件与自检同一入口）────────────────────────────────────────

        public static bool TrySetItem(CampaignState state, int serial, string itemId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindStock, KindSupply))
            {
                return false;
            }
            if (!ItemCatalog.TryGet(itemId, out ItemDef item) || !HomeInventory.IsStorable(item))
            {
                message = GameText.Format("rules.msg.bad_target", itemId);
                return false;
            }
            r.ItemId = itemId;
            if (r.Kind == KindStock && !string.IsNullOrEmpty(r.RecipeId) && !RecipeProduces(r.RecipeId, itemId))
            {
                r.RecipeId = AutoRecipe(state, r) ?? string.Empty; // 换了物品：配方按工厂自动换成产出它的那一条（没有就清空，原因会写明）。
            }
            return Edited(state, r, out message);
        }

        public static bool TrySetThreshold(CampaignState state, int serial, int value, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            RuleKind k = r != null ? KindRow(r.Kind) : null;
            if (r == null || k == null || k.ThresholdKey == "none")
            {
                message = GameText.Get(r == null ? "rules.msg.not_found" : "rules.msg.wrong_kind");
                return false;
            }
            if (value < k.ThresholdMin || value > k.ThresholdMax)
            {
                message = GameText.Format("rules.msg.bad_value", k.ThresholdMin, k.ThresholdMax);
                return false;
            }
            r.Threshold = value;
            return Edited(state, r, out message);
        }

        public static bool TrySetBatch(CampaignState state, int serial, int value, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindSupply))
            {
                return false;
            }
            if (value < 1 || value > SupplyBatchMax)
            {
                message = GameText.Format("rules.msg.bad_value", 1, SupplyBatchMax);
                return false;
            }
            r.Batch = value;
            return Edited(state, r, out message);
        }

        /// <summary>库存维持：选工厂（只有一座）。配方自动换成这座工厂能做、产出该物品的第一条（多条时可再选）。</summary>
        public static bool TrySetFactory(CampaignState state, int serial, string buildingId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindStock))
            {
                return false;
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || !ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def) || def.Mode != ProducerMode.Recipe || def.FixedRecipe != null)
            {
                message = GameText.Format("rules.msg.bad_target", b != null ? BuildingOps.NameOf(b) : buildingId);
                return false;
            }
            r.Targets = new[] { buildingId };
            if (string.IsNullOrEmpty(r.RecipeId) || !def.AllowsRecipe(ItemCatalog.TryGetRecipe(r.RecipeId, out RecipeDef cur) ? cur : null)
                || !RecipeProduces(r.RecipeId, r.ItemId))
            {
                r.RecipeId = AutoRecipe(state, r) ?? string.Empty;
            }
            return Edited(state, r, out message);
        }

        public static bool TrySetRecipe(CampaignState state, int serial, string recipeId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindStock))
            {
                return false;
            }
            if (!ItemCatalog.TryGetRecipe(recipeId, out RecipeDef recipe))
            {
                message = GameText.Format("rules.msg.bad_target", recipeId);
                return false;
            }
            r.RecipeId = recipeId;
            return Edited(state, r, out message);
        }

        /// <summary>加一个建筑目标（阈值补给对象 / 战时暂停 / 静默夜储能站 / 自动重建范围的建筑类型）。</summary>
        public static bool TryAddTarget(CampaignState state, int serial, string id, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindSupply, KindWar, KindSilent, KindRebuild))
            {
                return false;
            }
            if (string.IsNullOrEmpty(id) || Array.IndexOf(r.Targets, id) >= 0)
            {
                message = GameText.Get("rules.msg.already");
                return false;
            }
            string bad = TargetProblem(state, r, id);
            if (bad != null)
            {
                message = GameText.Format("rules.msg.bad_target", bad);
                return false;
            }
            var list = new List<string>(r.Targets) { id };
            r.Targets = list.ToArray();
            return Edited(state, r, out message, sweep: r.Kind == KindRebuild); // 自动重建：范围放宽 → 补排
        }

        public static bool TryRemoveTarget(CampaignState state, int serial, string id, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (r == null || Array.IndexOf(r.Targets, id) < 0)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            var list = new List<string>(r.Targets);
            list.Remove(id);
            r.Targets = list.ToArray();
            return Edited(state, r, out message, sweep: r.Kind == KindRebuild && r.Targets.Length == 0); // 去掉最后一个类型 = 全部建筑（范围放宽）
        }

        public static bool TryAddMachine(CampaignState state, int serial, int logicId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindSilent, KindRepair))
            {
                return false;
            }
            if (Array.IndexOf(r.Machines, logicId) >= 0)
            {
                message = GameText.Get("rules.msg.already");
                return false;
            }
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || !m.IsAlive)
            {
                message = GameText.Format("rules.msg.bad_target", MachineNaming.Short(logicId)); // FG4-ECO-07：名字同源
                return false;
            }
            var list = new List<int>(r.Machines) { logicId };
            r.Machines = list.ToArray();
            return Edited(state, r, out message);
        }

        public static bool TryRemoveMachine(CampaignState state, int serial, int logicId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (r == null || Array.IndexOf(r.Machines, logicId) < 0)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            var list = new List<int>(r.Machines);
            list.Remove(logicId);
            r.Machines = list.ToArray();
            return Edited(state, r, out message);
        }

        /// <summary>静默夜驻防点 / 远征卸货的目的地（建筑 ID；空 = 归还核心 / 共用库存）。</summary>
        public static bool TrySetPoint(CampaignState state, int serial, string buildingId, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindSilent, KindUnload))
            {
                return false;
            }
            buildingId ??= string.Empty;
            if (buildingId.Length > 0)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
                bool storage = b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
                if (b == null || (r.Kind == KindUnload && !storage))
                {
                    message = GameText.Format("rules.msg.bad_target", b != null ? BuildingOps.NameOf(b) : buildingId);
                    return false;
                }
            }
            r.PointId = buildingId;
            return Edited(state, r, out message);
        }

        public static bool TrySetBoost(CampaignState state, int serial, bool boost, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindWar))
            {
                return false;
            }
            r.BoostRepair = boost;
            return Edited(state, r, out message);
        }

        // ── FG6-DEF-06（承接 DEBT-FG4ECO06-03）：战时预案的“突袭时炮塔 / 陷阱补给优先” ─────────────────────────────

        /// <summary>开 / 关战时预案的“突袭时炮塔 / 陷阱补给优先”。</summary>
        public static bool TrySetDefenseSupplyBoost(CampaignState state, int serial, bool boost, out string message)
        {
            StandingRuleRecord r = Find(state, serial);
            if (!Editable(r, out message, KindWar))
            {
                return false;
            }
            r.BoostDefenseSupply = boost;
            if (boost && !r.Enabled && ConfigIssue(state, r, out _, out _) == null)
            {
                r.Enabled = true; // 只缺动作而停用的预案：补上这一项就能执行（与新建时“设置完整就启用”同一口径）
            }
            return Edited(state, r, out message);
        }

        /// <summary>
        /// 防御总览的一键开关，与按钮显示的状态（<see cref="DefenseSupplyBoostConfigured"/>）同一口径（审查修复 P2）：
        /// 显示“开”（有启用且打开了这一项的战时预案）→ 关掉所有启用的战时预案的这一项；显示“关” → 打开第一条（按优先级）启用的战时预案的这一项，
        /// 没有启用的就用第一条战时预案（原先停用的一并启用，提示里写明），一条都没有就新建一条只开这一项的战时预案（维修优先关、不暂停建筑）。
        /// 返回切换后的状态。可逆操作，不弹确认（B04）。
        /// </summary>
        public static bool ToggleDefenseSupplyBoost(CampaignState state, out bool nowOn, out string message)
        {
            nowOn = false;
            if (DefenseSupplyBoostConfigured(state, out _))
            {
                var labels = new List<string>();
                foreach (StandingRuleRecord r in Ordered(state))
                {
                    if (r.Kind == KindWar && r.Enabled && r.BoostDefenseSupply && TrySetDefenseSupplyBoost(state, r.Serial, false, out _))
                    {
                        labels.Add(Label(r));
                    }
                }
                nowOn = DefenseSupplyBoostConfigured(state, out _);
                message = GameText.Format("rules.msg.boost_supply_off_all", labels.Count, string.Join(GameText.Get("rules.sep_list"), labels));
                return labels.Count > 0;
            }
            StandingRuleRecord war = null;
            foreach (StandingRuleRecord r in Ordered(state))
            {
                if (r.Kind == KindWar && r.Enabled)
                {
                    war = r;
                    break;
                }
            }
            if (war == null)
            {
                foreach (StandingRuleRecord r in Ordered(state))
                {
                    if (r.Kind == KindWar)
                    {
                        war = r;
                        break;
                    }
                }
            }
            if (war == null)
            {
                if (!TryCreate(state, KindWar, out war, out message))
                {
                    return false;
                }
                war.BoostRepair = false;
                war.BoostDefenseSupply = true;
                war.Enabled = ConfigIssue(state, war, out _, out _) == null;
                AfterEdit(state, war);
                nowOn = true;
                message = GameText.Format("rules.msg.boost_supply_created", Label(war));
                return true;
            }
            bool wasDisabled = !war.Enabled;
            bool ok = TrySetDefenseSupplyBoost(state, war.Serial, true, out message);
            if (ok && wasDisabled && war.Enabled)
            {
                message = GameText.Format("rules.msg.boost_supply_reenabled", message);
            }
            nowOn = DefenseSupplyBoostConfigured(state, out _);
            return ok;
        }

        /// <summary>
        /// 现在把炮塔 / 陷阱补给优先级提到最高的战时预案编号（0 = 没有）：启用、正在执行（突袭进行中）、打开了这一项的第一条（按优先级）。
        /// 炮塔 / 陷阱的对账每轮调用一次（O(规则数) ≤ rules.max_rules）。
        /// </summary>
        public static int DefenseSupplyBoostSerial(CampaignState state)
        {
            foreach (StandingRuleRecord r in Domain(state)?.Rules ?? Array.Empty<StandingRuleRecord>())
            {
                if (r != null && r.Enabled && r.Kind == KindWar && r.Active && r.BoostDefenseSupply)
                {
                    return r.Serial;
                }
            }
            return 0;
        }

        /// <summary>防御总览的开关状态：有没有打开了这一项的战时预案（不管现在有没有突袭）；<paramref name="serial"/> = 那一条。</summary>
        public static bool DefenseSupplyBoostConfigured(CampaignState state, out int serial)
        {
            serial = 0;
            foreach (StandingRuleRecord r in Ordered(state))
            {
                if (r.Kind == KindWar && r.Enabled && r.BoostDefenseSupply)
                {
                    serial = r.Serial;
                    return true;
                }
            }
            return false;
        }

        /// <summary>“规则 R3（战时预案）”这样的来源说明（炮塔补给一行写明是哪条规则提的优先级，FGR-BASE-020）。</summary>
        public static string RuleLabel(CampaignState state, int serial)
        {
            StandingRuleRecord r = Find(state, serial);
            return r != null ? Label(r) : string.Empty;
        }

        private static bool Editable(StandingRuleRecord r, out string message, params string[] kinds)
        {
            if (r == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (Array.IndexOf(kinds, r.Kind) < 0)
            {
                message = GameText.Get("rules.msg.wrong_kind");
                return false;
            }
            message = null;
            return true;
        }

        private static bool Edited(CampaignState state, StandingRuleRecord r, out string message, bool sweep = false)
        {
            AfterEdit(state, sweep ? r : null);
            message = GameText.Format("rules.msg.updated", Label(r));
            return true;
        }

        private static bool RecipeProduces(string recipeId, string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || !ItemCatalog.TryGetRecipe(recipeId, out RecipeDef rd))
            {
                return false;
            }
            foreach (RecipeLine l in rd.Lines)
            {
                if (l.Role == RecipeRole.Out && l.Item.Id == itemId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>工厂这一类能做、产出规则物品的配方（按配方表顺序取第一条）。</summary>
        public static string AutoRecipe(CampaignState state, StandingRuleRecord r)
        {
            BuildingRecord b = r.Targets.Length > 0 ? HomeGridService.FindBuilding(state, r.Targets[0]) : null;
            if (b == null || !ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def))
            {
                return null;
            }
            foreach (RecipeDef rd in def.Recipes)
            {
                if (RecipeProduces(rd.Id, r.ItemId))
                {
                    return rd.Id;
                }
            }
            return null;
        }

        /// <summary>这个目标为什么不能加（null = 可以）。</summary>
        private static string TargetProblem(CampaignState state, StandingRuleRecord r, string id)
        {
            if (r.Kind == KindRebuild)
            {
                return id == BeltTarget || GridContent.Building(id) != null ? null : id; // FG6-DEF-03：“传送带与物流节点”也可以选
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, id);
            if (b == null)
            {
                return id;
            }
            switch (r.Kind)
            {
                case KindSupply:
                    return ProducerCatalog.IsProducer(b.BuildingTypeId) ? null : BuildingOps.NameOf(b);
                case KindWar:
                    return BuildingOps.CanDisableType(b.BuildingTypeId) && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore ? null : BuildingOps.NameOf(b);
                case KindSilent:
                    return HomeValleyPowerGrid.IsStorageType(b.BuildingTypeId) ? null : BuildingOps.NameOf(b);
                default:
                    return BuildingOps.NameOf(b);
            }
        }

        // ── 设置是否完整（启用前）与这一刻的原因 ──────────────────────────────────────

        /// <summary>设置缺了什么（null = 完整，可以启用）。缺目标、缺物品、配方不对都算；目标暂时不存在（被拆）也算——写明哪一座。</summary>
        public static string ConfigIssue(CampaignState state, StandingRuleRecord r, out string key, out string arg)
        {
            key = null;
            arg = string.Empty;
            switch (r.Kind)
            {
                case KindStock:
                {
                    if (string.IsNullOrEmpty(r.ItemId))
                    {
                        key = "rules.issue.no_item";
                        break;
                    }
                    if (r.Targets.Length == 0)
                    {
                        key = "rules.issue.no_target";
                        break;
                    }
                    BuildingRecord b = HomeGridService.FindBuilding(state, r.Targets[0]);
                    if (b == null || b.ConstructionState == BuildingConstructionState.Damaged)
                    {
                        key = "rules.issue.target_missing";
                        arg = "@b:" + r.Targets[0];
                        break;
                    }
                    if (!ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def) || !ItemCatalog.TryGetRecipe(r.RecipeId, out RecipeDef rd)
                        || !def.AllowsRecipe(rd) || !RecipeProduces(rd.Id, r.ItemId))
                    {
                        key = "rules.issue.recipe_invalid";
                        arg = "@b:" + b.BuildingId + "|@recipe:" + r.RecipeId;
                    }
                    break;
                }
                case KindSupply:
                {
                    if (string.IsNullOrEmpty(r.ItemId))
                    {
                        key = "rules.issue.no_item";
                        break;
                    }
                    if (r.Targets.Length == 0)
                    {
                        key = "rules.issue.no_target";
                        break;
                    }
                    // 审查修复（P1）：多目标规则只有“一个可执行的目标都没有”时才整条不能执行；部分目标被拆 / 被毁时其余目标照常补给（原因由 CollectSupply 写明缺的那一座）。
                    string missing = FirstMissingTarget(state, r, damagedCounts: true, out bool anyUsable);
                    if (!anyUsable)
                    {
                        key = "rules.issue.target_missing";
                        arg = "@b:" + missing;
                    }
                    break;
                }
                case KindWar:
                {
                    bool hasAction = r.BoostRepair || r.BoostDefenseSupply; // FG6-DEF-06：补给优先也是一项动作
                    if (!hasAction && r.Targets.Length == 0)
                    {
                        key = "rules.issue.war_empty";
                        break;
                    }
                    // 维修优先本身就是一项动作：打开时目标全没了也照常执行；只暂停建筑的预案目标全没了才不能执行。
                    string missing = FirstMissingTarget(state, r, damagedCounts: false, out bool anyUsable);
                    if (!hasAction && !anyUsable)
                    {
                        key = "rules.issue.target_missing";
                        arg = "@b:" + missing;
                    }
                    break;
                }
                case KindSilent:
                {
                    if (r.Machines.Length == 0 && r.Targets.Length == 0)
                    {
                        key = "rules.issue.no_action";
                        break;
                    }
                    string missing = FirstMissingTarget(state, r, damagedCounts: false, out bool anyUsable);
                    if (r.Machines.Length == 0 && !anyUsable)
                    {
                        key = "rules.issue.target_missing";
                        arg = "@b:" + missing;
                    }
                    break;
                }
            }
            return key == null ? null : FormatArgs(key, arg);
        }

        /// <summary>规则目标里第一座不存在（<paramref name="damagedCounts"/> 时被摧毁的也算）的建筑 ID；没有返回 null。<paramref name="anyUsable"/> = 至少有一座还能执行。O(目标数)。</summary>
        private static string FirstMissingTarget(CampaignState state, StandingRuleRecord r, bool damagedCounts, out bool anyUsable)
        {
            string missing = null;
            anyUsable = false;
            foreach (string t in r.Targets)
            {
                if (TargetMissing(state, t, damagedCounts))
                {
                    missing ??= t;
                }
                else
                {
                    anyUsable = true;
                }
            }
            return missing;
        }

        private static bool TargetMissing(CampaignState state, string buildingId, bool damagedCounts)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            return b == null || (damagedCounts && b.ConstructionState == BuildingConstructionState.Damaged);
        }

        // ── 检查（三段：要什么 → 放手 → 执行）──────────────────────────────────────

        private readonly struct Want
        {
            public readonly StandingRuleRecord Rule;
            public readonly string Kind;
            public readonly string Key;
            public readonly string Target;
            public readonly int Machine;

            public Want(StandingRuleRecord rule, string kind, string key, string target, int machine)
            {
                Rule = rule;
                Kind = kind;
                Key = key;
                Target = target;
                Machine = machine;
            }
        }

        private static readonly List<Want> Wants = new List<Want>(32);
        private static readonly Dictionary<string, int> WantIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<int, (int Winner, string Key)> Conflicts = new Dictionary<int, (int, string)>();
        private static readonly List<StandingRuleRecord> Scratch = new List<StandingRuleRecord>(16);

        /// <summary>
        /// 检查全部规则一次（O(规则数 × 目标数 + 家园机器数 + 持有数)；只在定时检查步 / 改动后的下一步跑，不每帧）。
        /// </summary>
        public static void Evaluate(CampaignState state)
        {
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Wants.Clear();
            WantIndex.Clear();
            Conflicts.Clear();
            Scratch.Clear();
            Scratch.AddRange(Ordered(state));

            // ① 每条规则这一刻要控制什么（同一实体只给优先级最高的那条）。
            foreach (StandingRuleRecord r in Scratch)
            {
                if (!r.Enabled)
                {
                    continue;
                }
                string issue = ConfigIssue(state, r, out string issueKey, out string issueArg);
                if (issue != null)
                {
                    SetIssue(state, r, issueKey, issueArg);
                    SetActive(state, r, false, null);
                    continue;
                }
                switch (r.Kind)
                {
                    case KindStock:
                        CollectStock(state, r);
                        break;
                    case KindSupply:
                        CollectSupply(state, r);
                        break;
                    case KindWar:
                        CollectWar(state, r);
                        break;
                    case KindSilent:
                        CollectSilent(state, r);
                        break;
                    case KindRepair:
                        CollectRepair(state, r);
                        break;
                    default:
                        break; // 远征卸货 / 自动重建是事件类：原因由事件处理写和清（等材料的重建），定时检查不动它。
                }
            }
            // 冲突：只在变化时写日志 / 提醒。
            foreach (StandingRuleRecord r in Scratch)
            {
                Conflicts.TryGetValue(r.Serial, out (int Winner, string Key) c);
                if (c.Winner != r.ConflictWith || (c.Winner != 0 && c.Key != r.ConflictEntity))
                {
                    r.ConflictWith = c.Winner;
                    r.ConflictEntity = c.Key ?? string.Empty;
                    Revision++;
                    if (c.Winner != 0)
                    {
                        string entity = EntityArg(c.Key);
                        AddLog(state, r.Serial, "rules.log.conflict", c.Key, LabelArg(r.Serial), LabelArg(c.Winner), entity);
                        NotificationCenter.Post("standing_rule", Expand(state, "rules.log.conflict", new[] { LabelArg(r.Serial), LabelArg(c.Winner), entity }), PositionOf(state, c.Key));
                        GuidanceHooks.Raise(GuidanceHooks.RulesFirstConflict);
                    }
                }
            }

            // ② 不再需要的持有：恢复原样（被玩家改掉的只放手）；仍需要的：检查是不是被玩家改掉了。
            RuleHoldRecord[] holds = d.Holds;
            var keep = new List<RuleHoldRecord>(holds.Length);
            foreach (RuleHoldRecord h in holds)
            {
                if (h == null)
                {
                    continue;
                }
                bool wanted = WantIndex.TryGetValue(h.EntityId, out int wi) && Wants[wi].Rule.Serial == h.Rule && Wants[wi].Kind == h.Kind;
                HoldStatus st = Status(state, h);
                if (!wanted || st == HoldStatus.Done)
                {
                    if (!h.Overridden && st == HoldStatus.Applied)
                    {
                        Restore(state, h, log: true);
                    }
                    continue;
                }
                if (st == HoldStatus.Changed && !h.Overridden)
                {
                    h.Overridden = true;
                    AddLog(state, h.Rule, "rules.log.override", h.EntityId, LabelArg(h.Rule), EntityArg(h.EntityId));
                    Revision++;
                }
                else if (st == HoldStatus.Applied && !h.Overridden && h.Kind == HoldRecipe)
                {
                    FollowRecipeEdit(state, h, Wants[wi].Rule);
                }
                keep.Add(h);
            }
            d.Holds = keep.ToArray();

            // ③ 新需要的：执行并记下旧值。
            foreach (Want w in Wants)
            {
                if (FindHold(state, w.Key) != null)
                {
                    continue;
                }
                Apply(state, w);
            }

            EvaluationCount++;
            LastEvaluationMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastEvaluationMs > MaxEvaluationMs)
            {
                MaxEvaluationMs = LastEvaluationMs;
            }
        }

        /// <summary>
        /// 库存维持接管期间玩家在面板改了规则的配方（或换物品自动换了配方）：工厂跟着切到新配方、持有记下新值，旧值（接管前的配方）不变；
        /// 不算“玩家手动改动工厂”（审查修复 P2）。工厂停在玩家自己改的配方上时（持有已标让位）不走这里。
        /// </summary>
        private static void FollowRecipeEdit(CampaignState state, RuleHoldRecord h, StandingRuleRecord r)
        {
            if (string.IsNullOrEmpty(h.Applied))
            {
                h.Applied = r.RecipeId; // 旧持有：Status 已按规则当前设置判为“还在”，补记下来。
                return;
            }
            if (h.Applied == r.RecipeId)
            {
                return;
            }
            string building = h.EntityId.Substring(2);
            if (ProductionService.TrySetRecipeForRule(state, building, r.RecipeId, out _))
            {
                h.Applied = r.RecipeId;
                AddLog(state, r.Serial, "rules.log.stock_on", h.EntityId, LabelArg(r.Serial), "@item:" + r.ItemId, r.Threshold.ToString(CultureInfo.InvariantCulture),
                    HomeInventory.Stock(state, r.ItemId).ToString(CultureInfo.InvariantCulture), "@b:" + building, "@recipe:" + r.RecipeId);
                Revision++;
            }
        }

        private static void Wanted(StandingRuleRecord r, string kind, string key, string target = null, int machine = 0)
        {
            if (WantIndex.TryGetValue(key, out int i))
            {
                StandingRuleRecord winner = Wants[i].Rule;
                if (winner.Serial != r.Serial && !Conflicts.ContainsKey(r.Serial))
                {
                    Conflicts[r.Serial] = (winner.Serial, key);
                }
                return;
            }
            WantIndex[key] = Wants.Count;
            Wants.Add(new Want(r, kind, key, target, machine));
        }

        // ── 各类条件 ─────────────────────────────────────────────────────────────

        private static void CollectStock(CampaignState state, StandingRuleRecord r)
        {
            int have = HomeInventory.Stock(state, r.ItemId);
            bool active = have < r.Threshold;
            string factory = r.Targets[0];
            if (!active && r.Active)
            {
                // 补足：只有规则真的接管过这座工厂才写“恢复”（让给了别的规则 / 被玩家改掉的不写）。
                RuleHoldRecord h = FindHold(state, BuildingKey(factory));
                if (h != null && h.Rule == r.Serial && h.Kind == HoldRecipe && !h.Overridden)
                {
                    AddLog(state, r.Serial, "rules.log.stock_off", BuildingKey(factory), LabelArg(r.Serial), "@item:" + r.ItemId,
                        have.ToString(CultureInfo.InvariantCulture), "@b:" + factory, "@recipe:" + h.Prev);
                }
            }
            SetActive(state, r, active, null);
            SetIssue(state, r, null, null);
            if (active)
            {
                Wanted(r, HoldRecipe, BuildingKey(factory), factory);
            }
        }

        private static void CollectSupply(CampaignState state, StandingRuleRecord r)
        {
            ItemCatalog.TryGet(r.ItemId, out ItemDef item);
            string issueKey = null;
            string issueArg = null;
            string missing = null;
            foreach (string t in r.Targets)
            {
                if (TargetMissing(state, t, damagedCounts: true))
                {
                    // 审查修复（P1）：这一座被拆 / 被摧毁——只跳过它（它的补给单不再要，第②段取消并退回），其余目标照常补给；原因写明缺的是哪一座。
                    missing ??= t;
                    continue;
                }
                if (!ProductionService.TryGet(state, t, out ProductionService.Producer p))
                {
                    continue;
                }
                if (!Consumes(p, r.ItemId))
                {
                    issueKey = "rules.issue.not_consumer";
                    issueArg = "@b:" + t + "|@item:" + r.ItemId;
                    continue;
                }
                int have = ProductionService.Count(p.Rec.In, r.ItemId);
                string key = SupplyKey(r.Serial, t);
                RuleHoldRecord existing = FindHold(state, key);
                if (have >= r.Threshold && existing == null)
                {
                    continue;
                }
                if (existing == null && HomeInventory.Stock(state, item) <= 0)
                {
                    issueKey = "rules.issue.no_stock";
                    issueArg = "@item:" + r.ItemId;
                    continue;
                }
                Wanted(r, HoldSupply, key, t);
            }
            if (missing != null)
            {
                SetIssue(state, r, "rules.issue.target_missing", "@b:" + missing); // 缺目标优先写（玩家要知道哪一座没了）；只在变化时提醒一次。
            }
            else
            {
                SetIssue(state, r, issueKey, issueArg);
            }
        }

        private static bool Consumes(ProductionService.Producer p, string itemId)
        {
            RecipeDef rd = p.Recipe ?? p.Def.FixedRecipe;
            if (rd == null)
            {
                return false;
            }
            foreach (RecipeLine l in rd.Lines)
            {
                if (l.Role == RecipeRole.In && l.Item.Id == itemId && l.Item.Form != ItemForm.Fluid)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 默认的突袭判定：有目标为家园的突袭部队已经到达、还没撤退。FG6-DEF-04 复审：突袭导演可以把前哨站当目标——打远处前哨站的突袭不启动家园战时预案
        /// （目标种类为空 = 测试 / 调试派出或旧档的队伍，目标都是家园）。O(行进中的队伍数)。
        /// </summary>
        public static bool DefaultRaidActive(CampaignState state)
        {
            foreach (TransitGroupRecord g in state?.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>())
            {
                if (g != null && g.Kind == TransitGroupKind.Raid && g.State == TransitGroupState.Arrived && g.TargetKind != Defense.RaidDirectorService.TargetOutpost)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool RaidActive(CampaignState state) => RaidActiveProvider != null ? RaidActiveProvider(state) : DefaultRaidActive(state);

        private static void CollectWar(CampaignState state, StandingRuleRecord r)
        {
            bool active = RaidActive(state);
            if (active && !r.Active)
            {
                SetActive(state, r, true, null);
                Fired(state, r, "rules.log.war_on", null, LabelArg(r.Serial));
                if (r.BoostDefenseSupply)
                {
                    // FG6-DEF-06：补给优先由炮塔 / 陷阱的对账按 DefenseSupplyBoostSerial 落到管线消费者（下一轮对账生效，突袭结束自动恢复）；这里写明提了几个补给口。
                    AddLog(state, r.Serial, "rules.log.supply_boost", null, LabelArg(r.Serial),
                        Defense.TurretService.SupplyConsumerCount(state).ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (!active && r.Active)
            {
                SetActive(state, r, false, null);
                AddLog(state, r.Serial, "rules.log.war_off", null, LabelArg(r.Serial));
            }
            // 审查修复（P1）：突袭中某一座目标被拆——只跳过它、写明原因；其余目标继续暂停、维修优先照常（不因一座没了就整条恢复）。
            string missing = FirstMissingTarget(state, r, damagedCounts: false, out _);
            if (missing != null)
            {
                SetIssue(state, r, "rules.issue.target_missing", "@b:" + missing);
            }
            else
            {
                SetIssue(state, r, null, null);
            }
            if (!active)
            {
                return;
            }
            foreach (string t in r.Targets)
            {
                if (TargetMissing(state, t, damagedCounts: false))
                {
                    continue;
                }
                Wanted(r, HoldDisabled, BuildingKey(t), t);
            }
            if (r.BoostRepair)
            {
                foreach (WorkOrderRecord o in state.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                {
                    if (o != null && o.Kind == WorkOrderKind.Repair && HomeValleyWorkOrders.IsActive(o))
                    {
                        Wanted(r, HoldBoost, OrderKey(o.WorkOrderId), o.WorkOrderId);
                    }
                }
            }
        }

        /// <summary>当前 / 下一个静默夜的时间窗；没有返回 false。</summary>
        public static bool TrySilentWindow(CampaignState state, out long start, out long end)
        {
            start = end = -1;
            if (SilentNightWindowProvider == null)
            {
                return false;
            }
            (long s, long e) = SilentNightWindowProvider(state);
            if (s < 0 || e <= s)
            {
                return false;
            }
            start = s;
            end = e;
            return true;
        }

        private static void CollectSilent(CampaignState state, StandingRuleRecord r)
        {
            long now = GameClock.Ticks;
            bool active = false;
            long start = -1;
            if (TrySilentWindow(state, out start, out long end))
            {
                long lead = (long)r.Threshold * Math.Max(1, GameClock.StepHz);
                active = now >= start - lead && now < end;
            }
            if (active && !r.Active)
            {
                SetActive(state, r, true, null);
                long secs = Math.Max(0L, (start - now) / Math.Max(1, GameClock.StepHz));
                Fired(state, r, "rules.log.silent_on", null, LabelArg(r.Serial), secs.ToString(CultureInfo.InvariantCulture));
            }
            else if (!active && r.Active)
            {
                SetActive(state, r, false, null);
                AddLog(state, r.Serial, "rules.log.silent_off", null, LabelArg(r.Serial));
            }
            // 审查修复（P1）：某一座储能站被拆——只跳过它、写明原因；其余储能站与驻防机器照常执行。
            string missing = FirstMissingTarget(state, r, damagedCounts: false, out _);
            if (!active)
            {
                SetIssue(state, r, missing != null ? "rules.issue.target_missing" : null, missing != null ? "@b:" + missing : null);
                return;
            }
            string point = r.PointId;
            if (!string.IsNullOrEmpty(point) && HomeGridService.FindBuilding(state, point) == null)
            {
                SetIssue(state, r, "rules.issue.point_missing", "@b:" + point);
            }
            else if (missing != null)
            {
                SetIssue(state, r, "rules.issue.target_missing", "@b:" + missing);
            }
            else
            {
                SetIssue(state, r, null, null);
            }
            foreach (int m in r.Machines)
            {
                // 要的是“这台机器”（活着、在家园）；玩家正在用它时执行那一步跳过——玩家用完了才派，派了以后玩家接管 = 这次不再派。
                if (MachineRegistry.TryGetRecord(m, out MachineRecord rec) && rec.IsAlive && rec.RegionId == HomeValleyLayout.RegionId)
                {
                    Wanted(r, HoldGarrison, MachineKey(m), null, m);
                }
            }
            foreach (string t in r.Targets)
            {
                if (TargetMissing(state, t, damagedCounts: false))
                {
                    continue;
                }
                Wanted(r, HoldStorage, BuildingKey(t), t);
            }
        }

        /// <summary>家园里能被规则派活的机器：活着、在家园、不在厂里、玩家没在直控 / 没在执行玩家命令。</summary>
        public static bool MachineEligible(int logicId, out MachineRecord m)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out m) || !m.IsAlive || m.IsInFactory || m.RegionId != HomeValleyLayout.RegionId)
            {
                return false;
            }
            Func<int, bool> taken = HomeValleyWorkOrders.TakenByPlayer;
            return taken == null || !taken(logicId);
        }

        /// <summary>
        /// FG4-ECO-07（FGR-ECO-040 / FGR-BASE-020）：岗位为“闲置”的机器规则不派它（闲置的机器不做任何事）——规则上写明原因（可追溯，不静默跳过）。
        /// </summary>
        private static bool RoleBlocks(CampaignState state, StandingRuleRecord r, int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || MachineRoster.AllowsRuleDispatch(m))
            {
                return false;
            }
            SetIssue(state, r, "rules.issue.machine_idle", "@m:" + logicId.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>伤势百分比（0～100）。</summary>
        public static int InjuryPercent(MachineRecord m) =>
            m == null || m.MaxHealth <= 0f ? 0 : Mathf.Clamp(Mathf.RoundToInt((1f - Mathf.Clamp01(m.Health / m.MaxHealth)) * 100f), 0, 100);

        private static readonly List<int> MachineScratch = new List<int>(16);

        private static void CollectRepair(CampaignState state, StandingRuleRecord r)
        {
            MachineScratch.Clear();
            if (r.Machines.Length > 0)
            {
                MachineScratch.AddRange(r.Machines);
            }
            else
            {
                foreach (MachineRecord m in MachineRegistry.AllRecords)
                {
                    if (m != null)
                    {
                        MachineScratch.Add(m.LogicId);
                    }
                }
            }
            MachineScratch.Sort();
            bool any = false;
            foreach (int id in MachineScratch)
            {
                string key = MachineKey(id);
                RuleHoldRecord h = FindHold(state, key);
                bool mine = h != null && h.Rule == r.Serial && h.Kind == HoldMachineRepair;
                bool repairing = mine && OrderActive(state, h.OrderId);
                bool injured = MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId
                               && InjuryPercent(m) > r.Threshold;
                // 正在修的继续要；被玩家接管过（持有还在）且仍然受伤的继续要（不再派，等伤势回到阈值以下才放手）；新受伤的要（执行时玩家正在用就跳过）。
                if (!repairing && !(mine && injured) && !injured)
                {
                    continue;
                }
                any = true;
                Wanted(r, HoldMachineRepair, key, null, id);
            }
            if (!any)
            {
                SetIssue(state, r, null, null);
            }
            r.Active = any;
        }

        // ── 持有：状态、执行、恢复 ────────────────────────────────────────────────

        private enum HoldStatus
        {
            /// <summary>规则设下的值还在。</summary>
            Applied,
            /// <summary>被玩家改掉了（不再接管）。</summary>
            Changed,
            /// <summary>这一项已经结束（工单完成 / 失败、实体没了），放手。</summary>
            Done,
        }

        private static HoldStatus Status(CampaignState state, RuleHoldRecord h)
        {
            switch (h.Kind)
            {
                case HoldRecipe:
                {
                    string id = h.EntityId.Substring(2);
                    if (!ProductionService.TryGet(state, id, out ProductionService.Producer p))
                    {
                        return HoldStatus.Done;
                    }
                    // 拿规则实际设下的配方比（审查修复 P2）：玩家在面板改了规则的配方 / 物品不算“手动改动工厂”。旧持有没记下时按规则当前设置比。
                    string applied = h.Applied;
                    if (string.IsNullOrEmpty(applied))
                    {
                        applied = Find(state, h.Rule)?.RecipeId;
                    }
                    return p.Recipe != null && !string.IsNullOrEmpty(applied) && p.Recipe.Id == applied ? HoldStatus.Applied : HoldStatus.Changed;
                }
                case HoldDisabled:
                {
                    BuildingRecord b = HomeGridService.FindBuilding(state, h.EntityId.Substring(2));
                    if (b == null || b.ConstructionState == BuildingConstructionState.Damaged)
                    {
                        return HoldStatus.Done;
                    }
                    return BuildingOps.IsDisabled(b) ? HoldStatus.Applied : HoldStatus.Changed;
                }
                case HoldStorage:
                {
                    string id = h.EntityId.Substring(2);
                    BuildingRecord b = HomeGridService.FindBuilding(state, id);
                    if (b == null)
                    {
                        return HoldStatus.Done;
                    }
                    StorageSettings s = HomeValleyPowerGrid.GetStorageSettings(state, id);
                    return s.NoCharge && !s.NoDischarge ? HoldStatus.Applied : HoldStatus.Changed;
                }
                case HoldBoost:
                {
                    WorkOrderRecord o = HomeValleyWorkOrders.Find(state, h.OrderId);
                    if (o == null || !HomeValleyWorkOrders.IsActive(o))
                    {
                        return HoldStatus.Done;
                    }
                    return o.Priority == WarRepairPriority ? HoldStatus.Applied : HoldStatus.Changed;
                }
                default:
                {
                    // 工单类（驻防 / 送修 / 补给）：进行中 = 还在；玩家取消 = 改掉了；完成 / 失败 = 结束。
                    WorkOrderRecord o = HomeValleyWorkOrders.Find(state, h.OrderId);
                    if (o == null)
                    {
                        return HoldStatus.Done;
                    }
                    if (HomeValleyWorkOrders.IsActive(o))
                    {
                        return HoldStatus.Applied;
                    }
                    if (o.State == WorkOrderState.Cancelled)
                    {
                        return HoldStatus.Changed;
                    }
                    // FG4-ECO-07 审查修复：送修 / 驻防到不了目标而失败——寻路判“无法到达”，或赶路看门狗判“路径持续受阻”
                    // （与 MachineRoster.GarrisonBlocked 同一口径）——持有按“仍在执行”保留，条件不变时不再补派
                    // （否则每次检查都开一张新单，机器原地“出发—卡住—失败—再出发”循环）；条件结束后照常收尾，下一次条件成立再试。
                    return o.State == WorkOrderState.Failed && IsCannotReach(o.FailureReason) ? HoldStatus.Applied : HoldStatus.Done;
                }
            }
        }

        /// <summary>送修 / 驻防单“到不了”的两种失败码：寻路无法到达（unreachable:*）与赶路看门狗判定的路径持续受阻（path-blocked）。</summary>
        private static bool IsCannotReach(string reason) =>
            HomeValleyWorkOrders.IsUnreachableReason(reason) || reason == HomeValleyWorkOrders.PathBlockedReason;

        private static bool OrderActive(CampaignState state, string orderId) =>
            !string.IsNullOrEmpty(orderId) && HomeValleyWorkOrders.IsActive(HomeValleyWorkOrders.Find(state, orderId));

        private static void AddHold(CampaignState state, StandingRuleRecord r, string kind, string key, string prev = "", int prevInt = 0, string orderId = "", string applied = "")
        {
            StandingRuleState d = Domain(state);
            var list = new List<RuleHoldRecord>(d.Holds)
            {
                new RuleHoldRecord
                {
                    Rule = r.Serial, Kind = kind, EntityId = key, Prev = prev ?? string.Empty, PrevInt = prevInt, SinceTick = GameClock.Ticks, OrderId = orderId ?? string.Empty,
                    Applied = applied ?? string.Empty,
                },
            };
            d.Holds = list.ToArray();
            Revision++;
        }

        private static string NextOrderId(CampaignState state, StandingRuleRecord r, string what)
        {
            StandingRuleState d = Domain(state);
            return "rule:R" + r.Serial.ToString(CultureInfo.InvariantCulture) + ":" + what + ":" + (d.NextOrderSerial++).ToString(CultureInfo.InvariantCulture);
        }

        private static void Apply(CampaignState state, Want w)
        {
            StandingRuleRecord r = w.Rule;
            switch (w.Kind)
            {
                case HoldRecipe:
                {
                    if (!ProductionService.TryGet(state, w.Target, out ProductionService.Producer p))
                    {
                        return;
                    }
                    string prev = p.Recipe?.Id ?? string.Empty;
                    // 走不记忆的入口：规则切配方不改写“这类建筑记住的上一次配方”（新建的同类建筑不会沿用规则的选择，FGR-BASE-020；审查修复 P2）。
                    if (prev != r.RecipeId && !ProductionService.TrySetRecipeForRule(state, w.Target, r.RecipeId, out string why))
                    {
                        SetIssue(state, r, "rules.issue.recipe_invalid", "@b:" + w.Target + "|@recipe:" + r.RecipeId);
                        return;
                    }
                    AddHold(state, r, HoldRecipe, w.Key, prev, applied: r.RecipeId);
                    Fired(state, r, "rules.log.stock_on", w.Key, LabelArg(r.Serial), "@item:" + r.ItemId, r.Threshold.ToString(CultureInfo.InvariantCulture),
                        HomeInventory.Stock(state, r.ItemId).ToString(CultureInfo.InvariantCulture), "@b:" + w.Target, "@recipe:" + r.RecipeId);
                    return;
                }
                case HoldDisabled:
                {
                    BuildingRecord b = HomeGridService.FindBuilding(state, w.Target);
                    if (b == null || BuildingOps.IsDisabled(b) || !BuildingOps.CanToggle(state, b, out _))
                    {
                        return; // 已经被玩家禁用 / 不能禁用（虚影、摧毁）：规则不碰它，突袭结束也不替玩家启用。
                    }
                    if (BuildingOps.TrySetEnabled(state, w.Target, false, out _))
                    {
                        AddHold(state, r, HoldDisabled, w.Key);
                        Fired(state, r, "rules.log.paused", w.Key, LabelArg(r.Serial), "@b:" + w.Target);
                    }
                    return;
                }
                case HoldStorage:
                {
                    StorageSettings s = HomeValleyPowerGrid.GetStorageSettings(state, w.Target);
                    int prevBits = (s.NoCharge ? 1 : 0) | (s.NoDischarge ? 2 : 0);
                    if (HomeValleyPowerGrid.TrySetStorageSettings(state, w.Target, noCharge: true, noDischarge: false).Success)
                    {
                        AddHold(state, r, HoldStorage, w.Key, string.Empty, prevBits);
                        Fired(state, r, "rules.log.discharge", w.Key, LabelArg(r.Serial), "@b:" + w.Target);
                    }
                    return;
                }
                case HoldBoost:
                {
                    WorkOrderRecord o = HomeValleyWorkOrders.Find(state, w.Target);
                    if (o == null || !HomeValleyWorkOrders.IsActive(o))
                    {
                        return;
                    }
                    int prev = o.Priority;
                    o.Priority = WarRepairPriority;
                    if (o.RuleSerial == 0)
                    {
                        o.RuleSerial = r.Serial; // 别的规则派的单子（自动重建）保留它自己的来源；提优先级另由持有追溯。
                    }
                    HomeValleyWorkOrders.MarkAssignmentDirty();
                    AddHold(state, r, HoldBoost, w.Key, string.Empty, prev, o.WorkOrderId);
                    Fired(state, r, "rules.log.boosted", BuildingKey(o.TargetId), LabelArg(r.Serial), "@b:" + o.TargetId);
                    return;
                }
                case HoldGarrison:
                {
                    if (RoleBlocks(state, r, w.Machine) || !MachineEligible(w.Machine, out _))
                    {
                        return;
                    }
                    string point = string.IsNullOrEmpty(r.PointId) || HomeGridService.FindBuilding(state, r.PointId) == null ? string.Empty : r.PointId;
                    string id = NextOrderId(state, r, "garrison");
                    HomeValleyWorkOrders.WorkOrderOpResult res = HomeValleyWorkOrders.TryCreateGarrison(state, id, w.Machine, point, r.Serial);
                    if (res.Success)
                    {
                        AddHold(state, r, HoldGarrison, w.Key, string.Empty, 0, res.WorkOrderId);
                        string pointArg = string.IsNullOrEmpty(point) ? "@b:" + HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeCore : "@b:" + point;
                        Fired(state, r, "rules.log.garrison", w.Key, LabelArg(r.Serial), "@m:" + w.Machine.ToString(CultureInfo.InvariantCulture), pointArg);
                    }
                    return;
                }
                case HoldMachineRepair:
                {
                    if (RoleBlocks(state, r, w.Machine) || !MachineEligible(w.Machine, out MachineRecord m))
                    {
                        return;
                    }
                    BuildingRecord bay = PickBay(state, m);
                    if (bay == null)
                    {
                        SetIssue(state, r, "rules.issue.no_bay", string.Empty);
                        return;
                    }
                    string id = NextOrderId(state, r, "repair");
                    HomeValleyWorkOrders.WorkOrderOpResult res = HomeValleyWorkOrders.TryCreateMachineRepair(state, id, w.Machine, bay.BuildingId, r.Serial);
                    if (res.Success)
                    {
                        SetIssue(state, r, null, null);
                        AddHold(state, r, HoldMachineRepair, w.Key, string.Empty, 0, res.WorkOrderId);
                        Fired(state, r, "rules.log.repair_send", w.Key, LabelArg(r.Serial), "@m:" + w.Machine.ToString(CultureInfo.InvariantCulture),
                            InjuryPercent(m).ToString(CultureInfo.InvariantCulture), r.Threshold.ToString(CultureInfo.InvariantCulture), "@b:" + bay.BuildingId);
                    }
                    return;
                }
                case HoldSupply:
                {
                    if (!ProductionService.TryGet(state, w.Target, out ProductionService.Producer p) || !ItemCatalog.TryGet(r.ItemId, out ItemDef item))
                    {
                        return;
                    }
                    int have = ProductionService.Count(p.Rec.In, r.ItemId);
                    if (have >= r.Threshold)
                    {
                        return;
                    }
                    int room = Math.Max(0, ProductionService.InCapacity(p, item) - have);
                    int amount = Math.Min(Math.Min(r.Batch, room), HomeInventory.Stock(state, item));
                    if (amount <= 0)
                    {
                        if (room > 0)
                        {
                            SetIssue(state, r, "rules.issue.no_stock", "@item:" + r.ItemId);
                        }
                        return;
                    }
                    string id = NextOrderId(state, r, "supply");
                    HomeValleyWorkOrders.WorkOrderOpResult res = HomeValleyWorkOrders.TryCreateDeliverPool(state, id, w.Target, r.ItemId, amount, UnloadSeconds, r.Serial);
                    if (res.Success)
                    {
                        AddHold(state, r, HoldSupply, w.Key, string.Empty, amount, res.WorkOrderId);
                        Fired(state, r, "rules.log.supply_send", BuildingKey(w.Target), LabelArg(r.Serial), "@b:" + w.Target, "@item:" + r.ItemId,
                            have.ToString(CultureInfo.InvariantCulture), r.Threshold.ToString(CultureInfo.InvariantCulture), amount.ToString(CultureInfo.InvariantCulture));
                    }
                    return;
                }
            }
        }

        /// <summary>最近的一座运转、通电、还有空位（rules.repair_bay.slots）的维修台；没有返回 null。</summary>
        public static BuildingRecord PickBay(CampaignState state, MachineRecord m)
        {
            BuildingRecord best = null;
            float bestD = float.MaxValue;
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeRepairBay || b.ConstructionState != BuildingConstructionState.Operational
                    || HomeGridService.IsRelocationGhost(b) || (b.PowerState != BuildingPowerState.Powered && b.PowerState != BuildingPowerState.NotApplicable))
                {
                    continue;
                }
                if (BayLoad(state, b.BuildingId) >= BaySlots)
                {
                    continue;
                }
                float dist = m != null ? Vector2.Distance(m.WorldPosition, b.Position) : 0f;
                if (best == null || dist < bestD - 0.001f || (Math.Abs(dist - bestD) <= 0.001f && string.CompareOrdinal(b.BuildingId, best.BuildingId) < 0))
                {
                    best = b;
                    bestD = dist;
                }
            }
            return best;
        }

        /// <summary>这座维修台正在修 / 正赶过来的机器数。</summary>
        public static int BayLoad(CampaignState state, string bayId)
        {
            int n = 0;
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.MachineRepair && o.TargetId == bayId && HomeValleyWorkOrders.IsActive(o))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>这台机器正停在维修台上修理（接入口据此拒绝，FG01“目标在维修台上”；DEBT-FG1SIG03-01）。</summary>
        public static bool IsOnRepairBay(CampaignState state, int logicId)
        {
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.MachineRepair && o.AssignedMachineLogicId == logicId && o.State == WorkOrderState.InProgress)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>恢复规则设下的改动（只在规则设下的值还在时调用）。</summary>
        private static void Restore(CampaignState state, RuleHoldRecord h, bool log)
        {
            string label = LabelArg(h.Rule);
            switch (h.Kind)
            {
                case HoldRecipe:
                    ProductionService.TrySetRecipeForRule(state, h.EntityId.Substring(2), h.Prev, out _);
                    break;
                case HoldDisabled:
                    BuildingOps.TrySetEnabled(state, h.EntityId.Substring(2), true, out _);
                    if (log)
                    {
                        AddLog(state, h.Rule, "rules.log.resumed", h.EntityId, label, "@" + h.EntityId);
                    }
                    break;
                case HoldStorage:
                    HomeValleyPowerGrid.TrySetStorageSettings(state, h.EntityId.Substring(2), noCharge: (h.PrevInt & 1) != 0, noDischarge: (h.PrevInt & 2) != 0);
                    if (log)
                    {
                        AddLog(state, h.Rule, "rules.log.discharge_end", h.EntityId, label, "@" + h.EntityId);
                    }
                    break;
                case HoldBoost:
                {
                    WorkOrderRecord o = HomeValleyWorkOrders.Find(state, h.OrderId);
                    if (o != null)
                    {
                        o.Priority = h.PrevInt;
                        if (o.RuleSerial == h.Rule)
                        {
                            o.RuleSerial = 0; // 只撤掉自己的标记（自动重建派的单子仍写“由规则 R… 触发”）。
                        }
                        HomeValleyWorkOrders.MarkAssignmentDirty();
                    }
                    break;
                }
                case HoldGarrison:
                {
                    bool wasActive = HomeValleyWorkOrders.IsActive(HomeValleyWorkOrders.Find(state, h.OrderId)); // 到不了而失败的驻防不写“结束驻防”
                    HomeValleyWorkOrders.EndRuleOrder(state, h.OrderId);
                    if (log && wasActive)
                    {
                        AddLog(state, h.Rule, "rules.log.garrison_end", h.EntityId, label, "@" + h.EntityId);
                    }
                    break;
                }
                case HoldMachineRepair:
                case HoldSupply:
                    HomeValleyWorkOrders.EndRuleOrder(state, h.OrderId);
                    break;
            }
            Revision++;
        }

        /// <summary>规则停用 / 删除：它持有的改动全部恢复（被玩家改掉的只放手），等材料的重建一并撤掉。</summary>
        private static void ReleaseAllOf(CampaignState state, StandingRuleRecord r, bool log)
        {
            StandingRuleState d = Domain(state);
            var keep = new List<RuleHoldRecord>(d.Holds.Length);
            foreach (RuleHoldRecord h in d.Holds)
            {
                if (h == null)
                {
                    continue;
                }
                if (h.Rule != r.Serial)
                {
                    keep.Add(h);
                    continue;
                }
                if (!h.Overridden && Status(state, h) == HoldStatus.Applied)
                {
                    Restore(state, h, log);
                }
            }
            d.Holds = keep.ToArray();
            if (r.Kind == KindRebuild)
            {
                var pending = new List<RuleEventRecord>();
                foreach (RuleEventRecord e in d.Pending)
                {
                    if (e != null && !(e.Kind == EventRebuildRetry && e.Rule == r.Serial))
                    {
                        pending.Add(e);
                    }
                }
                d.Pending = pending.ToArray();
            }
            Revision++;
        }

        // ── 事件处理（下一个模拟步）────────────────────────────────────────────────

        private static void ProcessEvents(CampaignState state, StandingRuleState d)
        {
            RuleEventRecord[] events = d.Pending;
            bool retryDue = GameClock.Ticks >= d.NextCheckTick || d.EvaluateNow;
            if (!retryDue && OnlyRetries(events))
            {
                return; // 只有等材料的重建、还没到检查步：原样留着，不新建列表 / 数组（审查修复 P2：热更层每步不产生垃圾）。
            }
            d.Pending = Array.Empty<RuleEventRecord>();
            var retry = new List<RuleEventRecord>();
            foreach (RuleEventRecord e in events)
            {
                if (e == null)
                {
                    continue;
                }
                switch (e.Kind)
                {
                    case EventDestroyed:
                        HandleDestroyed(state, e.EntityId, retry);
                        break;
                    case EventRebuildRetry:
                        // 等材料的重建：只在定时检查步重试（O(等待数)，不每步重试）。
                        if (retryDue)
                        {
                            RetryRebuild(state, e, retry);
                        }
                        else
                        {
                            retry.Add(e);
                        }
                        break;
                    case EventExpedition:
                        HandleExpedition(state, e.Machines);
                        break;
                    case EventBeltGhost:
                        HandleBeltGhost(state, e.EntityId); // FG6-DEF-03：被摧毁的传送带 / 物流节点
                        break;
                }
            }
            if (retry.Count > 0 || d.Pending.Length > 0)
            {
                retry.AddRange(d.Pending);
                d.Pending = retry.ToArray();
            }
        }

        private static bool OnlyRetries(RuleEventRecord[] events)
        {
            foreach (RuleEventRecord e in events)
            {
                if (e != null && e.Kind != EventRebuildRetry)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasRetry(CampaignState state, int rule, string exceptBuilding)
        {
            foreach (RuleEventRecord e in Domain(state).Pending)
            {
                if (e != null && e.Kind == EventRebuildRetry && e.Rule == rule && e.EntityId != exceptBuilding)
                {
                    return true;
                }
            }
            return false;
        }

        // FG6-DEF-03：RebuildRuleFor（类型范围 + 重建区域）在 StandingRuleService.Rebuild.cs。

        private static void HandleDestroyed(CampaignState state, string buildingId, List<RuleEventRecord> retry)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || b.ConstructionState != BuildingConstructionState.Damaged)
            {
                return;
            }
            StandingRuleRecord r = RebuildRuleFor(state, b.BuildingTypeId, CellOf(b));
            if (r == null)
            {
                return; // 没有玩家开的自动重建：留下虚影，等玩家自己点“重建”（FGR-BASE-020）。
            }
            TryRebuild(state, r, b, retry, first: true);
        }

        private static void RetryRebuild(CampaignState state, RuleEventRecord e, List<RuleEventRecord> retry)
        {
            StandingRuleRecord r = Find(state, e.Rule);
            BuildingRecord b = HomeGridService.FindBuilding(state, e.EntityId);
            if (r == null || !r.Enabled || b == null || b.ConstructionState != BuildingConstructionState.Damaged
                || HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId) != null
                || !Covers(r, b.BuildingTypeId, CellOf(b))) // FG6-DEF-03：等材料期间区域被关掉 / 范围改了 = 不再等
            {
                // 不用再等了（建筑已被重建 / 拆掉、玩家自己派了单、规则停用）：清掉“等材料”的原因。
                if (r != null && r.IssueKey == "rules.issue.rebuild_wait" && !HasRetry(state, r.Serial, e.EntityId))
                {
                    SetIssue(state, r, null, null);
                }
                return;
            }
            TryRebuild(state, r, b, retry, first: false);
        }

        private static void TryRebuild(CampaignState state, StandingRuleRecord r, BuildingRecord b, List<RuleEventRecord> retry, bool first)
        {
            if (HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId) != null)
            {
                return;
            }
            if (BuildingOps.TryOrderRepair(state, b.BuildingId, out string message))
            {
                WorkOrderRecord o = HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId);
                if (o != null)
                {
                    o.RuleSerial = r.Serial;
                }
                if (r.IssueKey == "rules.issue.rebuild_wait")
                {
                    SetIssue(state, r, null, null);
                }
                Fired(state, r, "rules.log.rebuild", BuildingKey(b.BuildingId), LabelArg(r.Serial), "@b:" + b.BuildingId);
                return;
            }
            retry.Add(new RuleEventRecord { Kind = EventRebuildRetry, EntityId = b.BuildingId, Rule = r.Serial, Tick = GameClock.Ticks });
            if (first)
            {
                AddLog(state, r.Serial, "rules.log.rebuild_wait", BuildingKey(b.BuildingId), LabelArg(r.Serial), "@b:" + b.BuildingId, "@raw:" + message);
                SetIssue(state, r, "rules.issue.rebuild_wait", "@b:" + b.BuildingId + "|@raw:" + message);
                NotificationCenter.Post("standing_rule", Expand(state, "rules.log.rebuild_wait", new[] { LabelArg(r.Serial), "@b:" + b.BuildingId, "@raw:" + message }),
                    new Vector3(b.Position.x, 0f, b.Position.y));
            }
        }

        private static void HandleExpedition(CampaignState state, int[] machines)
        {
            StandingRuleRecord r = null;
            foreach (StandingRuleRecord x in Ordered(state))
            {
                if (x.Enabled && x.Kind == KindUnload)
                {
                    r = x;
                    break;
                }
            }
            if (r == null)
            {
                return; // 玩家关掉了远征卸货：货留在机器货舱里（FGR-BASE-020）。
            }
            BuildingRecord dest = string.IsNullOrEmpty(r.PointId) ? null : HomeGridService.FindBuilding(state, r.PointId);
            Vector2 at = dest?.Position ?? HomeValleyLayout.Core.Position;
            int total = 0;
            foreach (int id in machines ?? Array.Empty<int>())
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || !m.IsAlive || m.Cargo == null || m.Cargo.Length == 0)
                {
                    continue;
                }
                for (int i = 0; i < m.Cargo.Length; i++)
                {
                    CargoEntry c = m.Cargo[i];
                    if (c == null || c.Amount <= 0)
                    {
                        continue;
                    }
                    HomeValleyConstruction.ReturnMaterials(state, at, c.ResourceType, c.Amount,
                        "rule:R" + r.Serial.ToString(CultureInfo.InvariantCulture) + ":unload:" + id.ToString(CultureInfo.InvariantCulture) + ":" + i.ToString(CultureInfo.InvariantCulture)
                        + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                    total += c.Amount;
                }
                m.Cargo = Array.Empty<CargoEntry>();
            }
            string destArg = dest != null ? "@b:" + dest.BuildingId : "@key:rules.shared_storage";
            if (total > 0)
            {
                HomeInventory.Touch();
                Fired(state, r, "rules.log.unload", dest != null ? BuildingKey(dest.BuildingId) : null, LabelArg(r.Serial), total.ToString(CultureInfo.InvariantCulture), destArg);
            }
            else
            {
                Fired(state, r, "rules.log.unload_empty", null, LabelArg(r.Serial));
            }
        }

        // ── 工单回调（HomeValleyWorkOrders）─────────────────────────────────────────

        /// <summary>维修单刚开出来：有执行中的战时预案（打开了维修优先）就立刻提到最高优先级（不等下一次检查）。</summary>
        public static void OnRepairOrderCreated(CampaignState state, WorkOrderRecord order)
        {
            if (state == null || order == null || order.Kind != WorkOrderKind.Repair)
            {
                return;
            }
            foreach (StandingRuleRecord r in Ordered(state))
            {
                if (r.Enabled && r.Kind == KindWar && r.Active && r.BoostRepair)
                {
                    Apply(state, new Want(r, HoldBoost, OrderKey(order.WorkOrderId), order.WorkOrderId, 0));
                    return;
                }
            }
        }

        /// <summary>补给送到：放进目标建筑的输入缓存（生产建筑，按输入缓存余量；放不下 / 不是生产建筑 = 退回仓库）。返回放进去的件数。</summary>
        public static int DepositSupply(CampaignState state, BuildingRecord target, string itemId, int amount, string dropId)
        {
            if (amount <= 0 || !ItemCatalog.TryGet(itemId, out ItemDef item))
            {
                return 0;
            }
            int put = 0;
            if (ProductionService.TryGet(state, target.BuildingId, out ProductionService.Producer p))
            {
                int room = Math.Max(0, ProductionService.InCapacity(p, item) - ProductionService.Count(p.Rec.In, itemId));
                put = Math.Min(room, amount);
                if (put > 0)
                {
                    ProductionService.Add(ref p.Rec.In, itemId, put);
                }
            }
            int rest = amount - put;
            if (rest > 0)
            {
                HomeValleyConstruction.ReturnMaterials(state, target.Position, ItemCatalog.ResourceTypeOf(itemId), rest, dropId + ":rest");
            }
            return put;
        }

        public static void OnDeliverCompleted(CampaignState state, WorkOrderRecord order, int put, int amount)
        {
            if (order == null || order.RuleSerial <= 0)
            {
                return;
            }
            AddLog(state, order.RuleSerial, "rules.log.supply_done", BuildingKey(order.TargetId), LabelArg(order.RuleSerial),
                put.ToString(CultureInfo.InvariantCulture), "@item:" + order.ReservedItemId, "@b:" + order.TargetId);
            RequestEvaluation(state); // 送完马上看要不要再送下一批（不用等整分钟）。
        }

        public static void OnMachineRepaired(CampaignState state, WorkOrderRecord order, int machine)
        {
            if (order == null || order.RuleSerial <= 0)
            {
                return;
            }
            AddLog(state, order.RuleSerial, "rules.log.repair_done", MachineKey(machine), LabelArg(order.RuleSerial), "@m:" + machine.ToString(CultureInfo.InvariantCulture));
        }

        // ── 原因 / 触发 / 日志 ─────────────────────────────────────────────────────

        private static void SetIssue(CampaignState state, StandingRuleRecord r, string key, string arg)
        {
            key ??= string.Empty;
            arg ??= string.Empty;
            if (r.IssueKey == key && r.IssueArg == arg)
            {
                return;
            }
            r.IssueKey = key;
            r.IssueArg = arg;
            Revision++;
            if (key.Length > 0 && key != "rules.issue.rebuild_wait")
            {
                // 原因只在变化时写一条日志 + 一条警告（同类聚合、可定位），不每分钟刷屏（B08）。
                string reason = FormatArgs(key, arg);
                string entity = FirstEntity(arg);
                AddLog(state, r.Serial, "rules.log.issue", entity, LabelArg(r.Serial), "@raw:" + reason);
                NotificationCenter.Post("standing_rule", Expand(state, "rules.log.issue", new[] { LabelArg(r.Serial), "@raw:" + reason }), PositionOf(state, entity));
            }
        }

        private static string FirstEntity(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return null;
            }
            foreach (string part in arg.Split('|'))
            {
                if (part.StartsWith("@b:", StringComparison.Ordinal))
                {
                    return "b:" + part.Substring(3);
                }
            }
            return null;
        }

        private static void SetActive(CampaignState state, StandingRuleRecord r, bool active, string _)
        {
            if (r.Active != active)
            {
                r.Active = active;
                Revision++;
            }
        }

        /// <summary>规则执行了一个动作：记触发次数与时间、写日志、第一次触发的引导钩子。</summary>
        private static void Fired(CampaignState state, StandingRuleRecord r, string key, string entityKey, params string[] args)
        {
            r.LastFiredTick = GameClock.Ticks;
            r.FireCount++;
            AddLog(state, r.Serial, key, entityKey, args);
            GuidanceHooks.Raise(GuidanceHooks.RulesFirstTriggered);
        }

        private static void AddLog(CampaignState state, int rule, string key, string entityKey, params string[] args)
        {
            StandingRuleState d = Domain(state);
            Vector3? pos = PositionOf(state, entityKey);
            var e = new RuleLogRecord
            {
                Serial = d.NextLogSerial++,
                Tick = GameClock.Ticks,
                Rule = rule,
                Key = key,
                Args = args ?? Array.Empty<string>(),
                EntityId = entityKey ?? string.Empty,
                HasPos = pos.HasValue,
                X = pos?.x ?? 0f,
                Y = pos?.z ?? 0f,
            };
            int max = LogMax;
            var list = new List<RuleLogRecord>(Math.Min(max, d.Log.Length + 1));
            int skip = Math.Max(0, d.Log.Length + 1 - max);
            for (int i = skip; i < d.Log.Length; i++)
            {
                list.Add(d.Log[i]);
            }
            list.Add(e);
            d.Log = list.ToArray();
            Revision++;
            // FG4-ECO-09（FG-GAP-098）：远征在外时规则做的事同时记进离家报告（日志只留最近 100 条，报告单独留离家期间的）。
            AwayReportService.OnRuleLog(state, e);
        }

        /// <summary>实体在世界里的位置（建筑 / 机器；工单 = 它的目标）。没有返回 null。</summary>
        public static Vector3? PositionOf(CampaignState state, string entityKey)
        {
            if (string.IsNullOrEmpty(entityKey) || entityKey.Length < 3)
            {
                return null;
            }
            string id = entityKey.Substring(2);
            switch (entityKey[0])
            {
                case 'b':
                {
                    BuildingRecord b = HomeGridService.FindBuilding(state, id);
                    return b != null ? new Vector3(b.Position.x, 0f, b.Position.y) : (Vector3?)null;
                }
                case 'm':
                {
                    if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logicId) && MachineRegistry.TryGetRecord(logicId, out MachineRecord m))
                    {
                        Vector2 p = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : m.WorldPosition;
                        return new Vector3(p.x, 0f, p.y);
                    }
                    return null;
                }
                case 's':
                {
                    int colon = id.IndexOf(':');
                    return colon >= 0 ? PositionOf(state, "b:" + id.Substring(colon + 1)) : null;
                }
                case 'c':
                {
                    // FG6-DEF-03：一格（被重建的传送带虚影）“c:x,y”。
                    int comma = id.IndexOf(',');
                    if (comma > 0 && int.TryParse(id.Substring(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cx)
                        && int.TryParse(id.Substring(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cy))
                    {
                        return new Vector3(cx, 0f, cy);
                    }
                    return null;
                }
                default:
                    return null;
            }
        }
    }
}
