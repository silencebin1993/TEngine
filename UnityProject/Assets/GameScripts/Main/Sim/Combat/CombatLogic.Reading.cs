using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG2-FW-02（FG02 FGR-FW-010 读法矩阵零死对；设计案 5.1 载体、5.3 四类包装；ADR-FW-002）：载体投送与固件读法的内核实现。
    ///
    /// 规则只认 <see cref="CombatReading"/> 里的数（载体 + 字段），不认识任何固件 ID——热更层按“作战组件的载体 × 生效固件的读法字段”查表累加
    /// （fg.TbCarrierReading），不写两两特例。一次开火分三段：
    /// 1. 载体投送：射弹 = 主目标一个；格斗 = 前方扇形内触及的全部敌对单位；力场 = 攻击者周围一圈；布区 = 主目标 + 目标脚下一块区域；
    ///    无人机 = 补足伴飞无人机（无人机自己索敌、命中）。
    /// 2. 逐目标读法（每个被投送命中的目标）：伤害（增幅 / 穿甲 / 侧后）→ 状态 → 处决 → 回收修复 → 击退。
    /// 3. 锚定读法（只围绕主目标结算一次）：额外目标 / 穿透 / 连锁 / 溅射 / 环扫（这些二次命中只结算伤害与状态，不再触发读法）→ 牵引 → 跃击 →
    ///    区域 / 连网 → 回波 → 通用标记跳转 → 过热爆发。
    /// 区域、回波、无人机、状态都在内核里按统一时钟逐步推进（暂停不走、倍速按游戏时间），随快照进存档；与是否被观察无关。
    /// 全部在 Burst 作业里跑（编队攻击命令），直控点击走托管的同一份代码。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>配置没填（0）时的默认值（热更层按 fg.TbHomeTuning reading.* 填进 <see cref="CombatConfig"/>）：
        /// 无人机移动速度（米 / 秒）与触及距离（米，另加目标半径）；状态持续伤害按节拍结算（不是每步扣血，报告单位不会每步发一条受伤事件）；区域节拍。</summary>
        private const float DefaultDroneSpeed = 9f;
        private const float DefaultDroneReach = 1.2f;
        private const float DefaultStatusTick = 0.5f;
        private const float DefaultZoneTick = 0.5f;
        private const float DefaultZoneStatusSeconds = 2f;
        private const float DefaultWeaveMargin = 0.6f;

        private static float DroneSpeedOf(ref CombatData d) => d.Config.DroneSpeed > 0f ? d.Config.DroneSpeed : DefaultDroneSpeed;
        private static float DroneReachOf(ref CombatData d) => d.Config.DroneReach > 0f ? d.Config.DroneReach : DefaultDroneReach;
        private static float StatusTickOf(ref CombatData d) => d.Config.StatusTick > 0f ? d.Config.StatusTick : DefaultStatusTick;
        private static float ZoneTickOf(ref CombatData d) => d.Config.ZoneTick > 0f ? d.Config.ZoneTick : DefaultZoneTick;
        private static float ZoneStatusSecondsOf(ref CombatData d) => d.Config.ZoneStatusSeconds > 0f ? d.Config.ZoneStatusSeconds : DefaultZoneStatusSeconds;
        private static float WeaveMarginOf(ref CombatData d) => d.Config.WeaveMargin > 0f ? d.Config.WeaveMargin : DefaultWeaveMargin;

        // ─────────────────────────────── 投送 ───────────────────────────────

        /// <summary>即时命中武器的一次投送（编队攻击 / 直控点击的 <see cref="FireAt"/> 与驻守开火的 <see cref="UnitAttack"/> 共用）。
        /// 返回 Ok / Invulnerable（主目标当前无法被击伤时不打标记、不跳转、不触发读法，与 Demo 一致）。</summary>
        internal static CombatFireResult DeliverInstant(ref CombatData d, int a, int t, in CombatWeapon wp, bool namedReactions)
        {
            CombatReading r = wp.Reading;
            double now = d.Scalars[0].Time;
            if (r.Carrier == CombatCarrier.Summon)
            {
                Cue(ref d, CombatEventKind.Fired, a, d.Id[t], 0f, d.Pos[t], 0);
                LaunchDrones(ref d, a, wp, r.Drones + r.EscortDrones, t);
                return CombatFireResult.Ok;
            }

            bool wasMarked = d.MarkedUntil[t] > now;
            double2 primaryPos = d.Pos[t];
            float baseDamage = math.max(0f, wp.Damage);
            float dealt = StrikeDamage(ref d, a, t, baseDamage, r, d.Pos[a], true, 0f);
            Cue(ref d, CombatEventKind.Fired, a, d.Id[t], 0f, d.Pos[t], 0);
            if (!DamageUnit(ref d, t, dealt, a))
            {
                return CombatFireResult.Invulnerable; // Demo：首领阶段不可伤时结算失败，不打标记、不跳转。
            }
            ReflectMelee(ref d, a, t, dealt);
            PerTarget(ref d, a, t, dealt, r, d.Pos[a]);

            // 载体投送的其余目标（格斗扇形 / 力场一圈）：逐目标读法，不锚定。
            if (r.Carrier == CombatCarrier.Melee || r.Carrier == CombatCarrier.Aura)
            {
                StrikeCarrierArea(ref d, a, t, baseDamage, r);
            }
            else if (r.Carrier == CombatCarrier.Field && r.FieldSeconds > 0f && r.Area > 0f)
            {
                // FG2-VFX-02：震荡脉冲器（布区·脉冲）的区域落在自己脚下（画成冲击波）；其余布区落在目标脚下（液池）。
                bool atSelf = r.FieldPlacement == CombatZonePlacement.Attacker;
                double2 fieldPos = atSelf ? d.Pos[a] : primaryPos;
                if (SpawnZone(ref d, a, FactionOfSlot(ref d, a), fieldPos, r.Area, r.FieldSeconds, baseDamage * math.max(0f, r.FieldDpsRatio), r.ZoneGrowth, r.ZoneTickScale,
                    r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln))
                {
                    SetLastZoneLook(ref d, atSelf ? CombatZoneLook.Pulse : CombatZoneLook.Pool);
                    NoteReading(ref d, CombatConst.ReadingFeedZone, fieldPos, a);
                }
            }

            if (wp.MarkSeconds > 0f && d.IsAlive(t))
            {
                d.MarkedUntil[t] = now + wp.MarkSeconds;
            }
            if (namedReactions && wp.Reaction == CombatReaction.MarkJump && wasMarked && !d.Has(a, CombatUnitFlags.ReactionSpent))
            {
                MarkJump(ref d, a, t, primaryPos, dealt, wp);
            }
            Anchored(ref d, a, t, primaryPos, dealt, baseDamage, wp, wasMarked, d.Pos[a], false);
            if (r.EscortDrones > 0)
            {
                LaunchDrones(ref d, a, wp, r.EscortDrones);
            }
            return CombatFireResult.Ok;
        }

        /// <summary>出手间隔 = 武器冷却 × 读法的出手间隔倍率（电容蓄力 / 乱流 · 力场；0 视为 1）。重炮冷却、驻守开火 / 瞄准线 / 突袭者、直控蓄力门槛共用
        /// （编队攻击命令的冷却在 TickCommand 里乘同一倍率）。</summary>
        internal static float EffectiveCooldown(in CombatWeapon wp) => wp.Cooldown * (wp.Reading.CooldownScale > 0f ? wp.Reading.CooldownScale : 1f);

        /// <summary>蓄力读法：出手间隔倍率 &gt; 1（少发高伤）。直控点击据此等蓄满。</summary>
        internal static bool IsCharged(in CombatWeapon wp) => wp.Reading.CooldownScale > 1f && wp.Cooldown > 0f;

        /// <summary>找目标 / 追击用的交战距离：格斗 / 力场按触及（区域半径 + 自身半径）收紧，其余是武器射程。驻守开火的炮塔据此只打够得着的敌人。</summary>
        internal static float EngageRange(ref CombatData d, int i, in CombatWeapon wp)
        {
            if (IsContactCarrier(wp.Reading))
            {
                float reach = wp.Reading.Area + d.Radius[i];
                return wp.Range > 0f ? math.min(wp.Range, reach) : reach;
            }
            if (IsAnchoredSummon(wp.Reading))
            {
                // 哨戒桩（FG2-VFX-02 修复）：插下的桩打得到的距离才算交战距离（目标半径另算，这里取 0 偏保守），驻守开火 / 突袭者据此找目标、靠近。
                float reach = SentryReach(ref d, i, wp.Reading, 0f);
                return wp.Range > 0f ? math.min(wp.Range, reach) : reach;
            }
            return wp.Range;
        }

        /// <summary>非重炮武器一次开火的共同门槛（编队攻击 / 直控的 <see cref="FireAt"/> 与驻守开火的 <see cref="UnitAttack"/> 共用）：
        /// 格斗 / 力场要贴近（触及 = 区域半径 + 双方半径），过热迟滞（过热后降到恢复线以下才再开火）。不通过时不算开火、不积热。</summary>
        internal static CombatFireResult PreFireGate(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            if (IsContactCarrier(wp.Reading)
                && math.distance(d.Pos[a], d.Pos[t]) > wp.Reading.Area + d.Radius[a] + d.Radius[t])
            {
                return CombatFireResult.OutOfRange;
            }
            if (IsAnchoredSummon(wp.Reading)
                && math.distance(d.Pos[a], d.Pos[t]) > SentryReach(ref d, a, wp.Reading, d.Radius[t]))
            {
                // FG2-VFX-02 修复：目标在插下的桩够不着的地方——不算开火（不插桩、不发开火提示、不积热），与格斗 / 力场同一口径。
                return CombatFireResult.OutOfRange;
            }
            if (d.Has(a, CombatUnitFlags.Overheated))
            {
                if (d.Heat[a] > wp.RecoverBelow)
                {
                    return CombatFireResult.Overheated;
                }
                d.Set(a, CombatUnitFlags.Overheated, false);
            }
            // FG6-DEF-01（FGR-DEF-004）：每发要消耗补给的武器（流体类固件的流体），存量不够一发时停火（不算开火、不积热、不扣补给）。
            if (!HasAmmoFor(ref d, a, wp))
            {
                return CombatFireResult.NoAmmo;
            }
            return CombatFireResult.Ok;
        }

        /// <summary>FG6-DEF-01：补给存量够不够打一发（不需要补给的武器恒为 true）。</summary>
        internal static bool HasAmmoFor(ref CombatData d, int a, in CombatWeapon wp) =>
            wp.AmmoPerShot <= 0f || d.Ammo[a] + 1e-4f >= wp.AmmoPerShot;

        /// <summary>要贴近才出手的载体：格斗、力场，以及落在自己脚下的布区（震荡脉冲器，FG2-VFX-02）。触及 = 区域半径。</summary>
        internal static bool IsContactCarrier(in CombatReading r) =>
            r.Area > 0f && (r.Carrier == CombatCarrier.Melee || r.Carrier == CombatCarrier.Aura
                            || (r.Carrier == CombatCarrier.Field && r.FieldPlacement == CombatZonePlacement.Attacker));

        /// <summary>定点无人机（哨戒桩）：插在母机朝目标方向身前 <see cref="SentryPlantAhead"/> 米、左右错开（第 q 根错开 <see cref="SentrySpread"/>），之后不动。</summary>
        internal static bool IsAnchoredSummon(in CombatReading r) => r.Carrier == CombatCarrier.Summon && r.DroneAnchored != 0;

        private const float SentryPlantAhead = 1.5f;

        /// <summary>第 q 根桩相对朝向的横向错开（米，左右交替、逐对外扩）。</summary>
        private static float SentrySpread(int q) => ((q % 2 == 0) ? 1f : -1f) * (0.9f + 0.6f * (q / 2));

        private static float DroneLeashOf(in CombatReading r) => r.DroneLeash > 0f ? r.DroneLeash : 10f;

        /// <summary>
        /// 哨戒桩从母机中心量起的打击距离：目标在母机正前方这么远时，插下的每一根桩（含错开最多的那根）都在“牵引绳 + 目标半径”以内。
        /// = 母机半径 + 身前插桩距离 + √((牵引绳 + 目标半径)² − 最大错开²)。开火门槛与交战距离共用，保证“开火了就打得到”。
        /// </summary>
        internal static float SentryReach(ref CombatData d, int a, in CombatReading r, float targetRadius)
        {
            int want = math.clamp(r.Drones + r.EscortDrones, 1, CombatConst.MaxDronesPerOwner);
            float spread = math.abs(SentrySpread(want - 1));
            float l = DroneLeashOf(r) + math.max(0f, targetRadius);
            return d.Radius[a] + SentryPlantAhead + math.sqrt(math.max(0f, l * l - spread * spread));
        }

        /// <summary>
        /// FG2-VFX-02（设计案 5.6 尖刺外装：被近战攻击时反伤）：目标 <paramref name="t"/> 装着反伤、攻击者 <paramref name="a"/> 是敌对阵营、
        /// 且站在触及范围内（反伤触及 + 双方半径）时，攻击者吃 固定值 + 这一击伤害 × 比例。只由即时近身攻击调用（弹体、重炮、区域、无人机、回波都不算近战）；
        /// 反伤本身不再触发反伤。O(1)。
        /// </summary>
        internal static void ReflectMelee(ref CombatData d, int a, int t, float dealt)
        {
            if (a < 0 || t < 0 || a >= d.Count || t >= d.Count || a == t || !d.IsAlive(a) || d.Faction[a] == d.Faction[t])
            {
                return;
            }
            int w = d.Weapon[t];
            if (w < 0 || w >= d.Weapons.Length)
            {
                return;
            }
            CombatReading tr = d.Weapons[w].Reading;
            if (tr.Thorns <= 0f && tr.ThornsFlat <= 0f)
            {
                return;
            }
            if (math.distance(d.Pos[a], d.Pos[t]) > tr.ThornsReach + d.Radius[a] + d.Radius[t])
            {
                return;
            }
            float reflect = math.max(0f, tr.ThornsFlat) + math.max(0f, dealt) * math.max(0f, tr.Thorns);
            if (reflect <= 0f)
            {
                return;
            }
            if (DamageUnit(ref d, a, reflect, t))
            {
                NoteReading(ref d, CombatConst.ReadingFeedThorns, d.Pos[t], t);
            }
        }

        /// <summary>FG6-DEF-01（FGR-DEF-004）：一发扣一发的补给（只在门槛通过、真的开火之后）。非重炮在 <see cref="AddShotHeat"/> 里扣，重炮在 <see cref="FireCannon"/> 里扣——同一个口径。</summary>
        internal static void ConsumeShotAmmo(ref CombatData d, int a, in CombatWeapon wp)
        {
            if (wp.AmmoPerShot > 0f)
            {
                d.Ammo[a] = math.max(0f, d.Ammo[a] - wp.AmmoPerShot);
            }
        }

        /// <summary>一发（任何开火方式）的积热（DEBT-FG1SIG06-02：即时命中武器也按固件积热）。重炮在 <see cref="FireCannon"/> 里自己算（含熔穿过载）。</summary>
        internal static void AddShotHeat(ref CombatData d, int a, in CombatWeapon wp)
        {
            // FG6-DEF-01（FGR-DEF-004）：一发扣一发的补给（与积热同一处：只在门槛通过、真的开火之后）。
            ConsumeShotAmmo(ref d, a, wp);
            if (wp.HeatPerShot <= 0f)
            {
                return;
            }
            float heat = d.Heat[a] + wp.HeatPerShot;
            d.Heat[a] = heat;
            if (wp.OverheatAt > 0f && heat >= wp.OverheatAt && !d.Has(a, CombatUnitFlags.Overheated))
            {
                d.Set(a, CombatUnitFlags.Overheated, true);
                Cue(ref d, CombatEventKind.Overheat, a, 0, heat, d.Pos[a], 0);
            }
        }

        /// <summary>格斗扇形 / 力场一圈里除主目标以外的敌对单位：逐目标读法。</summary>
        private static void StrikeCarrierArea(ref CombatData d, int a, int t, float baseDamage, in CombatReading r)
        {
            byte want = WantOf(ref d, a);
            double2 origin = d.Pos[a];
            bool melee = r.Carrier == CombatCarrier.Melee;
            double2 toT = d.Pos[t] - origin;
            float2 dir = math.lengthsq(toT) > 1e-12 ? (float2)math.normalize(toT) : new float2(0f, 1f);
            float reach = melee ? math.max(r.Area, (float)math.length(toT)) : r.Area;
            float cosHalf = r.Cone >= 180f ? -2f : math.cos(math.radians(math.max(0f, r.Cone)));
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == t || k == a || d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double2 to = d.Pos[k] - origin;
                double dist = math.length(to);
                if (dist > reach + d.Radius[k])
                {
                    continue;
                }
                if (melee && dist > 1e-6 && math.dot(dir, (float2)(to / dist)) < cosHalf)
                {
                    continue;
                }
                float dealt = StrikeDamage(ref d, a, k, baseDamage, r, origin, false, 0f);
                if (DamageUnit(ref d, k, dealt, a))
                {
                    if (melee)
                    {
                        ReflectMelee(ref d, a, k, dealt);
                    }
                    PerTarget(ref d, a, k, dealt, r, origin);
                }
            }
        }

        // ─────────────────────────────── 命中结算 ───────────────────────────────

        /// <summary>对一个目标的一次伤害（未落地）：× 伤害倍率 × 增幅（目标带状态）→ 正面装甲（减去穿甲）→ 侧后加成。<paramref name="extraPierce"/> 给重炮熔穿过载。</summary>
        internal static float StrikeDamage(ref CombatData d, int a, int t, float baseDamage, in CombatReading r, double2 from, bool cueArmor, float extraPierce)
        {
            float dmg = math.max(0f, baseDamage);
            if (r.DamageScale > 0f)
            {
                dmg *= r.DamageScale;
            }
            if (r.StatusAmp > 0f && d.StatusActive(t, d.Scalars[0].Time))
            {
                dmg *= 1f + r.StatusAmp;
            }
            if (IsFrontalArmored(ref d, t, from))
            {
                float frac = math.max(0f, d.Armor[t].x - math.max(0f, r.ArmorPierce) - math.max(0f, extraPierce));
                dmg *= math.max(0f, 1f - frac);
                if (cueArmor)
                {
                    Cue(ref d, CombatEventKind.ArmorHit, t, a >= 0 ? d.Id[a] : 0, 0f, d.Pos[t], 0);
                }
            }
            dmg *= BackMultiplier(ref d, t, from);
            return dmg;
        }

        /// <summary>逐目标读法（伤害已落地之后）：状态 → 处决 → 回收修复 → 击退。击退从 <paramref name="origin"/>（出手的那个身体：机器本身，或无人机）推开。</summary>
        private static void PerTarget(ref CombatData d, int a, int t, float dealt, in CombatReading r, double2 origin)
        {
            if (r.StatusMask != 0u)
            {
                ApplyStatus(ref d, t, r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln, a, dealt);
            }
            if (r.ExecuteBelow > 0f && d.IsAlive(t) && d.MaxHp[t] > 0f && d.Hp[t] / d.MaxHp[t] <= r.ExecuteBelow && !d.Has(t, CombatUnitFlags.Invulnerable))
            {
                DamageUnit(ref d, t, d.Hp[t] + 1f, a);
            }
            if (r.Lifesteal > 0f && a >= 0 && d.IsAlive(a) && dealt > 0f)
            {
                Heal(ref d, a, dealt * r.Lifesteal, a);
            }
            if (r.Knockback > 0f && a >= 0 && d.IsAlive(t))
            {
                Displace(ref d, t, origin, -r.Knockback, 0.0);
            }
        }

        /// <summary>锚定读法（围绕主目标结算一次）。二次命中只结算伤害与状态，不再触发读法。
        /// <paramref name="viaDrone"/>：这一击是无人机打的（<paramref name="from"/> = 无人机位置）——“以出手者为中心”的读法（牵引回拉、脚下区域、中点拖尾）以无人机为锚，
        /// 会移动出手者的读法（跃击）不结算：母机只做玩家让它做的事，不被自己的无人机拖向玩家没指定的敌人（FGR-BASE-020）。
        /// 环扫仍以母机为中心（无人机 · 绕轨的短语就是“扫过母机身边”），它只结算伤害、不移动任何己方单位。</summary>
        private static void Anchored(ref CombatData d, int a, int t, double2 hitPos, float dealt, float baseDamage, in CombatWeapon wp, bool wasMarked, double2 from, bool viaDrone)
        {
            CombatReading r = wp.Reading;
            if (!r.HasFirmwareReading)
            {
                return;
            }
            bool hasSelf = viaDrone || a >= 0;
            double2 self = viaDrone ? from : a >= 0 ? d.Pos[a] : hitPos;
            double now = d.Scalars[0].Time;
            byte want = WantOf(ref d, a);
            var hit = new FixedList128Bytes<int>();
            hit.Add(t);

            // 额外目标（霰射 / 分裂 / 分叉 / 广角……）：命中点附近最近的几个。
            if (r.ExtraHits > 0 && r.ExtraRadius > 0f)
            {
                int max = math.min(r.ExtraHits, CombatConst.MaxReadingTargets);
                for (int q = 0; q < max; q++)
                {
                    int k = NearestExcept(ref d, hitPos, r.ExtraRadius, want, ref hit);
                    if (k < 0)
                    {
                        break;
                    }
                    hit.Add(k);
                    SecondaryStrike(ref d, a, k, baseDamage * r.ExtraRatio, r, from);
                }
            }
            // 穿透：攻击方向上、目标身后的几个。
            if (r.PierceHits > 0 && r.PierceRange > 0f)
            {
                PierceBehind(ref d, a, t, from, hitPos, baseDamage * (r.PierceRatio > 0f ? r.PierceRatio : 1f), r, want, ref hit);
            }
            // 连锁：从命中点依次跳向最近的未命中目标。
            if (r.ChainHits > 0 && r.ChainRange > 0f)
            {
                int max = math.min(r.ChainHits, CombatConst.MaxReadingTargets);
                double2 at = hitPos;
                float dmg = dealt;
                for (int q = 0; q < max; q++)
                {
                    int k = NearestExcept(ref d, at, r.ChainRange, want, ref hit);
                    if (k < 0)
                    {
                        break;
                    }
                    hit.Add(k);
                    dmg *= r.ChainFalloff > 0f ? r.ChainFalloff : 1f;
                    DamageUnit(ref d, k, dmg, a);
                    if (r.StatusMask != 0u)
                    {
                        ApplyStatus(ref d, k, r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln, a, dmg);
                    }
                    at = d.Pos[k];
                }
            }
            // 溅射：命中点周围。
            if (r.BlastRadius > 0f && r.BlastRatio > 0f)
            {
                AreaStrike(ref d, a, hitPos, r.BlastRadius, baseDamage * r.BlastRatio, r, from, t);
            }
            // 环扫：攻击者周围。
            if (r.SweepRadius > 0f && r.SweepRatio > 0f && a >= 0)
            {
                AreaStrike(ref d, a, d.Pos[a], r.SweepRadius, baseDamage * r.SweepRatio, r, from, t);
            }
            // 牵引：拉向命中点或攻击者。
            if (r.PullStrength > 0f)
            {
                double2 center = r.PullToAttacker != 0 && hasSelf ? self : hitPos;
                float radius = r.PullRadius > 0f ? r.PullRadius : 4f;
                int n = d.Count;
                for (int k = 0; k < n; k++)
                {
                    if (d.Faction[k] != want || !d.IsAlive(k))
                    {
                        continue;
                    }
                    double dist = math.distance(d.Pos[k], center);
                    if (dist > radius || dist < 0.6)
                    {
                        continue;
                    }
                    Displace(ref d, k, center, math.min(r.PullStrength, (float)dist - 0.5f), 0.5);
                }
            }
            // 跃击：攻击者扑近。
            if (r.Lunge > 0f && !viaDrone && a >= 0 && d.IsAlive(a) && d.IsAlive(t))
            {
                double gap = math.distance(d.Pos[a], d.Pos[t]) - d.Radius[a] - d.Radius[t] - 0.2;
                if (gap > 0.05)
                {
                    double2 dir = math.normalize(d.Pos[t] - d.Pos[a]);
                    double step = math.min(r.Lunge, gap);
                    d.Pos[a] = MoveCollide(ref d, a, d.Pos[a], d.Pos[a] + dir * step);
                }
            }
            // 区域（驻留 / 拖尾）。
            if (r.ZoneSeconds > 0f && r.ZoneRadius > 0f)
            {
                double2 at = r.ZonePlacement == CombatZonePlacement.Attacker && hasSelf ? self
                    : r.ZonePlacement == CombatZonePlacement.Midpoint && hasSelf ? (self + hitPos) * 0.5
                    : hitPos;
                if (SpawnZone(ref d, a, FactionOfSlot(ref d, a), at, r.ZoneRadius, r.ZoneSeconds, r.ZoneDps, r.ZoneGrowth, r.ZoneTickScale,
                    r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln))
                {
                    NoteReading(ref d, CombatConst.ReadingFeedZone, at, a);
                }
            }
            // 连网：主目标与最近的另一个敌对单位之间拉一块减速网。
            if (r.WeaveRadius > 0f && r.WeaveSeconds > 0f)
            {
                var only = new FixedList128Bytes<int>();
                only.Add(t);
                int k = NearestExcept(ref d, hitPos, r.WeaveRadius, want, ref only);
                if (k >= 0)
                {
                    double2 mid = (hitPos + d.Pos[k]) * 0.5;
                    float half = (float)math.distance(hitPos, d.Pos[k]) * 0.5f + WeaveMarginOf(ref d);
                    if (SpawnZone(ref d, a, FactionOfSlot(ref d, a), mid, half, r.WeaveSeconds, r.ZoneDps > 0f ? r.ZoneDps : dealt * math.max(0f, r.WeaveDpsRatio), 0f, 1f,
                        r.StatusMask | CombatConst.StatusBitZoneSlow, r.StatusSeconds, r.StatusDps, math.max(r.StatusSlow, r.WeaveSlow), r.StatusVuln))
                    {
                        SetLastZoneLook(ref d, CombatZoneLook.Web);
                        NoteReading(ref d, CombatConst.ReadingFeedZone, mid, a);
                    }
                }
            }
            // 回波：对同一目标隔几秒再结算。
            if (r.EchoCount > 0 && r.EchoRatio > 0f)
            {
                int cnt = math.min(r.EchoCount, CombatConst.MaxEchoesPerHit);
                float delay = r.EchoDelay > 0f ? r.EchoDelay : 0.4f;
                for (int q = 0; q < cnt; q++)
                {
                    QueueEcho(ref d, a, t, now + delay * (q + 1), dealt * r.EchoRatio, r);
                }
            }
            // 通用标记跳转（只在目标已被标记时跳；具名反应“标记跳转”另走 MarkJump）。
            if (r.JumpMax > 0 && wasMarked && wp.Reaction != CombatReaction.MarkJump)
            {
                int jumped = MarkJumpCore(ref d, a, t, hitPos, dealt, r.JumpRange, r.JumpFalloff, r.JumpMax);
                if (jumped > 0)
                {
                    Cue(ref d, CombatEventKind.MarkJump, a, d.Id[t], jumped, hitPos, 1);
                }
            }
            // 过热爆发：攻击者积热过了阈值，这一发在命中点再爆一圈（含主目标）。
            if (r.HeatBurstAt > 0f && a >= 0 && wp.OverheatAt > 0f && d.Heat[a] >= wp.OverheatAt * r.HeatBurstAt && r.HeatBurstRadius > 0f)
            {
                AreaStrike(ref d, a, hitPos, r.HeatBurstRadius, baseDamage * r.HeatBurstRatio, r, from, -1);
            }
        }

        /// <summary>弹体（驻守开火的弹体武器）命中后的读法：逐目标 + 锚定。开火者可能已阵亡（<paramref name="owner"/> = -1）。</summary>
        internal static void ProjectileReadingHit(ref CombatData d, int owner, int hit, double2 hitPos, float dealt, in CombatWeapon wp, double2 from)
        {
            if (!wp.Reading.HasFirmwareReading)
            {
                return;
            }
            PerTarget(ref d, owner, hit, dealt, wp.Reading, owner >= 0 ? d.Pos[owner] : from);
            Anchored(ref d, owner, hit, hitPos, dealt, wp.Damage, wp, false, from, false);
        }

        /// <summary>重炮命中后的读法（逐目标 + 锚定；投送固定是射弹）。</summary>
        internal static void CannonReadingHit(ref CombatData d, int a, int t, double2 hitPos, float dealt, in CombatWeapon wp, bool wasMarked)
        {
            if (!wp.Reading.HasFirmwareReading)
            {
                return;
            }
            PerTarget(ref d, a, t, dealt, wp.Reading, d.Pos[a]);
            Anchored(ref d, a, t, hitPos, dealt, wp.Damage, wp, wasMarked, d.Pos[a], false);
        }

        private static void SecondaryStrike(ref CombatData d, int a, int k, float damage, in CombatReading r, double2 from)
        {
            float dealt = StrikeDamage(ref d, a, k, damage, r, from, false, 0f);
            if (DamageUnit(ref d, k, dealt, a) && r.StatusMask != 0u)
            {
                ApplyStatus(ref d, k, r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln, a, dealt);
            }
        }

        /// <summary>圆内（不含 <paramref name="exclude"/>）的敌对单位各吃一次二次命中。</summary>
        private static void AreaStrike(ref CombatData d, int a, double2 center, float radius, float damage, in CombatReading r, double2 from, int exclude)
        {
            byte want = WantOf(ref d, a);
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == exclude || d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], center) > radius + d.Radius[k])
                {
                    continue;
                }
                SecondaryStrike(ref d, a, k, damage, r, from);
            }
        }

        private static void PierceBehind(ref CombatData d, int a, int t, double2 from, double2 hitPos, float damage, in CombatReading r, byte want, ref FixedList128Bytes<int> hit)
        {
            double2 axis = hitPos - from;
            double len = math.length(axis);
            if (len < 1e-6)
            {
                return;
            }
            double2 dir = axis / len;
            int max = math.min(r.PierceHits, CombatConst.MaxReadingTargets);
            for (int q = 0; q < max; q++)
            {
                int best = -1;
                double bestAlong = double.MaxValue;
                int n = d.Count;
                for (int k = 0; k < n; k++)
                {
                    if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || Contains(ref hit, k))
                    {
                        continue;
                    }
                    double2 rel = d.Pos[k] - hitPos;
                    double along = math.dot(rel, dir);
                    if (along <= 0.0 || along > r.PierceRange)
                    {
                        continue;
                    }
                    double lateral = math.length(rel - dir * along);
                    if (lateral > d.Radius[k] + 0.6)
                    {
                        continue;
                    }
                    if (along < bestAlong)
                    {
                        bestAlong = along;
                        best = k;
                    }
                }
                if (best < 0)
                {
                    return;
                }
                hit.Add(best);
                SecondaryStrike(ref d, a, best, damage, r, from);
            }
        }

        /// <summary>离 <paramref name="p"/> 最近、在半径内、没在 <paramref name="hit"/> 里的敌对单位；并列取槽位小者。</summary>
        private static int NearestExcept(ref CombatData d, double2 p, float radius, byte want, ref FixedList128Bytes<int> hit)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || Contains(ref hit, k))
                {
                    continue;
                }
                double dist = math.distance(p, d.Pos[k]);
                if (dist > radius + d.Radius[k] || dist >= bestDist)
                {
                    continue;
                }
                bestDist = dist;
                best = k;
            }
            return best;
        }

        private static bool Contains(ref FixedList128Bytes<int> list, int v)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == v)
                {
                    return true;
                }
            }
            return false;
        }

        private static byte WantOf(ref CombatData d, int a) =>
            a >= 0 && a < d.Count && d.Faction[a] == (byte)CombatFaction.Hostile ? (byte)CombatFaction.Player : (byte)CombatFaction.Hostile;

        /// <summary>把单位朝 <paramref name="center"/> 移动 <paramref name="meters"/> 米（负数 = 推开），不越过 <paramref name="keep"/> 米。
        /// 结构物、炮塔、主核心（优先级 0）不被推动；星球格网上走碰撞（不穿越悬崖 / 建筑）。</summary>
        private static void Displace(ref CombatData d, int k, double2 center, float meters, double keep)
        {
            if (math.abs(meters) < 1e-4f || !CanDisplace(ref d, k))
            {
                return;
            }
            double2 to = center - d.Pos[k];
            double len = math.length(to);
            double2 dir = len > 1e-6 ? to / len : new double2(0, 1);
            double step = meters;
            if (step > 0 && len - step < keep)
            {
                step = math.max(0.0, len - keep);
            }
            if (math.abs(step) < 1e-4)
            {
                return;
            }
            double2 from = d.Pos[k];
            d.Pos[k] = MoveCollide(ref d, k, from, from + dir * step);
        }

        private static bool CanDisplace(ref CombatData d, int k)
        {
            byte kind = d.Kind[k];
            if (kind == (byte)CombatUnitKind.Structure || kind == (byte)CombatUnitKind.Turret)
            {
                return false;
            }
            return !(d.Faction[k] == (byte)CombatFaction.Hostile && d.Priority[k] == 0);
        }

        // ─────────────────────────────── 状态 ───────────────────────────────

        /// <summary>挂状态（没有这一击伤害的场合：外部 API、测试）。</summary>
        internal static void ApplyStatus(ref CombatData d, int k, uint mask, float seconds, float dps, float slow, float vuln, int source) =>
            ApplyStatus(ref d, k, mask, seconds, dps, slow, vuln, source, 0f);

        /// <summary>挂状态：标签位并入、到期取晚、效果同类取大（已到期的整组先清空）。<paramref name="source"/> = 挂上它的单位槽位（持续伤害归属）。
        /// FG2-FW-03：
        /// - 反应读标签：单位身上已有的标签 ∪ 这一次的标签含某条具名反应的两个配料、且这一次至少带来其中一个时，按规则顺序只结算一条
        ///   （这一击伤害 <paramref name="hit"/> × (倍率 − 1) 的额外伤害或克制返还、消耗配料、附加标签、残留区域），剩下的标签留给下一击；
        ///   这一次没有伤害（<paramref name="hit"/> ≤ 0）时，只有带附加标签或残留区域的规则会结算；
        /// - 叠层：这一次带来的每个标签层数 +1（上限 <see cref="CombatConfig.StatusStackCap"/>）；持续伤害逐位按“该标签每秒伤害 × 该位层数”取大（与 <see cref="RecomputeStatusFx"/> 同一算法），减速 / 易伤不随层数增强。</summary>
        internal static void ApplyStatus(ref CombatData d, int k, uint mask, float seconds, float dps, float slow, float vuln, int source, float hit)
        {
            if (mask == 0u || !d.IsAlive(k))
            {
                return;
            }
            double now = d.Scalars[0].Time;
            if (!d.StatusActive(k, now))
            {
                ClearStatus(ref d, k);
            }
            // 区域减速位带来的减速值单独记（在反应附加标签把别的减速并进 slow 之前取）。
            float zoneSlowIn = (mask & CombatConst.StatusBitZoneSlow) != 0u ? math.saturate(slow) : 0f;
            if (d.Reactions.Length > 0)
            {
                uint have = d.Status[k] & ~CombatConst.StatusBitZoneSlow;
                uint incoming = mask & ~CombatConst.StatusBitZoneSlow;
                uint combined = have | incoming;
                for (int i = 0; i < d.Reactions.Length; i++)
                {
                    CombatReactionRule rule = d.Reactions[i];
                    if (rule.Pair == 0u || (combined & rule.Pair) != rule.Pair || (incoming & rule.Pair) == 0u)
                    {
                        continue;
                    }
                    // 这一次没有伤害（纯状态区域的节拍、外部挂状态）时，只靠“这一击 × 倍率”起作用的反应什么也做不了：
                    // 不结算、不消耗配料、不报名字，让给下一条有附加标签 / 残留区域的规则（都没有就原样挂上标签）。
                    if (hit <= 0f && !HasNonDamageEffect(rule))
                    {
                        continue;
                    }
                    mask = FireReaction(ref d, k, i, rule, mask, hit, source, ref dps, ref slow, ref vuln);
                    break;
                }
                if (mask == 0u || !d.IsAlive(k))
                {
                    return;
                }
            }
            uint before = d.Status[k];
            d.Status[k] |= mask;
            double until = now + math.max(0.1f, seconds > 0f ? seconds : 3f);
            if (until > d.StatusUntil[k])
            {
                d.StatusUntil[k] = until;
            }
            // FG2-FW-04（DEBT-FG2FW03-02）：逐位到期——这一次带来的每一位各自续到这一次的到期（已有的位取晚、新挂的位直接取这一次，
            // 不会继承之前被消耗 / 到期清掉时残留的旧时间）；别的位不跟着续。
            int baseIdx = k * CombatConst.StatusBitStride;
            uint touched = mask;
            while (touched != 0u)
            {
                int b = math.tzcnt(touched);
                touched &= touched - 1u;
                int idx = baseIdx + b;
                d.StatusBitUntil[idx] = (before & (1u << b)) != 0u ? math.max(d.StatusBitUntil[idx], until) : until;
            }
            int cap = StackCapOf(ref d);
            ulong stacks = d.StatusStacks[k];
            uint dotMask = DotMaskOf(ref d);
            int dotStack = 1;
            float dotDps = 0f;
            uint bits = mask & ~CombatConst.StatusBitZoneSlow;
            while (bits != 0u)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1u;
                int shift = b * 2;
                int cur = (int)((stacks >> shift) & 3UL);
                int next = math.min(cap, cur + 1);
                stacks = (stacks & ~(3UL << shift)) | ((ulong)next << shift);
                if (dotMask == 0u)
                {
                    dotStack = math.max(dotStack, next);
                }
                else if ((dotMask & (1u << b)) != 0u)
                {
                    // 与 RecomputeStatusFx 同一算法：逐位 该标签每秒伤害 × 该位层数，再取大。
                    dotDps = math.max(dotDps, d.StatusFx[b].Amount * next);
                }
            }
            d.StatusStacks[k] = stacks;
            // 状态位表没配置（旧地点 / 测试内核）：这一次的持续伤害 × 带来的位里最高的层数；配置了：逐位算，这一次的持续伤害只作不叠层的下限。
            float newDps = dotMask == 0u ? math.max(0f, dps) * dotStack : math.max(dotDps, math.max(0f, dps));
            d.StatusDps[k] = math.max(d.StatusDps[k], newDps);
            d.StatusSlow[k] = math.max(d.StatusSlow[k], math.saturate(slow));
            if ((mask & CombatConst.StatusBitZoneSlow) != 0u)
            {
                d.StatusZoneSlow[k] = (before & CombatConst.StatusBitZoneSlow) != 0u ? math.max(d.StatusZoneSlow[k], zoneSlowIn) : zoneSlowIn;
            }
            d.StatusVuln[k] = math.max(d.StatusVuln[k], math.max(0f, vuln));
            d.StatusSource[k] = source >= 0 && source < d.Count ? d.Id[source] : 0;
        }

        private static void ClearStatus(ref CombatData d, int k)
        {
            d.Status[k] = 0u;
            d.StatusStacks[k] = 0UL;
            d.StatusDps[k] = 0f;
            d.StatusSlow[k] = 0f;
            d.StatusVuln[k] = 0f;
            d.StatusZoneSlow[k] = 0f;
        }

        private static int StackCapOf(ref CombatData d) =>
            d.Config.StatusStackCap <= 0 ? 1 : math.min(CombatConst.MaxStatusStacks, d.Config.StatusStackCap);

        /// <summary>持续伤害效果的状态位（按 <see cref="CombatData.StatusFx"/>；没配置时返回 0 = 不区分）。</summary>
        private static uint DotMaskOf(ref CombatData d)
        {
            uint m = 0u;
            for (int b = 0; b < 31; b++)
            {
                if (d.StatusFx[b].Effect == CombatStatusFx.Dot)
                {
                    m |= 1u << b;
                }
            }
            return m;
        }

        /// <summary>FG2-FW-03：结算一条具名标签反应（<see cref="ApplyStatus(ref CombatData,int,uint,float,float,float,float,int,float)"/> 内调用）。返回这一次还要挂上的标签位。</summary>
        private static uint FireReaction(ref CombatData d, int k, int ri, in CombatReactionRule rule, uint mask, float hit, int source,
            ref float dps, ref float slow, ref float vuln)
        {
            // 消耗：目标身上的与这一次带来的配料都去掉（含叠层）。
            if (rule.Consume != 0u)
            {
                d.Status[k] &= ~rule.Consume;
                ulong stacks = d.StatusStacks[k];
                uint c = rule.Consume;
                while (c != 0u)
                {
                    int b = math.tzcnt(c);
                    c &= c - 1u;
                    stacks &= ~(3UL << (b * 2));
                }
                d.StatusStacks[k] = stacks;
                mask &= ~rule.Consume;
                RecomputeStatusFx(ref d, k);
                RecomputeStatusUntil(ref d, k);
            }
            float bonus = hit * (rule.DamageMult - 1f);
            d.ReactionCount[ri] = d.ReactionCount[ri] + 1;
            d.ReactionDamage[ri] = d.ReactionDamage[ri] + bonus;
            // FG2-FW-04：反馈与伤害归因的进给（不进快照）：最后一次的位置 / 出手者 / 目标；打在敌对阵营身上的额外伤害（易伤照样乘，与实际掉血一致）。
            d.ReactionLastPos[ri] = d.Pos[k];
            d.ReactionLastSource[ri] = source >= 0 && source < d.Count ? d.Id[source] : 0;
            d.ReactionLastTarget[ri] = d.Id[k];
            if (bonus > 0f && d.Faction[k] != (byte)CombatFaction.Player && d.IsAlive(k) && !d.Has(k, CombatUnitFlags.Invulnerable))
            {
                d.ReactionHostileDamage[ri] = d.ReactionHostileDamage[ri] + bonus * VulnMultiplier(ref d, k);
            }
            CombatCounters counters = d.Counters[0];
            counters.ReactionsFired++;
            d.Counters[0] = counters;
            Cue(ref d, CombatEventKind.TagReaction, source, d.Id[k], bonus, d.Pos[k], (byte)ri);
            if (bonus > 0f)
            {
                DamageUnit(ref d, k, bonus, source);
            }
            else if (bonus < 0f)
            {
                Heal(ref d, k, -bonus, -1);
            }
            // 附加标签：效果按状态位表。
            uint g = rule.Grant;
            while (g != 0u)
            {
                int b = math.tzcnt(g);
                g &= g - 1u;
                mask |= 1u << b;
                MergeFx(d.StatusFx[b], ref dps, ref slow, ref vuln);
            }
            // 残留区域：目标脚下一块挂残留标签的区域（与布区同一套区域结算，暂停不走、倍速按游戏时间、进存档）。
            if (rule.ResidueBit != 0u && rule.ResidueSeconds > 0f && rule.ResidueRadius > 0f)
            {
                float rd = 0f, rs = 0f, rv = 0f;
                uint rb = rule.ResidueBit;
                while (rb != 0u)
                {
                    int b = math.tzcnt(rb);
                    rb &= rb - 1u;
                    MergeFx(d.StatusFx[b], ref rd, ref rs, ref rv);
                }
                // 阵营显式按目标定：残留区域打的是目标这一边（出手者已死、区域节拍里的槽位为 -1 时也不会落到己方头上）。
                CombatFaction residueFaction = d.Faction[k] == (byte)CombatFaction.Player ? CombatFaction.Hostile : CombatFaction.Player;
                if (SpawnZone(ref d, source, residueFaction, d.Pos[k], rule.ResidueRadius, rule.ResidueSeconds, 0f, 0f, 1f, rule.ResidueBit, 0f, rd, rs, rv))
                {
                    SetLastZoneLook(ref d, CombatZoneLook.Residue);
                }
            }
            return mask;
        }

        /// <summary>规则除了“这一击 × 倍率”以外还有没有别的效果（附加标签 / 残留区域）。</summary>
        private static bool HasNonDamageEffect(in CombatReactionRule rule) =>
            rule.Grant != 0u || (rule.ResidueBit != 0u && rule.ResidueSeconds > 0f && rule.ResidueRadius > 0f);

        /// <summary>槽位的阵营（无效槽位 = 己方，沿用旧的区域默认）。</summary>
        private static CombatFaction FactionOfSlot(ref CombatData d, int slot) =>
            slot >= 0 && slot < d.Count ? (CombatFaction)d.Faction[slot] : CombatFaction.Player;

        private static void MergeFx(in CombatStatusFx fx, ref float dps, ref float slow, ref float vuln)
        {
            switch (fx.Effect)
            {
                case CombatStatusFx.Dot: dps = math.max(dps, fx.Amount); break;
                case CombatStatusFx.Slow: slow = math.max(slow, fx.Amount); break;
                case CombatStatusFx.Vuln: vuln = math.max(vuln, fx.Amount); break;
            }
        }

        /// <summary>消耗标签后按剩下的标签重算效果（持续伤害 = 剩下的持续伤害标签 × 层数取大；减速 / 易伤取大）。状态位表没配置时只在标签全清空时归零。</summary>
        private static void RecomputeStatusFx(ref CombatData d, int k)
        {
            uint left = d.Status[k] & ~CombatConst.StatusBitZoneSlow;
            if (left == 0u && (d.Status[k] & CombatConst.StatusBitZoneSlow) == 0u)
            {
                ClearStatus(ref d, k);
                return;
            }
            if (DotMaskOf(ref d) == 0u && !AnyFx(ref d))
            {
                return;
            }
            // 区域减速位的起点取它自己的值（不是 StatusSlow 这个混合最大值：那里面可能还算着刚到期 / 被消耗的减速标签）。
            float dps = 0f, slow = (d.Status[k] & CombatConst.StatusBitZoneSlow) != 0u ? d.StatusZoneSlow[k] : 0f, vuln = 0f;
            ulong stacks = d.StatusStacks[k];
            while (left != 0u)
            {
                int b = math.tzcnt(left);
                left &= left - 1u;
                CombatStatusFx fx = d.StatusFx[b];
                int n = math.max(1, (int)((stacks >> (b * 2)) & 3UL));
                if (fx.Effect == CombatStatusFx.Dot)
                {
                    dps = math.max(dps, fx.Amount * n);
                }
                else if (fx.Effect == CombatStatusFx.Slow)
                {
                    slow = math.max(slow, fx.Amount);
                }
                else if (fx.Effect == CombatStatusFx.Vuln)
                {
                    vuln = math.max(vuln, fx.Amount);
                }
            }
            d.StatusDps[k] = dps;
            d.StatusSlow[k] = math.saturate(slow);
            d.StatusVuln[k] = vuln;
        }

        /// <summary>FG2-FW-04：整组到期时间 = 还挂着的各位里最晚的一个（消耗 / 逐位到期之后重算）。</summary>
        private static void RecomputeStatusUntil(ref CombatData d, int k)
        {
            uint bits = d.Status[k];
            double latest = 0;
            int baseIdx = k * CombatConst.StatusBitStride;
            while (bits != 0u)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1u;
                latest = math.max(latest, d.StatusBitUntil[baseIdx + b]);
            }
            d.StatusUntil[k] = latest;
        }

        private static bool AnyFx(ref CombatData d)
        {
            for (int b = 0; b < 31; b++)
            {
                if (d.StatusFx[b].Effect != CombatStatusFx.None)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>减速后的移动速度（状态到期后恢复）。</summary>
        internal static float SlowedSpeed(ref CombatData d, int i, float speed)
        {
            if (d.StatusSlow[i] <= 0f || !d.StatusActive(i, d.Scalars[0].Time))
            {
                return speed;
            }
            return speed * (1f - math.min(0.9f, d.StatusSlow[i]));
        }

        /// <summary>易伤倍率（<see cref="DamageUnit"/> 统一乘上）。</summary>
        internal static float VulnMultiplier(ref CombatData d, int t)
        {
            if (d.StatusVuln[t] <= 0f || !d.StatusActive(t, d.Scalars[0].Time))
            {
                return 1f;
            }
            return 1f + d.StatusVuln[t];
        }

        /// <summary>状态推进：到期清掉；持续伤害按 0.5 游戏秒节拍结算（节拍按统一时钟对齐，暂停不走、倍速一致）。</summary>
        private static void StepStatus(ref CombatData d, float dt)
        {
            double now = d.Scalars[0].Time;
            float statusTick = StatusTickOf(ref d);
            bool tick = math.floor(now / statusTick) != math.floor((now - dt) / statusTick);
            int n = d.Count;
            // 状态位效果表配置了（正式地点）才逐位到期：没配置的旧地点 / 测试内核不知道哪份效果属于哪一位，沿用整组到期。
            bool perBit = DotMaskOf(ref d) != 0u || AnyFx(ref d);
            for (int k = 0; k < n; k++)
            {
                if (d.Status[k] == 0u)
                {
                    continue;
                }
                if (!d.IsAlive(k) || d.StatusUntil[k] <= now)
                {
                    d.Status[k] = 0u;
                    d.StatusStacks[k] = 0UL;
                    d.StatusDps[k] = 0f;
                    d.StatusSlow[k] = 0f;
                    d.StatusVuln[k] = 0f;
                    d.StatusZoneSlow[k] = 0f;
                    continue;
                }
                if (perBit)
                {
                    // FG2-FW-04（DEBT-FG2FW03-02）：先挂的标签先到期——只清到期的那几位（含叠层），按剩下的位重算效果与整组到期。
                    uint bits = d.Status[k];
                    uint expired = 0u;
                    int baseIdx = k * CombatConst.StatusBitStride;
                    while (bits != 0u)
                    {
                        int b = math.tzcnt(bits);
                        bits &= bits - 1u;
                        if (d.StatusBitUntil[baseIdx + b] <= now)
                        {
                            expired |= 1u << b;
                        }
                    }
                    if (expired != 0u)
                    {
                        d.Status[k] &= ~expired;
                        ulong stacks = d.StatusStacks[k];
                        uint e = expired;
                        while (e != 0u)
                        {
                            int b = math.tzcnt(e);
                            e &= e - 1u;
                            stacks &= ~(3UL << (b * 2));
                        }
                        d.StatusStacks[k] = stacks;
                        if ((expired & CombatConst.StatusBitZoneSlow) != 0u)
                        {
                            d.StatusZoneSlow[k] = 0f; // 区域减速到期：减速按剩下的标签重算（下一行）。
                        }
                        RecomputeStatusFx(ref d, k);
                        if (d.Status[k] == 0u)
                        {
                            continue;
                        }
                        RecomputeStatusUntil(ref d, k);
                    }
                }
                if (tick && d.StatusDps[k] > 0f)
                {
                    DamageUnit(ref d, k, d.StatusDps[k] * statusTick, d.SlotOf(d.StatusSource[k]));
                }
            }
        }

        // ─────────────────────────────── 区域 ───────────────────────────────

        /// <summary>生成一块区域。<paramref name="faction"/> = 区域属于哪一边（打另一边），由调用方显式给出，不从槽位推断。</summary>
        private static bool SpawnZone(ref CombatData d, int owner, CombatFaction faction, double2 pos, float radius, float seconds, float dps, float growth, float tickScale,
            uint mask, float statusSeconds, float statusDps, float slow, float vuln, byte kind = CombatConst.ZoneKindReading)
        {
            if (radius <= 0f || seconds <= 0f)
            {
                return false;
            }
            // FG6-DEF-02 复审修复：读法区域与陷阱场地各有自己的上限、分开计数（场地铺满不挤掉炮塔 / 机器的读法区域，反之亦然）。
            // 总数没到某一种的上限时那一种必然没满（O(1)）；只有接近上限时才数一遍（O(区域数)，区域数 ≤ 两个上限之和）。
            bool field = kind == CombatConst.ZoneKindField;
            int cap = field ? d.FieldZoneCap : d.ZoneCap;
            if (d.Zones.Length >= cap && CountZones(ref d, kind) >= cap)
            {
                if (!field)
                {
                    RefuseReading(ref d); // 场地被拒由调用方（陷阱发射器）逐座计数并写进状态行
                }
                return false;
            }
            double now = d.Scalars[0].Time;
            float interval = ZoneTickOf(ref d) / (tickScale > 0f ? tickScale : 1f);
            d.Zones.Add(new CombatZone
            {
                Pos = pos,
                Radius = radius,
                Growth = growth,
                Born = now,
                Until = now + seconds,
                NextTick = now + interval,
                TickInterval = interval,
                Dps = math.max(0f, dps),
                StatusMask = mask,
                StatusSeconds = statusSeconds > 0f ? statusSeconds : ZoneStatusSecondsOf(ref d),
                StatusDps = statusDps,
                StatusSlow = slow,
                StatusVuln = vuln,
                Owner = owner >= 0 && owner < d.Count ? d.Id[owner] : 0,
                Faction = faction,
                Kind = kind,
            });
            CombatCounters c = d.Counters[0];
            c.ZonesSpawned++;
            d.Counters[0] = c;
            return true;
        }

        /// <summary>某一种区域此刻有几块（只在总数接近上限时调用）。</summary>
        private static int CountZones(ref CombatData d, byte kind)
        {
            int n = 0;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                if (d.Zones[z].Kind == kind)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>FG6-DEF-02（FGR-DEF-013 陷阱发射器）：不属于任何单位的场地（陷阱发射器铺的油膜带 / 冷却液带 / 电磁场……），与读法区域同一套结算（节拍伤害、挂标签、触发反应）。
        /// 容量满时不生成并计数（场地自己的上限 <see cref="CombatData.FieldZoneCap"/>，与读法区域分开，互不挤占）。</summary>
        internal static bool SpawnFieldZone(ref CombatData d, CombatFaction faction, double2 pos, float radius, float seconds, float dps, uint mask, float statusSeconds,
            float statusDps, float slow, float vuln, CombatZoneLook look)
        {
            if (!SpawnZone(ref d, -1, faction, pos, radius, seconds, dps, 0f, 1f, mask, statusSeconds, statusDps, slow, vuln, CombatConst.ZoneKindField))
            {
                return false;
            }
            SetLastZoneLook(ref d, look);
            return true;
        }

        /// <summary>FG2-VFX-02：刚生成的那块区域画成什么样（只影响画面）。</summary>
        private static void SetLastZoneLook(ref CombatData d, CombatZoneLook look)
        {
            int last = d.Zones.Length - 1;
            if (last >= 0)
            {
                CombatZone z = d.Zones[last];
                z.Look = look;
                d.Zones[last] = z;
            }
        }

        /// <summary>FG2-FW-04（DEBT-FG2FW02-02 读法弹字与音效）：读法生成了一块区域 / 一次回波 / 一架无人机——记进给（累计次数、最后的位置与出手者；不进快照）。</summary>
        private static void NoteReading(ref CombatData d, int kind, double2 pos, int ownerSlot)
        {
            d.ReadingFeedCount[kind] = d.ReadingFeedCount[kind] + 1;
            d.ReadingFeedPos[kind] = pos;
            d.ReadingFeedOwner[kind] = ownerSlot >= 0 && ownerSlot < d.Count ? d.Id[ownerSlot] : 0;
        }

        /// <summary>区域推进：到期移除；按节拍对区域内的敌对阵营单位造成伤害并挂状态（区域的“减速”只要站在里面就挂上）。O(区域 × 单位)。</summary>
        private static void StepZones(ref CombatData d, float dt)
        {
            double now = d.Scalars[0].Time;
            int m = d.Zones.Length;
            int write = 0;
            for (int z = 0; z < m; z++)
            {
                CombatZone zone = d.Zones[z];
                if (zone.Until <= now)
                {
                    continue;
                }
                zone.Radius += zone.Growth * dt;
                if (now >= zone.NextTick)
                {
                    zone.NextTick += zone.TickInterval;
                    if (zone.NextTick <= now)
                    {
                        zone.NextTick = now + zone.TickInterval;
                    }
                    // FG6-LOG-10：液洼先看自己有没有遇上能起反应的标签（整片反应成残留区域），再按节拍给站在里面的单位挂标签。
                    if (zone.Kind == CombatConst.ZoneKindLeak && zone.Phase == CombatConst.LeakPhasePuddle)
                    {
                        TryReactLeak(ref d, ref zone, z, now);
                    }
                    int owner = d.SlotOf(zone.Owner);
                    byte want = zone.Faction == CombatFaction.Hostile ? (byte)CombatFaction.Player : (byte)CombatFaction.Hostile;
                    // FG6-LOG-10：中立区域（液洼与它反应成的残留）对己方机器与敌人一视同仁；建筑 / 炮塔类结构单位不在这里结算
                    // （它们挨的火由热更层按建筑耐久结算一次，避免内核与建筑记录各扣一遍）。
                    bool neutral = zone.Faction == CombatFaction.Neutral;
                    int n = d.Count;
                    for (int k = 0; k < n; k++)
                    {
                        if (neutral
                                ? d.Faction[k] == (byte)CombatFaction.Neutral || d.Kind[k] == (byte)CombatUnitKind.Turret || d.Kind[k] == (byte)CombatUnitKind.Structure
                                : d.Faction[k] != want)
                        {
                            continue;
                        }
                        if (!d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                        {
                            continue;
                        }
                        if (math.distance(d.Pos[k], zone.Pos) > zone.Radius + d.Radius[k])
                        {
                            continue;
                        }
                        float tickDamage = zone.Dps > 0f ? zone.Dps * zone.TickInterval : 0f;
                        if (tickDamage > 0f)
                        {
                            DamageUnit(ref d, k, tickDamage, owner);
                        }
                        uint mask = zone.StatusMask != 0u ? zone.StatusMask : (zone.StatusSlow > 0f ? CombatConst.StatusBitZoneSlow : 0u);
                        // 区域节拍的“这一击”：区域自己的节拍伤害；没有直接伤害的纯状态区域取它挂的持续伤害一个节拍的量（如爆燃残留的燃烧区）；
                        // 两者都没有（减速网之类）= 0，只靠倍率的反应不结算（见 ApplyStatus）。
                        float reactionHit = tickDamage > 0f ? tickDamage : math.max(0f, zone.StatusDps) * zone.TickInterval;
                        ApplyStatus(ref d, k, mask, zone.StatusSeconds, zone.StatusDps, zone.StatusSlow, zone.StatusVuln, owner, reactionHit);
                    }
                }
                d.Zones[write++] = zone;
            }
            // 节拍里结算的反应（爆燃 → 燃烧区）会在循环中往列表尾部追加新区域（下标 ≥ m）：搬到压缩后的尾部保留下来，不能被截掉。
            int end = d.Zones.Length;
            for (int z = m; z < end; z++)
            {
                d.Zones[write++] = d.Zones[z];
            }
            d.Zones.ResizeUninitialized(write);
        }

        // ─────────────────────────────── 液洼（FG6-LOG-10，FGR-LOG-046）───────────────────────────────

        /// <summary>液洼区域的“长期存在”：由热更层移除（修好管线后逐渐缩小到 0）。</summary>
        private const double LeakForeverSeconds = 1e9;

        /// <summary>登记或更新编号为 <paramref name="leakId"/> 的液洼（还没反应的那块）：位置 / 半径 / 标签就地改写，节拍相位不变；不存在时新建（中立、长期存在，由热更层移除）。
        /// 同编号的区域已经反应成残留时不动，返回 false。O(区域数)。</summary>
        internal static bool UpsertLeakZone(ref CombatData d, int leakId, double2 pos, float radius, uint mask)
        {
            if (leakId <= 0 || !(radius > 0f) || mask == 0u)
            {
                return false;
            }
            int owner = -leakId;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                CombatZone zn = d.Zones[z];
                if (zn.Kind != CombatConst.ZoneKindLeak || zn.Owner != owner)
                {
                    continue;
                }
                if (zn.Phase != CombatConst.LeakPhasePuddle)
                {
                    return false;
                }
                zn.Pos = pos;
                zn.Radius = radius;
                zn.StatusMask = mask;
                d.Zones[z] = zn;
                return true;
            }
            double now = d.Scalars[0].Time;
            float interval = ZoneTickOf(ref d);
            d.Zones.Add(new CombatZone
            {
                Pos = pos,
                Radius = radius,
                Born = now,
                Until = now + LeakForeverSeconds,
                NextTick = now + interval,
                TickInterval = interval,
                StatusMask = mask,
                StatusSeconds = ZoneStatusSecondsOf(ref d),
                Owner = owner,
                Faction = CombatFaction.Neutral,
                Look = CombatZoneLook.Pool,
                Kind = CombatConst.ZoneKindLeak,
                Phase = CombatConst.LeakPhasePuddle,
            });
            return true;
        }

        /// <summary>移除编号为 <paramref name="leakId"/> 的液洼区域（含已反应的残留）。返回移除了几块。O(区域数)。</summary>
        internal static int RemoveLeakZone(ref CombatData d, int leakId)
        {
            int owner = -leakId;
            int write = 0;
            int removed = 0;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                CombatZone zn = d.Zones[z];
                if (zn.Kind == CombatConst.ZoneKindLeak && zn.Owner == owner)
                {
                    removed++;
                    continue;
                }
                d.Zones[write++] = zn;
            }
            d.Zones.ResizeUninitialized(write);
            return removed;
        }

        /// <summary>
        /// 液洼遇上反应（只在液洼的区域节拍上检查）：此刻和它重叠的其余区域（读法区域、陷阱场地、已反应的液洼；不含别的还没反应的液洼——两摊液体挨着不算）挂的标签，
        /// 加上站在它里面的单位身上正在生效的标签，合起来是“遇到的标签”。按反应登记顺序找第一条：配料一半在液洼上、另一半在遇到的标签里、且这条反应会留下残留区域——
        /// 整片液洼就地变成这条反应的残留区域（半径取液洼与残留的较大者；持续 max(残留时长, 液洼反应时长配置)；中立：站进去的己方机器与敌人都挨），
        /// 记反应计数、发反应提示事件（与单位身上的反应同一套命名 / 反馈），并给热更层记一条液洼反应（告警、烧坏范围内的管线 / 传送带 / 建筑）。
        /// 只靠倍率起作用的反应（短路、电解……）液洼本身不结算：站进液洼、挂上标签的单位挨打时照常触发。O(区域数 + 单位数)。
        /// </summary>
        private static void TryReactLeak(ref CombatData d, ref CombatZone leak, int self, double now)
        {
            if (d.Reactions.Length == 0)
            {
                return;
            }
            uint have = leak.StatusMask & ~CombatConst.StatusBitZoneSlow;
            uint met = 0u;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                if (z == self)
                {
                    continue;
                }
                CombatZone o = d.Zones[z];
                if (o.Until <= now || (o.Kind == CombatConst.ZoneKindLeak && o.Phase == CombatConst.LeakPhasePuddle))
                {
                    continue;
                }
                if (math.distance(o.Pos, leak.Pos) <= o.Radius + leak.Radius)
                {
                    met |= o.StatusMask;
                }
            }
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (!d.IsAlive(k) || d.Status[k] == 0u || !d.StatusActive(k, now))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], leak.Pos) <= leak.Radius + d.Radius[k])
                {
                    met |= d.Status[k];
                }
            }
            met &= ~CombatConst.StatusBitZoneSlow;
            if (met == 0u)
            {
                return;
            }
            for (int i = 0; i < d.Reactions.Length; i++)
            {
                CombatReactionRule rule = d.Reactions[i];
                if (rule.Pair == 0u || rule.ResidueBit == 0u || rule.ResidueSeconds <= 0f || rule.ResidueRadius <= 0f)
                {
                    continue;
                }
                uint onPuddle = have & rule.Pair;
                uint other = rule.Pair & ~onPuddle;
                if (onPuddle == 0u || other == 0u || (met & other) != other)
                {
                    continue;
                }
                float rd = 0f, rs = 0f, rv = 0f;
                uint rb = rule.ResidueBit;
                while (rb != 0u)
                {
                    int b = math.tzcnt(rb);
                    rb &= rb - 1u;
                    MergeFx(d.StatusFx[b], ref rd, ref rs, ref rv);
                }
                float seconds = math.max(rule.ResidueSeconds, math.max(0f, d.Config.LeakReactSeconds));
                leak.Phase = CombatConst.LeakPhaseReacted;
                leak.StatusMask = rule.ResidueBit;
                leak.StatusDps = rd;
                leak.StatusSlow = rs;
                leak.StatusVuln = rv;
                leak.Radius = math.max(leak.Radius, rule.ResidueRadius);
                leak.Born = now;
                leak.Until = now + seconds;
                leak.Look = CombatZoneLook.Residue;
                d.ReactionCount[i] = d.ReactionCount[i] + 1;
                d.ReactionLastPos[i] = leak.Pos;
                d.ReactionLastSource[i] = 0;
                d.ReactionLastTarget[i] = 0;
                CombatCounters counters = d.Counters[0];
                counters.ReactionsFired++;
                d.Counters[0] = counters;
                Cue(ref d, CombatEventKind.TagReaction, -1, 0, 0f, leak.Pos, (byte)i);
                d.LeakReactions.Add(new int2(-leak.Owner, i));
                return;
            }
        }

        // ─────────────────────────────── 回波 ───────────────────────────────

        private static void QueueEcho(ref CombatData d, int a, int t, double at, float damage, in CombatReading r)
        {
            if (damage <= 0f)
            {
                return;
            }
            if (d.Echoes.Length >= d.EchoCap)
            {
                RefuseReading(ref d);
                return;
            }
            d.Echoes.Add(new CombatEcho
            {
                At = at,
                Target = d.Id[t],
                Owner = a >= 0 && a < d.Count ? d.Id[a] : 0,
                Damage = damage,
                StatusMask = r.StatusMask,
                StatusSeconds = r.StatusSeconds,
                StatusDps = r.StatusDps,
                StatusSlow = r.StatusSlow,
                StatusVuln = r.StatusVuln,
            });
            CombatCounters c = d.Counters[0];
            c.EchoesQueued++;
            d.Counters[0] = c;
            NoteReading(ref d, CombatConst.ReadingFeedEcho, t >= 0 && t < d.Count ? d.Pos[t] : double2.zero, a);
        }

        private static void StepEchoes(ref CombatData d)
        {
            double now = d.Scalars[0].Time;
            int m = d.Echoes.Length;
            int write = 0;
            for (int e = 0; e < m; e++)
            {
                CombatEcho echo = d.Echoes[e];
                if (echo.At > now)
                {
                    d.Echoes[write++] = echo;
                    continue;
                }
                int t = d.SlotOf(echo.Target);
                if (t < 0 || !d.IsAlive(t) || !d.Has(t, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                int owner = d.SlotOf(echo.Owner);
                if (DamageUnit(ref d, t, echo.Damage, owner))
                {
                    ApplyStatus(ref d, t, echo.StatusMask, echo.StatusSeconds, echo.StatusDps, echo.StatusSlow, echo.StatusVuln, owner, echo.Damage);
                }
            }
            d.Echoes.ResizeUninitialized(write);
        }

        // ─────────────────────────────── 无人机 ───────────────────────────────

        /// <summary>补足这台单位的无人机到 <paramref name="want"/> 架（已有的刷新存活时间；上限 <see cref="CombatConst.MaxDronesPerOwner"/>）。
        /// FG2-VFX-02：定点无人机（哨戒桩，<see cref="CombatReading.DroneAnchored"/>）插在母机朝目标方向的身前、之后不动。
        /// 再开火时，够不着的旧桩（母机离锚点超过两倍牵引绳，或这次的目标不在它“牵引绳 + 目标半径”内）当场拔掉、在身前补插新桩——
        /// 同一台母机任何时刻最多 <paramref name="want"/> 根桩（修复轮：原先旧桩不计数、可以越插越多）。母机不再开火时，已插的桩原地打到到期。</summary>
        private static void LaunchDrones(ref CombatData d, int a, in CombatWeapon wp, int want, int targetSlot = -1)
        {
            CombatReading r = wp.Reading;
            want = math.min(want, CombatConst.MaxDronesPerOwner);
            if (want <= 0 || a < 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            int ownerId = d.Id[a];
            float seconds = r.DroneSeconds > 0f ? r.DroneSeconds : 8f;
            bool anchored = r.DroneAnchored != 0;
            float leash = DroneLeashOf(r);
            // 插桩方向 = 母机朝当前目标（没有目标时朝 +Y）。
            float2 face = new float2(0f, 1f);
            int tgt = targetSlot >= 0 && targetSlot < d.Count ? targetSlot : (d.Cmd[a].Target > 0 ? d.SlotOf(d.Cmd[a].Target) : -1);
            if (tgt >= 0 && math.lengthsq(d.Pos[tgt] - d.Pos[a]) > 1e-8)
            {
                face = (float2)math.normalize(d.Pos[tgt] - d.Pos[a]);
            }
            int have = 0;
            var stale = new FixedList128Bytes<int>();
            for (int k = 0; k < d.Drones.Length; k++)
            {
                CombatDrone dr = d.Drones[k];
                if (dr.Owner != ownerId || dr.Until <= now)
                {
                    continue;
                }
                // 跟飞无人机照旧全部刷新；定点桩只留够得着的、且不超过 want 根。
                bool keep = dr.Anchored == 0
                            || (have < want
                                && math.distance(dr.Anchor, d.Pos[a]) <= leash * 2f
                                && (tgt < 0 || math.distance(dr.Anchor, d.Pos[tgt]) <= dr.Leash + d.Radius[tgt]));
                if (!keep)
                {
                    // 够不着的旧桩 / 超出上限的桩：拔掉（槽位留给新桩复用；用不上的当场到期，下一步推进时移除）。
                    if (stale.Length < stale.Capacity)
                    {
                        stale.Add(k);
                    }
                    dr.Until = now;
                    d.Drones[k] = dr;
                    continue;
                }
                have++;
                dr.Until = now + seconds;
                d.Drones[k] = dr;
            }
            int reuse = 0;
            for (int q = have; q < want; q++)
            {
                bool recycled = reuse < stale.Length;
                if (!recycled && d.Drones.Length >= d.DroneCap)
                {
                    RefuseReading(ref d);
                    return;
                }
                float ang = q * 2.399963f; // 黄金角，几架无人机不叠在一处
                double2 at = d.Pos[a] + new double2(math.cos(ang), math.sin(ang)) * (d.Radius[a] + 0.8);
                if (anchored)
                {
                    // 哨戒桩：插在母机身前 1.5 米、左右错开（第 q 根桩），不叠在一处。
                    float2 side = new float2(-face.y, face.x);
                    at = d.Pos[a] + (double2)(face * (d.Radius[a] + SentryPlantAhead) + side * SentrySpread(q));
                }
                var fresh = new CombatDrone
                {
                    Pos = at,
                    Prev = at,
                    Anchored = (byte)(anchored ? 1 : 0),
                    Anchor = at,
                    Until = now + seconds,
                    NextHit = now + 0.2 * (q + 1),
                    Owner = ownerId,
                    Weapon = d.Weapon[a],
                    Damage = math.max(0f, wp.Damage) * (r.DroneRatio > 0f ? r.DroneRatio : 0.4f),
                    Leash = leash,
                    Cooldown = r.DroneCooldown > 0f ? r.DroneCooldown : 0.8f,
                    Faction = (CombatFaction)d.Faction[a],
                };
                if (recycled)
                {
                    d.Drones[stale[reuse++]] = fresh;
                }
                else
                {
                    d.Drones.Add(fresh);
                }
                CombatCounters c = d.Counters[0];
                c.DronesLaunched++;
                d.Counters[0] = c;
                NoteReading(ref d, CombatConst.ReadingFeedDrone, at, a);
            }
        }

        /// <summary>无人机推进：母机阵亡 / 到期即消失；在母机牵引绳内追最近的敌对单位，按间隔命中（命中带母机武器的读法）；没有目标时回到母机身边。</summary>
        private static void StepDrones(ref CombatData d, float dt)
        {
            double now = d.Scalars[0].Time;
            int m = d.Drones.Length;
            int write = 0;
            for (int q = 0; q < m; q++)
            {
                CombatDrone dr = d.Drones[q];
                int owner = d.SlotOf(dr.Owner);
                if (owner < 0 || !d.IsAlive(owner) || dr.Until <= now)
                {
                    continue;
                }
                dr.Prev = dr.Pos;
                byte want = dr.Faction == CombatFaction.Hostile ? (byte)CombatFaction.Player : (byte)CombatFaction.Hostile;
                bool post = dr.Anchored != 0;
                double2 home = post ? dr.Anchor : d.Pos[owner];
                int target = -1;
                double bestDist = double.MaxValue;
                int n = d.Count;
                for (int k = 0; k < n; k++)
                {
                    if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                    {
                        continue;
                    }
                    if (math.distance(d.Pos[k], home) > dr.Leash + d.Radius[k])
                    {
                        continue;
                    }
                    double dist = math.distance(d.Pos[k], dr.Pos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        target = k;
                    }
                }
                double2 goal;
                double stop;
                if (post)
                {
                    // 哨戒桩不移动：射程 = 牵引绳（锚点周围），够得着就按间隔打。
                    goal = dr.Anchor;
                    stop = 0.0;
                }
                else if (target >= 0)
                {
                    goal = d.Pos[target];
                    stop = DroneReachOf(ref d) * 0.8 + d.Radius[target];
                }
                else
                {
                    float ang = (q % CombatConst.MaxDronesPerOwner) * 2.399963f + (float)now * 1.5f;
                    goal = home + new double2(math.cos(ang), math.sin(ang)) * (d.Radius[owner] + 1.2);
                    stop = 0.05;
                }
                double2 to = goal - dr.Pos;
                double len = math.length(to);
                if (len > stop)
                {
                    dr.Pos += to / len * math.min(DroneSpeedOf(ref d) * dt, len - stop);
                }
                bool inReach = target >= 0 && (post
                    ? math.distance(dr.Anchor, d.Pos[target]) <= dr.Leash + d.Radius[target]
                    : math.distance(dr.Pos, d.Pos[target]) <= DroneReachOf(ref d) + d.Radius[target]);
                if (inReach && now >= dr.NextHit)
                {
                    dr.NextHit = now + dr.Cooldown;
                    if (dr.Weapon >= 0 && dr.Weapon < d.Weapons.Length)
                    {
                        CombatWeapon wp = d.Weapons[dr.Weapon];
                        CombatReading r = wp.Reading;
                        double tNow = now;
                        bool wasMarked = d.MarkedUntil[target] > tNow;
                        double2 hitPos = d.Pos[target];
                        float dealt = StrikeDamage(ref d, owner, target, dr.Damage, r, dr.Pos, false, 0f);
                        if (DamageUnit(ref d, target, dealt, owner))
                        {
                            PerTarget(ref d, owner, target, dealt, r, dr.Pos);
                            // 无人机命中的锚定读法：伤害基数是无人机自己的伤害；不再补无人机（伴飞只在母机开火时补）；
                            // 以无人机为锚（viaDrone）——跃击不把母机拖向无人机自己挑的目标，回拉 / 脚下区域以无人机为中心（FGR-BASE-020）。
                            CombatWeapon droneWp = wp;
                            droneWp.Damage = dr.Damage;
                            droneWp.Reading.EscortDrones = 0;
                            Anchored(ref d, owner, target, hitPos, dealt, dr.Damage, droneWp, wasMarked, dr.Pos, true);
                        }
                    }
                }
                d.Drones[write++] = dr;
            }
            d.Drones.ResizeUninitialized(write);
        }

        private static void RefuseReading(ref CombatData d)
        {
            CombatCounters c = d.Counters[0];
            c.ReadingRefused++;
            d.Counters[0] = c;
        }

        /// <summary>标记跳转的核心（具名反应与通用读法共用）：主目标附近已标记、视线可达的存活敌对单位，按距离升序至多跳 <paramref name="jumpMax"/> 个，每跳 × 衰减。返回跳了几个。</summary>
        private static int MarkJumpCore(ref CombatData d, int a, int primary, double2 primaryPos, float primaryDamage, float jumpRange, float jumpFalloff, int jumpMax)
        {
            double now = d.Scalars[0].Time;
            int max = math.min(jumpMax, CombatConst.MaxJumpTargets);
            var picked = new FixedList64Bytes<int>();
            var pickedDist = new FixedList64Bytes<float>();
            int n = d.Count;
            byte hostile = d.Faction[primary];
            for (int k = 0; k < n; k++)
            {
                if (k == primary || !d.IsAlive(k) || d.Faction[k] != hostile || d.MarkedUntil[k] <= now)
                {
                    continue;
                }
                float dist = (float)math.distance(primaryPos, d.Pos[k]);
                if (dist > jumpRange || !LineOfSight(ref d, primaryPos, d.Pos[k]))
                {
                    continue;
                }
                int at = pickedDist.Length;
                for (int q = 0; q < pickedDist.Length; q++)
                {
                    if (dist < pickedDist[q])
                    {
                        at = q;
                        break;
                    }
                }
                if (at >= max)
                {
                    continue;
                }
                pickedDist.Insert(at, dist);
                picked.Insert(at, k);
                if (picked.Length > max)
                {
                    picked.RemoveAt(picked.Length - 1);
                    pickedDist.RemoveAt(pickedDist.Length - 1);
                }
            }
            float jump = primaryDamage;
            for (int q = 0; q < picked.Length; q++)
            {
                jump *= jumpFalloff;
                DamageUnit(ref d, picked[q], jump, a);
            }
            return picked.Length;
        }

        /// <summary>FG2-E2E-01（FG-GAP-043）：记一条引信弹迹（开火那一刻：炮口 → 命中点；<paramref name="flashOnly"/> 时只有炮口装定闪光，弹体自己会飞）。
        /// 只在武器带引信弹迹标记、且配置的停留时间 &gt; 0 时记；满了挤掉最老的一条。O(1) 摊还（挤掉时整体前移 ≤ 容量）。</summary>
        internal static void PushTrace(ref CombatData d, int a, int t, in CombatWeapon wp, bool flashOnly)
        {
            if (wp.FuseTrace == 0 || !(d.Config.FuseTraceSeconds > 0f) || a < 0 || a >= d.Count)
            {
                return;
            }
            if (d.Traces.Length >= CombatConst.TraceCapacity)
            {
                d.Traces.RemoveAt(0);
            }
            double2 from = d.Pos[a];
            double2 to = t >= 0 && t < d.Count ? d.Pos[t] : from;
            d.Traces.Add(new CombatShotTrace { From = from, To = to, Born = d.Scalars[0].Time, Faction = d.Faction[a], Line = (byte)(flashOnly ? 0 : 1) });
        }

        /// <summary>FG2-E2E-01：弹迹按游戏时间到期（暂停不推进内核 = 不消失；倍速多推进几步 = 同步加快）。</summary>
        private static void StepTraces(ref CombatData d)
        {
            double cut = d.Scalars[0].Time - d.Config.FuseTraceSeconds;
            int write = 0;
            for (int i = 0; i < d.Traces.Length; i++)
            {
                CombatShotTrace tr = d.Traces[i];
                if (tr.Born > cut)
                {
                    d.Traces[write++] = tr;
                }
            }
            d.Traces.ResizeUninitialized(write);
        }

        /// <summary>FG2-FW-02：读法相关的逐步推进（在弹体之后、兴趣点之前）：无人机 → 区域 → 回波 → 状态。</summary>
        private static void StepReadings(ref CombatData d, float dt)
        {
            if (d.Drones.Length > 0)
            {
                StepDrones(ref d, dt);
            }
            if (d.Zones.Length > 0)
            {
                StepZones(ref d, dt);
            }
            if (d.Echoes.Length > 0)
            {
                StepEchoes(ref d);
            }
            if (d.Traces.Length > 0)
            {
                StepTraces(ref d);
            }
            StepStatus(ref d, dt);
        }
    }
}
