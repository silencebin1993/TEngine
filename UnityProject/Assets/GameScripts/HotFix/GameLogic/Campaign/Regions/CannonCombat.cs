using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-REACT-02 STORY-EXECUTION-CARDS.md：铸造重炮（<see cref="ComponentCatalog.CompCannonId"/>）
    /// 独立于普通连射器/切割束即时命中路径的瞄准线/冷却/热量状态机——`ComponentCatalog.StructFinId`
    /// 类注释此前明确写"热量 Stat 管线未建立"，本类是它的真实落地。<see cref="FracturedCityRegion.TryAttackEnemy"/>
    /// 检测到 <c>resolution.Preview.HasCannonPrimary</c> 时唯一委托到本类，不再走该方法自己的即时
    /// 伤害/标记逻辑（重炮的反应槽只对熔穿过载生效，不参与标记跳转——"重炮+过载是熔穿过载唯一配方"，
    /// DEMO-CONTENT-LOCK.md §2.4）。
    ///
    /// 数值权威来源全部是 DEMO-CONTENT-LOCK.md §5："铸造重炮 射程20米/基础55伤害/3秒冷却/1秒瞄准线"、
    /// "熔穿过载 基础积热40，反应再加25即每发65；额外穿甲+30%；热量达100停火，低于60方可恢复；
    /// 每秒散热10，散热鳍额外+5；HeatResistant只减额外穿甲效果，不清除基础重炮伤害"——**基础伤害
    /// 55 不受熔穿过载影响，过载只改变积热与穿甲，不是伤害加成**（原文"重炮基础积热40，反应再加25
    /// 即每发65"整句在描述热量，不是伤害；damage 数字全文档只出现"基础55伤害/发"一处）。</summary>
    public static class CannonCombat
    {
        /// <summary>"瞄准中"（<see cref="MachineRecord.CannonAimReadyAtPlaySeconds"/> 已设置但还没到
        /// 时间）与"已开火"共用同一个 <see cref="FracturedCityRegion.ActionResult"/>——<c>Success=true</c>
        /// 但 <see cref="StillAiming"/> 为真时调用方不应当把它当成"命中失败"，只是这一次调用只是推进了
        /// 瞄准线，还没真正打出去（玩家/AI 需要在冷却允许的下一次调用里再触发一次攻击才会真正命中，
        /// 与 <see cref="RegionSquadCommandContext.AttackCooldownSeconds"/>/直控重复点击的既有调用节奏
        /// 天然契合，不需要调用方专门写一套"等瞄准完成"的新逻辑）。</summary>
        public static bool LastCallWasStillAiming { get; private set; }

        /// <summary><paramref name="isFrontalArmoredHit"/>/<paramref name="armorReductionFraction"/>/
        /// <paramref name="targetHeatResistant"/> 是 ER6-ADAPT-01 补上的护甲穿透接口——调用方（目前唯一是
        /// <see cref="FoundryOutpostRegion.TryAttackEnemy"/>）在目标是护甲机且命中正面时传入基线减伤
        /// 比例，本方法据此经 <see cref="ApplyArmorPierce"/> 结合"是否熔穿过载生效"与"目标是否
        /// HeatResistant 适应"计算最终有效减伤。<paramref name="isFrontalArmoredHit"/> 为假（默认值，
        /// <see cref="FracturedCityRegion.TryAttackEnemy"/> 现役敌人无正面装甲概念，调用方不传即为此
        /// 默认）时行为与此前完全一致（伤害不打折）。</summary>
        public static FracturedCityRegion.ActionResult TryFire(CampaignState state, int attackerLogicId,
            string enemyInstanceId, MachineCombatResolution resolution, System.Func<Vector2, Vector2, bool> isReachable,
            bool isFrontalArmoredHit = false, float armorReductionFraction = 0f, bool targetHeatResistant = false)
        {
            LastCallWasStillAiming = false;

            if (!MachineRegistry.TryGetRecord(attackerLogicId, out MachineRecord attacker) || !attacker.IsAlive)
            {
                return FracturedCityRegion.ActionResult.Fail("攻击者不存在或已阵亡。");
            }

            float now = state.PlaySeconds;

            // "热量达100停火，低于60方可恢复"——迟滞下限，不是"降到100以下就能开火"。
            if (attacker.IsWeaponOverheated)
            {
                if (attacker.WeaponHeat > FracturedCityLayout.WeaponHeatRecoverThreshold)
                {
                    return FracturedCityRegion.ActionResult.Fail("weapon-overheated");
                }
                attacker.IsWeaponOverheated = false;
            }

            if (now < attacker.NextCannonActionAtPlaySeconds)
            {
                return FracturedCityRegion.ActionResult.Fail("weapon-cooldown");
            }

            if (attacker.CannonAimReadyAtPlaySeconds <= 0f)
            {
                // 第一次调用：开始瞄准，本次不产生任何伤害/热量（"攻击前1秒瞄准线"字面要求——
                // 瞄准是攻击动作真正开始前的独立阶段，不是伤害结算的一部分）。
                attacker.CannonAimReadyAtPlaySeconds = now + FracturedCityLayout.CannonAimSeconds;
                LastCallWasStillAiming = true;
                return FracturedCityRegion.ActionResult.Ok();
            }

            if (now < attacker.CannonAimReadyAtPlaySeconds)
            {
                // 瞄准线还没转完，本次调用只是"仍在瞄准"的确认，不重复推迟瞄准完成时间。
                LastCallWasStillAiming = true;
                return FracturedCityRegion.ActionResult.Ok();
            }

            RegionEnemyRecord enemy = FracturedCityRegion.FindEnemy(state, enemyInstanceId);
            if (enemy == null || !enemy.IsAlive)
            {
                attacker.CannonAimReadyAtPlaySeconds = 0f; // 目标消失：瞄准作废，不留一个悬空的"已瞄准"状态。
                return FracturedCityRegion.ActionResult.Fail("目标已阵亡或不存在。");
            }

            // "射程20米"（DEMO-CONTENT-LOCK.md §5）——本方法不重复实现这道距离校验：唯一两个调用点
            // （FracturedCityController 的 squad Attack 6米/直控瞄准 12米）已经在调用本方法之前用
            // 各自的真实 Transform 位置把距离卡得比重炮射程更紧，重炮永远不会在比它们更远处开火，
            // 结构上这条要求已经满足。这里如果自己再查一次距离，只能读 MachineRecord.WorldPosition——
            // 那是"只在 Exit 时才同步"的过期字段（DIGEST 已记录的陷阱），会读到错误位置产生假阴性
            // 拒绝，属于比"不查"更糟的行为，因此故意不做。"机动敌人可躲瞄准线"字面要求的是"1秒瞄准
            // 期间敌人有真实移动能力可以逃出攻击者当前站定的射程"，这一点由调用方在瞄准未完成期间
            // 允许敌人正常移动（FracturedCityEnemyAi 从不因为"正被瞄准"而冻结）已经天然成立，不需要
            // 本方法再显式二次查距离。<paramref name="isReachable"/> 同理保留给未来若需要视线遮挡判定
            // 时不必改签名，当前 DEMO-CONTENT-LOCK.md 未点名"墙可挡瞄准线"，不强制启用。
            _ = isReachable;

            // 瞄准完成：真正开火，结算热量+伤害，重置瞄准/进入冷却。
            attacker.CannonAimReadyAtPlaySeconds = 0f;
            attacker.NextCannonActionAtPlaySeconds = now + FracturedCityLayout.CannonCooldownSeconds;

            bool overloadActive = resolution.Preview.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId;
            float heatThisShot = FracturedCityLayout.CannonBaseHeatPerShot
                + (overloadActive ? FracturedCityLayout.OverloadExtraHeatPerShot : 0f);
            attacker.WeaponHeat += heatThisShot;
            if (attacker.WeaponHeat >= FracturedCityLayout.WeaponHeatOverheatThreshold)
            {
                attacker.IsWeaponOverheated = true;
                Log.Info($"[CannonCombat] 机器 {attackerLogicId} 重炮过热（{attacker.WeaponHeat:F0}），停火直到降到60以下。");
            }

            // 基础伤害不受过载影响（过载只改变热量与穿甲，见类注释）。穿甲只在目标有正面装甲概念时
            // 才有意义——ER6-ADAPT-01 落地前 FracturedCity 现役敌人（侦察/干扰机）没有正面装甲，调用方
            // 不传 isFrontalArmoredHit 即维持"不减伤"旧行为；铸造前哨护甲机接入后，调用方传入基线
            // 减伤比例，这里用 ApplyArmorPierce 结合过载/HeatResistant 算出最终有效减伤——
            // "HeatResistant 只减额外穿甲效果，不清除基础重炮伤害"：没有过载时不受影响，
            // 有过载但目标 HeatResistant 时减伤比例回落到基线（穿甲加成被完全抵消），两者都不改变
            // CannonBaseDamage 这个基础值本身。
            float damage = FracturedCityLayout.CannonBaseDamage;
            if (isFrontalArmoredHit && armorReductionFraction > 0f)
            {
                float effectiveReduction = ApplyArmorPierce(armorReductionFraction, overloadActive, targetHeatResistant);
                damage *= Mathf.Max(0f, 1f - effectiveReduction);
            }
            return FracturedCityRegion.TryDamageEnemy(state, enemyInstanceId, damage);
        }

        /// <summary>DEMO-CONTENT-LOCK.md §5"额外穿甲+30%...HeatResistant只减额外穿甲效果，不清除
        /// 基础重炮伤害"——纯函数，独立于 <see cref="Content.EnemyCatalog.ComputeFrontalArmorReducedDamage"/>
        /// 的硬编码0.4正面减伤常量（不同敌人未来可能有不同护甲值，这里只处理"穿甲怎么削减护甲减伤
        /// 这个百分比"这一层，不关心减伤基线具体是多少），<paramref name="targetHeatResistant"/> 是
        /// ER6-ADAPT-01 敌方 HeatResistant 适应的读取口，由 <see cref="TryFire"/> 经调用方传入的
        /// <see cref="RegionRecord.AdaptationId"/> 判定结果驱动（见 <see cref="FoundryOutpostRegion.TryAttackEnemy"/>）。</summary>
        public static float ApplyArmorPierce(float armorReductionFraction, bool overloadActive, bool targetHeatResistant)
        {
            if (!overloadActive || targetHeatResistant)
            {
                return armorReductionFraction;
            }
            return Mathf.Max(0f, armorReductionFraction - FracturedCityLayout.OverloadArmorPierceBonus);
        }

        /// <summary>被动散热——由 <see cref="FracturedCityController.Update"/>/<see cref="FoundryOutpostController.Update"/>
        /// 每帧各自调用一次（机器数量个位数，O(1) 量级）。只处理有热量在身的机器，避免每帧对全部
        /// 机器做一次 <see cref="MachineLoadoutRegistry.Resolve"/>。
        ///
        /// ── ER6-FOUNDRY-01 修复的真实缺陷 ──
        /// 本方法原本额外要求 <c>m.RegionId == FracturedCityLayout.RegionId</c>——ER6-REACT-02 落地时
        /// 铸造重炮唯一可战斗区域只有破碎都市，这条过滤形同"只处理有热量的机器"的等价写法；铸造前哨
        /// 外围接入重炮战斗（护甲机穿甲）后，这条过滤会让 foundry_outpost 里积热的机器永远不散热
        /// （本方法从两区域各自的 Update 调用，但过滤条件只认一个区域），已移除该区域限制——热量是
        /// 机体自身属性，不该因为"当前站在哪个区域"而冻结，回家园后继续散热同样是正确行为。</summary>
        public static void TickHeatDissipation(CampaignState state, float dt)
        {
            if (state == null || dt <= 0f)
            {
                return;
            }
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.WeaponHeat <= 0f)
                {
                    continue;
                }
                float rate = FracturedCityLayout.WeaponHeatDissipationPerSecond;
                if (MachineLoadoutRegistry.IsRegistered(m.LogicId))
                {
                    MachineCombatResolution resolution = MachineLoadoutRegistry.Resolve(state, m.LogicId, state.RandomSeed);
                    if (resolution.Success && resolution.Preview.HasHeatSinkStructure)
                    {
                        rate += FracturedCityLayout.HeatSinkBonusDissipationPerSecond;
                    }
                }
                m.WeaponHeat = Mathf.Max(0f, m.WeaponHeat - rate * dt);
                if (m.IsWeaponOverheated && m.WeaponHeat < FracturedCityLayout.WeaponHeatRecoverThreshold)
                {
                    m.IsWeaponOverheated = false;
                }
            }
        }
    }
}
