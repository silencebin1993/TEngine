using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Stats;
using UnityEngine;

namespace GameLogic.Cards
{
    /// <summary>
    /// 抽卡服务。实现 Cell_Stage_Spec.md §8.5 的权重公式与保底规则。
    ///
    /// weight = baseWeight(rarity) * phaseGate * routeAffinity
    ///        * synergyBonus * antiDupePenalty * pityBonus
    ///
    /// 设计意图：路线亲和让 build 能成型（上限 1.8 倍避免锁死），
    /// 保底避免连续吃普通卡，低血保底避免死亡螺旋。
    /// </summary>
    public sealed class DraftService
    {
        private readonly List<CardSpec> _pool = new List<CardSpec>(160);
        private readonly List<float> _weights = new List<float>(160);
        private readonly List<CardSpec> _result = new List<CardSpec>(4);

        private Deck _deck;
        private StatSheet _stats;

        /// <summary>连续未出稀有及以上的次数。</summary>
        private int _pityCounter;

        /// <summary>连续 4 次未出稀有，第 5 次强制稀有。</summary>
        private const int PityThreshold = 4;

        public void Bind(Deck deck, StatSheet stats)
        {
            _deck = deck;
            _stats = stats;
        }

        public void Reset()
        {
            _pityCounter = 0;
        }

        /// <summary>选项数量。对应 Spec §6.2。</summary>
        public static int OptionCount(DraftKind kind)
        {
            switch (kind)
            {
                case DraftKind.Elite: return 4;
                case DraftKind.Repair: return 2;
                default: return 3;
            }
        }

        /// <summary>
        /// 生成一次选卡的选项。
        /// </summary>
        /// <param name="kind">选卡类型。</param>
        /// <param name="currentPhase">当前生态时期序号，控制卡池解锁。</param>
        /// <param name="healthPercent">当前生命百分比，用于低血保底。</param>
        public List<CardSpec> Roll(DraftKind kind, int currentPhase, float healthPercent)
        {
            _result.Clear();
            int want = OptionCount(kind);

            bool forceRare = _pityCounter >= PityThreshold || kind == DraftKind.Elite;
            bool needSurvival = healthPercent <= 0.3f;

            // 低血保底：先塞一张生存向卡（Spec §8.5）
            if (needSurvival && kind != DraftKind.Legacy)
            {
                CardSpec surv = PickOne(currentPhase, kind, requireSurvival: true, requireRare: false);
                if (surv != null)
                {
                    _result.Add(surv);
                }
            }

            // 精英/保底：至少一张稀有及以上
            if (forceRare && _result.Count < want)
            {
                CardSpec rare = PickOne(currentPhase, kind, requireSurvival: false, requireRare: true);
                if (rare != null)
                {
                    _result.Add(rare);
                }
            }

            while (_result.Count < want)
            {
                CardSpec c = PickOne(currentPhase, kind, requireSurvival: false, requireRare: false);
                if (c == null)
                {
                    break;
                }
                _result.Add(c);
            }

            // 更新保底计数
            bool gotRare = false;
            for (int i = 0; i < _result.Count; i++)
            {
                if (_result[i].Rarity >= CardRarity.Rare)
                {
                    gotRare = true;
                    break;
                }
            }
            _pityCounter = gotRare ? 0 : _pityCounter + 1;

            return _result;
        }

        private CardSpec PickOne(int currentPhase, DraftKind kind,
            bool requireSurvival, bool requireRare)
        {
            BuildPool(currentPhase, kind, requireSurvival, requireRare);
            if (_pool.Count == 0)
            {
                return null;
            }

            float total = 0f;
            for (int i = 0; i < _weights.Count; i++)
            {
                total += _weights[i];
            }
            if (total <= 0f)
            {
                return _pool[Random.Range(0, _pool.Count)];
            }

            float r = Random.value * total;
            for (int i = 0; i < _pool.Count; i++)
            {
                r -= _weights[i];
                if (r <= 0f)
                {
                    return _pool[i];
                }
            }
            return _pool[_pool.Count - 1];
        }

        private void BuildPool(int currentPhase, DraftKind kind,
            bool requireSurvival, bool requireRare)
        {
            _pool.Clear();
            _weights.Clear();

            IReadOnlyList<CardSpec> all = DataRegistry.Instance.AllCards;
            for (int i = 0; i < all.Count; i++)
            {
                CardSpec c = all[i];

                // 时期门槛
                if (c.UnlockPhase > currentPhase)
                {
                    continue;
                }
                // 已达叠层上限
                if (_deck != null && !_deck.CanAcquire(c))
                {
                    continue;
                }
                // 已在本次结果里
                if (_result.Contains(c))
                {
                    continue;
                }
                if (requireRare && c.Rarity < CardRarity.Rare)
                {
                    continue;
                }
                if (requireSurvival && !IsSurvivalCard(c))
                {
                    continue;
                }

                // 遗产卡只在遗产选择里出
                if (c.Rarity == CardRarity.Legacy && kind != DraftKind.Legacy)
                {
                    continue;
                }
                if (kind == DraftKind.Legacy && c.Rarity != CardRarity.Legacy)
                {
                    continue;
                }
                // 污染选择偏向异化卡
                if (kind == DraftKind.Corrupt && c.Rarity < CardRarity.Rare)
                {
                    continue;
                }

                _pool.Add(c);
                _weights.Add(Weight(c, kind));
            }
        }

        private float Weight(CardSpec c, DraftKind kind)
        {
            float w = BaseWeight(c.Rarity);

            // 路线亲和：已有该路线卡越多，越容易再出同路线（上限 1.8）
            if (_deck != null && c.Route != CardRoute.None)
            {
                int owned = _deck.RouteCount(c.Route);
                w *= Mathf.Min(1.8f, 1f + owned * 0.13f);
            }

            // 联动加成：与已有卡的 SynergyTags 有交集则加权（上限 1.5）
            w *= SynergyBonus(c);

            // 污染选择里异化卡额外加权
            if (kind == DraftKind.Corrupt && c.Rarity == CardRarity.Aberrant)
            {
                w *= 2.2f;
            }

            return Mathf.Max(0.01f, w);
        }

        private static float BaseWeight(CardRarity rarity)
        {
            // 对应 Spec §8.1 的占比目标：40/33/17/8/2
            switch (rarity)
            {
                case CardRarity.Common: return 40f;
                case CardRarity.Rare: return 33f;
                case CardRarity.Epic: return 17f;
                case CardRarity.Aberrant: return 8f;
                case CardRarity.Legacy: return 2f;
                default: return 1f;
            }
        }

        private float SynergyBonus(CardSpec c)
        {
            if (_deck == null || c.SynergyTags == null || c.SynergyTags.Length == 0)
            {
                return 1f;
            }

            int matches = 0;
            IReadOnlyList<DeckEntry> owned = _deck.Entries;
            for (int i = 0; i < owned.Count; i++)
            {
                CardSpec o = owned[i].Spec;
                if (o?.SynergyTags == null)
                {
                    continue;
                }
                for (int t = 0; t < c.SynergyTags.Length; t++)
                {
                    if (o.HasSynergyTag(c.SynergyTags[t]))
                    {
                        matches++;
                        break;
                    }
                }
            }

            return Mathf.Min(1.5f, 1f + matches * 0.07f);
        }

        /// <summary>
        /// 生存向判定。用于低血保底。
        /// 靠 SynergyTag 与属性修正判断，而不是给卡加一个"isSurvival"字段——
        /// 少一个字段就少一处配表出错的机会。
        /// </summary>
        private static bool IsSurvivalCard(CardSpec c)
        {
            if (c.HasSynergyTag(SynergyTag.Survival))
            {
                return true;
            }
            if (c.StatMods == null)
            {
                return false;
            }
            for (int i = 0; i < c.StatMods.Count; i++)
            {
                StatId s = c.StatMods[i].Stat;
                if (s == StatId.MaxHealth || s == StatId.HealthRegen
                    || s == StatId.KillHeal || s == StatId.DamageTaken)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
