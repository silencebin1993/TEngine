using System.Collections.Generic;
using GameConfig.fp;
using GameLogic.Config;

namespace GameLogic.FirstPlayable
{
    /// <summary>三条原型路线。Spec §6。数值与 Luban fp.ERoute 对齐。</summary>
    public enum FPRoute
    {
        None = 0,
        /// <summary>吞噬扩张型。</summary>
        Devour = 1,
        /// <summary>功能特化型。</summary>
        Specialist = 2,
        /// <summary>科技统治型。</summary>
        Tech = 3,
    }

    /// <summary>细胞阶段 3 选 1 微选择。Spec §4.1。与 fp.EMicroChoice 对齐。</summary>
    public enum FPMicroChoice
    {
        None = 0,
        /// <summary>贪食囊：吞噬获得生物质 +25%。</summary>
        Gluttony = 1,
        /// <summary>趋光纤毛：移速 +20%。</summary>
        Phototaxis = 2,
        /// <summary>代谢泡：每次吞噬回复 3 生命值。</summary>
        Metabolic = 3,
    }

    /// <summary>器官阶段构筑模块。Spec §7.3。与 fp.EModuleId 对齐。</summary>
    public enum FPModuleId
    {
        None = 0,
        /// <summary>吞噬口器。</summary>
        Maw = 1,
        /// <summary>厚壁细胞层。</summary>
        ThickWall = 2,
        /// <summary>感知纤毛。</summary>
        Cilia = 3,
        /// <summary>协同神经束。</summary>
        Nerve = 4,
        /// <summary>原始放电囊。</summary>
        Zap = 5,
        /// <summary>能量导流组织。</summary>
        Conduit = 6,
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
    /// 叠加时用加法累加增量（speedMul = 1 + Σdelta）。
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

    /// <summary>模块表门面。数据来自 Luban fp.TbModule / fp.TbGlobal。</summary>
    public static class FPModuleTable
    {
        private static List<FPModuleDef> _cache;

        public static IReadOnlyList<FPModuleDef> All
        {
            get
            {
                EnsureCache();
                return _cache;
            }
        }

        public static FPModuleDef Get(FPModuleId id)
        {
            EnsureCache();
            for (int i = 0; i < _cache.Count; i++)
            {
                if (_cache[i].Id == id)
                {
                    return _cache[i];
                }
            }

            return null;
        }

        public static float ZapDamage => FPConfig.Global.ZapDamage;
        public static float ZapRange => FPConfig.Global.ZapRange;
        public static float ZapCooldown => FPConfig.Global.ZapCooldown;

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
            var cfg = FPConfig.GetMicroChoice((EMicroChoice)(int)choice);
            if (cfg == null)
            {
                return FPRoute.None;
            }

            return (FPRoute)(int)cfg.Route;
        }

        private static void EnsureCache()
        {
            if (_cache != null)
            {
                return;
            }

            var list = FPConfig.Modules;
            _cache = new List<FPModuleDef>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Module m = list[i];
                _cache.Add(new FPModuleDef
                {
                    Id = (FPModuleId)(int)m.Id,
                    Name = m.Name,
                    Route = (FPRoute)(int)m.Route,
                    Price = m.Price,
                    Desc = m.Desc,
                    MaxHpFlat = m.MaxHpFlat,
                    SpeedMulDelta = m.SpeedMulDelta,
                    MeleeMulDelta = m.MeleeMulDelta,
                    StaminaMaxFlat = m.StaminaMaxFlat,
                    StaminaCostMulDelta = m.StaminaCostMulDelta,
                    StaminaRegenMulDelta = m.StaminaRegenMulDelta,
                    DashInvulnFlat = m.DashInvulnFlat,
                    AggroMulDelta = m.AggroMulDelta,
                    KillHeal = m.KillHeal,
                    UnlockZap = m.UnlockAbilityId > 0,
                });
            }
        }
    }
}
