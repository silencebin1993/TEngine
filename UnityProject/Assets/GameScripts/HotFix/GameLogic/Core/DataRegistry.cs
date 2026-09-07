using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
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
        private readonly Dictionary<int, List<BossPhaseSpec>> _bossPhases = new Dictionary<int, List<BossPhaseSpec>>(4);
        private readonly Dictionary<string, OrganModuleParamsSpec> _organModuleParams =
            new Dictionary<string, OrganModuleParamsSpec>(16);

        private readonly Dictionary<string, GeneModuleParamsSpec> _geneModuleParams =
            new Dictionary<string, GeneModuleParamsSpec>(48);

        /// <summary>查无此行时返回的空参数（全 0 = ComposeEngine 各模块的构造默认值）。</summary>
        private static readonly OrganModuleParamsSpec EmptyOrganModuleParams = new OrganModuleParamsSpec();

        /// <summary>同上，基因侧的空参数（story-007）。</summary>
        private static readonly GeneModuleParamsSpec EmptyGeneModuleParams = new GeneModuleParamsSpec();

        private readonly Dictionary<string, StructuralEffectParamsSpec> _structuralEffectParams =
            new Dictionary<string, StructuralEffectParamsSpec>(16);

        private readonly Dictionary<string, StructuralTriggerHookParamsSpec> _structuralTriggerHookParams =
            new Dictionary<string, StructuralTriggerHookParamsSpec>(32);

        /// <summary>同上，结构器官常驻被动侧的空参数（story-008，全 0 = 不挂任何 StatModifier）。</summary>
        private static readonly StructuralEffectParamsSpec EmptyStructuralEffectParams = new StructuralEffectParamsSpec();

        /// <summary>同上，结构器官触发钩子侧的空参数（story-008，全 0 = TriggerHookSpec 各字段默认值）。</summary>
        private static readonly StructuralTriggerHookParamsSpec EmptyStructuralTriggerHookParams =
            new StructuralTriggerHookParamsSpec();

        private readonly List<CardSpec> _cardList = new List<CardSpec>(160);
        private readonly List<AbilitySpec> _abilityList = new List<AbilitySpec>(32);
        private readonly List<EnemySpec> _enemyList = new List<EnemySpec>(40);

        public bool Loaded { get; private set; }
        /// <summary>true 表示用的是内置兜底内容，不是 Luban 表。</summary>
        public bool UsingFallback { get; private set; }

        public IReadOnlyList<CardSpec> AllCards => _cardList;
        public IReadOnlyList<AbilitySpec> AllAbilities => _abilityList;
        /// <summary>story-002 D1：图鉴数据源出口，全量敌人（含未采购/未出现过的）。</summary>
        public IReadOnlyList<EnemySpec> AllEnemies => _enemyList;
        public IReadOnlyList<PhaseSpec> Phases => _phases;
        public IReadOnlyList<EcoEventSpec> EcoEvents => _ecoEvents;
        public IReadOnlyList<BehaviorArchetype> Archetypes => _archetypes;
        public GlobalSpec Global { get; private set; } = new GlobalSpec();
        /// <summary>gene-organ-universal-reaction story-006：攻击器官 CreateModule 的构造参数表出口。</summary>
        public IReadOnlyDictionary<string, OrganModuleParamsSpec> OrganModuleParams => _organModuleParams;
        /// <summary>gene-organ-universal-reaction story-007：基因 CreateModule 的构造参数表出口。</summary>
        public IReadOnlyDictionary<string, GeneModuleParamsSpec> GeneModuleParams => _geneModuleParams;
        /// <summary>gene-organ-universal-reaction story-008：结构器官常驻被动（StatModifier 数值）表出口。</summary>
        public IReadOnlyDictionary<string, StructuralEffectParamsSpec> StructuralEffectParams => _structuralEffectParams;
        /// <summary>gene-organ-universal-reaction story-008：结构器官触发钩子数值表出口。</summary>
        public IReadOnlyDictionary<string, StructuralTriggerHookParamsSpec> StructuralTriggerHookParams
            => _structuralTriggerHookParams;

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

            // story-006：器官模块参数缺表会让弹速/角度等静默变 0（攻击直接失效），
            // 所以不管走哪条路径，空了就补内置兜底，不允许带着空表继续。
            if (_organModuleParams.Count == 0)
            {
                CellContentSeed.SeedOrganModuleParams(this);
                TEngine.Log.Warning("[DataRegistry] OrganModuleParams 表为空，已回落内置器官模块参数。");
            }

            // story-007：同理，基因模块参数缺表会让追踪强度/留坑秒数等静默变 0（改装静默失效）。
            if (_geneModuleParams.Count == 0)
            {
                CellContentSeed.SeedGeneModuleParams(this);
                TEngine.Log.Warning("[DataRegistry] GeneModuleParams 表为空，已回落内置基因模块参数。");
            }

            // story-008：同理，结构器官参数缺表会让减伤/反伤/残留秒数等静默变 0（装了没效果）。
            if (_structuralEffectParams.Count == 0)
            {
                CellContentSeed.SeedStructuralEffectParams(this);
                TEngine.Log.Warning("[DataRegistry] StructuralEffectParams 表为空，已回落内置结构器官被动参数。");
            }

            if (_structuralTriggerHookParams.Count == 0)
            {
                CellContentSeed.SeedStructuralTriggerHookParams(this);
                TEngine.Log.Warning("[DataRegistry] StructuralTriggerHookParams 表为空，已回落内置结构器官触发参数。");
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
            return CellLubanLoader.TryLoad(this);
        }

        public void Clear()
        {
            _cards.Clear();
            _cardList.Clear();
            _abilities.Clear();
            _abilityList.Clear();
            _enemies.Clear();
            _enemyList.Clear();
            _phases.Clear();
            _ecoEvents.Clear();
            _archetypes.Clear();
            _bossPhases.Clear();
            _organModuleParams.Clear();
            _geneModuleParams.Clear();
            _structuralEffectParams.Clear();
            _structuralTriggerHookParams.Clear();
            Global = new GlobalSpec();
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
            if (spec == null || _abilities.ContainsKey(spec.Id))
            {
                return;
            }
            _abilities[spec.Id] = spec;
            _abilityList.Add(spec);
        }

        public void AddEnemy(EnemySpec spec)
        {
            if (spec != null && !_enemies.ContainsKey(spec.Id))
            {
                _enemies[spec.Id] = spec;
                _enemyList.Add(spec);
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

        public void SetGlobal(GlobalSpec spec)
        {
            if (spec != null)
            {
                Global = spec;
            }
        }

        /// <summary>注册器官模块构造参数（story-006）。同 id 先到先得。</summary>
        public void AddOrganModuleParams(OrganModuleParamsSpec spec)
        {
            if (spec != null && !string.IsNullOrEmpty(spec.Id) && !_organModuleParams.ContainsKey(spec.Id))
            {
                _organModuleParams[spec.Id] = spec;
            }
        }

        /// <summary>注册基因模块构造参数（story-007）。同 id 先到先得。</summary>
        public void AddGeneModuleParams(GeneModuleParamsSpec spec)
        {
            if (spec != null && !string.IsNullOrEmpty(spec.Id) && !_geneModuleParams.ContainsKey(spec.Id))
            {
                _geneModuleParams[spec.Id] = spec;
            }
        }

        /// <summary>注册结构器官常驻被动参数（story-008）。同 id 先到先得。</summary>
        public void AddStructuralEffectParams(StructuralEffectParamsSpec spec)
        {
            if (spec != null && !string.IsNullOrEmpty(spec.Id) && !_structuralEffectParams.ContainsKey(spec.Id))
            {
                _structuralEffectParams[spec.Id] = spec;
            }
        }

        /// <summary>注册结构器官触发钩子参数（story-008）。同 id 先到先得。</summary>
        public void AddStructuralTriggerHookParams(StructuralTriggerHookParamsSpec spec)
        {
            if (spec != null && !string.IsNullOrEmpty(spec.Id) && !_structuralTriggerHookParams.ContainsKey(spec.Id))
            {
                _structuralTriggerHookParams[spec.Id] = spec;
            }
        }

        /// <summary>注册首领阶段。同一首领的多个阶段按 BossEnemyId 分组。</summary>
        public void AddBossPhase(BossPhaseSpec spec)
        {
            if (spec == null)
            {
                return;
            }
            if (!_bossPhases.TryGetValue(spec.BossEnemyId, out List<BossPhaseSpec> list))
            {
                list = new List<BossPhaseSpec>(4);
                _bossPhases[spec.BossEnemyId] = list;
            }
            list.Add(spec);
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

        /// <summary>
        /// 取器官的模块构造参数（story-006）。器官目录的 CreateModule 是延迟求值的 lambda，
        /// 可能早于显式 <see cref="Load"/> 被调用（DebugTools 探针/编辑器），故这里兜一次懒加载——
        /// 否则会拿到全 0 参数，弹速/角度归零而不报错。
        /// </summary>
        public OrganModuleParamsSpec GetOrganModuleParams(string id)
        {
            if (!Loaded)
            {
                Load();
            }
            return id != null && _organModuleParams.TryGetValue(id, out OrganModuleParamsSpec s)
                ? s
                : EmptyOrganModuleParams;
        }

        /// <summary>
        /// 取基因的模块构造参数（story-007）。与 <see cref="GetOrganModuleParams"/> 同款懒加载守卫：
        /// 基因目录的 CreateModule 也是延迟求值 lambda，可能早于显式 <see cref="Load"/> 被调用，
        /// 不兜底会静默拿到全 0 参数（追踪强度/留坑秒数归零而不报错）。
        /// </summary>
        public GeneModuleParamsSpec GetGeneModuleParams(string id)
        {
            if (!Loaded)
            {
                Load();
            }
            return id != null && _geneModuleParams.TryGetValue(id, out GeneModuleParamsSpec s)
                ? s
                : EmptyGeneModuleParams;
        }

        /// <summary>
        /// 取结构器官的常驻被动数值（story-008）。与 <see cref="GetOrganModuleParams"/> 同款懒加载守卫：
        /// <see cref="MetabolicSlice.ContentCatalog.OrganelleDef.StructuralEffects"/> 是延迟求值的工厂，
        /// 可能早于显式 <see cref="Load"/> 被调用（DebugTools 探针/编辑器），不兜底会静默拿到全 0
        /// （减伤/生命上限归零而不报错）。查无此行返回全 0 的共享空实例。
        /// </summary>
        public StructuralEffectParamsSpec GetStructuralEffectParams(string id)
        {
            if (!Loaded)
            {
                Load();
            }
            return id != null && _structuralEffectParams.TryGetValue(id, out StructuralEffectParamsSpec s)
                ? s
                : EmptyStructuralEffectParams;
        }

        /// <summary>
        /// 取结构器官的触发钩子数值（story-008）。守卫理由同 <see cref="GetStructuralEffectParams"/>：
        /// 不兜底会让反伤比例/残留秒数/冷却全 0——冷却为 0 的低血量钩子还会退化成每帧触发。
        /// </summary>
        public StructuralTriggerHookParamsSpec GetStructuralTriggerHookParams(string id)
        {
            if (!Loaded)
            {
                Load();
            }
            return id != null && _structuralTriggerHookParams.TryGetValue(id, out StructuralTriggerHookParamsSpec s)
                ? s
                : EmptyStructuralTriggerHookParams;
        }

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

        /// <summary>某首领的全部阶段（未按顺序排序，调用方自行按 HpThreshold 判定）。无阶段返回 null。</summary>
        public IReadOnlyList<BossPhaseSpec> GetBossPhases(int bossEnemyId)
        {
            return _bossPhases.TryGetValue(bossEnemyId, out List<BossPhaseSpec> list) ? list : null;
        }

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
                if (e.IsBoss && !_bossPhases.ContainsKey(e.Id))
                {
                    TEngine.Log.Warning($"[DataRegistry] 首领 {e.Id}({e.Name}) 未配置任何 BossPhase，三阶段切换不会生效");
                    problems++;
                }
            }

            foreach (var kv in _bossPhases)
            {
                List<BossPhaseSpec> list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    BossPhaseSpec p = list[i];
                    if (p.ArchetypeIndex < 0 || p.ArchetypeIndex >= _archetypes.Count)
                    {
                        TEngine.Log.Error(
                            $"[DataRegistry] 首领阶段 {p.Id}(首领{p.BossEnemyId}/阶段{p.PhaseIndex}) 行为原型索引越界: {p.ArchetypeIndex}");
                        problems++;
                    }
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
        /// <summary>story-002 D1：图鉴/tooltip 用的中文一句话机制说明。默认空串。</summary>
        public string Description = "";
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

    /// <summary>
    /// 首领阶段定义（TR-cell-011）。同一首领可有多个阶段，按血量阈值切换。
    /// 判定规则：取 (当前血量% ≤ HpThreshold) 中 HpThreshold 最小的一项——
    /// 即"血量掉到哪一档，就用哪一档最贴近的行为"，与阈值在表里的顺序无关。
    /// </summary>
    public sealed class BossPhaseSpec
    {
        public int Id;
        /// <summary>所属首领的敌人 id。</summary>
        public int BossEnemyId;
        /// <summary>阶段序号，0 起。</summary>
        public int PhaseIndex;
        public string Name;
        /// <summary>进入本阶段的血量百分比上限，(0,1]。</summary>
        public float HpThreshold;
        /// <summary>本阶段生效的行为原型索引，覆盖敌人默认原型。</summary>
        public int ArchetypeIndex;
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

    /// <summary>全局常量定义，对应 CellGlobal 表。</summary>
    public sealed class GlobalSpec
    {
        public int ObstacleCount = 14;
        public float ArenaHalfExtent = 90f;
        public int UnitCapacity = 16384;
        public float HashCellSize = 4f;
        public int PityThreshold = 4;
        public float LowHealthPercent = 0.3f;
        public float RouteAffinityPerCard = 0.13f;
        public float RouteAffinityCap = 1.8f;
        public float SynergyBonusPerMatch = 0.07f;
        public float SynergyBonusCap = 1.5f;
        // cell-global-stat-defaults-to-table：breachedDiscount/corrodedDiscount 在 Main/Sim/
        // 内使用，走 SetupSim() → SimConfig 传递（见 CellStageFlow.SetupSim），不直接被 Sim 读
        // 取本类。
        public float BreachedDiscount = 0.7f;
        public float CorrodedDiscount = 0.85f;
        public float BaseMaxHealth = 160f;
        public float BaseMoveSpeed = 8f;
        public float BaseVolume = 1f;
        public float BaseMeleeDamage = 8f;
        public float DevourRatio = 1.05f;
        public float StaminaMax = 100f;
        public float StaminaRegen = 18f;
        public float PollutionCap = 100f;
        public int StartAbilitySlots = 2;
        public int MaxAbilitySlots = 5;
        public float VolumeGrowthRatio = 0.055f;
        public float ComboWindow = 2.5f;
        public float ComboGainPerStack = 0.06f;
    }

    /// <summary>
    /// 攻击器官 CreateModule 的构造参数，对应 cell.OrganModuleParams 表
    /// （gene-organ-universal-reaction story-006）。
    ///
    /// 一行覆盖一个器官用到的全部模块参数，用不到的列恒 0（= ComposeEngine 各模块的构造默认值）。
    /// "接哪个模块类型 / 组合顺序"仍留在 <see cref="MetabolicSlice.ContentCatalog.OrganelleCatalog"/>
    /// 代码里，只有数字搬到表；SummonModule.summonId 是指向 BehaviorArchetype 的内容引用，不进本表。
    /// </summary>
    public sealed class OrganModuleParamsSpec
    {
        public string Id;
        /// <summary>BallisticsModule.speed。</summary>
        public float BallisticsSpeed;
        /// <summary>BallisticsModule.lifetime（秒）。</summary>
        public float BallisticsLifetime;
        /// <summary>SpreadModule.angleDegrees。</summary>
        public float SpreadAngle;
        /// <summary>Thorns.reflectDamage。</summary>
        public float ReflectDamage;
        /// <summary>TickModule.ratePerSecond。</summary>
        public float TickRate;
        /// <summary>LingerModule.seconds。</summary>
        public float LingerSeconds;
        /// <summary>AuraModule.radius。</summary>
        public float AuraRadius;
        /// <summary>OrbitSpin.angularSpeed。</summary>
        public float OrbitSpeed;
        /// <summary>Scatterer.baseCount。</summary>
        public int ScattererCount;
        /// <summary>KnockbackModule.amount。</summary>
        public float KnockbackForce;
        /// <summary>PierceModule.count。</summary>
        public int PierceCount;
        /// <summary>Grow.baseGrowRate。</summary>
        public float GrowScale;
        /// <summary>SummonModule.count。</summary>
        public int SummonCount;
    }

    /// <summary>
    /// 基因 CreateModule 的构造参数，对应 cell.GeneModuleParams 表
    /// （gene-organ-universal-reaction story-007）。
    ///
    /// 一行覆盖一条基因用到的全部模块参数，用不到的列恒 0（= ComposeEngine 各模块的构造默认值）。
    /// "接哪个模块类型 / 组合顺序"仍留在 <see cref="MetabolicSlice.ContentCatalog.GeneCatalog"/>
    /// 代码里，只有数字搬到表；TagAttach 的字符串标签是内容/规则选择，不进本表。
    /// 同语义字段与 <see cref="OrganModuleParamsSpec"/> 共用列名（story-007 Required 1）。
    /// </summary>
    public sealed class GeneModuleParamsSpec
    {
        public string Id;
        /// <summary>HomingModule.strength。</summary>
        public float HomingStrength;
        /// <summary>SpreadModule.angleDegrees。</summary>
        public float SpreadAngle;
        /// <summary>Scatterer.baseCount。</summary>
        public int ScattererCount;
        /// <summary>BounceModule.count。</summary>
        public int BounceCount;
        /// <summary>PierceModule.count。</summary>
        public int PierceCount;
        /// <summary>Grow.baseGrowRate。</summary>
        public float GrowScale;
        /// <summary>OrbitSpin.angularSpeed。</summary>
        public float OrbitSpeed;
        /// <summary>OrbitRadiusModule.radius。</summary>
        public float OrbitRadius;
        /// <summary>EchoModule.delaySeconds。</summary>
        public float EchoDelay;
        /// <summary>TrailModule.damage。</summary>
        public float TrailDamage;
        /// <summary>LingerModule.seconds。</summary>
        public float LingerSeconds;
        /// <summary>ChainModule.targetCount。</summary>
        public int ChainCount;
        /// <summary>Capacitor.chargeMult。</summary>
        public float CapacitorRatio;
        /// <summary>SplitModule.count。</summary>
        public int SplitCount;
        /// <summary>PullModule.strength。</summary>
        public float PullStrength;
        /// <summary>BallisticsModule.speed。</summary>
        public float BallisticsSpeed;
        /// <summary>BallisticsModule.lifetime（秒）。</summary>
        public float BallisticsLifetime;
        /// <summary>BallisticsModule.gravity。</summary>
        public float BallisticsGravity;
        /// <summary>TickModule.ratePerSecond。</summary>
        public float TickRate;
        /// <summary>RippleModule.ratePerSecond。</summary>
        public float RippleRate;
        /// <summary>RhythmModule.ratePerSecond。</summary>
        public float RhythmRate;
        /// <summary>CatalystModule.amplifier。</summary>
        public float CatalystAmplifier;
        /// <summary>WeaveModule.linkRadius。</summary>
        public float WeaveRadius;
    }

    /// <summary>
    /// 结构器官常驻被动（<see cref="Stats.StatModifier"/> 数值），对应 cell.StructuralEffectParams 表
    /// （gene-organ-universal-reaction story-008）。
    ///
    /// 每个用到的 <see cref="Stats.StatId"/> 各一列，列名后缀标出叠加口径（Pct=PctAdd / Flat=Flat）——
    /// "改哪条属性 / 怎么叠"是类型选择，仍写在
    /// <see cref="MetabolicSlice.ContentCatalog.OrganelleCatalog"/> 代码里，只有 Value 搬到表。
    /// 某列为 0 = 该器官不挂这条修正（目录里按 0 就不生成该 StatModifier）。
    /// 减伤 / 降仇恨天然是负值，原样搬家不取绝对值。
    /// </summary>
    public sealed class StructuralEffectParamsSpec
    {
        public string Id;
        /// <summary>StatId.DamageTaken / ModifierOp.PctAdd。</summary>
        public float DamageTakenPct;
        /// <summary>StatId.MoveSpeed / ModifierOp.PctAdd。</summary>
        public float MoveSpeedPct;
        /// <summary>StatId.MaxHealth / ModifierOp.Flat。</summary>
        public float MaxHealthFlat;
        /// <summary>StatId.HealthRegen / ModifierOp.Flat。</summary>
        public float HealthRegenFlat;
        /// <summary>StatId.PickupRadius / ModifierOp.PctAdd。</summary>
        public float PickupRadiusPct;
        /// <summary>StatId.NutrientGain / ModifierOp.PctAdd。</summary>
        public float NutrientGainPct;
        /// <summary>StatId.AggroScale / ModifierOp.PctAdd。</summary>
        public float AggroScalePct;
        /// <summary>StatId.StaminaMax / ModifierOp.Flat。</summary>
        public float StaminaMaxFlat;
        /// <summary>StatId.StaminaRegen / ModifierOp.PctAdd。</summary>
        public float StaminaRegenPct;
        /// <summary>StatId.ShieldMax / ModifierOp.Flat。</summary>
        public float ShieldMaxFlat;
        /// <summary>StatId.ShieldRegen / ModifierOp.Flat。</summary>
        public float ShieldRegenFlat;
    }

    /// <summary>
    /// 结构器官触发钩子的数值字段，对应 cell.StructuralTriggerHookParams 表
    /// （gene-organ-universal-reaction story-008），逐字段对应
    /// <see cref="MetabolicSlice.Structural.TriggerHookSpec"/> 的 float 成员。
    ///
    /// <c>Kind</c>（触发时机）与 <c>Tag</c>（挂哪种 Substance 标记）是行为/内容选择不是数值，
    /// 不进本表，仍写在 <see cref="MetabolicSlice.ContentCatalog.OrganelleCatalog"/> 代码里。
    /// 用不到的列恒 0 = TriggerHookSpec 的 struct 默认值。
    /// </summary>
    public sealed class StructuralTriggerHookParamsSpec
    {
        public string Id;
        /// <summary>TriggerHookSpec.Probability。</summary>
        public float Probability;
        /// <summary>TriggerHookSpec.ThornsRatio。</summary>
        public float ThornsRatio;
        /// <summary>TriggerHookSpec.AbsorbRatio。</summary>
        public float AbsorbRatio;
        /// <summary>TriggerHookSpec.LingerRadius。</summary>
        public float LingerRadius;
        /// <summary>TriggerHookSpec.LingerSeconds。</summary>
        public float LingerSeconds;
        /// <summary>TriggerHookSpec.LowHealthThreshold。</summary>
        public float LowHealthThreshold;
        /// <summary>TriggerHookSpec.Cooldown。</summary>
        public float Cooldown;
        /// <summary>TriggerHookSpec.TickRate。</summary>
        public float TickRate;
        /// <summary>TriggerHookSpec.MoveDistanceThreshold。</summary>
        public float MoveDistanceThreshold;
        /// <summary>TriggerHookSpec.KillHealAmount（story-009 新增，org_blood_vacuole 用）。</summary>
        public float KillHealAmount;
    }
}
