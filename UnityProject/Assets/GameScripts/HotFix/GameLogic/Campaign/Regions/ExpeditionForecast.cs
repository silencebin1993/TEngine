using System.Collections.Generic;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>出征风险等级。</summary>
    public enum ExpeditionRisk
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    /// <summary>DEBT-ER5EXP01-01：远征准备面板“总火力 / 维修能力 / 风险”的真实计算（此前只显示“武器 有/无”）。
    ///
    /// 口径（写进这里就是设计依据，面板同时显示原始数字，玩家能看懂怎么来的）：
    /// - 每轮火力＝所选机器各自装配单次命中伤害之和（铸造重炮取固定基础伤害，其余取电路编译出的
    ///   <see cref="BlueprintCircuitPreview.TotalNormalizedDamage"/>，与实战结算同一来源）；
    /// - 维修能力＝主组件为维修束的机器台数；我方耐久＝所选机器当前耐久之和；
    /// - 敌方＝目标区域已生成的存活敌人耐久之和；尚未进入过该区域（敌人未生成）时按区域编制估算，
    ///   并标注“估算”——估算表引用的就是区域播种用的同一批耐久常量，自检会拿它与真实播种结果对账；
    /// - 约几轮清场＝敌方耐久 ÷ 每轮火力；风险：≤6 轮低、≤12 轮中、其余高；目标区域警戒 ≥60 再升一档；
    ///   没有任何武器直接判高。
    /// 纯查询，不写任何状态。</summary>
    public static class ExpeditionForecast
    {
        public const float LowRiskMaxRounds = 6f;
        public const float MediumRiskMaxRounds = 12f;
        public const float AlertBumpThreshold = 60f;

        public readonly struct Result
        {
            public readonly float FirepowerPerRound;
            public readonly int RepairUnits;
            public readonly float TeamHealth;
            public readonly int EnemyCount;
            public readonly float EnemyHealth;
            public readonly bool EnemyEstimated;
            public readonly float RoundsToClear;
            public readonly ExpeditionRisk Risk;

            public Result(float firepower, int repairUnits, float teamHealth, int enemyCount, float enemyHealth,
                bool enemyEstimated, float roundsToClear, ExpeditionRisk risk)
            {
                FirepowerPerRound = firepower;
                RepairUnits = repairUnits;
                TeamHealth = teamHealth;
                EnemyCount = enemyCount;
                EnemyHealth = enemyHealth;
                EnemyEstimated = enemyEstimated;
                RoundsToClear = roundsToClear;
                Risk = risk;
            }
        }

        public static Result Compute(CampaignState state, IReadOnlyList<int> selectedLogicIds,
            ExpeditionDepartureService.ExpeditionTarget target)
        {
            float firepower = 0f;
            int repairUnits = 0;
            float teamHealth = 0f;
            if (selectedLogicIds != null)
            {
                for (int i = 0; i < selectedLogicIds.Count; i++)
                {
                    int logicId = selectedLogicIds[i];
                    if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord machine) || !machine.IsAlive)
                    {
                        continue;
                    }
                    teamHealth += machine.Health;
                    MachineCombatResolution resolution = MachineLoadoutRegistry.ResolveForAi(state, logicId, 0);
                    if (!resolution.Success || resolution.Preview == null)
                    {
                        continue;
                    }
                    if (resolution.Preview.PrimaryId == ComponentCatalog.CompRepairBeamId)
                    {
                        repairUnits++;
                        continue;
                    }
                    if (resolution.Preview.HasCannonPrimary)
                    {
                        firepower += FracturedCityLayout.CannonBaseDamage;
                    }
                    else if (resolution.Preview.HasCombatOutput)
                    {
                        firepower += Mathf.Max(0f, resolution.Preview.TotalNormalizedDamage);
                    }
                }
            }

            string regionId = RegionIdFor(target);
            int enemyCount = 0;
            float enemyHealth = 0f;
            bool estimated = false;
            if (regionId != null && state?.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord enemy in state.RegionEnemies)
                {
                    if (enemy != null && enemy.RegionId == regionId && enemy.IsAlive
                        && enemy.EnemyTypeId != FoundryOutpostLayout.BossNodeTypeId
                        && enemy.EnemyTypeId != FoundryOutpostLayout.BossCoreTypeId)
                    {
                        enemyCount++;
                        enemyHealth += enemy.Health;
                    }
                }
            }
            if (enemyCount == 0 && regionId != null && !HasAnyRecordFor(state, regionId))
            {
                EstimateGarrison(target, out enemyCount, out enemyHealth);
                estimated = true;
            }

            float rounds = firepower > 0f ? enemyHealth / firepower : float.PositiveInfinity;
            RegionRecord region = FindRegion(state, regionId);
            ExpeditionRisk risk = RiskFor(firepower, enemyHealth, region != null ? region.EnemyAlertLevel : 0f);
            return new Result(firepower, repairUnits, teamHealth, enemyCount, enemyHealth, estimated, rounds, risk);
        }

        /// <summary>风险分档（纯函数，自检直接断言边界）：没有火力→高；按“约几轮清场”分档，警戒 ≥60 升一档。</summary>
        public static ExpeditionRisk RiskFor(float firepowerPerRound, float enemyHealth, float alertLevel)
        {
            if (firepowerPerRound <= 0f)
            {
                return ExpeditionRisk.High;
            }
            float rounds = enemyHealth / firepowerPerRound;
            ExpeditionRisk risk = rounds <= LowRiskMaxRounds ? ExpeditionRisk.Low
                : rounds <= MediumRiskMaxRounds ? ExpeditionRisk.Medium
                : ExpeditionRisk.High;
            if (alertLevel >= AlertBumpThreshold && risk < ExpeditionRisk.High)
            {
                risk++;
            }
            return risk;
        }

        /// <summary>尚未进入过的区域按播种编制估算（与 EnsureEnemiesSeeded 同一批常量；自检对账）。</summary>
        public static void EstimateGarrison(ExpeditionDepartureService.ExpeditionTarget target, out int count, out float health)
        {
            switch (target)
            {
                case ExpeditionDepartureService.ExpeditionTarget.SilentRuins:
                    count = 3;
                    health = FracturedCityLayout.ScoutMaxHealth * 2f + FracturedCityLayout.JammerMaxHealth;
                    return;
                case ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost:
                    count = 4;
                    health = FoundryOutpostLayout.ArmorBotMaxHealth * 2f + FoundryOutpostLayout.StriderMaxHealth
                             + FoundryOutpostLayout.RepairBotMaxHealth;
                    return;
                default:
                    count = 0;
                    health = 0f;
                    return;
            }
        }

        public static string RiskText(ExpeditionRisk risk)
        {
            switch (risk)
            {
                case ExpeditionRisk.Low: return "低";
                case ExpeditionRisk.Medium: return "中";
                default: return "高";
            }
        }

        /// <summary>面板一行文字（数字全部真实计算，可读出口径）。</summary>
        public static string Describe(Result r)
        {
            string rounds = float.IsInfinity(r.RoundsToClear) ? "无法清场（没有武器）" : $"约 {Mathf.CeilToInt(r.RoundsToClear)} 轮清场";
            string enemy = r.EnemyCount > 0
                ? $"敌方 {r.EnemyCount} 个·耐久 {r.EnemyHealth:F0}{(r.EnemyEstimated ? "（按编制估算）" : string.Empty)}"
                : "未发现存活敌人";
            return $"预估：火力 {r.FirepowerPerRound:F0}/轮｜维修 {r.RepairUnits} 台｜我方耐久 {r.TeamHealth:F0}｜{enemy}｜{rounds}｜风险 {RiskText(r.Risk)}";
        }

        private static string RegionIdFor(ExpeditionDepartureService.ExpeditionTarget target)
        {
            switch (target)
            {
                case ExpeditionDepartureService.ExpeditionTarget.SilentRuins: return FracturedCityLayout.RegionId;
                case ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost: return FoundryOutpostLayout.RegionId;
                default: return null;
            }
        }

        private static bool HasAnyRecordFor(CampaignState state, string regionId)
        {
            if (state?.RegionEnemies == null)
            {
                return false;
            }
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy != null && enemy.RegionId == regionId)
                {
                    return true;
                }
            }
            return false;
        }

        private static RegionRecord FindRegion(CampaignState state, string regionId)
        {
            if (state?.RegionRecords == null || regionId == null)
            {
                return null;
            }
            foreach (RegionRecord region in state.RegionRecords)
            {
                if (region != null && region.RegionId == regionId)
                {
                    return region;
                }
            }
            return null;
        }
    }
}
