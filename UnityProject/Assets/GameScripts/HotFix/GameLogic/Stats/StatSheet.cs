using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Stats
{
    /// <summary>
    /// 属性 id。集中定义，避免用字符串 key 导致拼写错误。
    /// 新增属性 = 加一个枚举值 + 在 StatSheet 默认值里给基础值。
    /// </summary>
    public enum StatId
    {
        None = 0,
        MaxHealth = 1,
        MoveSpeed = 2,
        /// <summary>体积：细胞阶段同时是攻击力、防御力与移动惩罚。</summary>
        Volume = 3,
        MeleeDamage = 4,
        /// <summary>吞噬体积门槛倍率，越小越容易吞。</summary>
        DevourRatio = 5,
        /// <summary>吞噬收益倍率（营养质 + 进化能）。</summary>
        DevourGain = 6,
        StaminaMax = 7,
        StaminaRegen = 8,
        DashCost = 9,
        DashDistance = 10,
        DashInvulnTime = 11,
        /// <summary>技能冷却缩减（0-0.8）。</summary>
        CooldownReduction = 12,
        /// <summary>技能范围倍率。</summary>
        AreaScale = 13,
        /// <summary>技能伤害倍率。</summary>
        AbilityPower = 14,
        /// <summary>电系伤害倍率。</summary>
        ElectricPower = 15,
        /// <summary>电弧连锁次数加成。</summary>
        ChainBonus = 16,
        /// <summary>附属体数量上限。</summary>
        MinionCap = 17,
        /// <summary>附属体伤害倍率。</summary>
        MinionPower = 18,
        /// <summary>生命回复（每秒）。</summary>
        HealthRegen = 19,
        /// <summary>击杀回复量。</summary>
        KillHeal = 20,
        /// <summary>受到伤害倍率（减伤是 &lt; 1）。</summary>
        DamageTaken = 21,
        /// <summary>敌人仇恨倍率。</summary>
        AggroScale = 22,
        /// <summary>拾取半径。</summary>
        PickupRadius = 23,
        /// <summary>进化能获取倍率。</summary>
        EvoGain = 24,
        /// <summary>营养质获取倍率。</summary>
        NutrientGain = 25,
        /// <summary>主动技能槽位数（1-5）。</summary>
        AbilitySlots = 26,
        /// <summary>污染度上限。</summary>
        PollutionCap = 27,
        /// <summary>菌毯面积倍率。</summary>
        MyceliumScale = 28,
        /// <summary>投射物数量加成。</summary>
        ProjectileCount = 29,
        /// <summary>状态持续时间倍率。</summary>
        StatusDuration = 30,

        Count = 31,
    }

    /// <summary>修正器叠加层级。顺序固定，避免乘法爆炸。</summary>
    public enum ModifierOp
    {
        /// <summary>加值：+20 生命。</summary>
        Flat = 0,
        /// <summary>加法百分比：+15% 移速，同类相加。</summary>
        PctAdd = 1,
        /// <summary>乘法百分比：稀有卡专用，独立相乘。</summary>
        PctMul = 2,
    }

    public struct StatModifier
    {
        public StatId Stat;
        public ModifierOp Op;
        public float Value;
        /// <summary>来源 id（卡牌 id / 状态 id）。用于按来源批量移除。</summary>
        public int SourceId;

        public StatModifier(StatId stat, ModifierOp op, float value, int sourceId = 0)
        {
            Stat = stat;
            Op = op;
            Value = value;
            SourceId = sourceId;
        }
    }

    /// <summary>
    /// 属性容器。脏标记重算，不每帧算。
    ///
    /// 公式：final = (base + Σflat) * (1 + ΣpctAdd) * Π(1 + pctMul)
    /// 详见 DesignDocs/Game_Framework_Design.md §5.3。
    /// </summary>
    public sealed class StatSheet
    {
        private readonly float[] _base = new float[(int)StatId.Count];
        private readonly float[] _final = new float[(int)StatId.Count];
        private readonly List<StatModifier> _mods = new List<StatModifier>(64);
        private bool _dirty = true;

        public StatSheet()
        {
            ResetToDefaults();
        }

        /// <summary>细胞阶段初始面板。数值最终应来自 cell.Global 配置表。</summary>
        public void ResetToDefaults()
        {
            for (int i = 0; i < _base.Length; i++)
            {
                _base[i] = 0f;
            }

            _base[(int)StatId.MaxHealth] = 160f;
            _base[(int)StatId.MoveSpeed] = 8f;
            _base[(int)StatId.Volume] = 1f;
            _base[(int)StatId.MeleeDamage] = 8f;
            _base[(int)StatId.DevourRatio] = 1.05f;
            _base[(int)StatId.DevourGain] = 1f;
            _base[(int)StatId.StaminaMax] = 100f;
            _base[(int)StatId.StaminaRegen] = 18f;
            _base[(int)StatId.DashCost] = 25f;
            _base[(int)StatId.DashDistance] = 4.5f;
            _base[(int)StatId.DashInvulnTime] = 0.15f;
            _base[(int)StatId.CooldownReduction] = 0f;
            _base[(int)StatId.AreaScale] = 1f;
            _base[(int)StatId.AbilityPower] = 1f;
            _base[(int)StatId.ElectricPower] = 1f;
            _base[(int)StatId.ChainBonus] = 0f;
            _base[(int)StatId.MinionCap] = 0f;
            _base[(int)StatId.MinionPower] = 1f;
            _base[(int)StatId.HealthRegen] = 0.5f;
            _base[(int)StatId.KillHeal] = 0f;
            _base[(int)StatId.DamageTaken] = 1f;
            _base[(int)StatId.AggroScale] = 1f;
            _base[(int)StatId.PickupRadius] = 2.5f;
            _base[(int)StatId.EvoGain] = 1f;
            _base[(int)StatId.NutrientGain] = 1f;
            _base[(int)StatId.AbilitySlots] = 2f;
            _base[(int)StatId.PollutionCap] = 100f;
            _base[(int)StatId.MyceliumScale] = 1f;
            _base[(int)StatId.ProjectileCount] = 0f;
            _base[(int)StatId.StatusDuration] = 1f;

            _mods.Clear();
            _dirty = true;
        }

        public void SetBase(StatId stat, float value)
        {
            _base[(int)stat] = value;
            _dirty = true;
        }

        public float GetBase(StatId stat) => _base[(int)stat];

        public void Add(in StatModifier mod)
        {
            _mods.Add(mod);
            _dirty = true;
        }

        public void AddRange(IReadOnlyList<StatModifier> mods)
        {
            if (mods == null)
            {
                return;
            }
            for (int i = 0; i < mods.Count; i++)
            {
                _mods.Add(mods[i]);
            }
            _dirty = true;
        }

        /// <summary>按来源移除。状态结束、卡牌失效时用。</summary>
        public int RemoveBySource(int sourceId)
        {
            int removed = 0;
            for (int i = _mods.Count - 1; i >= 0; i--)
            {
                if (_mods[i].SourceId != sourceId)
                {
                    continue;
                }
                _mods.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                _dirty = true;
            }
            return removed;
        }

        public void ClearModifiers()
        {
            _mods.Clear();
            _dirty = true;
        }

        public float Get(StatId stat)
        {
            if (_dirty)
            {
                Recalculate();
            }
            return _final[(int)stat];
        }

        public int GetInt(StatId stat) => Mathf.RoundToInt(Get(stat));

        /// <summary>强制标记需要重算。修正器由外部直接改动时调用。</summary>
        public void MarkDirty() => _dirty = true;

        private void Recalculate()
        {
            int n = (int)StatId.Count;

            // 三层分别累加，最后合成
            for (int i = 0; i < n; i++)
            {
                _final[i] = _base[i];
            }

            // Flat
            for (int m = 0; m < _mods.Count; m++)
            {
                if (_mods[m].Op == ModifierOp.Flat)
                {
                    _final[(int)_mods[m].Stat] += _mods[m].Value;
                }
            }

            // PctAdd：同类相加后一次性应用
            for (int i = 0; i < n; i++)
            {
                float pctAdd = 0f;
                for (int m = 0; m < _mods.Count; m++)
                {
                    if (_mods[m].Op == ModifierOp.PctAdd && (int)_mods[m].Stat == i)
                    {
                        pctAdd += _mods[m].Value;
                    }
                }
                if (pctAdd != 0f)
                {
                    _final[i] *= 1f + pctAdd;
                }
            }

            // PctMul：独立相乘
            for (int m = 0; m < _mods.Count; m++)
            {
                if (_mods[m].Op == ModifierOp.PctMul)
                {
                    _final[(int)_mods[m].Stat] *= 1f + _mods[m].Value;
                }
            }

            // 硬性夹取，防止配表失误导致负数或荒谬值
            _final[(int)StatId.MaxHealth] = Mathf.Max(1f, _final[(int)StatId.MaxHealth]);
            _final[(int)StatId.MoveSpeed] = Mathf.Max(0.5f, _final[(int)StatId.MoveSpeed]);
            _final[(int)StatId.Volume] = Mathf.Max(0.1f, _final[(int)StatId.Volume]);
            _final[(int)StatId.DevourRatio] = Mathf.Max(0.2f, _final[(int)StatId.DevourRatio]);
            _final[(int)StatId.CooldownReduction] =
                Mathf.Clamp(_final[(int)StatId.CooldownReduction], 0f, 0.8f);
            _final[(int)StatId.DamageTaken] = Mathf.Max(0.05f, _final[(int)StatId.DamageTaken]);
            _final[(int)StatId.AggroScale] = Mathf.Max(0.1f, _final[(int)StatId.AggroScale]);
            _final[(int)StatId.AbilitySlots] =
                Mathf.Clamp(_final[(int)StatId.AbilitySlots], 1f, 5f);
            _final[(int)StatId.StatusDuration] =
                Mathf.Max(0.1f, _final[(int)StatId.StatusDuration]);

            _dirty = false;
        }
    }
}
