using System;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-FAC-02（FG09 FGR-FAC-004 / 第 5 章“反制：在现有四态的基础上，按玩家常用类别选择”）：铸造阵营的反制——只在铸造自己的技术树内选对策，
    /// 每个游戏日更新一次并在当天锁定（替代 Demo 的“出发时锁定”，沿用 <see cref="EnemyAdaptationService"/> 的判定作为四态部分）。
    ///
    /// | 玩家常用 | 铸造的对策 | 落地 |
    /// |---|---|---|
    /// | 无人机载体（存活机器主武器是无人机的比例 ≥ foundry.counter.drone_share） | 对空加强：空爆炮台进驻 | 编成条件 counter_antiair |
    /// | 格斗载体（比例 ≥ foundry.counter.melee_share） | 雷区：布雷车在外围铺雷 | 编成条件 counter_minefield |
    /// | 其余 | Demo 四态（无 / 耐热 / 侧袭 / 干扰支援），沿用 <see cref="EnemyAdaptationService.ComputeAdaptation"/>（信号暴露 60 门槛） | <see cref="RegionRecord.AdaptationId"/> |
    ///
    /// 优先级：无人机 &gt; 格斗 &gt; 四态（同时满足时取前者，确定）。锁定在日切那一步（<see cref="WorldStep"/>，只看游戏日，与观察无关）或当天第一次查询时做；
    /// 出发时 <see cref="EnemyAdaptationService.LockAdaptation"/> 把当天的锁定值写进这次远征的区域记录。界面与情报只读 <see cref="PeekLocked"/> / <see cref="Preview"/>。
    /// 突袭的阵营反制仍走突袭导演自己的 fg.TbRaidCounter（铸造：引信 → 护甲机 / 攻城重甲……）。
    /// </summary>
    public static class FoundryCounterService
    {
        public const string CounterNone = "";

        public static int LockCount { get; private set; }

        public static void ResetForTests()
        {
            LockCount = 0;
        }

        private static FactionState F(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Factions == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Factions;
        }

        private static int Today(CampaignState s) => Math.Max(1, s?.Clock?.Day ?? 1);

        /// <summary>每个世界模拟步：日切那一步重算并锁定当天的对策（O(1)；日切当步 O(存活机器)）。第 1 天不在模拟步里锁（当天第一次查询时锁）。</summary>
        public static void WorldStep(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null || f.FoundryCounterDay == Today(s) || f.FoundryCounterDay == 0)
            {
                return;
            }
            Lock(s, f);
        }

        private static void Lock(CampaignState s, FactionState f)
        {
            f.FoundryCounterId = Decide(s);
            f.FoundryCounterDay = Today(s);
            LockCount++;
        }

        /// <summary>当天锁定的对策（AdaptationCatalog.* / counter_antiair / counter_minefield / 空串）。当天还没锁定时当场锁定一次（同一天之后不再变）。</summary>
        public static string Locked(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return CounterNone;
            }
            if (f.FoundryCounterDay != Today(s))
            {
                Lock(s, f);
            }
            return f.FoundryCounterId ?? CounterNone;
        }

        /// <summary>界面用：当天锁定的对策，只读——今天已锁定就读锁定值，否则按同一规则预先算出将要锁定的值，不写状态。</summary>
        public static string PeekLocked(CampaignState s)
        {
            FactionState f = s?.Factions;
            if (f != null && f.FoundryCounterDay == Today(s))
            {
                return f.FoundryCounterId ?? CounterNone;
            }
            return Decide(s);
        }

        /// <summary>下一次日切的预测（情报“下一次更新的预测”）：按当前构筑算，不改状态。</summary>
        public static string Preview(CampaignState s) => Decide(s);

        /// <summary>按当前构筑与暴露决定对策（纯函数）。</summary>
        public static string Decide(CampaignState s)
        {
            if (s == null)
            {
                return CounterNone;
            }
            BuildShares(s, out float drone, out float melee);
            if (drone >= Tuning("foundry.counter.drone_share", 0.4f))
            {
                return EnemyDefs.ConditionCounterAntiAir;
            }
            if (melee >= Tuning("foundry.counter.melee_share", 0.4f))
            {
                return EnemyDefs.ConditionCounterMinefield;
            }
            string four = EnemyAdaptationService.ComputeAdaptation(s);
            return four == AdaptationCatalog.None ? CounterNone : four;
        }

        /// <summary>锁定值里属于 Demo 四态的部分（写进 <see cref="RegionRecord.AdaptationId"/>）；新增的两种对策 → 无。</summary>
        public static string AdaptationPart(string counterId) =>
            counterId == AdaptationCatalog.HeatResistant || counterId == AdaptationCatalog.Flanker || counterId == AdaptationCatalog.JammerSupport
                ? counterId : AdaptationCatalog.None;

        /// <summary>存活机器里主武器是无人机 / 格斗载体的比例（按 AI 配置的装配解析；O(存活机器)，只在锁定与情报刷新时）。</summary>
        public static void BuildShares(CampaignState s, out float droneShare, out float meleeShare)
        {
            int total = 0, drone = 0, melee = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                MachineCombatResolution res = MachineLoadoutRegistry.ResolveForAi(s, m.LogicId, s.RandomSeed);
                if (!res.Success || res.Preview == null || string.IsNullOrEmpty(res.Preview.PrimaryId))
                {
                    continue;
                }
                total++;
                if (CarrierReadings.TryGetCarrier(res.Preview.PrimaryId, out FirmwareCarrier carrier))
                {
                    drone += carrier == FirmwareCarrier.Summon ? 1 : 0;
                    melee += carrier == FirmwareCarrier.Melee ? 1 : 0;
                }
            }
            droneShare = total > 0 ? drone / (float)total : 0f;
            meleeShare = total > 0 ? melee / (float)total : 0f;
        }

        public static string CounterName(string counterId) => GameText.Get(counterId switch
        {
            AdaptationCatalog.HeatResistant => "foundry.counter.heat",
            AdaptationCatalog.Flanker => "foundry.counter.flank",
            AdaptationCatalog.JammerSupport => "foundry.counter.jammer",
            EnemyDefs.ConditionCounterAntiAir => "foundry.counter.antiair",
            EnemyDefs.ConditionCounterMinefield => "foundry.counter.minefield",
            _ => "foundry.counter.none",
        });

        /// <summary>FGR-FAC-004：情报“敌方反制预览”里铸造前哨的一行（今天锁定 + 下一次日切预测）。</summary>
        public static string IntelLine(string locked, string next) =>
            GameText.Format("intel.counter.foundry", GameLogic.Campaign.Combat.CombatSites.SiteName(FoundryOutpostLayout.RegionId), CounterName(locked), CounterName(next));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
