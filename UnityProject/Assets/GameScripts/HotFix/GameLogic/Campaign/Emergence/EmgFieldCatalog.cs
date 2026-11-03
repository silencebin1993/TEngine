using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG2-FLD-01（FG19 FGR-EMG-122；FG19B 第 5 节）：把 fg.TbFieldCarrierRule 编成内核 <see cref="EmgFieldRule"/>（20 行，下标 = <see cref="EmgFieldConst"/> 行号）。只在地点载入 / 表重载时编译。
    /// 内核只拿幅度、阵营关系、执行者码，按下标读，不拿规则 ID（FGR-EMG-013）。行序 / 阵营 / 执行者不对时整表拒绝（内核保留缺省表并 Log.Error，不静默）。
    /// </summary>
    public static class EmgFieldCatalog
    {
        public sealed class Compiled
        {
            public readonly List<EmgFieldRule> Rules = new List<EmgFieldRule>();
            public readonly List<string> Ids = new List<string>();
            public readonly List<string> Problems = new List<string>();
            public bool Ok => Problems.Count == 0;
        }

        /// <summary>行序（= 内核 EmgFieldConst 行号，FG19B 第 5 节表序）。</summary>
        public static readonly string[] Order =
        {
            "fieldrule_corona_payload", "fieldrule_corona_unit", "fieldrule_magnet_payload", "fieldrule_magnet_unit", "fieldrule_deflect_payload", "fieldrule_force_payload",
            "fieldrule_force_unit", "fieldrule_fire_payload", "fieldrule_oil_payload", "fieldrule_water_payload", "fieldrule_cryo_payload", "fieldrule_acid_payload",
            "fieldrule_polymer_payload", "fieldrule_dust_payload", "fieldrule_smoke_payload", "fieldrule_steam_payload", "fieldrule_obscure_unit", "fieldrule_glass_payload",
            "fieldrule_ice_payload", "fieldrule_solid_absorb",
        };

        private static readonly string[] Factions = { "any", "own", "hostile" };
        private static readonly string[] Impls = { "deferred", "kernel", "coat" };
        private static Compiled _current;

        public static Compiled Current => _current ??= Compile(Load());

        public static void Reload() => _current = null;

        private static IReadOnlyList<GameConfig.fg.FieldCarrierRule> Load()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbFieldCarrierRule?.DataList;
            }
            catch (Exception e)
            {
                Log.Error("[EmgFieldCatalog] 读取 fg.TbFieldCarrierRule 失败：" + e.Message);
                return null;
            }
        }

        public static Compiled Compile(IReadOnlyList<GameConfig.fg.FieldCarrierRule> rows)
        {
            var c = new Compiled();
            if (rows == null || rows.Count != EmgFieldConst.RuleCount)
            {
                c.Problems.Add($"fg.TbFieldCarrierRule 应为 {EmgFieldConst.RuleCount} 行，实际 {rows?.Count ?? 0}");
                return c;
            }
            for (int k = 0; k < rows.Count; k++)
            {
                GameConfig.fg.FieldCarrierRule r = rows[k];
                int fac = Array.IndexOf(Factions, r?.Faction);
                int alt = Array.IndexOf(Factions, r?.FactionAlt);
                int impl = Array.IndexOf(Impls, r?.Impl);
                if (r == null || r.Id != Order[k] || fac < 0 || alt < 0 || impl < 0 || !(r.Amount >= 0f) || !(r.Param >= 0f))
                {
                    c.Problems.Add($"fg.TbFieldCarrierRule 第 {k + 1} 行应为 {Order[k]}，阵营 / 执行者要在封闭清单、幅度非负（{r?.Id}）");
                    continue;
                }
                c.Ids.Add(r.Id);
                c.Rules.Add(new EmgFieldRule
                {
                    Amount = r.Amount, Param = r.Param, Once = (byte)(r.Once ? 1 : 0), Faction = (byte)fac, FactionAlt = (byte)alt, Impl = (byte)impl,
                });
            }
            return c;
        }
    }
}
