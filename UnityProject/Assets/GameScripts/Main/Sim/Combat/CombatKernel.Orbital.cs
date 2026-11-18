using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG12-EVT-04 轨道坠落的内核接口（热更层只经 Campaign/Combat/CombatSite 调用）：撞击时一次性的圈内单位伤害、护盾吸收、坠落残骸。
    /// 都是事件那一刻调用一次的一次性结算（不是每步），不新增任何内核状态：残骸 / 护盾本来就在快照里（不升 FormatVersion）。
    /// </summary>
    public sealed partial class CombatKernel
    {
        /// <summary>
        /// 圈内单位受撞击：圆心 <paramref name="pos"/>、半径 <paramref name="radius"/>（含单位半径）内活着的机器与敌人各受 <paramref name="damage"/>，
        /// 走统一伤害入口（evt.hurt 来源码 = 冲击；外部血量单位发伤害请求，由热更层判死，FGR-EMG-113）。炮塔 / 结构体不在这里（按建筑在热更层结算，避免重复）。
        /// O(单位数)，只在撞击那一刻。返回受击单位数。
        /// </summary>
        public int StrikeCircle(double2 pos, float radius, float damage)
        {
            if (!IsFinite(pos) || !(radius > 0f) || !(damage > 0f))
            {
                return 0;
            }
            int hits = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i))
                {
                    continue;
                }
                byte kind = _d.Kind[i];
                if (kind != (byte)CombatUnitKind.Machine && kind != (byte)CombatUnitKind.Enemy)
                {
                    continue;
                }
                double reach = radius + _d.Radius[i];
                if (math.distancesq(_d.Pos[i], pos) > reach * reach)
                {
                    continue;
                }
                CombatLogic.DamageFrom(ref _d, i, damage, -1, EmgCode.SrcImpact);
                hits++;
            }
            if (hits > 0)
            {
                Touch();
            }
            return hits;
        }

        /// <summary>
        /// 护盾吸收一次外来冲击（与弹体被护盾吸收 TryAbsorbByShield 同一套读数：护盾值减少、累计吸收量 / 次数增加、发 ShieldAbsorb 提示；打空计一次耗尽并收起）。不活跃或没有护盾值时吸收 0。
        /// 返回实际吸收量（≤ <paramref name="amount"/>、≤ 剩余护盾值）。
        /// </summary>
        public float AbsorbShield(int extKey, float amount)
        {
            int at = ShieldIndexOf(extKey);
            if (at < 0 || !(amount > 0f))
            {
                return 0f;
            }
            CombatShield sh = _d.Shields[at];
            if (sh.Active == 0 || !(sh.Hp > 0f))
            {
                return 0f;
            }
            float took = math.min(sh.Hp, amount);
            sh.Hp -= took;
            sh.Absorbed += took;
            sh.Hits++;
            byte emptied = 0;
            if (sh.Hp <= 0f)
            {
                // 与 TryAbsorbByShield 同一口径：打空即收起（热更层状态机按 Active == 0 转过载、发过载通知）
                sh.Hp = 0f;
                sh.Active = 0;
                sh.Depletions++;
                emptied = 1;
            }
            _d.Shields[at] = sh;
            CombatLogic.Cue(ref _d, CombatEventKind.ShieldAbsorb, -1, sh.ExtKey, took, sh.Pos, emptied);
            Touch();
            return took;
        }

        /// <summary>
        /// 直接生成一块残骸（坠落残骸，FGR-EMG-113：挡弹、减速、可燃 / 导电按实体档案、可被场推动）；满容量时最早的崩解为碎屑（与阵亡残骸同一规则）。
        /// 数值不合理返回 0，否则返回残骸稳定 Id。
        /// </summary>
        public int SpawnWreckAt(double2 pos, float radius, float maxHp, float mass, float temp)
        {
            if (!IsFinite(pos) || !(radius > 0f) || !(maxHp > 0f) || !(mass >= 0f) || float.IsNaN(temp) || float.IsInfinity(temp))
            {
                return 0;
            }
            int id = CombatLogic.EmgPlaceWreck(ref _d, pos, radius, maxHp, mass, temp, (byte)CombatUnitKind.Structure);
            Touch();
            return id;
        }
    }
}
