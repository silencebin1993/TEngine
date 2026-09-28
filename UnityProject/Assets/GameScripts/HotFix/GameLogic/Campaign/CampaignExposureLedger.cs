using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>FG1-SIG-06：暴露来源种类（存进 <see cref="SignalExposureEventRecord.Kind"/> 的稳定键；显示名走文本键 exposure.source.*）。</summary>
    public static class ExposureSourceKind
    {
        public const string HighPower = "high_power";
        public const string CoreFire = "core_fire";
        public const string RawFire = "raw_fire";
        public const string AlienTech = "alien_tech";
        public const string NodeDestroyed = "node_destroyed";
        public const string NodeLinkCut = "node_link_cut";
        public const string TowerOff = "tower_off";
        // 规则改写前（Demo 规则）的来源：只出现在旧档的明细里，不再产生。
        public const string LegacyHeavy = "legacy_heavy";
        public const string LegacyCrossFaction = "legacy_cross_faction";
        public const string LegacyDirectControl = "legacy_direct_control";
        public const string LegacyOther = "legacy_other";

        public static bool IsLegacy(string kind) => kind != null && kind.StartsWith("legacy_", StringComparison.Ordinal);
    }

    /// <summary>
    /// 信号暴露（<see cref="CampaignState.SignalExposure"/>，0～100）的唯一写入口。任何其它代码都不得直接改暴露值。
    ///
    /// FG1-SIG-06 起按正式版规则（FG01 FGR-SIG-070；数值是 FG16 第 9 章初值，全部在 fg.TbHomeTuning exposure.* / signal.raw.*）：
    /// <list type="bullet">
    /// <item>高功率生产：家园总用电需求超过阈值的部分线性换算，每游戏小时最多 +3；按游戏时间累计（家园不被观察时照常），每满一个游戏小时记一笔（<see cref="SimStepHighPower"/>）。</item>
    /// <item>核心固件发动：每次 +0.5（“发动” = 具名反应真正打出的那一发，与核心冷却同一个时刻，<see cref="GrantCoreFire"/>）。</item>
    /// <item>裸跑敌方固件：每次发动 +2（<see cref="GrantRawFire"/>；裸跑的核心固件按裸跑计，不再另计核心 +0.5）。</item>
    /// <item>使用异派技术：出发的远征队里有机器装着跨阵营蓝图，每次远征 +2（<see cref="GrantAlienTechForExpedition"/>）。</item>
    /// <item>摧毁敌方节点：每个 +3；切断监听链 -15（Demo 的降暴露手段，两笔独立记录，<see cref="GrantNodeDestroyed"/>）。</item>
    /// <item>关闭信号塔主动广播：每 10 游戏秒 -2，带宽 -3（Demo 的降暴露手段，<see cref="TickTowerBroadcastOff"/>）。</item>
    /// </list>
    /// **不再计入**：接入本身与接入时长（Demo“远征直控每 30 秒 +5”）、Demo“生产重型机 +8”“首次使用异派固件 +10”。
    /// 旧档：<see cref="MigrateLegacy"/> 在读档时把规则版本 0 迁到 1——暴露值原样保留（不追溯扣减，阈值与已锁定的反制不跳变），旧明细补上来源种类（面板标“旧规则”），直控累计停用。
    ///
    /// 明细（<see cref="CampaignState.SignalExposureEvents"/>）只保留最近 exposure.history_max 条（按 <see cref="SignalExposureEventRecord.Seq"/>）；
    /// 各来源 / 各阵营的累计在 <see cref="CampaignState.SignalExposureTotals"/>，不随明细截断丢失（FGU-44“各阵营贡献”）。
    /// 一次性事件（节点、每次远征）经 <see cref="CampaignEventLedger"/> 幂等；重复性来源（发动、每小时、每 10 秒）不进事件账本（避免账本无限增长）。
    /// </summary>
    public static class CampaignExposureLedger
    {
        /// <summary>正式版暴露规则的版本号（<see cref="CampaignState.ExposureRulesVersion"/>）。</summary>
        public const int CurrentRulesVersion = 1;

        public const float MinExposure = 0f;
        public static float MaxExposure => Tuning("exposure.max", 100f);

        /// <summary>塔关广播的带宽代价——由 <c>HomeValleyPowerGrid.Recompute</c> 读取 <see cref="CampaignState.SignalTowerBroadcastOff"/> 时直接使用
        /// （带宽唯一真相仍是电网仲裁）。</summary>
        public const float TowerBroadcastOffBandwidthPenalty = 3f;

        public const float ThresholdScoutTip = 30f;
        public const float ThresholdAdaptationIntel = 60f;
        public const float ThresholdCoreReinforcement = 90f;

        public const string FactionNone = "none";
        public const string FactionSilent = "silent";
        public const string FactionFoundry = "foundry";

        // ── 调参（fg.TbHomeTuning，数据源 tools/cell_tables/fgdata_signal.py）──────────────

        public static float CoreFireDelta => Tuning("exposure.core_fire", 0.5f);
        public static float RawFireDelta => RawFirmwareService.ExposurePerFire;
        public static float AlienTechDelta => Tuning("exposure.alien_tech_per_expedition", 2f);
        public static float NodeDestroyedDelta => Tuning("exposure.node_destroyed", 3f);
        public static float NodeLinkCutDelta => -Math.Abs(Tuning("exposure.node_link_cut_drop", 15f));
        public static float TowerBroadcastOffDeltaPer10Seconds => -Math.Abs(Tuning("exposure.tower_off_drop_per_10s", 2f));
        public static float HighPowerThreshold => Tuning("exposure.high_power.threshold", 100f);
        public static float HighPowerFullAt => Math.Max(HighPowerThreshold + 1f, Tuning("exposure.high_power.full_at", 200f));
        public static float HighPowerCapPerHour => Tuning("exposure.high_power.cap_per_hour", 3f);
        public static float HighPowerSettleSeconds => Math.Max(1f, Tuning("exposure.high_power.settle_seconds", 50f));
        public static int HistoryMax => Math.Max(8, (int)Math.Round(Tuning("exposure.history_max", 64f)));

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[CampaignExposureLedger] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>实际变化小于它视为没变（不写明细、不计累计）。</summary>
        private const float NoChangeEpsilon = 1e-4f;

        /// <summary>界面刷新用：每写一笔 +1（面板按它判断要不要重建）。</summary>
        public static int Revision { get; private set; } = 1;

        private static RegionRecord FindCurrentRegion(CampaignState state) =>
            state?.RegionRecords?.FirstOrDefault(r => r.RegionId == state.CurrentRegionId);

        // ── 写入（唯一改写 SignalExposure 的地方）──────────────────────────────────────

        /// <summary>一次性来源：经 <paramref name="eventId"/> 幂等（同一事件只结算一次）。</summary>
        private static bool TryApplyOnce(CampaignState state, string eventId, string kind, string detail, string faction, float delta)
        {
            if (state == null || string.IsNullOrEmpty(eventId))
            {
                return false;
            }
            if (!CampaignEventLedger.TryGrant(state, eventId, "SignalExposure", state.PlaySeconds,
                    kind + ":" + delta.ToString("+0.##;-0.##", CultureInfo.InvariantCulture)))
            {
                return false;
            }
            Apply(state, eventId, kind, detail, faction, delta);
            return true;
        }

        /// <summary>重复性来源（每次发动 / 每小时 / 每 10 秒）：不进事件账本，明细 ID 用登记序号。</summary>
        private static void ApplyRepeating(CampaignState state, string kind, string detail, string faction, float delta)
        {
            if (state == null)
            {
                return;
            }
            Apply(state, $"exposure:{kind}:{state.ExposureEventSeq + 1}", kind, detail, faction, delta);
        }

        /// <summary>
        /// 写一笔。明细与累计都按钳制后的<b>实际变化</b>（after − before）记：封顶 / 见底时名义值与实际不同，按实际记，
        /// 各来源 / 阵营累计之和才等于真实暴露变化（FGR-SIG-070“玩家能看出自己为什么被盯上”）。实际变化为 0 时（暴露已 0 时塔关又降、
        /// 已封顶时又加）不写明细也不计累计——长期关广播不会把真正的来源挤出“最近来源”。
        /// </summary>
        private static void Apply(CampaignState state, string eventId, string kind, string detail, string faction, float delta)
        {
            float before = state.SignalExposure;
            float after = Mathf.Clamp(before + delta, MinExposure, MaxExposure);
            float actual = after - before;
            if (Mathf.Abs(actual) < NoChangeEpsilon)
            {
                return;
            }
            state.SignalExposure = after;
            int seq = ++state.ExposureEventSeq;
            var record = new SignalExposureEventRecord
            {
                EventId = eventId,
                Source = SourceText(kind, detail, GameLanguage.ZhCn), // 旧字段：保留中文来源，便于日志与旧工具阅读
                Delta = actual,
                AtPlaySeconds = state.PlaySeconds,
                ResultingExposure = after,
                Kind = kind,
                Detail = detail ?? string.Empty,
                Faction = string.IsNullOrEmpty(faction) ? FactionNone : faction,
                Seq = seq,
            };
            SignalExposureEventRecord[] events = (state.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>()).Append(record).ToArray();
            int max = HistoryMax;
            if (events.Length > max)
            {
                events = events.OrderByDescending(e => e.Seq).Take(max).ToArray();
            }
            state.SignalExposureEvents = events;
            AddTotal(state, record.Kind, record.Faction, actual);
            Revision++;
            Log.Info($"[CampaignExposureLedger] {kind}({detail}) 名义 {delta:+0.##;-0.##}、实际 {actual:+0.##;-0.##} -> {after:0.##}（{eventId}）。");
            CheckThresholdCrossing(state, before, after);
        }

        private static void AddTotal(CampaignState state, string kind, string faction, float delta)
        {
            SignalExposureTotalRecord[] totals = state.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>();
            SignalExposureTotalRecord t = totals.FirstOrDefault(x => x != null && x.Kind == kind && x.Faction == faction);
            if (t == null)
            {
                t = new SignalExposureTotalRecord { Kind = kind, Faction = faction };
                totals = totals.Append(t).ToArray();
                state.SignalExposureTotals = totals;
            }
            if (delta >= 0f)
            {
                t.Added += delta;
            }
            else
            {
                t.Removed += -delta;
            }
            t.Count++;
        }

        // ── 正式版来源（FGR-SIG-070）──────────────────────────────────────────────

        /// <summary>核心固件发动（FG16 +0.5）：由 <see cref="SignalUplinkService.OnReactionFired(CampaignState,int,string,bool)"/> 在核心冷却开始的同一时刻调用
        /// （同一次冷却只计一次）。<paramref name="raw"/> = 这枚核心固件未破解（信号裸跑）——按裸跑 +2 计，不再另计 +0.5。</summary>
        public static void GrantCoreFire(CampaignState state, string firmwareId, bool raw)
        {
            if (raw)
            {
                GrantRawFire(state, firmwareId);
                return;
            }
            ApplyRepeating(state, ExposureSourceKind.CoreFire, firmwareId, FirmwareKinds.FactionOf(firmwareId), CoreFireDelta);
        }

        /// <summary>裸跑敌方固件发动一次（FGR-SIG-061：+2，确定性）。由 <see cref="RawFirmwareService"/> 调用。</summary>
        public static void GrantRawFire(CampaignState state, string firmwareId) =>
            ApplyRepeating(state, ExposureSourceKind.RawFire, firmwareId, FirmwareKinds.FactionOf(firmwareId), RawFireDelta);

        /// <summary>
        /// 使用异派技术（FG16：每次远征 +2）：由 <c>ExpeditionDepartureService.TryDepart</c> 在出发事务成功后调用。
        /// 远征队里有机器装着跨阵营蓝图（版本的 <see cref="BlueprintVersionRecord.FactionTags"/> ≥ 2 个，与 Demo 的“异派”同一判据）时记一笔，
        /// 同一次远征（地点 + 第几次出发）只记一次。返回是否记了。
        /// </summary>
        public static bool GrantAlienTechForExpedition(CampaignState state, IReadOnlyCollection<int> machineLogicIds, string regionId, int expeditionCount)
        {
            if (state == null || machineLogicIds == null)
            {
                return false;
            }
            string alien = null;
            foreach (int id in machineLogicIds)
            {
                // 机器的实时记录在 MachineRegistry（存档时才同步回 state.MachineRecords）；查不到再退回存档里的记录。
                MachineRecord m = MachineRegistry.TryGetRecord(id, out MachineRecord live) && live != null
                    ? live
                    : state.MachineRecords?.FirstOrDefault(r => r != null && r.LogicId == id);
                if (m == null)
                {
                    continue;
                }
                BlueprintVersionRecord v = state.BlueprintRecords?.FirstOrDefault(b => b != null && b.BlueprintId == m.BlueprintId)?
                    .Versions?.FirstOrDefault(x => x != null && x.Version == m.BlueprintVersion);
                string[] tags = v?.FactionTags ?? Array.Empty<string>();
                if (tags.Length < 2)
                {
                    continue;
                }
                string f = tags.Select(FactionKeyOfTag).Where(k => k != FirmwareKinds.FactionReclaim && k != FactionNone)
                    .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
                if (f != null && (alien == null || string.CompareOrdinal(f, alien) < 0))
                {
                    alien = f;
                }
            }
            if (alien == null)
            {
                return false;
            }
            return TryApplyOnce(state, $"exposure:alien_tech:{regionId}:{expeditionCount}", ExposureSourceKind.AlienTech, alien, alien, AlienTechDelta);
        }

        /// <summary>蓝图版本的派系标签（中文，<c>BlueprintCircuitBoard.ComputeFactionTags</c>）→ 阵营键。</summary>
        public static string FactionKeyOfTag(string tag) => tag switch
        {
            "归还" => FirmwareKinds.FactionReclaim,
            "静默" => FactionSilent,
            "铸造" => FactionFoundry,
            _ => FactionNone,
        };

        /// <summary>摧毁监听节点——由 <c>FracturedCityRegion.TryDestroyListeningNode</c> 成功时调用，产生两笔独立记录
        /// （摧毁敌方节点 +3、切断监听链 -15；净值不合并成神秘数值）。</summary>
        public static void GrantNodeDestroyed(CampaignState state, string nodeId)
        {
            TryApplyOnce(state, $"exposure:node_combat:{nodeId}", ExposureSourceKind.NodeDestroyed, nodeId, FactionSilent, NodeDestroyedDelta);
            TryApplyOnce(state, $"exposure:node_link_cut:{nodeId}", ExposureSourceKind.NodeLinkCut, nodeId, FactionSilent, NodeLinkCutDelta);
        }

        /// <summary>家园关闭信号塔主动广播——由家园模拟步（与观察无关）调用，累计到 10 游戏秒结算一笔 -2；一步跨越多个周期时逐个结算。</summary>
        public static void TickTowerBroadcastOff(CampaignState state, float dt)
        {
            if (state == null || !state.SignalTowerBroadcastOff || dt <= 0f)
            {
                return;
            }
            state.TowerBroadcastOffElapsedSeconds += dt;
            while (state.TowerBroadcastOffElapsedSeconds >= 10f)
            {
                state.TowerBroadcastOffElapsedSeconds -= 10f;
                ApplyRepeating(state, ExposureSourceKind.TowerOff, null, FactionNone, TowerBroadcastOffDeltaPer10Seconds);
            }
        }

        /// <summary>玩家主动切换信号塔广播开关——唯一写入口。切换即重置 10 秒累计计时器。</summary>
        public static void SetTowerBroadcastOff(CampaignState state, bool off)
        {
            if (state == null)
            {
                return;
            }
            state.SignalTowerBroadcastOff = off;
            state.TowerBroadcastOffElapsedSeconds = 0f;
        }

        /// <summary>高功率生产每游戏小时的暴露速率（用电需求 <paramref name="demand"/>）：阈值以下为 0，阈值到满额之间线性，封顶 cap。</summary>
        public static float HighPowerRatePerHour(float demand)
        {
            float t = (demand - HighPowerThreshold) / (HighPowerFullAt - HighPowerThreshold);
            return HighPowerCapPerHour * Mathf.Clamp01(t);
        }

        /// <summary>
        /// 高功率生产（FGR-SIG-070，FG16“按总功率换算，每游戏小时最多 +3”）：家园模拟步（整个世界同时运行，与观察无关）按游戏时间累计，
        /// 每满 exposure.high_power.settle_seconds 游戏秒（= 1 游戏小时）记一笔。暂停不走、倍速只改变每帧步数，结果与倍速无关。O(1)。
        /// </summary>
        public static void SimStepHighPower(CampaignState state, float dt)
        {
            if (state == null || dt <= 0f)
            {
                return;
            }
            float hour = (float)Math.Max(1.0, GameClock.DaySeconds / 24.0);
            float demand = state.PowerDemand;
            state.HighPowerAccrued += HighPowerRatePerHour(demand) * dt / hour;
            state.HighPowerPeakDemand = Math.Max(state.HighPowerPeakDemand, demand);
            // 窗口计时用 double：一个游戏小时 3000 个固定步的 float 累加会差出一步，结算时刻就不再与步数对齐。
            state.HighPowerElapsedSeconds += dt;
            double window = HighPowerSettleSeconds;
            if (state.HighPowerElapsedSeconds + 1e-6 < window)
            {
                return;
            }
            state.HighPowerElapsedSeconds -= window;
            float amount = state.HighPowerAccrued;
            float peak = state.HighPowerPeakDemand;
            state.HighPowerAccrued = 0f;
            state.HighPowerPeakDemand = demand;
            if (amount >= 0.005f)
            {
                ApplyRepeating(state, ExposureSourceKind.HighPower, peak.ToString("0", CultureInfo.InvariantCulture), FactionNone, amount);
            }
        }

        // ── 旧档迁移（Demo 规则 → 正式版规则）────────────────────────────────────────

        /// <summary>
        /// 读档时调用（<c>CampaignSaveService.Load</c>），幂等。规则版本 0（Demo 规则）的存档：
        /// 暴露值原样保留（不追溯扣减：已越过的阈值、已锁定的敌方反制不跳变）；旧明细按事件 ID 前缀补上来源种类、阵营与序号
        /// （直控 / 重型机 / 首次异派 → “旧规则”来源）；各来源 / 阵营的累计由旧明细重建；远征直控累计清零停用（接入时长不再计入）。
        /// 返回迁移了几条旧明细（已是新规则时返回 0）。
        /// </summary>
        public static int MigrateLegacy(CampaignState state)
        {
            if (state == null || state.ExposureRulesVersion >= CurrentRulesVersion)
            {
                return 0;
            }
            SignalExposureEventRecord[] events = state.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>();
            int migrated = 0;
            int seq = state.ExposureEventSeq;
            foreach (SignalExposureEventRecord e in events.Where(x => x != null).OrderBy(x => x.AtPlaySeconds).ThenBy(x => x.EventId, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(e.Kind))
                {
                    (e.Kind, e.Faction) = LegacyKindOf(e.EventId);
                    if (e.Kind == ExposureSourceKind.LegacyOther)
                    {
                        e.Detail = e.Source ?? string.Empty;
                    }
                    migrated++;
                }
                if (e.Seq <= 0)
                {
                    e.Seq = ++seq;
                }
                seq = Math.Max(seq, e.Seq);
            }
            state.ExposureEventSeq = seq;
            if (state.SignalExposureTotals == null || state.SignalExposureTotals.Length == 0)
            {
                state.SignalExposureTotals = Array.Empty<SignalExposureTotalRecord>();
                foreach (SignalExposureEventRecord e in events.Where(x => x != null))
                {
                    AddTotal(state, e.Kind, string.IsNullOrEmpty(e.Faction) ? FactionNone : e.Faction, e.Delta);
                }
            }
            foreach (RegionRecord r in state.RegionRecords ?? Array.Empty<RegionRecord>())
            {
                if (r != null)
                {
                    r.DirectControlAccumulatedSeconds = 0f;
                }
            }
            state.ExposureRulesVersion = CurrentRulesVersion;
            Revision++;
            Log.Info($"[CampaignExposureLedger] 旧档暴露规则迁移：{migrated} 条明细补上来源种类，暴露值 {state.SignalExposure:0.##} 原样保留。");
            return migrated;
        }

        private static (string Kind, string Faction) LegacyKindOf(string eventId)
        {
            string id = eventId ?? string.Empty;
            if (id.StartsWith("exposure:heavy_produced:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.LegacyHeavy, FactionNone);
            }
            if (id.StartsWith("exposure:cross_faction_firmware:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.LegacyCrossFaction, FactionNone);
            }
            if (id.StartsWith("exposure:direct_control_30s:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.LegacyDirectControl, FactionNone);
            }
            if (id.StartsWith("exposure:node_combat:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.NodeDestroyed, FactionSilent);
            }
            if (id.StartsWith("exposure:node_link_cut:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.NodeLinkCut, FactionSilent);
            }
            if (id.StartsWith("exposure:tower_off_tick:", StringComparison.Ordinal))
            {
                return (ExposureSourceKind.TowerOff, FactionNone);
            }
            return (ExposureSourceKind.LegacyOther, FactionNone);
        }

        // ── 显示 ──────────────────────────────────────────────────────────────────

        /// <summary>来源名（当前语言）。</summary>
        public static string SourceText(SignalExposureEventRecord e) =>
            e == null ? string.Empty : SourceText(e.Kind, e.Detail, GameText.Language, e.Source);

        public static string SourceText(string kind, string detail, GameLanguage language, string legacySource = null)
        {
            string Name(string id) => FirmwareKinds.DisplayName(id) ?? id ?? string.Empty;
            switch (kind)
            {
                case ExposureSourceKind.HighPower: return Fmt(language, "exposure.source.high_power", detail ?? "0");
                case ExposureSourceKind.CoreFire: return Fmt(language, "exposure.source.core_fire", Name(detail));
                case ExposureSourceKind.RawFire: return Fmt(language, "exposure.source.raw_fire", Name(detail));
                case ExposureSourceKind.AlienTech: return Fmt(language, "exposure.source.alien_tech", FactionName(detail, language));
                case ExposureSourceKind.NodeDestroyed: return GameText.Get("exposure.source.node_destroyed", language);
                case ExposureSourceKind.NodeLinkCut: return GameText.Get("exposure.source.node_link_cut", language);
                case ExposureSourceKind.TowerOff: return GameText.Get("exposure.source.tower_off", language);
                case ExposureSourceKind.LegacyHeavy: return GameText.Get("exposure.source.legacy_heavy", language);
                case ExposureSourceKind.LegacyCrossFaction: return GameText.Get("exposure.source.legacy_cross_faction", language);
                case ExposureSourceKind.LegacyDirectControl: return GameText.Get("exposure.source.legacy_direct_control", language);
                default: return Fmt(language, "exposure.source.legacy_other", string.IsNullOrEmpty(detail) ? legacySource ?? kind ?? string.Empty : detail);
            }
        }

        private static string Fmt(GameLanguage language, string key, params object[] args)
        {
            string pattern = GameText.Get(key, language);
            try
            {
                return string.Format(pattern, args);
            }
            catch (FormatException)
            {
                return pattern;
            }
        }

        /// <summary>阵营名（faction.* 文本键；none = “无阵营（家园）”）。</summary>
        public static string FactionName(string faction, GameLanguage language) =>
            GameText.Get("faction." + (string.IsNullOrEmpty(faction) ? FactionNone : faction), language);

        public static string FactionName(string faction) => FactionName(faction, GameText.Language);

        /// <summary>最近 N 笔暴露事件（新的在前，按登记序号；旧档没有序号的按时间）。</summary>
        public static SignalExposureEventRecord[] RecentEvents(CampaignState state, int count)
        {
            if (state?.SignalExposureEvents == null)
            {
                return Array.Empty<SignalExposureEventRecord>();
            }
            return state.SignalExposureEvents
                .Where(e => e != null)
                .OrderByDescending(e => e.Seq)
                .ThenByDescending(e => e.AtPlaySeconds)
                .Take(count)
                .ToArray();
        }

        /// <summary>这个阵营键是不是敌方阵营（静默 / 铸造……）——己方（归还）与无阵营（家园）不是。</summary>
        public static bool IsEnemyFaction(string faction) =>
            !string.IsNullOrEmpty(faction) && faction != FactionNone && faction != FirmwareKinds.FactionReclaim;

        /// <summary>
        /// FGU-44“各阵营贡献”：只汇总<b>敌方</b>阵营的累计实际增加（只算正数部分），从多到少。己方（归还）核心固件发动、家园高功率生产等
        /// 己方 / 无阵营来源不算进任何阵营，见 <see cref="OwnActivityAdded"/>。FG6-DEF-04 选突袭阵营（FG06“暴露贡献最多的阵营”）直接取第一项，
        /// 不会选中己方（口径见 ADR-SIG-006 修订）。
        /// </summary>
        public static IReadOnlyList<(string Faction, float Added)> FactionContributions(CampaignState state)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (SignalExposureTotalRecord t in state?.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>())
            {
                if (t == null || t.Added <= 0f || !IsEnemyFaction(t.Faction))
                {
                    continue;
                }
                string f = t.Faction;
                map[f] = (map.TryGetValue(f, out float v) ? v : 0f) + t.Added;
            }
            return map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value)).ToList();
        }

        /// <summary>己方与家园活动（归还阵营、无阵营）的累计实际增加——面板单列一行，不算进“各敌方阵营贡献”。</summary>
        public static float OwnActivityAdded(CampaignState state) =>
            (state?.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>()).Where(t => t != null && t.Added > 0f && !IsEnemyFaction(t.Faction)).Sum(t => t.Added);

        /// <summary>某种来源的累计增加（自检与面板用）。</summary>
        public static float TotalAdded(CampaignState state, string kind) =>
            (state?.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>()).Where(t => t != null && t.Kind == kind).Sum(t => t.Added);

        // ── 阈值事件（沿用 Demo：只在从下向上跨越时触发一次）────────────────────────────

        /// <summary>30 可重复（降到阈值下再升高再次触发）；60/90 按“当前区域 + 当次远征”幂等。</summary>
        private static void CheckThresholdCrossing(CampaignState state, float before, float after)
        {
            if (before < ThresholdScoutTip && after >= ThresholdScoutTip)
            {
                state.ScoutTipCrossCount++;
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_scout_tip:{state.ScoutTipCrossCount}",
                    "ExposureThresholdScoutTip", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过30：静默侦察提示。");
            }
            if (before < ThresholdAdaptationIntel && after >= ThresholdAdaptationIntel)
            {
                RegionRecord region = FindCurrentRegion(state);
                string key = region != null ? $"{region.RegionId}:{region.ExpeditionCount}" : "no-region";
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_adaptation:{key}",
                    "ExposureThresholdAdaptationIntel", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过60：下一次出征 adaptation 情报可用。");
            }
            if (before < ThresholdCoreReinforcement && after >= ThresholdCoreReinforcement)
            {
                RegionRecord region = FindCurrentRegion(state);
                string key = region != null ? $"{region.RegionId}:{region.ExpeditionCount}" : "no-region";
                CampaignEventLedger.TryGrant(state, $"exposure_threshold_core_reinforcement:{key}",
                    "ExposureThresholdCoreReinforcement", state.PlaySeconds);
                Log.Info("[CampaignExposureLedger] 暴露越过90：下一次核心战入口护甲机增援预告。");
            }
        }

        /// <summary>三档阈值是否已跨越（HUD/日志展示当前"档位"，不重复实现判定逻辑）。</summary>
        public static bool HasReachedScoutTip(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdScoutTip;
        public static bool HasReachedAdaptationIntel(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdAdaptationIntel;
        public static bool HasReachedCoreReinforcement(CampaignState state) => (state?.SignalExposure ?? 0f) >= ThresholdCoreReinforcement;
    }
}
