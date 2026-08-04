using System.Collections.Generic;

namespace GameLogic.Ability
{
    /// <summary>技能瞄准方式。</summary>
    public enum TargetMode
    {
        /// <summary>无需瞄准，以自身为中心。</summary>
        Self = 0,
        /// <summary>朝移动方向。</summary>
        MoveDirection = 1,
        /// <summary>朝鼠标方向。</summary>
        Cursor = 2,
        /// <summary>自动锁定最近敌人。</summary>
        NearestEnemy = 3,
        /// <summary>自动锁定已标记敌人，无标记则退化为最近。</summary>
        MarkedEnemy = 4,
    }

    /// <summary>
    /// 主动技能定义。纯数据。
    /// 对应 Cell_Stage_Spec.md §11 的 28 个主动技能。
    /// 新增技能 = 加一行 cell.Ability 配置 + 若干行 cell.AbilityEffect。
    /// </summary>
    public sealed class AbilitySpec
    {
        public int Id;
        public string Name;
        public string Desc;

        /// <summary>冷却（秒）。实际冷却受 CooldownReduction 影响。</summary>
        public float Cooldown = 1f;
        /// <summary>充能数。&gt;1 表示可存多次施放。</summary>
        public int Charges = 1;
        /// <summary>体力消耗。</summary>
        public float StaminaCost;
        /// <summary>施放距离。0 表示不限。</summary>
        public float CastRange;

        public TargetMode TargetMode = TargetMode.Self;

        /// <summary>有序效果列表。</summary>
        public List<EffectSpec> Effects = new List<EffectSpec>(2);

        /// <summary>
        /// 标签。卡牌用它做"强化所有电系技能"这类批量修正，
        /// 避免每张卡枚举技能 id。
        /// </summary>
        public string[] Tags;

        /// <summary>是否是初始技能（冲刺）。</summary>
        public bool IsStarter;

        public bool HasTag(string tag)
        {
            if (Tags == null || string.IsNullOrEmpty(tag))
            {
                return false;
            }
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Tags[i] == tag)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>技能标签常量。集中定义避免拼写错误。</summary>
    public static class AbilityTag
    {
        public const string Electric = "electric";
        public const string Devour = "devour";
        public const string Mobility = "mobility";
        public const string Spore = "spore";
        public const string Nest = "nest";
        public const string Corrupt = "corrupt";
        public const string Survival = "survival";
        public const string Area = "area";
        public const string Projectile = "projectile";
        public const string Summon = "summon";
        public const string Ultimate = "ultimate";
    }

    /// <summary>
    /// 技能运行时状态。冷却与充能。
    /// 与 AbilitySpec 分开：Spec 是共享的只读数据，Runtime 是每局每槽位的可变状态。
    /// </summary>
    public sealed class AbilityRuntime
    {
        public AbilitySpec Spec;
        /// <summary>当前可用充能数。</summary>
        public int ChargesLeft;
        /// <summary>距下一次充能恢复的剩余时间。</summary>
        public float CooldownLeft;
        /// <summary>本局施放次数，用于统计与"施放 N 次后强化"类卡牌。</summary>
        public int CastCount;

        public AbilityRuntime(AbilitySpec spec)
        {
            Spec = spec;
            ChargesLeft = spec != null ? spec.Charges : 1;
            CooldownLeft = 0f;
        }

        public bool Ready => ChargesLeft > 0;

        /// <summary>冷却进度 0-1，供 UI 画环形冷却。</summary>
        public float CooldownProgress(float effectiveCooldown)
        {
            if (ChargesLeft > 0 || effectiveCooldown <= 0f)
            {
                return 1f;
            }
            return 1f - UnityEngine.Mathf.Clamp01(CooldownLeft / effectiveCooldown);
        }

        public void Tick(float dt, float effectiveCooldown, int maxCharges)
        {
            if (ChargesLeft >= maxCharges)
            {
                CooldownLeft = 0f;
                return;
            }

            CooldownLeft -= dt;
            if (CooldownLeft > 0f)
            {
                return;
            }

            ChargesLeft++;
            // 溢出时间结转到下一次充能，避免高冷却缩减下的取整损失
            CooldownLeft = ChargesLeft < maxCharges
                ? effectiveCooldown + CooldownLeft
                : 0f;
        }

        public void Consume(float effectiveCooldown)
        {
            ChargesLeft = UnityEngine.Mathf.Max(0, ChargesLeft - 1);
            CastCount++;
            if (CooldownLeft <= 0f)
            {
                CooldownLeft = effectiveCooldown;
            }
        }

        public void Reset()
        {
            ChargesLeft = Spec != null ? Spec.Charges : 1;
            CooldownLeft = 0f;
            CastCount = 0;
        }
    }
}
