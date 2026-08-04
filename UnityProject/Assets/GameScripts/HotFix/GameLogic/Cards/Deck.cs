using System.Collections.Generic;

namespace GameLogic.Cards
{
    /// <summary>已获得的一张卡及其层数。</summary>
    public sealed class DeckEntry
    {
        public CardSpec Spec;
        public int Stack;
        /// <summary>OnTick 卡的计时器。</summary>
        public float TickTimer;
        /// <summary>触发内置冷却剩余。</summary>
        public float TriggerCooldownLeft;

        public DeckEntry(CardSpec spec)
        {
            Spec = spec;
            Stack = 1;
        }
    }

    /// <summary>
    /// 本局卡组。只记录"拿了什么、几层"，不含触发逻辑（那在 CardTriggerBus）。
    /// </summary>
    public sealed class Deck
    {
        private readonly List<DeckEntry> _entries = new List<DeckEntry>(32);
        private readonly Dictionary<int, DeckEntry> _byId = new Dictionary<int, DeckEntry>(32);
        private readonly int[] _routeCount = new int[8];

        public IReadOnlyList<DeckEntry> Entries => _entries;

        /// <summary>卡牌总数（含叠层）。</summary>
        public int TotalCards { get; private set; }

        /// <summary>不同卡牌种类数。</summary>
        public int UniqueCards => _entries.Count;

        public DeckEntry Find(int cardId)
        {
            return _byId.TryGetValue(cardId, out DeckEntry e) ? e : null;
        }

        public int StackOf(int cardId)
        {
            DeckEntry e = Find(cardId);
            return e?.Stack ?? 0;
        }

        public bool CanAcquire(CardSpec spec)
        {
            if (spec == null)
            {
                return false;
            }
            DeckEntry e = Find(spec.Id);
            return e == null || e.Stack < spec.MaxStack;
        }

        /// <summary>加入卡牌或叠层。返回新层数，失败返回 0。</summary>
        public int Acquire(CardSpec spec)
        {
            if (!CanAcquire(spec))
            {
                return 0;
            }

            DeckEntry e = Find(spec.Id);
            if (e == null)
            {
                e = new DeckEntry(spec);
                _entries.Add(e);
                _byId[spec.Id] = e;
                if ((int)spec.Route < _routeCount.Length)
                {
                    _routeCount[(int)spec.Route]++;
                }
            }
            else
            {
                e.Stack++;
            }

            TotalCards++;
            return e.Stack;
        }

        /// <summary>某路线的卡牌种类数。抽卡权重的路线亲和用它。</summary>
        public int RouteCount(CardRoute route)
        {
            int i = (int)route;
            return i >= 0 && i < _routeCount.Length ? _routeCount[i] : 0;
        }

        /// <summary>主导路线。用于结算展示与 StageOutcome。</summary>
        public CardRoute DominantRoute()
        {
            int best = 0;
            CardRoute bestRoute = CardRoute.None;
            // 从 1 起跳过 None
            for (int i = 1; i < _routeCount.Length; i++)
            {
                // Hybrid 不算独立路线，它服务于其它路线
                if (i == (int)CardRoute.Hybrid)
                {
                    continue;
                }
                if (_routeCount[i] > best)
                {
                    best = _routeCount[i];
                    bestRoute = (CardRoute)i;
                }
            }
            return bestRoute;
        }

        /// <summary>路线分布快照。HUD 画路线分布图用。</summary>
        public void CopyRouteCounts(int[] dst)
        {
            if (dst == null)
            {
                return;
            }
            int n = System.Math.Min(dst.Length, _routeCount.Length);
            for (int i = 0; i < n; i++)
            {
                dst[i] = _routeCount[i];
            }
        }

        /// <summary>定义性卡牌（史诗及以上）。StageOutcome 的 KeyCards 用它。</summary>
        public List<CardSpec> KeyCards()
        {
            var list = new List<CardSpec>(6);
            for (int i = 0; i < _entries.Count; i++)
            {
                CardSpec s = _entries[i].Spec;
                if (s != null && s.Rarity >= CardRarity.Epic)
                {
                    list.Add(s);
                }
            }
            return list;
        }

        public void Clear()
        {
            _entries.Clear();
            _byId.Clear();
            for (int i = 0; i < _routeCount.Length; i++)
            {
                _routeCount[i] = 0;
            }
            TotalCards = 0;
        }
    }
}
