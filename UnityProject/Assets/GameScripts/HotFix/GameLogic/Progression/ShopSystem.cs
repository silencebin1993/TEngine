using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.Stats;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>商品效果种类。窄口径实现（Preflight H2）：固定目录，不建 Luban 表。</summary>
    public enum ShopEffectKind
    {
        HealPercent,
        RandomCard,
        ClearPollution,
        GainMutagen,
    }

    /// <summary>商品定义。当前写死在 <see cref="ShopSystem.Catalog"/>，配表化留给后续内容扩充 story。</summary>
    public struct ShopItemSpec
    {
        public string Name;
        public string Desc;
        public float Cost;
        public ShopEffectKind Effect;
        public float Value;
    }

    /// <summary>
    /// 局内商店（Cell_Stage_Spec.md §5/§12.3）。营养质购买，随机刷新库存。
    ///
    /// 固定商品目录（H2）：不建 Luban 表，效果直接调用现有资源/卡组 API 落地，
    /// 不经过 EffectSpec 执行器（那套是为卡牌效果设计的，商店只有 4 种效果，
    /// 没必要为此新开一条平行的效果解析路径）。
    /// </summary>
    public sealed class ShopSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Progression;

        private static readonly ShopItemSpec[] Catalog =
        {
            new ShopItemSpec
            {
                Name = "细胞修复", Desc = "回复 20% 生命上限",
                Cost = 12f, Effect = ShopEffectKind.HealPercent, Value = 0.2f,
            },
            new ShopItemSpec
            {
                Name = "随机基因", Desc = "获得一张随机普通卡牌",
                Cost = 18f, Effect = ShopEffectKind.RandomCard, Value = 0f,
            },
            new ShopItemSpec
            {
                Name = "净化脉冲", Desc = "清空污染度 10 点",
                Cost = 8f, Effect = ShopEffectKind.ClearPollution, Value = 10f,
            },
            new ShopItemSpec
            {
                Name = "突变浓缩", Desc = "获得 5 点突变质",
                Cost = 20f, Effect = ShopEffectKind.GainMutagen, Value = 5f,
            },
        };

        public const int SlotCount = 3;
        public const float RefreshCost = 8f;

        private ResourceWallet _wallet;
        private StatSheet _stats;
        private Deck _deck;
        private SimBridge _sim;

        private readonly ShopItemSpec[] _stock = new ShopItemSpec[SlotCount];
        private readonly bool[] _soldOut = new bool[SlotCount];

        public void Bind(ResourceWallet wallet, StatSheet stats, Deck deck, SimBridge sim)
        {
            _wallet = wallet;
            _stats = stats;
            _deck = deck;
            _sim = sim;
        }

        public override void OnEnter()
        {
            RollStock();
        }

        public ShopItemSpec GetSlot(int index) => _stock[index];

        public bool IsSoldOut(int index) => _soldOut[index];

        /// <summary>随机重抽全部库存，不消耗资源（局内进店时自动调用一次）。</summary>
        public void RollStock()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                _stock[i] = Catalog[Random.Range(0, Catalog.Length)];
                _soldOut[i] = false;
            }
        }

        /// <summary>付费刷新。库存变化正确性：不足则不扣款、不重抽。</summary>
        public bool TryRefresh()
        {
            if (!_wallet.TrySpend(ResourceKind.Nutrient, RefreshCost))
            {
                return false;
            }
            RollStock();
            return true;
        }

        /// <summary>购买指定槽位。资源不足或已售出则不扣款、不生效。</summary>
        public bool TryBuy(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount || _soldOut[slotIndex])
            {
                return false;
            }

            ShopItemSpec item = _stock[slotIndex];
            if (!_wallet.TrySpend(ResourceKind.Nutrient, item.Cost))
            {
                return false;
            }

            Apply(item);
            _soldOut[slotIndex] = true;
            TEngine.Log.Info($"[ShopSystem] 购买「{item.Name}」，花费营养质 {item.Cost:F0}");
            return true;
        }

        private void Apply(ShopItemSpec item)
        {
            switch (item.Effect)
            {
                case ShopEffectKind.HealPercent:
                {
                    float maxHp = _stats.Get(StatId.MaxHealth);
                    _sim.HealPlayer(maxHp * item.Value, maxHp);
                    break;
                }
                case ShopEffectKind.RandomCard:
                    GrantRandomCard();
                    break;
                case ShopEffectKind.ClearPollution:
                    _wallet.Add(ResourceKind.Pollution, -item.Value);
                    break;
                case ShopEffectKind.GainMutagen:
                    _wallet.Add(ResourceKind.Mutagen, item.Value);
                    break;
            }
        }

        /// <summary>抽一张可叠加的普通卡。卡池耗尽（找不到可叠加的）时退化为突变质补偿。</summary>
        private void GrantRandomCard()
        {
            var all = DataRegistry.Instance.AllCards;
            for (int attempt = 0; attempt < 10 && all.Count > 0; attempt++)
            {
                CardSpec spec = all[Random.Range(0, all.Count)];
                if (spec.Rarity != CardRarity.Common || !_deck.CanAcquire(spec))
                {
                    continue;
                }
                int stack = _deck.Acquire(spec);
                if (stack > 0)
                {
                    Signals.Publish(new CardAcquiredSignal { CardId = spec.Id, NewStack = stack });
                    return;
                }
            }

            // 没抽到可叠加的普通卡：退化为等值突变质，避免花了钱却什么都没拿到
            _wallet.Add(ResourceKind.Mutagen, 3f);
        }
    }
}
