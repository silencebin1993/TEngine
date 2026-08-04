using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Cards;

namespace GameLogic.Core
{
    /// <summary>
    /// 配置表统一门面。
    ///
    /// 存在意义：玩法代码**永不直接碰 Luban 生成的 Tables 类**，只认本层的 Spec 类型。
    /// 这样配置源可以替换（Luban / JSON / 内置默认），而调用方一行不改。
    ///
    /// 当前 Luban 的 cell.* 表尚未落地，所以先由 <see cref="CellContentSeed"/> 提供
    /// 一套内置默认内容，保证框架可运行、可测试。表落地后只需改 <see cref="LoadFromLuban"/>。
    ///
    /// 详见 DesignDocs/Game_Framework_Design.md §6。
    /// </summary>
    public sealed class DataRegistry
    {
        private static DataRegistry _instance;
        public static DataRegistry Instance => _instance ??= new DataRegistry();

        private readonly Dictionary<int, CardSpec> _cards = new Dictionary<int, CardSpec>(160);
        private readonly Dictionary<int, AbilitySpec> _abilities = new Dictionary<int, AbilitySpec>(32);
        private readonly Dictionary<int, EnemySpec> _enemies = new Dictionary<int, EnemySpec>(40);
        private readonly List<PhaseSpec> _phases = new List<PhaseSpec>(8);
        private readonly List<EcoEventSpec> _ecoEvents = new List<EcoEventSpec>(20);
        private readonly List<BehaviorArchetype> _archetypes = new List<BehaviorArchetype>(12);

        private readonly List<CardSpec> _cardList = new List<CardSpec>(160);

        public bool Loaded { get; private set; }
        /// <summary>true 表示用的是内置兜底内容，不是 Luban 表。</summary>
        public bool UsingFallback { get; private set; }

        public IReadOnlyList<CardSpec> AllCards => _cardList;
        public IReadOnlyList<PhaseSpec> Phases => _phases;
        public IReadOnlyList<EcoEventSpec> EcoEvents => _ecoEvents;
        public IReadOnlyList<BehaviorArchetype> Archetypes => _archetypes;

        public void Load()
        {
            if (Loaded)
            {
                return;
            }

            Clear();

            // Luban 表尚未落地时不应让整局起不来——回落到内置内容并明确告警。
            bool ok = false;
            try
            {
                ok = LoadFromLuban();
            }
            catch (System.Exception e)
            {
                TEngine.Log.Warning($"[DataRegistry] Luban 表读取失败，回落内置内容: {e.Message}");
                ok = false;
            }

            if (!ok)
            {
                CellContentSeed.Populate(this);
                UsingFallback = true;
                TEngine.Log.Warning("[DataRegistry] 使用内置兜底内容（cell.* 配置表未就绪）。");
            }

            Validate();
            Loaded = true;
        }

        /// <summary>
        /// 从 Luban 读取。cell.* 表落地后在此实现映射。
        /// 返回 false 表示表不存在或为空，调用方会回落内置内容。
        /// </summary>
        private bool LoadFromLuban()
        {
            // TODO(Luban): cell.Card / cell.Ability / cell.Enemy / cell.Phase / cell.EcoEvent
            //   / cell.BehaviorArchetype 表生成后，在此把 GameConfig.cell.* 映射到本层 Spec。
            //   映射代码集中在这一个方法里，是"配置源可替换"的关键。
            return false;
        }

        public void Clear()
        {
            _cards.Clear();
            _cardList.Clear();
            _abilities.Clear();
            _enemies.Clear();
            _phases.Clear();
            _ecoEvents.Clear();
            _archetypes.Clear();
            Loaded = false;
            UsingFallback = false;
        }

        // ── 注册（内容种子与 Luban 映射共用）──

        public void AddCard(CardSpec spec)
        {
            if (spec == null || _cards.ContainsKey(spec.Id))
            {
                return;
            }
            _cards[spec.Id] = spec;
            _cardList.Add(spec);
        }

        public void AddAbility(AbilitySpec spec)
        {
            if (spec != null && !_abilities.ContainsKey(spec.Id))
            {
                _abilities[spec.Id] = spec;
            }
        }

        public void AddEnemy(EnemySpec spec)
        {
            if (spec != null && !_enemies.ContainsKey(spec.Id))
            {
                _enemies[spec.Id] = spec;
            }
        }

        public void AddPhase(PhaseSpec spec)
        {
            if (spec != null)
            {
                _phases.Add(spec);
            }
        }

        public void AddEcoEvent(EcoEventSpec spec)
        {
            if (spec != null)
            {
                _ecoEvents.Add(spec);
            }
        }

        /// <summary>注册行为原型。返回其索引，敌人配置用这个索引引用它。</summary>
        public int AddArchetype(BehaviorArchetype arc)
        {
            _archetypes.Add(arc);
            return _archetypes.Count - 1;
        }

        // ── 查询 ──

        public CardSpec GetCard(int id) => _cards.TryGetValue(id, out CardSpec c) ? c : null;
        public AbilitySpec GetAbility(int id) => _abilities.TryGetValue(id, out AbilitySpec a) ? a : null;
        public EnemySpec GetEnemy(int id) => _enemies.TryGetValue(id, out EnemySpec e) ? e : null;

        public PhaseSpec GetPhase(int index)
        {
            return index >= 0 && index < _phases.Count ? _phases[index] : null;
        }

        public EcoEventSpec GetEcoEvent(int id)
        {
            for (int i = 0; i < _ecoEvents.Count; i++)
            {
                if (_ecoEvents[i].Id == id)
                {
                    return _ecoEvents[i];
                }
            }
            return null;
        }

        public BehaviorArchetype[] ArchetypeArray() => _archetypes.ToArray();

        /// <summary>
        /// 数据自检。在加载期暴露断链，而不是等运行时崩。
        /// 这是"内容表膨胀后配表易错"风险项的对策（框架文档 §10）。
        /// </summary>
        public void Validate()
        {
            int problems = 0;

            for (int i = 0; i < _cardList.Count; i++)
            {
                CardSpec c = _cardList[i];
                if (c.GrantAbilityId > 0 && !_abilities.ContainsKey(c.GrantAbilityId))
                {
                    TEngine.Log.Error($"[DataRegistry] 卡牌 {c.Id}({c.Name}) 引用了不存在的技能 {c.GrantAbilityId}");
                    problems++;
                }
                if (c.Rarity >= CardRarity.Aberrant && string.IsNullOrEmpty(c.DrawbackDesc))
                {
                    TEngine.Log.Warning($"[DataRegistry] 异化及以上卡牌 {c.Id}({c.Name}) 缺副作用描述（Spec §8.1 要求）");
                    problems++;
                }
                if (c.MaxStack < 1)
                {
                    TEngine.Log.Error($"[DataRegistry] 卡牌 {c.Id}({c.Name}) MaxStack < 1");
                    problems++;
                }
            }

            foreach (var kv in _enemies)
            {
                EnemySpec e = kv.Value;
                if (e.ArchetypeIndex < 0 || e.ArchetypeIndex >= _archetypes.Count)
                {
                    TEngine.Log.Error($"[DataRegistry] 敌人 {e.Id}({e.Name}) 行为原型索引越界: {e.ArchetypeIndex}");
                    problems++;
                }
                if (e.SpawnCost <= 0f)
                {
                    TEngine.Log.Error($"[DataRegistry] 敌人 {e.Id}({e.Name}) SpawnCost <= 0，压力预算会失效");
                    problems++;
                }
            }

            // 纯数值卡占比约束（Spec §16）
            if (_cardList.Count > 0)
            {
                int pure = 0;
                for (int i = 0; i < _cardList.Count; i++)
                {
                    if (_cardList[i].IsPureStatCard)
                    {
                        pure++;
                    }
                }
                float ratio = (float)pure / _cardList.Count;
                if (ratio > 0.15f)
                {
                    TEngine.Log.Warning(
                        $"[DataRegistry] 纯数值卡占比 {ratio:P0} 超过 15% 上限（{pure}/{_cardList.Count}）。" +
                        "设计约束要求卡牌优先改变机制而非数值。");
                }
            }

            if (problems == 0)
            {
                TEngine.Log.Info($"[DataRegistry] 校验通过：{_cardList.Count} 卡 / {_abilities.Count} 技能 / " +
                                 $"{_enemies.Count} 敌人 / {_archetypes.Count} 原型 / {_phases.Count} 时期 / " +
                                 $"{_ecoEvents.Count} 事件");
            }
        }
    }

    /// <summary>敌人定义。</summary>
    public sealed class EnemySpec
    {
        public int Id;
        public string Name;
        /// <summary>行为原型索引，指向 DataRegistry.Archetypes。</summary>
        public int ArchetypeIndex;

        public float Health = 10f;
        public float Radius = 0.5f;
        public float MaxSpeed = 3f;

        /// <summary>压力预算成本。导演按此采购敌人。</summary>
        public float SpawnCost = 1f;
        /// <summary>最早出现的生态时期序号。</summary>
        public int MinPhase;
        /// <summary>最晚出现的生态时期序号。-1 表示不限。</summary>
        public int MaxPhase = -1;

        /// <summary>被吞噬/击杀给的进化能。</summary>
        public float EvoEnergy = 1f;
        /// <summary>被吞噬/击杀给的营养质。</summary>
        public float Nutrient = 1f;
        /// <summary>被击杀给的突变质（通常只有精英 &gt; 0）。</summary>
        public float Mutagen;

        public SimStatus InitialStatus = SimStatus.None;
        public int VisualId;

        public bool IsElite;
        public bool IsBoss;
    }

    /// <summary>生态时期定义。对应 Cell_Stage_Spec.md §3。</summary>
    public sealed class PhaseSpec
    {
        public int Id;
        public string Name;
        /// <summary>时期切换文案。</summary>
        public string FlavorText;
        /// <summary>本时期时长（秒）。</summary>
        public float Duration = 480f;

        /// <summary>压力预算基数。</summary>
        public float PressureBase = 20f;
        /// <summary>压力预算下限（随时间硬性抬升，防止玩家压制 build 换低难度）。</summary>
        public float PressureFloor = 10f;

        /// <summary>本时期可用的敌人 id。</summary>
        public int[] EnemyPool;
        /// <summary>本时期可触发的生态事件 id。</summary>
        public int[] EcoEventPool;
        /// <summary>本时期结束时是否刷精英。</summary>
        public bool SpawnEliteAtEnd;
        /// <summary>精英敌人 id。</summary>
        public int EliteEnemyId;
    }

    /// <summary>生态事件定义。对应 Cell_Stage_Spec.md §10。</summary>
    public sealed class EcoEventSpec
    {
        public int Id;
        public string Name;
        public string Desc;
        public float Duration = 45f;

        /// <summary>压力预算倍率。</summary>
        public float PressureMul = 1f;
        /// <summary>玩家移速倍率。</summary>
        public float PlayerSpeedMul = 1f;
        /// <summary>敌人移速倍率。</summary>
        public float EnemySpeedMul = 1f;
        /// <summary>吞噬收益倍率。</summary>
        public float DevourGainMul = 1f;

        /// <summary>事件期间提升权重的路线（抽卡偏向）。</summary>
        public CardRoute FavoredRoute = CardRoute.None;
        /// <summary>完成奖励的资源种类与数量。</summary>
        public ResourceKind RewardKind = ResourceKind.None;
        public float RewardAmount;
        /// <summary>是否直接给一次选卡。</summary>
        public bool GrantsDraft;
        public DraftKind DraftKind = DraftKind.Normal;
    }
}
