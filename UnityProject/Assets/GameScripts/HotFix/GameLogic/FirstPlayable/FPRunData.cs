using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 生物阶段最终属性。由 <see cref="FPRunData.ResolveStats"/> 从构筑推导，只读消费。
    /// </summary>
    public sealed class FPStats
    {
        public float MaxHp;
        public float Speed;
        public float MeleeDamage;
        public float StaminaMax;
        public float DashCost;
        public float StaminaRegen;
        public float DashInvuln;
        public float AggroMul;
        public float KillHeal;
        public bool HasZap;
    }

    /// <summary>
    /// 跨阶段运行时数据。Spec §12：由 FPGame 持有，阶段切换不重建。
    /// </summary>
    public sealed class FPRunData
    {
        public const int SlotLimit = 2;

        public int Biomass;
        public int EvoPoint;
        public FPMicroChoice MicroChoice = FPMicroChoice.None;
        public readonly List<FPModuleId> Modules = new List<FPModuleId>(SlotLimit);

        // 本局记录，仅用于结算展示
        public float CellSeconds;
        public int FoodEaten;
        public int ThreatEaten;
        public int WaveReached;
        public float EliteFightSeconds;
        public int CreatureRetryCount;

        public void ResetAll()
        {
            Biomass = 0;
            EvoPoint = 0;
            MicroChoice = FPMicroChoice.None;
            Modules.Clear();
            CellSeconds = 0f;
            FoodEaten = 0;
            ThreatEaten = 0;
            WaveReached = 0;
            EliteFightSeconds = 0f;
            CreatureRetryCount = 0;
        }

        public bool CanEvolve => EvoPoint >= FPTuning.EvoPointThreshold;

        public bool HasModule(FPModuleId id) => Modules.Contains(id);

        public bool SlotFull => Modules.Count >= SlotLimit;

        public bool CanAfford(FPModuleDef def) => def != null && Biomass >= def.Price;

        /// <summary>购买模块。成功返回 true。</summary>
        public bool BuyModule(FPModuleId id)
        {
            FPModuleDef def = FPModuleTable.Get(id);
            if (def == null || SlotFull || HasModule(id) || !CanAfford(def))
            {
                return false;
            }
            Biomass -= def.Price;
            Modules.Add(id);
            return true;
        }

        /// <summary>取消选择并退款。Spec §4.2。</summary>
        public bool RefundModule(FPModuleId id)
        {
            FPModuleDef def = FPModuleTable.Get(id);
            if (def == null || !Modules.Remove(id))
            {
                return false;
            }
            Biomass += def.Price;
            return true;
        }

        /// <summary>
        /// 路线计分。Spec §5.3：微选择 +1，每模块 +1，上限 3 点。
        /// </summary>
        public void ScoreRoutes(out int devour, out int specialist, out int tech, out int moduleOnlyMax)
        {
            devour = specialist = tech = 0;
            int md = 0, ms = 0, mt = 0;

            FPRoute microRoute = FPModuleTable.MicroChoiceRoute(MicroChoice);
            AddScore(microRoute, ref devour, ref specialist, ref tech);

            for (int i = 0; i < Modules.Count; i++)
            {
                FPModuleDef def = FPModuleTable.Get(Modules[i]);
                if (def == null)
                {
                    continue;
                }
                AddScore(def.Route, ref devour, ref specialist, ref tech);
                AddScore(def.Route, ref md, ref ms, ref mt);
            }

            moduleOnlyMax = Mathf.Max(md, Mathf.Max(ms, mt));
            _moduleScore[0] = md;
            _moduleScore[1] = ms;
            _moduleScore[2] = mt;
        }

        private readonly int[] _moduleScore = new int[3];

        private static void AddScore(FPRoute route, ref int d, ref int s, ref int t)
        {
            switch (route)
            {
                case FPRoute.Devour: d++; break;
                case FPRoute.Specialist: s++; break;
                case FPRoute.Tech: t++; break;
            }
        }

        /// <summary>
        /// 主导路线。平票时器官模块所属路线优先；三路各 1 点判定为混合型（isMixed = true）。
        /// </summary>
        public FPRoute DominantRoute(out bool isMixed)
        {
            ScoreRoutes(out int d, out int s, out int t, out int _);
            isMixed = false;

            if (d == 1 && s == 1 && t == 1)
            {
                isMixed = true;
                return FPRoute.None;
            }

            int max = Mathf.Max(d, Mathf.Max(s, t));
            if (max <= 0)
            {
                return FPRoute.None;
            }

            FPRoute best = FPRoute.None;
            int bestModule = -1;
            TryPick(FPRoute.Devour, d, max, _moduleScore[0], ref best, ref bestModule);
            TryPick(FPRoute.Specialist, s, max, _moduleScore[1], ref best, ref bestModule);
            TryPick(FPRoute.Tech, t, max, _moduleScore[2], ref best, ref bestModule);
            return best;
        }

        private static void TryPick(FPRoute route, int score, int max, int moduleScore,
            ref FPRoute best, ref int bestModule)
        {
            if (score != max || moduleScore <= bestModule)
            {
                return;
            }
            best = route;
            bestModule = moduleScore;
        }

        /// <summary>
        /// 由构筑推导生物阶段属性。增量加法累加，见 <see cref="FPModuleDef"/> 注释。
        /// </summary>
        public FPStats ResolveStats()
        {
            float hpFlat = 0f, staMaxFlat = 0f, dashInvulnFlat = 0f, killHeal = 0f;
            float speedD = 0f, meleeD = 0f, staCostD = 0f, staRegenD = 0f, aggroD = 0f;
            bool zap = false;

            for (int i = 0; i < Modules.Count; i++)
            {
                FPModuleDef def = FPModuleTable.Get(Modules[i]);
                if (def == null)
                {
                    continue;
                }
                hpFlat += def.MaxHpFlat;
                staMaxFlat += def.StaminaMaxFlat;
                dashInvulnFlat += def.DashInvulnFlat;
                killHeal += def.KillHeal;
                speedD += def.SpeedMulDelta;
                meleeD += def.MeleeMulDelta;
                staCostD += def.StaminaCostMulDelta;
                staRegenD += def.StaminaRegenMulDelta;
                aggroD += def.AggroMulDelta;
                zap |= def.UnlockZap;
            }

            return new FPStats
            {
                MaxHp = FPTuning.CreatureBaseMaxHp + hpFlat,
                Speed = FPTuning.CreatureBaseSpeed * (1f + speedD),
                MeleeDamage = FPTuning.CreatureBaseMelee * (1f + meleeD),
                StaminaMax = FPTuning.StaminaMax + staMaxFlat,
                DashCost = FPTuning.DashCost * Mathf.Max(0.1f, 1f + staCostD),
                StaminaRegen = FPTuning.StaminaRegen * (1f + staRegenD),
                DashInvuln = FPTuning.DashInvulnTime + dashInvulnFlat,
                AggroMul = Mathf.Max(0.1f, 1f + aggroD),
                KillHeal = killHeal,
                HasZap = zap,
            };
        }

        public string ModuleSummary()
        {
            if (Modules.Count == 0)
            {
                return "基础形态（无模块）";
            }
            string s = "";
            for (int i = 0; i < Modules.Count; i++)
            {
                FPModuleDef def = FPModuleTable.Get(Modules[i]);
                if (def == null)
                {
                    continue;
                }
                if (i > 0)
                {
                    s += " + ";
                }
                s += def.Name;
            }
            return s;
        }
    }
}
