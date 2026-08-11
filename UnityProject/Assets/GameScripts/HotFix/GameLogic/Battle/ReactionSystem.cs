using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Core;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Battle
{
    /// <summary>
    /// 反应矩阵核心：状态碰撞的化学反应判定（Cell_Stage_Spec.md §17.1）。
    ///
    /// 判定时机是"状态施加的那一刻"（由 <see cref="StatusSystem"/> 的
    /// ApplyTimed/ApplyTimedArea 与 <c>EffectDealDamage</c> 的晶化特例挂钩调用），
    /// 不是逐帧扫描——<see cref="OnUpdate"/> 只做冷却/层数表的倒计时与失效清理，
    /// 不遍历敌人。
    /// </summary>
    public sealed class ReactionSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Reaction;

        private struct CooldownEntry
        {
            public int UnitIndex;
            public int RuleId;
            public float TimeLeft;
        }

        /// <summary>晶化层数计数。只服务"3 层×任意命中"这一条规则，见 ReactionRuleSpec 注释。</summary>
        private struct CrystalEntry
        {
            public int UnitIndex;
            public int LogicId;
            public int Layers;
        }

        private const int CrystallizeLayerThreshold = 3;
        /// <summary>反应二级效果的 SourceId，避开卡牌/技能 id 区间（均为正整数，见 Decision E1）。</summary>
        private const int ReactionSourceId = -900;

        private readonly List<CooldownEntry> _cooldowns = new List<CooldownEntry>(64);
        private readonly List<CrystalEntry> _crystals = new List<CrystalEntry>(32);
        private readonly Dictionary<ulong, ReactionRuleSpec> _byPair = new Dictionary<ulong, ReactionRuleSpec>(16);

        private ReactionRuleSpec _crystallizeRule;
        private SimBridge _sim;
        private StatSheet _stats;

        public void Bind(SimBridge sim, StatSheet stats)
        {
            _sim = sim;
            _stats = stats;
        }

        public override void OnEnter()
        {
            _cooldowns.Clear();
            _crystals.Clear();
            LoadRules();
        }

        public override void OnExit()
        {
            _cooldowns.Clear();
            _crystals.Clear();
        }

        private void LoadRules()
        {
            _byPair.Clear();
            _crystallizeRule = null;

            IReadOnlyList<ReactionRuleSpec> rules = DataRegistry.Instance.ReactionRules;
            for (int i = 0; i < rules.Count; i++)
            {
                ReactionRuleSpec r = rules[i];
                if (r.StatusA == SimStatus.None)
                {
                    TEngine.Log.Warning($"[ReactionSystem] 规则 {r.Id}({r.Name}) StatusA 为 None，已跳过。");
                    continue;
                }

                if (r.StatusB == SimStatus.None)
                {
                    if (_crystallizeRule != null)
                    {
                        TEngine.Log.Warning(
                            $"[ReactionSystem] 多条规则的 StatusB=None（{_crystallizeRule.Id} 与 {r.Id}），" +
                            "只有第一条会作为晶化特例生效。");
                        continue;
                    }
                    _crystallizeRule = r;
                    continue;
                }

                ulong key = PairKey((uint)r.StatusA, (uint)r.StatusB);
                if (_byPair.ContainsKey(key))
                {
                    TEngine.Log.Warning($"[ReactionSystem] 规则 {r.Id}({r.Name}) 的状态对与已注册规则重复，已覆盖。");
                }
                _byPair[key] = r;
            }
        }

        private static ulong PairKey(uint a, uint b)
        {
            return a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
        }

        /// <summary>
        /// 状态施加那一刻的反应判定。由 StatusSystem 在提交内核前调用（Decision H1）。
        /// oldMask 是施加前的状态位，newStatus 是本次将要施加的状态（可能是多位掩码）。
        /// </summary>
        public void TryReact(int unitIndex, SimStatus oldMask, SimStatus newStatus)
        {
            if (_sim == null || !_sim.Running || unitIndex < 0
                || oldMask == SimStatus.None || newStatus == SimStatus.None)
            {
                return;
            }

            uint newBits = (uint)newStatus & ~(uint)oldMask;
            if (newBits == 0u)
            {
                return;
            }

            uint oldBits = (uint)oldMask;
            for (int nb = 0; nb < 32; nb++)
            {
                uint newBit = 1u << nb;
                if ((newBits & newBit) == 0u)
                {
                    continue;
                }
                for (int ob = 0; ob < 32; ob++)
                {
                    uint oldBit = 1u << ob;
                    if ((oldBits & oldBit) == 0u || oldBit == newBit)
                    {
                        continue;
                    }
                    if (_byPair.TryGetValue(PairKey(oldBit, newBit), out ReactionRuleSpec rule))
                    {
                        Trigger(unitIndex, rule, (SimStatus)oldBit, (SimStatus)newBit);
                    }
                }
            }
        }

        /// <summary>
        /// 晶化特例挂钩：由 EffectDealDamage 在目标已带晶化状态时的 Target 命中里直接调用
        /// （Decision H2，零额外查找成本）。累积 3 层后触发一次并清零。
        /// </summary>
        public void OnTargetHit(int unitIndex, int logicId)
        {
            if (_sim == null || !_sim.Running || unitIndex < 0 || _crystallizeRule == null)
            {
                return;
            }

            for (int i = 0; i < _crystals.Count; i++)
            {
                if (_crystals[i].UnitIndex != unitIndex)
                {
                    continue;
                }
                if (_crystals[i].LogicId != logicId)
                {
                    // 槽位被复用（旧单位死亡后 index 复用给新单位），丢弃旧条目重新计数
                    _crystals.RemoveAt(i);
                    break;
                }

                CrystalEntry e = _crystals[i];
                e.Layers++;
                if (e.Layers >= CrystallizeLayerThreshold)
                {
                    _crystals.RemoveAt(i);
                    Trigger(unitIndex, _crystallizeRule, SimStatus.Crystallized, SimStatus.None);
                }
                else
                {
                    _crystals[i] = e;
                }
                return;
            }

            _crystals.Add(new CrystalEntry { UnitIndex = unitIndex, LogicId = logicId, Layers = 1 });
        }

        private void Trigger(int unitIndex, ReactionRuleSpec rule, SimStatus matchedA, SimStatus matchedB)
        {
            if (OnCooldown(unitIndex, rule.Id))
            {
                return;
            }

            SimSnapshot snap = _sim.Snapshot;
            if (unitIndex >= snap.Count || snap.Alive[unitIndex] == 0)
            {
                return;
            }

            var ctx = new EffectContext
            {
                Hub = Hub,
                Sim = _sim,
                Stats = _stats,
                Origin = snap.Position[unitIndex],
                Direction = float2.zero,
                TargetIndex = unitIndex,
                SourceId = ReactionSourceId,
                Stack = 1,
                TriggerMagnitude = 0f,
            };

            Hub.Get<AbilitySystem>()?.RunEffect(rule.ResultEffect, in ctx);

            // matchedA/matchedB 是实际命中的两个位；consumeA/consumeB 按规则声明顺序对应，
            // 命中顺序可能和声明顺序相反，这里对齐回去再决定清哪个位。
            bool aIsMatchedA = matchedA == rule.StatusA;
            SimStatus removeForA = aIsMatchedA ? matchedA : matchedB;
            SimStatus removeForB = aIsMatchedA ? matchedB : matchedA;

            if (rule.ConsumeA && removeForA != SimStatus.None)
            {
                _sim.ApplyStatusUnit(unitIndex, removeForA, false);
            }
            if (rule.ConsumeB && removeForB != SimStatus.None)
            {
                _sim.ApplyStatusUnit(unitIndex, removeForB, false);
            }

            RegisterCooldown(unitIndex, rule.Id, rule.Cooldown);

            int logicId = unitIndex < snap.Count ? snap.LogicId[unitIndex] : 0;
            Signals.Publish(new ReactionSignal
            {
                TargetId = logicId,
                ReactionId = rule.Id,
                StatusA = matchedA,
                StatusB = matchedB,
            });
            TEngine.Log.Info(
                $"[ReactionSystem] 反应触发 id={rule.Id} name={rule.Name} unit={unitIndex} " +
                $"({matchedA} × {matchedB})");
        }

        private bool OnCooldown(int unitIndex, int ruleId)
        {
            for (int i = 0; i < _cooldowns.Count; i++)
            {
                if (_cooldowns[i].UnitIndex == unitIndex && _cooldowns[i].RuleId == ruleId)
                {
                    return _cooldowns[i].TimeLeft > 0f;
                }
            }
            return false;
        }

        private void RegisterCooldown(int unitIndex, int ruleId, float cooldown)
        {
            if (cooldown <= 0f)
            {
                return;
            }
            for (int i = 0; i < _cooldowns.Count; i++)
            {
                if (_cooldowns[i].UnitIndex != unitIndex || _cooldowns[i].RuleId != ruleId)
                {
                    continue;
                }
                CooldownEntry e = _cooldowns[i];
                e.TimeLeft = cooldown;
                _cooldowns[i] = e;
                return;
            }
            _cooldowns.Add(new CooldownEntry { UnitIndex = unitIndex, RuleId = ruleId, TimeLeft = cooldown });
        }

        /// <summary>
        /// 只做冷却表/晶化层数表的倒计时与失效清理——不遍历敌人（架构红线，见 story Forbidden 段）。
        /// </summary>
        public override void OnUpdate(float dt)
        {
            for (int i = _cooldowns.Count - 1; i >= 0; i--)
            {
                CooldownEntry e = _cooldowns[i];
                e.TimeLeft -= dt;
                if (e.TimeLeft <= 0f)
                {
                    _cooldowns.RemoveAt(i);
                }
                else
                {
                    _cooldowns[i] = e;
                }
            }

            if (_sim == null || !_sim.Running || _crystals.Count == 0)
            {
                return;
            }

            SimSnapshot snap = _sim.Snapshot;
            for (int i = _crystals.Count - 1; i >= 0; i--)
            {
                CrystalEntry e = _crystals[i];
                if (e.UnitIndex < 0 || e.UnitIndex >= snap.Count
                    || snap.Alive[e.UnitIndex] == 0
                    || snap.LogicId[e.UnitIndex] != e.LogicId)
                {
                    _crystals.RemoveAt(i);
                }
            }
        }
    }
}
