using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>接入 HUD 一个信号核槽位的状态。</summary>
    public enum UplinkSlotState : byte
    {
        /// <summary>槽位是空的。</summary>
        Empty = 0,
        /// <summary>插进接入口、正在生效。</summary>
        Active = 1,
        /// <summary>插着，但核心固件冷却中（反应暂不发动）。</summary>
        Cooling = 2,
        /// <summary>没有插进去（超配额 / 超路径 / 没有接入口 / 不认识），<see cref="UplinkHudSlot.Reason"/> 写明原因。</summary>
        NotInserted = 3,
        /// <summary>插进去了，但接入口不在导线上，不生效。</summary>
        Ineffective = 4,
    }

    public struct UplinkHudSlot
    {
        /// <summary>信号核槽位（0 起；显示时 +1）。</summary>
        public int Index;
        public string FirmwareId;
        public UplinkSlotState State;
        /// <summary>未破解、由信号裸跑（FG1-SIG-06）。</summary>
        public bool Raw;
        public bool Core;
        public double CooldownLeft;
        /// <summary>未插入 / 不生效的原因（玩家可见文本，已按当前语言）。</summary>
        public string Reason;
    }

    /// <summary>链路强度档位（FGR-SIG-080“离覆盖边缘越近越弱”）。</summary>
    public enum UplinkLinkLevel : byte
    {
        /// <summary>这个地点不受覆盖限制（满格）。</summary>
        Unbounded = 0,
        Strong = 1,
        Medium = 2,
        Weak = 3,
        /// <summary>进入边缘预警（离边缘不到 signal.coverage.edge_warn_cells 格）。</summary>
        Edge = 4,
        /// <summary>已走出覆盖 / 在干扰场里（宽限中）。</summary>
        Lost = 5,
    }

    /// <summary>接入 HUD 的全部数据（一次 <see cref="UplinkHudModel.Build"/> 的结果；界面只读这里，不自己算）。</summary>
    public sealed class UplinkHudSnapshot
    {
        public int LogicId;
        public string Label = string.Empty;
        public string ChassisName = string.Empty;
        public bool HasPort;
        public bool CoreEmpty;
        public readonly List<UplinkHudSlot> Slots = new List<UplinkHudSlot>(5);
        public float Heat;
        public float HeatMax;
        public float HeatRecover;
        public bool Overheated;
        public float Battery;
        public float BatteryMax;
        public float Health;
        public float HealthMax;
        public string[] Injuries = Array.Empty<string>();
        public float LinkStrength01;
        public UplinkLinkLevel LinkLevel;
        public float LinkMarginCells;
        public SignalLinkBreakReason LinkLostReason;
        public float LinkGraceLeft;
        public float Exposure;
        public MorphMask Morph;
        public readonly List<string> MorphSources = new List<string>(4);
    }

    /// <summary>
    /// FG1-HUD-01（FG01 FGR-SIG-080；FG13 FGU-33；FG-GAP-045）：接入 HUD 的数据层——信号核各槽的固件与冷却 / 未插入原因、当前机体的热量 / 电池 / 伤势、
    /// 链路强度、暴露、机体编号与名字、机身状态与来源固件。全部读既有唯一真相（信号核、<see cref="MachineLoadoutRegistry.PlanForUplink"/> 与
    /// <see cref="MachineLoadoutRegistry.ResolveForPilot"/>、战斗内核热量、机器记录、覆盖采样、暴露），不另存任何状态。
    /// 开销：<see cref="BuildSlots"/> = O(槽位数) + 1 次装配解析，只在 <see cref="SlotsKey"/> 变化时调用；<see cref="BuildVitals"/> = O(1)，界面按量化键每帧比较。
    /// </summary>
    public static class UplinkHudModel
    {
        private static readonly Dictionary<int, bool> PortCache = new Dictionary<int, bool>(32);
        private static bool _hooked;

        /// <summary>自检读点：装配解析（带接入口判断 / 机身来源）真正执行的次数。</summary>
        public static int ResolveCount { get; private set; }

        public static float FullStrengthCells => Tuning("signal.link.strength_full_cells", 60f);
        public static float StrongPercent => Tuning("signal.link.strength_strong_percent", 67f);
        public static float MediumPercent => Tuning("signal.link.strength_medium_percent", 34f);

        // ── 快照 ──────────────────────────────────────────────────────────────

        /// <summary>整份快照（槽位 + 机体）。机器不存在时返回 false。</summary>
        public static bool Build(CampaignState s, int logicId, UplinkHudSnapshot into)
        {
            if (!BuildSlots(s, logicId, into))
            {
                return false;
            }
            BuildVitals(s, logicId, into);
            return true;
        }

        /// <summary>槽位、有无接入口、机身状态与来源（需要装配解析，按 <see cref="SlotsKey"/> 变化才调用）。</summary>
        public static bool BuildSlots(CampaignState s, int logicId, UplinkHudSnapshot into)
        {
            into.Slots.Clear();
            into.MorphSources.Clear();
            into.LogicId = logicId;
            if (s == null || logicId <= 0 || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return false;
            }
            into.Label = SignalPresence.MachineLabel(logicId);
            into.ChassisName = MechanicalContentFacade.ResolveChassisLabel(rec.ChassisId) ?? string.Empty;
            string[] core = SignalCoreService.CurrentContentIds(s);
            UplinkInsertionPlan plan = MachineLoadoutRegistry.PlanForUplink(s, logicId, core);
            ResolveCount++;
            into.HasPort = plan != null && plan.HasUplink;
            into.CoreEmpty = plan != null && plan.CoreEmpty;
            bool offPath = false;
            BlueprintCircuitPreview preview = null;
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, logicId, s.RandomSeed);
            if (r.Success)
            {
                preview = r.Preview;
            }
            if (plan != null && plan.Inserted.Count > 0 && preview != null && SignalUplinkService.IsUplinked(s, logicId) && !preview.UplinkOnPath)
            {
                offPath = true;
            }
            int unlocked = SignalCoreService.UnlockedSlots(s);
            for (int i = 0; i < unlocked; i++)
            {
                string content = SignalCoreService.SlotContentId(s, i);
                var slot = new UplinkHudSlot { Index = i, FirmwareId = content, State = UplinkSlotState.Empty, Reason = string.Empty };
                if (content.Length > 0)
                {
                    slot.Raw = FirmwareKinds.IsRaw(s, content);
                    slot.Core = FirmwareKinds.IsCore(content);
                    UplinkFirmwareEntry? inserted = FindEntry(plan?.Inserted, i);
                    UplinkFirmwareEntry? skipped = FindEntry(plan?.Skipped, i);
                    if (plan == null || !plan.HasUplink)
                    {
                        slot.State = UplinkSlotState.NotInserted;
                        slot.Reason = GameText.Get("uplink.hud.reason.no_uplink");
                    }
                    else if (inserted.HasValue)
                    {
                        double left = SignalUplinkService.CooldownRemaining(s, content);
                        slot.CooldownLeft = Math.Max(0, left);
                        if (offPath)
                        {
                            slot.State = UplinkSlotState.Ineffective;
                            slot.Reason = GameText.Get("uplink.hud.reason.off_path");
                        }
                        else
                        {
                            slot.State = slot.Core && left > 0 ? UplinkSlotState.Cooling : UplinkSlotState.Active;
                        }
                    }
                    else if (skipped.HasValue)
                    {
                        slot.State = UplinkSlotState.NotInserted;
                        slot.Reason = SkipReason(skipped.Value.Skip, plan);
                    }
                    else
                    {
                        slot.State = UplinkSlotState.NotInserted;
                        slot.Reason = GameText.Get("uplink.hud.reason.unknown");
                    }
                }
                into.Slots.Add(slot);
            }
            into.Morph = MorphOf(s, rec);
            AppendMorphSources(preview, into.Morph, into.MorphSources);
            return true;
        }

        /// <summary>机体数值、链路、暴露（O(1)，不做装配解析）。</summary>
        public static void BuildVitals(CampaignState s, int logicId, UplinkHudSnapshot into)
        {
            into.HeatMax = FracturedCityLayout.WeaponHeatOverheatThreshold;
            into.HeatRecover = FracturedCityLayout.WeaponHeatRecoverThreshold;
            into.Heat = 0f;
            into.Overheated = false;
            into.Exposure = s?.SignalExposure ?? 0f;
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return;
            }
            CombatSite site = CombatSites.Get(rec.RegionId);
            if (site != null && site.TryGetMachineHeat(logicId, out float heat, out bool over))
            {
                into.Heat = heat;
                into.Overheated = over;
            }
            else
            {
                into.Heat = rec.WeaponHeat;
                into.Overheated = rec.IsWeaponOverheated;
            }
            into.Battery = rec.Battery;
            into.BatteryMax = HomeValleyLayout.BatteryCapacity.TryGetValue(rec.ChassisId ?? string.Empty, out float cap) ? cap : 100f;
            into.Health = rec.Health;
            into.HealthMax = rec.MaxHealth;
            into.Injuries = rec.InjuryFlags ?? Array.Empty<string>();
            FillLink(logicId, into);
        }

        /// <summary>槽位部分的变化键：接入状态（含冷却整秒、信号核、固件表、语言）、机器、装配登记。</summary>
        public static int SlotsKey(CampaignState s, int logicId) =>
            HashCode.Combine(SignalUplinkService.StatusKey(s), logicId, PortRevision, (int)GameText.Language);

        /// <summary>机体部分的变化键（量化到显示精度：热量 / 电池 / 耐久整数、链路百分比、宽限整秒、暴露一位小数）。</summary>
        public static int VitalsKey(UplinkHudSnapshot v) =>
            HashCode.Combine(HashCode.Combine(Mathf.RoundToInt(v.Heat), v.Overheated, Mathf.RoundToInt(v.Battery), Mathf.RoundToInt(v.Health), Mathf.RoundToInt(v.HealthMax), v.Injuries.Length),
                Mathf.RoundToInt(v.LinkStrength01 * 100f), v.LinkLevel, Mathf.CeilToInt(v.LinkGraceLeft), Mathf.RoundToInt(v.LinkMarginCells),
                Mathf.RoundToInt(v.Exposure * 10f), (int)GameText.Language);

        // ── 链路强度 ───────────────────────────────────────────────────────────

        private static void FillLink(int logicId, UplinkHudSnapshot into)
        {
            SignalCoverageSample sample = SignalLinkService.WatchedLogicId == logicId ? SignalLinkService.WatchedSample : SignalCoverageService.SampleMachine(logicId);
            SignalLinkBreakReason grace = SignalLinkService.WatchedLogicId == logicId ? SignalLinkService.GraceReason : SignalLinkBreakReason.None;
            into.LinkLostReason = grace;
            into.LinkGraceLeft = grace != SignalLinkBreakReason.None ? SignalLinkService.GraceLeft : 0f;
            into.LinkMarginCells = sample.Bounded ? sample.MarginCells : float.PositiveInfinity;
            into.LinkLevel = LevelOf(sample, grace, out into.LinkStrength01);
            if (into.LinkLevel == UplinkLinkLevel.Lost && grace == SignalLinkBreakReason.None)
            {
                into.LinkLostReason = SignalLinkBreakReason.OutOfCoverage;
            }
        }

        /// <summary>覆盖采样 → 强度（0～1）与档位。没有边界 = 满格；宽限中 / 覆盖外 = 0；离边缘 ≥ full_cells 格 = 1，线性。</summary>
        public static UplinkLinkLevel LevelOf(SignalCoverageSample sample, SignalLinkBreakReason grace, out float strength01)
        {
            if (!sample.Bounded)
            {
                strength01 = grace != SignalLinkBreakReason.None ? 0f : 1f;
                return grace != SignalLinkBreakReason.None ? UplinkLinkLevel.Lost : UplinkLinkLevel.Unbounded;
            }
            if (grace != SignalLinkBreakReason.None || !sample.Covered || sample.MarginCells <= 0f)
            {
                strength01 = 0f;
                return UplinkLinkLevel.Lost;
            }
            strength01 = Mathf.Clamp01(sample.MarginCells / Mathf.Max(1f, FullStrengthCells));
            if (sample.MarginCells < SignalCoverageService.EdgeWarnCells)
            {
                return UplinkLinkLevel.Edge;
            }
            float pct = strength01 * 100f;
            return pct >= StrongPercent ? UplinkLinkLevel.Strong : pct >= MediumPercent ? UplinkLinkLevel.Medium : UplinkLinkLevel.Weak;
        }

        // ── 机身状态（FG-GAP-045）─────────────────────────────────────────────

        /// <summary>机器现在的机身状态（读战斗桥接层与武器参数同一次解析的结果；没有时按记录重新解析）。</summary>
        public static MorphMask MorphOf(CampaignState s, MachineRecord rec)
        {
            if (rec == null || !rec.IsAlive)
            {
                return MorphMask.None;
            }
            CombatSite site = CombatSites.Get(rec.RegionId);
            if (site != null && site.TryGetMachineWeapon(rec.LogicId, out MachineWeaponInfo info))
            {
                return info.Morph;
            }
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, rec.LogicId, s?.RandomSeed ?? 0);
            ResolveCount++;
            return r.Success ? MachineMorph.MaskOf(r.Preview) : MorphMask.None;
        }

        /// <summary>“形变态 + 喷口态（过载、拖尾）”/“常态”——机器列表悬停、机器详情用（O(1) 次装配解析，只在显示提示 / 刷新详情时调用）。</summary>
        public static string MorphText(CampaignState s, int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return StateName(MorphMask.None);
            }
            MorphMask mask = MorphOf(s, rec);
            if (mask == MorphMask.None)
            {
                return StateName(MorphMask.None);
            }
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, logicId, s?.RandomSeed ?? 0);
            ResolveCount++;
            var sources = new List<string>(4);
            AppendMorphSources(r.Success ? r.Preview : null, mask, sources);
            return Compose(mask, sources);
        }

        /// <summary>状态名 +（来源固件名）。</summary>
        public static string Compose(MorphMask mask, IReadOnlyList<string> sourceIds)
        {
            string state = StateName(mask);
            if (mask == MorphMask.None || sourceIds == null || sourceIds.Count == 0)
            {
                return state;
            }
            var names = new StringBuilder();
            string sep = GameText.Get("signal.uplink.status.list_sep");
            for (int i = 0; i < sourceIds.Count; i++)
            {
                if (i > 0)
                {
                    names.Append(sep);
                }
                names.Append(UplinkCompiler.FirmwareName(sourceIds[i]));
            }
            return GameText.Format("uplink.hud.morph_source", state, names.ToString());
        }

        /// <summary>机身状态名（多类叠加用“ + ”连接；FG-GAP-045 的文本键）。</summary>
        public static string StateName(MorphMask mask)
        {
            if (mask == MorphMask.None)
            {
                return GameText.Get("morph.state.none");
            }
            var parts = new List<string>(3);
            if ((mask & MorphMask.Limiter) != 0)
            {
                parts.Add(GameText.Get("morph.state.limiter"));
            }
            if ((mask & MorphMask.Fluid) != 0)
            {
                parts.Add(GameText.Get("morph.state.fluid"));
            }
            if ((mask & MorphMask.Electromagnetic) != 0)
            {
                parts.Add(GameText.Get("morph.state.em"));
            }
            return string.Join(GameText.Get("morph.state.join"), parts);
        }

        private static void AppendMorphSources(BlueprintCircuitPreview preview, MorphMask mask, List<string> into)
        {
            if (preview?.FirmwareIds == null || mask == MorphMask.None)
            {
                return;
            }
            foreach (string id in preview.FirmwareIds)
            {
                MorphMask bit = MachineMorph.BitOf(id);
                if (bit != MorphMask.None && (mask & bit) != 0 && !into.Contains(id))
                {
                    into.Add(id);
                }
            }
        }

        // ── 带接入口的机器（FGR-SIG-081）──────────────────────────────────────

        /// <summary>装配登记变化的版本号（带接入口判断的缓存随它失效）。</summary>
        public static int PortRevision { get; private set; } = 1;

        /// <summary>这台机器登记的装配有没有接入口（缓存；装配登记变化时失效，O(1)）。没登记时 false。</summary>
        public static bool HasUplinkPort(CampaignState s, int logicId)
        {
            Hook();
            if (PortCache.TryGetValue(logicId, out bool has))
            {
                return has;
            }
            UplinkInsertionPlan plan = MachineLoadoutRegistry.PlanForUplink(s, logicId, Array.Empty<string>());
            ResolveCount++;
            has = plan != null && plan.HasUplink;
            if (plan != null)
            {
                PortCache[logicId] = has; // 没登记（null）不缓存：登记后第一次查询就能拿到真值。
            }
            return has;
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            MachineLoadoutRegistry.Changed += OnLoadoutChanged;
        }

        private static void OnLoadoutChanged(int logicId)
        {
            if (logicId == 0)
            {
                PortCache.Clear();
            }
            else
            {
                PortCache.Remove(logicId);
            }
            PortRevision++;
        }

        public static void ResetForTests()
        {
            PortCache.Clear();
            PortRevision++;
            ResolveCount = 0;
        }

        // ── 文本 ──────────────────────────────────────────────────────────────

        /// <summary>一个槽位的状态文字（“生效”“冷却 3 秒”“未插入：超出接入口配额 2 个”……；裸跑再加“裸跑”）。</summary>
        public static string SlotStateText(in UplinkHudSlot slot)
        {
            string state = slot.State switch
            {
                UplinkSlotState.Empty => GameText.Get("uplink.hud.state.empty"),
                UplinkSlotState.Active => GameText.Get("uplink.hud.state.active"),
                UplinkSlotState.Cooling => GameText.Format("uplink.hud.state.cooling", Math.Ceiling(slot.CooldownLeft).ToString("0", CultureInfo.InvariantCulture)),
                UplinkSlotState.Ineffective => GameText.Format("uplink.hud.state.ineffective", slot.Reason),
                _ => GameText.Format("uplink.hud.state.not_inserted", slot.Reason),
            };
            return slot.Raw ? state + " · " + GameText.Get("uplink.hud.state.raw") : state;
        }

        /// <summary>链路行文字。</summary>
        public static string LinkText(UplinkHudSnapshot v)
        {
            switch (v.LinkLevel)
            {
                case UplinkLinkLevel.Unbounded:
                    return GameText.Get("uplink.hud.link.unbounded");
                case UplinkLinkLevel.Lost:
                    return GameText.Format("uplink.hud.link.grace", SignalLinkService.ReasonName(v.LinkLostReason == SignalLinkBreakReason.None ? SignalLinkBreakReason.OutOfCoverage : v.LinkLostReason),
                        Mathf.CeilToInt(v.LinkGraceLeft).ToString(CultureInfo.InvariantCulture));
                case UplinkLinkLevel.Edge:
                    return GameText.Format("uplink.hud.link", Pct(v.LinkStrength01), GameText.Format("uplink.hud.link.edge", Mathf.FloorToInt(Mathf.Max(0f, v.LinkMarginCells)).ToString(CultureInfo.InvariantCulture)));
                case UplinkLinkLevel.Strong:
                    return GameText.Format("uplink.hud.link", Pct(v.LinkStrength01), GameText.Get("uplink.hud.link.strong"));
                case UplinkLinkLevel.Medium:
                    return GameText.Format("uplink.hud.link", Pct(v.LinkStrength01), GameText.Get("uplink.hud.link.medium"));
                default:
                    return GameText.Format("uplink.hud.link", Pct(v.LinkStrength01), GameText.Get("uplink.hud.link.weak"));
            }
        }

        public static string Pct(float v01) => Mathf.RoundToInt(Mathf.Clamp01(v01) * 100f).ToString(CultureInfo.InvariantCulture);

        private static string SkipReason(UplinkSkipReason skip, UplinkInsertionPlan plan) => skip switch
        {
            UplinkSkipReason.OverQuota => GameText.Format("uplink.hud.reason.over_quota", plan?.Quota ?? 0),
            UplinkSkipReason.PathLimit => GameText.Format("uplink.hud.reason.path_limit", plan?.PathLimit ?? 0),
            UplinkSkipReason.Unknown => GameText.Get("uplink.hud.reason.unknown"),
            _ => GameText.Get("uplink.hud.reason.no_uplink"),
        };

        private static UplinkFirmwareEntry? FindEntry(List<UplinkFirmwareEntry> list, int coreSlot)
        {
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].CoreSlot == coreSlot)
                {
                    return list[i];
                }
            }
            return null;
        }

        private static float Tuning(string id, float fallback) =>
            GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
