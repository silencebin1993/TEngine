using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Ability.Executors;
using GameLogic.Battle;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stats;
using UnityEngine;

namespace GameLogic.Stage.CellStage
{
    /// <summary>
    /// 细胞阶段流程。第一个 <see cref="IStageFlow"/> 实现，也是后续阶段的样板。
    ///
    /// 本类的职责只有三件：
    ///   1. 装配模块（注册到 ModuleHub）
    ///   2. 推进阶段状态（进行中 / 选卡暂停 / 结束）
    ///   3. 产出 StageOutcome
    ///
    /// 它**不含任何玩法逻辑**——玩法都在各自模块里。这样后续阶段照抄这个结构，
    /// 换一批模块即可，StageDirector 完全不动（框架文档 §7）。
    /// </summary>
    public sealed class CellStageFlow : IStageFlow
    {
        public StageId Id => StageId.Cell;

        private readonly ModuleHub _hub = new ModuleHub();
        private readonly StatSheet _stats = new StatSheet();
        private readonly Deck _deck = new Deck();
        private readonly DraftService _draft = new DraftService();
        private readonly StageOutcome _outcome = new StageOutcome();

        private SimBridge _sim;
        private StatusSystem _status;
        private AreaZoneSystem _zones;
        private AbilitySystem _abilities;
        private CardTriggerBus _cards;
        private ResourceWallet _wallet;
        private ProgressionModule _progression;
        private SpawnDirector _director;
        private EcoEventScheduler _events;
        private PhaseTimeline _timeline;
        private CellDevourSystem _devour;
        private CellPlayerController _player;
        private MinionRegistry _minions;
        private BossPhaseController _bossPhase;
        private ShopSystem _shop;
        private CodexRegistry _codex;

        private SimRenderer _renderer;
        private Camera _camera;

        private bool _running;
        private bool _paused;
        private string _deathCause;

        /// <summary>选卡暂停时的待选项。UI 读它显示进化选择界面。</summary>
        public System.Collections.Generic.List<CardSpec> PendingOptions { get; private set; }
        public DraftKind PendingDraftKind { get; private set; }

        public bool Paused => _paused;
        public StatSheet Stats => _stats;
        public Deck Deck => _deck;
        public PhaseTimeline Timeline => _timeline;
        public ResourceWallet Wallet => _wallet;
        public ProgressionModule Progression => _progression;
        public SpawnDirector Director => _director;
        public EcoEventScheduler Events => _events;
        public AbilitySystem Abilities => _abilities;
        public CellDevourSystem Devour => _devour;
        public BossPhaseController BossPhase => _bossPhase;
        public ShopSystem Shop => _shop;
        public CodexRegistry Codex => _codex;
        public SimBridge Sim => _sim;
        public StatusSystem Status => _status;
        public AreaZoneSystem Zones => _zones;

        public void Enter(StageOutcome inherited)
        {
            DataRegistry.Instance.Load();
            RuleFlags.Current.ClearAll();
            Signals.Clear();

            _stats.ResetToDefaults();
            _deck.Clear();
            _draft.Bind(_deck, _stats);
            _draft.Reset();
            _outcome.Reset();
            _deathCause = null;

            // inherited 在细胞阶段为 null（它是第一个阶段）。
            // 保留这个参数是为了后续阶段能读上一阶段产物——接口先立住。
            if (inherited != null)
            {
                ApplyInherited(inherited);
            }

            SetupCamera();
            RegisterModules();
            SetupSim();
            GrantStarterAbilities();

            _hub.Enter();
            _running = true;
            _paused = false;
        }

        /// <summary>来源 id，用于按来源批量移除继承带来的属性修正（当前无移除需求，仅作标记）。</summary>
        private const int InheritedStatSourceId = -100;

        /// <summary>
        /// 应用上一阶段继承。当前无调用方，但结构先立住，
        /// 这样器官阶段接入时不需要改本类的其它部分。
        ///
        /// 按 prev.DominantRoute 给一条对应属性加成，按 prev.KeyCards 直接注入起始卡组。
        /// </summary>
        private void ApplyInherited(StageOutcome prev)
        {
            StatId bonusStat = RouteBonusStat(prev.DominantRoute);
            if (bonusStat != StatId.None)
            {
                _stats.Add(new StatModifier(bonusStat, ModifierOp.PctAdd, 0.1f, InheritedStatSourceId));
            }

            int injected = 0;
            if (prev.KeyCards != null)
            {
                for (int i = 0; i < prev.KeyCards.Count; i++)
                {
                    CardSpec spec = prev.KeyCards[i];
                    if (spec != null && _deck.Acquire(spec) > 0)
                    {
                        injected++;
                    }
                }
            }

            TEngine.Log.Info($"[CellStageFlow] 收到上一阶段产物：{prev.StageId}，"
                + $"主导路线 {prev.DominantRoute}（加成 {bonusStat}），注入定义性卡牌 {injected} 张");
        }

        /// <summary>路线 → 继承加成属性。每条路线对应它最核心的一项数值。</summary>
        private static StatId RouteBonusStat(CardRoute route)
        {
            switch (route)
            {
                case CardRoute.Devour: return StatId.DevourGain;
                case CardRoute.Agile: return StatId.MoveSpeed;
                case CardRoute.Electric: return StatId.ElectricPower;
                case CardRoute.Spore: return StatId.EvoGain;
                case CardRoute.Nest: return StatId.MyceliumScale;
                case CardRoute.Corrupt: return StatId.PollutionCap;
                default: return StatId.None;
            }
        }

        private void SetupCamera()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                _camera = go.GetComponent<Camera>();
            }
            _camera.orthographic = true;
            _camera.orthographicSize = 16f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.05f, 0.07f, 0.10f);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.transform.SetPositionAndRotation(
                new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
        }

        /// <summary>
        /// 装配。这是"新增系统 = 注册一行"的体现：
        /// 加一个玩法系统只需在这里多一行 Register，其它模块无感。
        /// </summary>
        private void RegisterModules()
        {
            _sim = _hub.Register(new SimBridge());
            _status = _hub.Register(new StatusSystem());
            _zones = _hub.Register(new AreaZoneSystem());
            _abilities = _hub.Register(new AbilitySystem());
            _cards = _hub.Register(new CardTriggerBus());
            _wallet = _hub.Register(new ResourceWallet());
            _progression = _hub.Register(new ProgressionModule());
            _director = _hub.Register(new SpawnDirector());
            _events = _hub.Register(new EcoEventScheduler());
            _timeline = _hub.Register(new PhaseTimeline());
            _devour = _hub.Register(new CellDevourSystem());
            _player = _hub.Register(new CellPlayerController());
            _minions = _hub.Register(new MinionRegistry());
            _bossPhase = _hub.Register(new BossPhaseController());
            _shop = _hub.Register(new ShopSystem());
            _codex = _hub.Register(new CodexRegistry());

            // 效果执行器注册。新增一种效果只需在此多一行。
            _abilities.RegisterExecutor(new EffectDealDamage());
            _abilities.RegisterExecutor(new EffectApplyStatus());
            _abilities.RegisterExecutor(new EffectSpawn());
            _abilities.RegisterExecutor(new EffectModifyStat());
            _abilities.RegisterExecutor(new EffectResource());
            _abilities.RegisterExecutor(new EffectDash());
            _abilities.RegisterExecutor(new EffectProjectile());
            _abilities.RegisterExecutor(new EffectArea());
            _abilities.RegisterExecutor(new EffectRule());

            // 依赖注入。模块之间不互相 new，只在这里接线。
            _abilities.Bind(_sim, _stats);
            _status.Bind(_sim);
            _zones.Bind(_sim, _status);
            _wallet.Bind(_stats);
            _progression.Bind(_wallet);
            _cards.Bind(_deck, _abilities, _sim, _stats);
            _director.Bind(_sim, _stats, _deck);
            _timeline.Bind(_director);
            _events.Bind(_director, _timeline, _progression, _wallet);
            _devour.Bind(_sim, _stats, _wallet, _events, _outcome.Statistics, _zones, _minions);
            _player.Bind(_sim, _stats, _abilities, _wallet, _camera);
            _bossPhase.Bind(_sim);
            _shop.Bind(_wallet, _stats, _deck, _sim);
        }

        private void SetupSim()
        {
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = 16384;
            cfg.ArenaHalfExtent = 90f;
            cfg.HashCellSize = 4f;

            _sim.Begin(cfg, DataRegistry.Instance.ArchetypeArray());
            _sim.SetPlayerStats(
                _stats.Get(StatId.MaxHealth),
                _stats.Get(StatId.MaxHealth),
                _stats.Get(StatId.Volume),
                _stats.Get(StatId.MoveSpeed));

            _renderer = new SimRenderer();
            _renderer.Initialize(BuildVisuals(), cfg.UnitCapacity);
        }

        /// <summary>
        /// 白模视觉。正式美术接入前用颜色分层保证可读性
        /// （Spec §16"万敌规模下可读性"风险项的第一道对策）。
        /// </summary>
        private static SimVisual[] BuildVisuals()
        {
            Mesh quad = BuildQuad();
            Material mat = CreateSimMaterial(Color.white);

            var visuals = new SimVisual[32];
            for (int i = 0; i < visuals.Length; i++)
            {
                visuals[i] = new SimVisual
                {
                    Mesh = quad,
                    Material = mat,
                    ScaleMul = 1f,
                    BaseColor = ColorFor(i),
                };
            }
            return visuals;
        }

        /// <summary>
        /// 阳光培养皿材质（BioGlass）。必须 GPU Instancing；
        /// 定案见 <c>DesignDocs/Material_LookDev_BioGlass.md</c>。
        /// </summary>
        private static Material CreateSimMaterial(Color color)
        {
            Shader shader = Shader.Find("BinGames/SimBioGlass");
            if (shader == null)
            {
                TEngine.Log.Warning("[CellStageFlow] 找不到 SimBioGlass，回退 SimInstancedUnlit");
                shader = Shader.Find("BinGames/SimInstancedUnlit");
            }
            if (shader == null)
            {
                TEngine.Log.Error("[CellStageFlow] 找不到实例化 Shader，回退 Unlit/Color（画面可能全空）");
                shader = Shader.Find("Unlit/Color");
            }

            var mat = new Material(shader)
            {
                color = color,
                enableInstancing = true,
            };

            // 软边 + 游动/受击形变（强度由 SimRenderer 写 _Motion/_Impact）
            if (mat.HasProperty("_RimColor"))
            {
                mat.SetColor("_RimColor", new Color(1f, 1f, 0.95f, 0.7f));
            }
            if (mat.HasProperty("_EdgeSoft"))
            {
                mat.SetFloat("_EdgeSoft", 0.08f);
            }
            if (mat.HasProperty("_OutlineWidth"))
            {
                mat.SetFloat("_OutlineWidth", 0.11f);
            }
            if (mat.HasProperty("_IdleWobble"))
            {
                mat.SetFloat("_IdleWobble", 0.028f);
            }
            if (mat.HasProperty("_SwimStretch"))
            {
                mat.SetFloat("_SwimStretch", 0.22f);
            }
            if (mat.HasProperty("_ImpactSquash"))
            {
                mat.SetFloat("_ImpactSquash", 0.32f);
            }
            if (mat.HasProperty("_BodyAlpha"))
            {
                mat.SetFloat("_BodyAlpha", 0.90f);
            }

            return mat;
        }

        /// <summary>吉卜力鲜艳生命色；污染/残块刻意降饱和。alpha 近 1，半透明交给描边环。</summary>
        private static Color ColorFor(int visualId)
        {
            switch (visualId)
            {
                case 0: return new Color(0.35f, 0.98f, 0.72f, 1f);  // 玩家：薄荷绿最高对比
                case 1: return new Color(1.00f, 0.86f, 0.42f, 1f);  // 浮游食团：蜜黄
                case 2: return new Color(1.00f, 0.55f, 0.42f, 1f);  // 刺膜：珊瑚
                case 3: return new Color(0.45f, 0.78f, 1.00f, 1f);  // 扫尾：天蓝
                case 4: return new Color(1.00f, 0.42f, 0.38f, 1f);  // 追猎：鲜红
                case 5: return new Color(0.72f, 0.48f, 1.00f, 1f);  // 噬菌：薰衣草
                case 6: return new Color(0.62f, 0.72f, 0.78f, 1f);  // 硬壳：灰青
                case 7: return new Color(0.35f, 0.92f, 1.00f, 1f);  // 导电：亮青
                case 8: return new Color(0.48f, 0.52f, 0.28f, 1f);  // 腐败：脏橄榄
                case 9: return new Color(1.00f, 0.78f, 0.28f, 1f);  // 游隼：暖金
                case 10: return new Color(0.55f, 0.95f, 0.40f, 1f); // 毒棘：草绿
                case 11: return new Color(0.40f, 0.62f, 0.38f, 1f); // 菌丝：苔绿
                case 20: return new Color(0.45f, 0.32f, 0.28f, 1f); // 残块：脏褐
                case 50: return new Color(1.00f, 0.62f, 0.18f, 1f); // 精英暖金
                case 51: return new Color(1.00f, 0.48f, 0.62f, 1f);
                case 52: return new Color(0.48f, 0.68f, 1.00f, 1f);
                case 90: return new Color(0.95f, 0.28f, 0.22f, 1f); // 首领
                default: return new Color(0.70f, 0.78f, 0.72f, 1f);
            }
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "SimQuad" };
            m.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, -0.5f),
            });
            m.SetUVs(0, new System.Collections.Generic.List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 0f),
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private void GrantStarterAbilities()
        {
            // 冲刺是初始技能，全路线通用
            AbilitySpec dash = DataRegistry.Instance.GetAbility(1);
            if (dash != null)
            {
                _abilities.Grant(dash);
            }
        }

        public void Update(float dt)
        {
            if (!_running)
            {
                return;
            }

            // 选卡时暂停玩法推进，但不暂停 UI
            if (_paused)
            {
                return;
            }

            _hub.Update(dt);

            TrackPeakEnemies();
            CheckDraft();
            CheckEnd();

            _renderer?.Draw(_sim.Snapshot);
            if (_sim.World != null)
            {
                _renderer?.DrawProjectiles(_sim.World.Projectiles, new SimVisual
                {
                    Mesh = BuildQuadCached(),
                    Material = ProjectileMaterial(),
                    ScaleMul = 1f,
                    BaseColor = new Color(1f, 0.9f, 0.5f),
                });
            }

            FollowCamera(dt);
        }

        /// <summary>镜头跟随。用非缩放时间，调试加速时跟随手感不变。</summary>
        private void FollowCamera(float dt)
        {
            if (_camera == null || _sim == null || !_sim.Running)
            {
                return;
            }
            Unity.Mathematics.float2 p = _sim.PlayerPosition;
            var want = new Vector3(p.x, _camera.transform.position.y, p.y);
            _camera.transform.position = Vector3.Lerp(
                _camera.transform.position, want, 1f - Mathf.Exp(-8f * dt));
        }

        private Mesh _quadCache;
        private Material _projMat;

        private Mesh BuildQuadCached() => _quadCache ??= BuildQuad();

        private Material ProjectileMaterial()
        {
            if (_projMat == null)
            {
                _projMat = CreateSimMaterial(new Color(1f, 0.9f, 0.5f));
            }
            return _projMat;
        }

        private void TrackPeakEnemies()
        {
            int live = _director?.LiveHostiles ?? 0;
            if (live > _outcome.Statistics.PeakEnemyCount)
            {
                _outcome.Statistics.PeakEnemyCount = live;
            }
        }

        private void CheckDraft()
        {
            if (_paused || _progression == null)
            {
                return;
            }
            if (!_progression.TryDequeueDraft(out DraftKind kind))
            {
                return;
            }

            float maxHp = _stats.Get(StatId.MaxHealth);
            float pct = maxHp > 0f ? _sim.PlayerHealth / maxHp : 1f;

            PendingOptions = _draft.Roll(kind, _timeline?.CurrentIndex ?? 0, pct);
            PendingDraftKind = kind;

            if (PendingOptions == null || PendingOptions.Count == 0)
            {
                // 没有可选卡（卡池耗尽），跳过而不是卡死
                return;
            }
            _paused = true;
        }

        /// <summary>UI 选定卡牌后调用，恢复玩法。</summary>
        public void ConfirmDraft(int cardId)
        {
            CardSpec spec = DataRegistry.Instance.GetCard(cardId);
            if (spec != null)
            {
                int stack = _deck.Acquire(spec);
                if (stack > 0)
                {
                    if (spec.PollutionCost > 0f)
                    {
                        _wallet.Add(ResourceKind.Pollution, spec.PollutionCost);
                    }
                    Signals.Publish(new CardAcquiredSignal { CardId = cardId, NewStack = stack });
                    _outcome.Statistics.LevelsGained++;
                }
            }

            PendingOptions = null;
            _paused = false;
        }

        /// <summary>放弃本次选卡（UI 的跳过按钮）。</summary>
        public void SkipDraft()
        {
            PendingOptions = null;
            _paused = false;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// GM：无消耗强制弹出选卡。若已在选卡暂停中，当场重掷选项。
        /// </summary>
        public void DebugForceDraft(DraftKind kind)
        {
            if (_progression == null || _draft == null)
            {
                return;
            }

            _progression.DebugForceDraft(kind);

            float maxHp = _stats.Get(StatId.MaxHealth);
            float pct = maxHp > 0f ? _sim.PlayerHealth / maxHp : 1f;

            // 立刻开面板：从队列取出刚塞入的请求，避免等下一帧 / 被暂停挡住
            if (_progression.TryDequeueDraft(out DraftKind dequeued))
            {
                kind = dequeued;
            }

            PendingOptions = _draft.Roll(kind, _timeline?.CurrentIndex ?? 0, pct);
            PendingDraftKind = kind;
            if (PendingOptions == null || PendingOptions.Count == 0)
            {
                TEngine.Log.Warning($"[GM] DebugForceDraft({kind}) 卡池为空，未弹出选卡");
                return;
            }

            _paused = true;
            TEngine.Log.Info($"[GM] 强制选卡 {kind}（等级 {_progression.Level}）");
        }

        /// <summary>GM：推进一个生态时期（跨时期，不花时间）。</summary>
        public void DebugAdvancePhase()
        {
            if (_timeline == null || _timeline.Finished)
            {
                TEngine.Log.Warning("[GM] 已无下一生态时期");
                return;
            }

            _timeline.Advance();
            TEngine.Log.Info($"[GM] 推进生态时期 → {_timeline.CurrentIndex + 1}/6");
        }

        /// <summary>GM：跳过剩余时期，立刻通关结算。</summary>
        public void DebugFinishTimeline()
        {
            if (_timeline == null)
            {
                return;
            }

            int guard = 0;
            while (!_timeline.Finished && guard++ < 16)
            {
                _timeline.Advance();
            }

            PendingOptions = null;
            _paused = false;
            _outcome.Victory = true;
            _running = false;
            TEngine.Log.Info("[GM] 已跳过全部生态时期并通关");
        }

        /// <summary>GM：灌满测试用资源（商店等），不触发选卡。</summary>
        public void DebugGrantResources()
        {
            _wallet?.Add(ResourceKind.Nutrient, 999f);
            _wallet?.Add(ResourceKind.Mutagen, 999f);
            _wallet?.Add(ResourceKind.EvoEnergy, 999f);
            TEngine.Log.Info("[GM] +999 营养质 / 突变质 / 进化能");
        }
#endif

        private void CheckEnd()
        {
            if (_sim.PlayerHealth <= 0f)
            {
                _deathCause = ResolveDeathCause();
                _running = false;
                return;
            }

            if (_timeline != null && _timeline.Finished)
            {
                _outcome.Victory = true;
                _running = false;
            }
        }

        /// <summary>按死因区分文案，避免单一挫败感（Spec §13）。</summary>
        private string ResolveDeathCause()
        {
            if (_wallet != null && _wallet.PollutionFull)
            {
                return "pollution";
            }
            // 被大型目标吞噬 vs 生命耗尽，目前无法从内核区分，
            // 统一走"生命耗尽"。TODO(内核): DeathEvent 里带上致死来源类型。
            return "health";
        }

        public bool IsRunning => _running;

        public StageOutcome Exit()
        {
            BuildOutcome();

            _hub.Exit();
            _hub.Dispose();

            _renderer?.Dispose();
            _renderer = null;

            Signals.Clear();
            RuleFlags.Current.ClearAll();

            return _outcome;
        }

        private void BuildOutcome()
        {
            _outcome.StageId = StageId.Cell;
            _outcome.DurationSeconds = _timeline?.RunElapsed ?? 0f;
            _outcome.DeathCause = _deathCause;
            _outcome.DominantRoute = _deck.DominantRoute();
            _deck.CopyRouteCounts(_outcome.RouteScores);
            _outcome.KeyCards.AddRange(_deck.KeyCards());
            _outcome.PollutionLevel = _wallet?.Pollution ?? 0f;
            _outcome.Level = _progression?.Level ?? 0;
            _outcome.Statistics.PhasesReached = (_timeline?.CurrentIndex ?? 0) + 1;

            var entries = _deck.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                _outcome.AllCards.Add((entries[i].Spec.Id, entries[i].Stack));
            }

            for (int i = 1; i < (int)StatId.Count; i++)
            {
                _outcome.FinalStats[i] = _stats.Get((StatId)i);
            }
        }
    }
}
