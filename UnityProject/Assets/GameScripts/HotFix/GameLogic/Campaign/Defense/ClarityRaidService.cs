using System;
using System.Collections.Generic;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG10-RAID-01（FG09 第 6 章“突袭特点：专门袭击净化塔和已净化区域，使其再污染；大量腐蚀单位攻击传送带和管线”；FG07 FGR-ENV-043；验收 FGT-FAC-007）：
    /// 澄净突袭的家园一侧。<b>唯一写入口</b>（<see cref="FactionState.ClarityRaidNextTick"/> 等四个字段）。
    ///
    /// - <b>触发</b>（收口 DEBT-FG6DEF04-01 的化工一份）：澄净的领地在战略地图上出现（与反应开放 / 反制情报同一口径）且家园有净化塔之后，
    ///   隔 raid.clarity.purify_first_delay_days 第一次调 <see cref="RaidDirectorService.OnPurificationTowerTargeted"/>，之后每 raid.clarity.purify_period_days ÷ 难度频率一次；
    ///   导演按普通触发排定（受最短间隔约束、首次突袭 1 级）。别的阵营的普通突袭还没出发时顺延 0.25 日（不并进别的阵营的计划，免得“针对净化塔”被吞掉）。
    ///   每游戏秒一次 O(1)（净化塔数在生产索引里，有缓存）。
    /// - <b>目标偏好</b>：攻城剧场里有澄净队伍 → <see cref="SiegeService"/> 换上净化塔为主目标的职能表（<see cref="SiegeCatalog.RoleBiasFor"/>）。
    /// - <b>投放目标</b>（收口 DEBT-FG10FAC01-02）：同一时刻给家园内核写灭活无人机的投放目标 = 每座净化塔（含被毁的：它净化回来的那一片还在）的中心，半径 raid.clarity.repollute_radius；
    ///   已净化区就在塔的周围，落点即“净化塔和已净化区”。只在有澄净队伍攻城时写（别的时候清空：星球上别处的澄净无人机不会被吸过来）；
    ///   随剧场对账 / 读档在第一步之前写（派生量，不进快照，读档后与不读档逐步一致）。
    /// - <b>再污染落地</b>（收口 DEBT-FG7ENV04-02）：内核投放完成事件 → <see cref="OnRepolluted"/> → <see cref="PurificationService.Repollute"/>（格子升级、建筑受污染、通知写重新净化的路径）
    ///   → 突袭结算 / 离家报告记一笔（<see cref="RaidResultService.NoteRepollution"/>）。
    /// 不做玩家没要求的事（FGR-BASE-020）：只改敌人的目标与污染层；净化塔照玩家的设置自动从内圈重新净化，自动重建规则照常生效。
    /// </summary>
    public static class ClarityRaidService
    {
        private const string Clarity = RaidCatalog.FactionChemical;

        // ── 调参 ─────────────────────────────────────────────────────────────

        public static float FirstDelayDays => Math.Max(0f, T("raid.clarity.purify_first_delay_days", 1f));
        public static float PeriodDays => Math.Max(1f, T("raid.clarity.purify_period_days", 3f));
        public static int RepolluteRadius => Math.Max(1, (int)Math.Round(T("raid.clarity.repollute_radius", 6f)));
        public static int RepolluteLevels => Math.Max(1, Math.Min(3, (int)Math.Round(T("raid.clarity.repollute_levels", 1f))));

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        // ── 自检读点 ─────────────────────────────────────────────────────────

        /// <summary>本会话发出的“针对净化塔”触发次数、落地的再污染次数、写投放目标的次数。</summary>
        public static int Triggered { get; private set; }
        public static int Landed { get; private set; }
        public static int TargetWrites { get; private set; }

        public static void ResetForTests()
        {
            Triggered = Landed = TargetWrites = 0;
            _targetSite = null;
            _targetRevision = int.MinValue;
            _targetClarity = false;
        }

        // ─────────────────────────────── 触发 ───────────────────────────────

        /// <summary>澄净的领地在地图上显示了没有（第二幕起，或玩家提前揭开它的危害带；与反应开放同一口径）。</summary>
        public static bool ClarityVisible(CampaignState s)
        {
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(Clarity);
            return t != null && HomeExplorationService.TerritoryVisible(s, t);
        }

        /// <summary>家园里立着的净化塔数（不含规划虚影；被毁的也算——它净化回来的那一片还在）。</summary>
        public static int StandingPurifiers(CampaignState s)
        {
            int n = 0;
            foreach (ProductionService.Producer p in PurificationService.PurifiersOf(s))
            {
                if (p?.Building != null && !HomeValleyController.IsPlannedGhost(p.Building))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>每游戏秒一次（世界步，与观察无关）：排“针对净化塔”的触发。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (s?.Factions == null || s.Raids == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            FactionState f = s.Factions;
            if (!ClarityVisible(s) || StandingPurifiers(s) == 0 || DifficultyService.StoryOnly(s))
            {
                return; // 条件不满足时不排（满足那一刻起再算首次延迟）；建造者难度只有剧情突袭
            }
            if (f.ClarityRaidNextTick < 0)
            {
                f.ClarityRaidNextTick = tick + RaidDirectorService.DayTicks(FirstDelayDays);
                return;
            }
            if (tick < f.ClarityRaidNextTick)
            {
                return;
            }
            RaidPlanRecord pending = RaidDirectorService.PendingNormalPlan(s.Raids.Director);
            if (pending != null && pending.Faction != Clarity)
            {
                f.ClarityRaidNextTick = tick + RaidDirectorService.DayTicks(0.25); // 别的阵营的普通突袭还没出发：顺延，不并进它
                return;
            }
            if (RaidDirectorService.OnPurificationTowerTargeted(s))
            {
                f.ClarityRaidTriggers++;
                Triggered++;
            }
            f.ClarityRaidNextTick = tick + RaidDirectorService.DayTicks(PeriodDays / Math.Max(0.05f, DifficultyService.Frequency(s)));
        }

        // ─────────────────────────────── 投放目标 ───────────────────────────────

        private static readonly List<float3> Targets = new List<float3>(8);
        private static CombatSite _targetSite;
        private static int _targetRevision = int.MinValue;
        private static bool _targetClarity;

        /// <summary>
        /// 家园内核的灭活无人机投放目标：有澄净队伍攻城时 = 每座净化塔的中心（x, y, 再污染半径），否则清空。生产索引 / 澄净在场 / 内核实例没变时跳过（O(1)）；
        /// <paramref name="force"/> = 读档 / 剧场重设时无条件重写（内核快照不存它）。
        /// </summary>
        public static void ApplyPolluteTargets(CampaignState s, CombatSite site, bool clarity, bool force)
        {
            if (site == null || site.IsDisposed)
            {
                return;
            }
            _ = ProductionService.ProducerCount; // 先让生产索引按当前建筑建好（建筑变了 → 索引重建、Revision 变）
            int rev = ProductionService.Revision;
            if (!force && ReferenceEquals(site, _targetSite) && rev == _targetRevision && clarity == _targetClarity)
            {
                return;
            }
            _targetSite = site;
            _targetRevision = rev;
            _targetClarity = clarity;
            Targets.Clear();
            if (clarity)
            {
                float r = RepolluteRadius;
                foreach (ProductionService.Producer p in PurificationService.PurifiersOf(s))
                {
                    if (p?.Building != null && !HomeValleyController.IsPlannedGhost(p.Building) && p.Building.RegionId == HomeValleyLayout.RegionId)
                    {
                        Targets.Add(new float3(p.Building.GridX, p.Building.GridY, r));
                    }
                }
            }
            site.SetPolluteTargets(Targets);
            TargetWrites++;
        }

        // ─────────────────────────────── 再污染落地 ───────────────────────────────

        /// <summary>
        /// 内核“投放完成”事件（<see cref="ClarityCombatFeedback.OnRepolluted"/> 调用）：只在家园 / 星球内核上落地——落点半径（事件带的半径，没有用 raid.clarity.repollute_radius）内
        /// 每格污染升 raid.clarity.repollute_levels 级。返回再污染结果（不是家园 / 没有战役 = 空结果）。
        /// </summary>
        public static RepolluteResult OnRepolluted(CombatSite site, Vector2 at, float radius)
        {
            CampaignState s = CampaignSession.Current;
            if (s?.Factions == null || site == null || site.SiteId != HomeValleyLayout.RegionId)
            {
                return default;
            }
            var center = new GridCell(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y));
            int r = radius >= 1f ? Mathf.RoundToInt(radius) : RepolluteRadius;
            RepolluteResult res = PurificationService.Repollute(s, center, r, RepolluteLevels);
            s.Factions.ClarityRaidDrops++;
            s.Factions.ClarityRaidCells += res.CellsRaised;
            Landed++;
            RaidResultService.NoteRepollution(s, res.CellsRaised, res.BuildingsContaminated, center);
            if (res.CellsRaised == 0)
            {
                Log.Info($"[ClarityRaid] 灭活无人机在 ({center.X},{center.Y}) 投放：这一片已经是 3 级污染，没有再升级。");
            }
            return res;
        }
    }
}
