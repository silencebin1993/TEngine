using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG9-DATA-01：地点给敌人生成提供的“场地信息”——只有锚点位置、当前反制、干扰场归属这类场地事实，
    /// 不含任何按敌人 ID 的行为分支（行为全部来自敌人表的读法与标签）。
    /// </summary>
    public interface IEnemySiteContext
    {
        string RegionId { get; }

        /// <summary>区域布局里的锚点位置（编成表的出生锚点 / 牵引中心）。</summary>
        bool TryAnchor(string anchorId, out Vector2 position);

        /// <summary>该地点在 <paramref name="state"/> 这份存档里锁定的阵营反制（AdaptationCatalog.*），没有为 null。
        /// 显式传存档（不读全局当前会话），自检 / 离线模拟 / 后台地点用非当前存档调用时也读对。</summary>
        string AdaptationIdFor(CampaignState state);

        /// <summary>
        /// true = 该地点的信号干扰场由场地设施提供（破碎都市的监听节点，Demo 规则：节点被毁干扰即解除，与干扰机死活无关），
        /// 干扰机读法在这里不另外投射信号干扰场；false = 干扰机按职能自带干扰场（其它所有地点）。
        /// </summary>
        bool AnchoredJamField { get; }
    }

    /// <summary>
    /// FG9-DATA-01（FGR-FAC-001、FGR-ARC-005、IC-REQ-012）：敌人记录 → 战斗内核出生模板的唯一入口（取代 Demo 的
    /// CombatDemoContent 逐 ID 翻译）。全部参数来自 fg.TbMechEnemy（读法 + 标签 + 职能 / 层级 + 数值）与 fg.TbEnemyEncounter（朝向 / 牵引中心）：
    /// - 内核单位类型：标签 structure → 结构，否则敌人；
    /// - 标志：external_health → 血量真相在热更层（首领状态机），不逐次报告、武器由状态机开关；否则逐次报告；
    ///   有武器或感知 → 需要视线；silent_fire → 开火不发事件；adapt_heat 且地点反制为“耐热” → 耐热；层级 elite → 精英标记；
    /// - 优先级：层级 boss 的本体（职能不是 structure）先于其它敌人结算；
    /// - 武器：射程 &gt; 0 才有，伤害 × 难度的敌人伤害倍率（DEBT-FG6DEF09-01）；
    /// - 行为参数：读法填（<see cref="IEnemyBehaviorReading.FillProfile"/>）；
    /// - 牵引中心：编成表 homeAnchor（锚点），none = 当前位置。
    /// 表行缺失 / 不完整时可见报错（<see cref="EnemyDefs.TryGetValid"/>）并返回 false，不生成默认替身。
    /// </summary>
    public static class EnemySpawnBuilder
    {
        public static bool TryBuild(CombatSite site, CampaignState state, RegionEnemyRecord e, IEnemySiteContext ctx, out CombatSpawn spawn)
        {
            spawn = default;
            if (site == null || e == null)
            {
                return false;
            }
            if (!EnemyDefs.TryGetValid(e.RegionId, e.EnemyInstanceId, e.EnemyTypeId, out MechEnemy row)
                || !EnemyBehaviorReadings.TryGet(row.Behavior, out IEnemyBehaviorReading reading))
            {
                return false;
            }

            bool external = EnemyDefs.HasTag(row, EnemyDefs.TagExternalHealth);
            bool hasWeapon = row.WeaponRange > 0f;
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable;
            flags |= external ? CombatUnitFlags.ExternalHealth : CombatUnitFlags.Report;
            if (hasWeapon || row.SenseRange > 0f)
            {
                flags |= CombatUnitFlags.NeedsLos;
            }
            if (hasWeapon && !external)
            {
                flags |= CombatUnitFlags.WeaponEnabled;
            }
            if (EnemyDefs.HasTag(row, EnemyDefs.TagSilentFire))
            {
                flags |= CombatUnitFlags.SilentFire;
            }
            if (EnemyDefs.HasTag(row, EnemyDefs.TagAdaptHeat) && ctx != null && ctx.AdaptationIdFor(state) == AdaptationCatalog.HeatResistant)
            {
                flags |= CombatUnitFlags.HeatResistant;
            }
            if (EnemyDefs.IsElite(row))
            {
                flags |= CombatUnitFlags.Elite;
            }

            Vector2 facing = EnemyDefs.FacingOf(e.EnemyInstanceId);
            spawn = new CombatSpawn
            {
                Kind = EnemyDefs.HasTag(row, EnemyDefs.TagStructure) ? CombatUnitKind.Structure : CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = reading.Kernel,
                Flags = flags,
                Radius = row.Radius,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = (byte)(EnemyDefs.IsBoss(row) && !EnemyDefs.IsStructure(row) ? 0 : 1), // 首领本体先结算（Demo：主核心）；首领的设施不动不打，照常 1
                ArmorFraction = row.FrontalDamageReduction,
                ArmorHalfAngleDeg = row.FrontalHalfAngleDeg,
                ArmorFacing = new float2(facing.x, facing.y),
            };
            if (hasWeapon)
            {
                spawn.Weapon = site.WeaponIndex(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant,
                    HasOutput = 1,
                    Range = row.WeaponRange,
                    Damage = row.WeaponDamage * DifficultyService.EnemyDamageMul(state),
                    Cooldown = row.WeaponCooldown,
                    AimSeconds = row.WeaponAim,
                    TargetMode = CombatTargetMode.Nearest,
                });
            }
            if (reading.NeedsProfile)
            {
                var profile = new CombatBehaviorProfile();
                reading.FillProfile(row, ref profile);
                spawn.BehaviorProfile = site.ProfileIndex(profile);
            }
            Vector2 home = e.Position;
            if (EnemyDefs.TryEncounter(e.EnemyInstanceId, out EnemyEncounter enc) && !string.IsNullOrEmpty(enc.HomeAnchor)
                && enc.HomeAnchor != EnemyDefs.None && ctx != null && ctx.TryAnchor(enc.HomeAnchor, out Vector2 anchor))
            {
                home = anchor;
            }
            spawn.Home = new double2(home.x, home.y);
            return true;
        }
    }
}
