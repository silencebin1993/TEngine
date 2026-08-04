using System.Collections.Generic;

namespace GameLogic.FirstPlayable
{
    /// <summary>三条原型路线。Spec §6。</summary>
    public enum FPRoute
    {
        None = 0,
        /// <summary>吞噬扩张型。</summary>
        Devour,
        /// <summary>功能特化型。</summary>
        Specialist,
        /// <summary>科技统治型。</summary>
        Tech,
    }

    /// <summary>细胞阶段 3 选 1 微选择。Spec §4.1。</summary>
    public enum FPMicroChoice
    {
        None = 0,
        /// <summary>贪食囊：吞噬获得生物质 +25%。</summary>
        Gluttony,
        /// <summary>趋光纤毛：移速 +20%。</summary>
        Phototaxis,
        /// <summary>代谢泡：每次吞噬回复 3 生命值。</summary>
        Metabolic,
    }

    /// <summary>器官阶段 6 个构筑模块。Spec §7.3。</summary>
    public enum FPModuleId
    {
        None = 0,
        /// <summary>吞噬口器。</summary>
        Maw,
        /// <summary>厚壁细胞层。</summary>
        ThickWall,
        /// <summary>感知纤毛。</summary>
        Cilia,
        /// <summary>协同神经束。</summary>
        Nerve,
        /// <summary>原始放电囊。</summary>
        Zap,
        /// <summary>能量导流组织。</summary>
        Conduit,
    }

    public enum FPStage
    {
        None = 0,
        Cell,
        Build,
        Creature,
        Result,
    }

    /// <summary>
    /// 模块静态定义。所有属性修正以"相对 1.0 的增量"存储，
    /// 叠加时用加法累加增量（speedMul = 1 + Σdelta），保证纯路线数值与 Spec §6 完全一致，
    /// 混合构筑也不会因连乘产生难以预期的偏差。
    /// </summary>
    public sealed class FPModuleDef
    {
        public FPModuleId Id;
        public string Name;
        public FPRoute Route;
        public int Price;
        public string Desc;

        public float MaxHpFlat;
        public float SpeedMulDelta;
        public float MeleeMulDelta;
        public float StaminaMaxFlat;
        public float StaminaCostMulDelta;
        public float StaminaRegenMulDelta;
        public float DashInvulnFlat;
        public float AggroMulDelta;
        public float KillHeal;
        public bool UnlockZap;
    }

    public static class FPModuleTable
    {
        public static readonly List<FPModuleDef> All = new List<FPModuleDef>
        {
            new FPModuleDef
            {
                Id = FPModuleId.Maw, Name = "吞噬口器", Route = FPRoute.Devour, Price = 120,
                Desc = "近战伤害 +60%，击杀普通敌人回血 8",
                MeleeMulDelta = 0.60f, KillHeal = 8f,
            },
            new FPModuleDef
            {
                Id = FPModuleId.ThickWall, Name = "厚壁细胞层", Route = FPRoute.Devour, Price = 140,
                Desc = "最大生命值 +50，移速 -8%",
                MaxHpFlat = 50f, SpeedMulDelta = -0.08f,
            },
            new FPModuleDef
            {
                Id = FPModuleId.Cilia, Name = "感知纤毛", Route = FPRoute.Specialist, Price = 150,
                Desc = "移速 +28%，敌人察觉范围 -35%",
                SpeedMulDelta = 0.28f, AggroMulDelta = -0.35f,
            },
            new FPModuleDef
            {
                Id = FPModuleId.Nerve, Name = "协同神经束", Route = FPRoute.Specialist, Price = 160,
                Desc = "冲刺无敌帧 +0.15 秒，体力消耗 -30%",
                DashInvulnFlat = 0.15f, StaminaCostMulDelta = -0.30f,
            },
            new FPModuleDef
            {
                Id = FPModuleId.Zap, Name = "原始放电囊", Route = FPRoute.Tech, Price = 190,
                Desc = "解锁远程放电：伤害 25，射程 3.5 米，冷却 1.2 秒",
                UnlockZap = true,
            },
            new FPModuleDef
            {
                Id = FPModuleId.Conduit, Name = "能量导流组织", Route = FPRoute.Tech, Price = 170,
                Desc = "体力上限 +40，体力回复 +50%",
                StaminaMaxFlat = 40f, StaminaRegenMulDelta = 0.50f,
            },
        };

        public static FPModuleDef Get(FPModuleId id)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Id == id)
                {
                    return All[i];
                }
            }
            return null;
        }

        public const float ZapDamage = 25f;
        public const float ZapRange = 3.5f;
        public const float ZapCooldown = 1.2f;

        public static string RouteName(FPRoute route)
        {
            switch (route)
            {
                case FPRoute.Devour: return "吞噬扩张型";
                case FPRoute.Specialist: return "功能特化型";
                case FPRoute.Tech: return "科技统治型";
                default: return "无";
            }
        }

        public static FPRoute MicroChoiceRoute(FPMicroChoice choice)
        {
            switch (choice)
            {
                case FPMicroChoice.Gluttony: return FPRoute.Devour;
                case FPMicroChoice.Phototaxis: return FPRoute.Specialist;
                case FPMicroChoice.Metabolic: return FPRoute.Tech;
                default: return FPRoute.None;
            }
        }
    }
}
