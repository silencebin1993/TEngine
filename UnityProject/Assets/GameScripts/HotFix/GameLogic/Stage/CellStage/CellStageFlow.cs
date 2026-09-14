using System;
using System.Collections.Generic;
using BinGames.Sim;
using Cysharp.Threading.Tasks;
using GameLogic.Ability;
using GameLogic.Ability.Executors;
using GameLogic.ArtBinding;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Cards;
using GameLogic.Command;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Digestion;
using GameLogic.MetabolicSlice.Structural;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stats;
using GameLogic.UI.Battle;
using GameLogic.View;
using UnityEngine;

namespace GameLogic.Stage.CellStage
{
    /// <summary>
    /// 细胞阶段的一次性进入方式。每次 <see cref="CellStageFlow.Enter"/> 都消费一项，随后自动回到
    /// <see cref="NewRun"/>；这样续局、LookDev 与试玩门不会靠上一次留下的布尔字段串进正常肉鸽。
    /// </summary>
    public enum CellStageEntryMode : byte
    {
        NewRun = 0,
        Resume = 1,
        LookDevSandbox = 2,
        ConsciousnessPlaytest = 3,
    }

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
        private ZoneVisualPresenter _zoneVisual;
        private HealthBarPresenter _healthBars;
        private MetabolicSliceBridge _metabolicBridge;
        private AbilitySystem _abilities;
        private CardTriggerBus _cards;
        private StructuralHookRunner _structuralHooks;
        private ResourceWallet _wallet;
        private SurgicalRewardLedger _surgicalRewards;
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
        private MetabolicSlice.Blueprint.BlueprintRegistry _blueprints;
        private MetabolicSlice.Lineage.LineageRegistry _lineages;
        private MetabolicSlice.Lineage.BiomassLedger _biomassLedger;
        private MetabolicSlice.Lineage.GerminationChamberRegistry _germinationChambers;
        private MetabolicSlice.Lineage.HomecomingRetrofitService _homecomingRetrofit;
        private MetabolicSlice.WildOrgan.WildOrganRegistry _wildOrgans;
        private MetabolicDigestionSystem _digestion;
        private CarrierBodyVisualPresenter _carrierBodyVisual;
        private StructuralVisualPresenter _structuralVisual;
        private EnvFluidBackground _envFluid;
        private ComposeProjectilePresenter _composeProjectilePresenter;
        private Battle.Feedback.DevUnitGoMirror _devUnitGoMirror;

        private SimRenderer _renderer;
        private SignalScope _controlPresentationScope;
        /// <summary>story-005：持有 BuildVisuals() 返回的同一个数组引用，供 ApplyFeatureArtVisualsAsync
        /// 原地覆盖 Mesh/Material（SimRenderer.Initialize 只存引用不复制，见 preflight-decisions R3）。</summary>
        private SimVisual[] _visuals;
        /// <summary>story-005：追踪本次 Enter 期间加载的功能美术 Prefab/Material 资源，Exit 时配对释放。</summary>
        private readonly List<UnityEngine.Object> _loadedArtAssets = new();
        private Camera _camera;
        private bool _cameraVerifyMode;
        private Vector3 _cameraFollowOffset = DefaultCameraOffset;
        /// <summary>M2-01 镜头状态机。刻意不进 _hub——暂停时它仍要跑（见 Update 里的注释）。</summary>
        private CameraDirector _cameraDirector;
        /// <summary>M2-02 选择/编组/命令。与镜头同理，不进 _hub：战略暂停下要能继续选人下令。</summary>
        private SquadCommandSystem _squadCommands;
        /// <summary>M2-02 白模叠加层（选择框 / 选中环 / 命令线）。</summary>
        private Battle.Feedback.WhiteboxSquadOverlay _squadOverlay;
        /// <summary>M2-04a：接管交还的延续、缓冲与安全位置。</summary>
        private Control.AiHandoffSystem _aiHandoff;
        private Battle.Feedback.WhiteboxAiHandoffOverlay _handoffOverlay;
        /// <summary>M2-02：本次暂停是不是"战略暂停"（玩法冻结但仍可选人下令）。</summary>
        private bool _strategicPause;
        /// <summary>M2-03a：装配按实体归属。与镜头/指挥同理不进 _hub——它要在暂停下也能被查询
        /// （战略暂停里查看某个单位装着什么，正是接管前的决策依据）。</summary>
        private Control.UnitLoadoutRegistry _unitLoadouts;
        /// <summary>M2-03b：受控实体的动作集与释放入口。同样不进 _hub——它订阅控制权变更信号，
        /// 而控制权在暂停下也可能变（死亡回弹、读档恢复），冻住它会让恢复后的动作集停在旧身体上。</summary>
        private Control.DirectControlActions _directActions;

        private bool _running;
        private bool _paused;
        private string _deathCause;

        private CellStageEntryMode _entryMode = CellStageEntryMode.NewRun;
        private CellStageEntryMode _nextEntryMode = CellStageEntryMode.NewRun;
        public bool IsSandboxMode => _entryMode == CellStageEntryMode.LookDevSandbox;
        public bool IsConsciousnessPlaytest => _entryMode == CellStageEntryMode.ConsciousnessPlaytest;

        /// <summary>
        /// 把当前进入方式落到模块实例上。LookDev 关闭所有真实战斗噪声；M2-06 只冻结随机刷怪与
        /// 时间线，保留玩家装配 Tick、RTS、接管和手术的生产链。
        /// </summary>
        private void ApplyEntryModeState()
        {
            bool fixedScenario = IsSandboxMode || IsConsciousnessPlaytest;
            if (_director != null)
            {
                _director.Suppressed = fixedScenario;
            }
            if (_timeline != null)
            {
                _timeline.Suppressed = fixedScenario;
            }
            if (_metabolicBridge != null)
            {
                _metabolicBridge.Suppressed = IsSandboxMode;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (IsSandboxMode && _cameraVerifyMode)
            {
                DebugToggleCameraVerifyMode();
            }
#endif
        }

        /// <summary>选卡暂停时的待选项。UI 读它显示进化选择界面。</summary>
        public System.Collections.Generic.List<CardSpec> PendingOptions { get; private set; }
        public DraftKind PendingDraftKind { get; private set; }

        public bool Paused => _paused;

        /// <summary>M2-01：镜头状态机。验收与调试读它，玩法层不应绕过 InputRouter 直接问镜头状态。</summary>
        public CameraDirector CameraDirector => _cameraDirector;

        /// <summary>M2-02：选择、编组与命令下达。</summary>
        public SquadCommandSystem SquadCommands => _squadCommands;

        /// <summary>M2-04a：接管交还可靠性。</summary>
        public Control.AiHandoffSystem AiHandoff => _aiHandoff;

        /// <summary>M2-03a：按实体归属的装配。直控动作集从这里读，没有第二张英雄技能表。</summary>
        public Control.UnitLoadoutRegistry UnitLoadouts => _unitLoadouts;

        /// <summary>M2-03b：受控实体的动作集。UI/验收读它取"现在能按出什么"。</summary>
        public Control.DirectControlActions DirectActions => _directActions;

        /// <summary>M2-02：当前是否为战略暂停（玩法冻结，但战略域输入保留）。</summary>
        public bool StrategicPause => _paused && _strategicPause;

        /// <summary>story-005：暂停菜单最小公开入口，复用 Draft 已验证的冻结语义（不碰 Time.timeScale）。</summary>
        /// <param name="strategic">
        /// M2-02 战略暂停：玩法照常冻结，但保留战略域输入，用于暂停下选人与排队下令。
        /// 选卡、商店、暂停菜单一律传 false——它们都伴随模态面板，本来就该全部让位。
        /// </param>
        public void SetPaused(bool paused, bool strategic = false)
        {
            if (!_running)
            {
                return;
            }
            _paused = paused;
            _strategicPause = paused && strategic;
        }

        /// <summary>story-005：放弃本局只标记死因，不调用 GameRoot——阶段不需要知道 director，同 Exit() 既有原则。</summary>
        public void MarkAbandoned()
        {
            _deathCause = "abandoned";
        }

        public StatSheet Stats => _stats;
        public Deck Deck => _deck;
        public PhaseTimeline Timeline => _timeline;
        public ResourceWallet Wallet => _wallet;
        public SurgicalRewardLedger SurgicalRewards => _surgicalRewards;
        public ProgressionModule Progression => _progression;
        public SpawnDirector Director => _director;
        public EcoEventScheduler Events => _events;
        public AbilitySystem Abilities => _abilities;
        public CellDevourSystem Devour => _devour;
        public CellPlayerController PlayerController => _player;
        public BossPhaseController BossPhase => _bossPhase;
        public ShopSystem Shop => _shop;
        public CodexRegistry Codex => _codex;
        public MetabolicSlice.Blueprint.BlueprintRegistry Blueprints => _blueprints;
        public MetabolicSlice.Lineage.LineageRegistry Lineages => _lineages;
        public MetabolicSlice.Lineage.BiomassLedger BiomassLedger => _biomassLedger;
        public MetabolicSlice.Lineage.GerminationChamberRegistry GerminationChambers => _germinationChambers;
        public MetabolicSlice.Lineage.HomecomingRetrofitService HomecomingRetrofit => _homecomingRetrofit;
        public MetabolicSlice.WildOrgan.WildOrganRegistry WildOrgans => _wildOrgans;
        public SimBridge Sim => _sim;
        public StatusSystem Status => _status;
        public AreaZoneSystem Zones => _zones;
        public HealthBarPresenter HealthBars => _healthBars;
        public MetabolicSliceBridge MetabolicBridge => _metabolicBridge;
        public MetabolicDigestionSystem Digestion => _digestion;

        public void Enter(StageOutcome inherited)
        {
            _entryMode = _nextEntryMode;
            _nextEntryMode = CellStageEntryMode.NewRun;

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
            ApplyEntryModeState();
            SetupSim();
            // M2-01：镜头状态机要读场地半径做平移边界，所以必须在 SetupSim 之后绑定。
            SetupCameraDirector();
            // CellStageFlow 实例跨局复用（GameRoot 只注册一个），视角沿检测的上一帧值必须跟着
            // Bind 一起回到 Direct，否则上一局停在战略视角会让这一局第一次切视角漏掉交还。
            _lastViewMode = ViewMode.Direct;
            _parkedControlUnit = SimEntityId.None;
            // Edit Mode 验收只建立模拟并验证纯逻辑，不具备运行时资源模块生命周期。
            // Editor Play 与 Player 中 Application.isPlaying 均为 true，功能美术加载路径保持不变。
            if (Application.isPlaying)
            {
                ApplyFeatureArtVisualsAsync().Forget();
            }
            GrantStarterAbilities();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (IsSandboxMode)
            {
                SpawnSandboxDummy();
            }
            else if (IsConsciousnessPlaytest)
            {
                SpawnConsciousnessPlaytestTarget();
            }
#endif

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

        /// <summary>透视相机参数（topdown-hud-projectile-fix 起改为仅 F12 调试态用，不再是默认）。</summary>
        private const float VerifyPitchDegrees = 50f;
        private const float VerifyFieldOfView = 50f;
        private const float VerifyViewDistance = 18f;

        /// <summary>正交俯视相机参数（topdown-hud-projectile-fix 起恢复为默认态）。</summary>
        private static readonly Vector3 DefaultCameraOffset = new Vector3(0f, 40f, 0f);
        private static readonly Quaternion DefaultCameraRotation = Quaternion.Euler(90f, 0f, 0f);

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
            _cameraFollowOffset = DefaultCameraOffset;
            _camera.transform.SetPositionAndRotation(_cameraFollowOffset, DefaultCameraRotation);
        }

        /// <summary>
        /// M2-02 战略暂停开关（战略视角下按空格）。
        ///
        /// 只从战略视角进入：直控下冻结世界再操作没有意义，而且 Space 在直控域是冲刺。
        /// 同一个键在两个域里各有含义，靠 <see cref="InputScope"/> 互斥，不靠这里判断镜头状态。
        ///
        /// 解除只允许解除**自己开的那次**暂停：选卡/商店/暂停菜单也走 `_paused`，
        /// 不加这道判断的话一个空格就能把三选一面板背后的世界解冻。
        /// </summary>
        private void HandleStrategicPauseInput()
        {
            if (!InputRouter.ConsumeKeyDown(KeyCode.Space, InputScope.Strategy))
            {
                return;
            }

            if (!_paused)
            {
                SetPaused(true, strategic: true);
            }
            else if (_strategicPause)
            {
                SetPaused(false);
            }
        }

        /// <summary>M2-01：镜头状态机。<see cref="SetupSim"/> 之后绑定——它要读场地半径做边界。</summary>
        private void SetupCameraDirector()
        {
            _cameraDirector = new CameraDirector();
            _cameraDirector.Bind(_camera, _sim, _cameraFollowOffset, _sim.ArenaHalfExtent);
            // 战略视角会放下意识，所以回直控前必须先重新接管一具，见 ReacquireDirectTarget。
            _cameraDirector.EnsureDirectTarget = ReacquireDirectTarget;
            _squadCommands = new SquadCommandSystem();
            _squadCommands.Bind(_sim, _camera);

            // M2-04a：接管交还可靠性。必须排在 _squadCommands 之后——它要在交还那一刻
            // 读编队归属；也必须与 CameraDirector / SquadCommandSystem 同样**不进 _hub**：
            // 控制权在暂停下也可能变（死亡回弹、读档恢复），_hub 会被暂停早退整个冻住。
            _aiHandoff = new Control.AiHandoffSystem();
            _aiHandoff.Bind(_sim, _squadCommands, DataRegistry.Instance.ArchetypeArray());

            // M2-02 白模叠加层：选择框 / 选中环 / 命令指示线。
            // 刻意不挂 DontDestroyOnLoad——那在 Edit 模式必抛，而回归测试会直接 Enter() 本阶段。
            var overlayGo = new GameObject("__SquadOverlay");
            _squadOverlay = overlayGo.AddComponent<WhiteboxSquadOverlay>();
            _squadOverlay.Bind(_sim, _squadCommands);
            // M2-04a 调试显示挂在同一个 GO 上：Exit 整个销毁它，不多一条要单独清理的路径。
            _handoffOverlay = overlayGo.AddComponent<Battle.Feedback.WhiteboxAiHandoffOverlay>();
            _handoffOverlay.Bind(_sim, _aiHandoff);
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
            _metabolicBridge = _hub.Register(new MetabolicSliceBridge());
            _abilities = _hub.Register(new AbilitySystem());
            _cards = _hub.Register(new CardTriggerBus());
            // story-010：结构器官触发钩子，与 Cards 相邻的独立轨道（不进攻击器官链）。
            _structuralHooks = _hub.Register(new StructuralHookRunner());
            _wallet = _hub.Register(new ResourceWallet());
            _surgicalRewards = _hub.Register(new SurgicalRewardLedger());
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
            // M3-02：蓝图库领域模型，与图鉴同一节奏（OnEnter 载入/迁移，OnExit 整份覆盖落盘）。
            _blueprints = _hub.Register(new MetabolicSlice.Blueprint.BlueprintRegistry());
            // M3-03：谱系/表型模板，架在蓝图库之上（纯内存，本期不落盘，见类型注释）。
            _lineages = _hub.Register(new MetabolicSlice.Lineage.LineageRegistry());
            // M3-05：萌生腔与新生传播——生物质账本 + 萌生队列，架在谱系/表型之上。
            _biomassLedger = _hub.Register(new MetabolicSlice.Lineage.BiomassLedger());
            _germinationChambers = _hub.Register(new MetabolicSlice.Lineage.GerminationChamberRegistry());
            // M3-06：回巢改造——架在萌生腔绑定表之上，同一批 Bind 依赖，注册顺序不影响正确性
            // （Bind 才是真正接线的时机，见 SetupUnitLoadouts）。
            _homecomingRetrofit = _hub.Register(new MetabolicSlice.Lineage.HomecomingRetrofitService());
            // M3-07：野生器官与单体临时移植——实物轨，消费蓝图库（解析）/生物质账本（拆解），
            // 装/卸临时器官经 UnitLoadoutRegistry 新增的 SetTemporaryOrgan/ClearTemporaryOrgan。
            _wildOrgans = _hub.Register(new MetabolicSlice.WildOrgan.WildOrganRegistry());
            _digestion = _hub.Register(new MetabolicDigestionSystem());
            // 战斗反馈表现层（story-002）：白模默认实现，只订阅 Signals，无需 Bind 依赖。
            _hub.Register(new CombatFeedbackPresenter());
            // 技能施放表现层（story-010）：与上面并列，管施放瞬间本身而非命中结算。
            _hub.Register(new AbilityCastPresenter());
            // 区域可视化表现层（story-007）：白模圆盘，唯一需要直接 Bind(AreaZoneSystem) 的 Presenter——
            // AreaZoneSystem 没有生成/过期信号，只有逐帧维护的 Zones 列表。
            _zoneVisual = _hub.Register(new ZoneVisualPresenter());
            // 血条表现层（story-008）：与 ZoneVisualPresenter 同款直接 Bind，需要连续读 Health/Position。
            _healthBars = _hub.Register(new HealthBarPresenter());
            // 组合弹道表现层（story-004）：与 AbilityCastPresenter 同骨架，只订阅 ComposeCastSignal。
            _composeProjectilePresenter = _hub.Register(new ComposeProjectilePresenter());
            // story-010 J3：组合弹道瞄准指示器（装配预览）：独立 8 位池，订阅 CarrierActivatedEvent。
            _hub.Register(new ComposeAimIndicatorPresenter());
            // 任务二：玩家 Carrier 本体随装配变化，同款轮询 AssemblyVersion。
            _carrierBodyVisual = _hub.Register(new CarrierBodyVisualPresenter());
            // 结构器官复合渲染路径（story-003）：chassis + 最多 4 个独立挂件子物体，同款轮询脏版本号。
            _structuralVisual = _hub.Register(new StructuralVisualPresenter());
            // 培养皿液体地面：涟漪跟随玩家；质量档 EnvFluidBackground.CurrentQuality。
            _envFluid = _hub.Register(new EnvFluidBackground());
            // Dev：Snapshot → 可点选 GO；默认关，菜单 BinGames/功能美术/Dev 单位 GO 镜像。
            _devUnitGoMirror = _hub.Register(new Battle.Feedback.DevUnitGoMirror());

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
            _lineages.Bind(_blueprints);
            _abilities.Bind(_sim, _stats);
            _status.Bind(_sim);
            _metabolicBridge.Bind(_sim, _stats, _abilities);
            _zones.Bind(_sim, _status);
            _carrierBodyVisual.Bind(_sim);
            _structuralVisual.Bind(_sim);
            _envFluid.Bind(_sim);
            _zoneVisual.Bind(_zones);
            _healthBars.Bind(_sim, _stats);
            _wallet.Bind(_stats);
            _progression.Bind(_wallet);
            _cards.Bind(_deck, _abilities, _sim, _stats);
            _structuralHooks.Bind(_sim, _stats, _status, _metabolicBridge);
            _director.Bind(_sim, _stats, _deck);
            _timeline.Bind(_director);
            _events.Bind(_director, _timeline, _progression, _wallet);
            _devour.Bind(_sim, _stats, _wallet, _events, _outcome.Statistics, _zones, _minions, _structuralHooks);
            // M2-03b：动作集在这里 new、在 SetupUnitLoadouts 里 Bind——注册表要等 SetupSim 才存在，
            // 而输入层的引用必须在这一轮接线里就交出去（_player 之后不会再被重绑）。
            _directActions = new Control.DirectControlActions();
            _player.Bind(_sim, _stats, _abilities, _wallet, _camera, _directActions);
            _bossPhase.Bind(_sim);
            _shop.Bind(_wallet, _stats, _deck, _sim);
        }

        private void SetupSim()
        {
            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = DataRegistry.Instance.Global.UnitCapacity;
            cfg.ArenaHalfExtent = DataRegistry.Instance.Global.ArenaHalfExtent;
            cfg.HashCellSize = DataRegistry.Instance.Global.HashCellSize;
            cfg.BreachedDiscount = DataRegistry.Instance.Global.BreachedDiscount;
            cfg.CorrodedDiscount = DataRegistry.Instance.Global.CorrodedDiscount;

            _sim.Begin(cfg, DataRegistry.Instance.ArchetypeArray());
            _sim.SetPlayerStats(
                _stats.Get(StatId.MaxHealth),
                _stats.Get(StatId.MaxHealth),
                _stats.Get(StatId.Volume),
                _stats.Get(StatId.MoveSpeed));
            SetupUnitLoadouts();
            SpawnControlAllies();

            // M1-06：只有显式“恢复旧局”才把意识放回上次那具躯体。
            // 正常 StartCellStage 是一局全新的肉鸽：旧 LogicId 可能恰好命中新生成的友军，
            // 若无条件恢复就会跨局串体，并让玩家本体的 MetabolicSlice 自动开火被关闭。
            // 目标单位要等下一次 Step 才真正落地，所以这不是一次性成败——
            // RequestControlRestore 会在宽限期内每帧重试，期间控制状态是 Suspended 而不是丢失。
            // 存档里没有记录（首次游玩、旧版本存档、读盘失败）时它直接返回 false，
            // 世界保留自带的默认受控实体，不需要额外分支。
            if (_entryMode == CellStageEntryMode.Resume)
            {
                _sim.RequestControlRestore(ControlPersistence.Load());
            }

            // 轻障碍（story-009）：数据驱动随机布局，白模一次性生成。
            //
            // 2026-09-14：**M2-06 固定试玩不生成障碍**。布局是随机的，偶尔会压在固定靶
            // (11,-2) 身上，JobIntegrate 把它推出障碍边界——实测偏成 (10.97,-2.04)，
            // 自检里"固定目标坐标应稳定"那条因此会偶发失败（按构造就会，不是谁改坏的）。
            // 更要紧的是它会挡住 RTS 下令与手术路线，让每次试玩的场景都不一样；
            // 而这个场景存在的意义就是把变量控死。
            ObstacleSpec[] obstacles = _entryMode == CellStageEntryMode.ConsciousnessPlaytest
                ? System.Array.Empty<ObstacleSpec>()
                : ObstacleGenerator.Generate(cfg.ArenaHalfExtent);
            _sim.SetObstacles(obstacles);
            WhiteboxObstacleVisual.Spawn(obstacles);
            _envFluid.Spawn(cfg.ArenaHalfExtent);

            _renderer = new SimRenderer();
            _visuals = BuildVisuals();
            _renderer.Initialize(_visuals, cfg.UnitCapacity);
            _controlPresentationScope?.Dispose();
            _controlPresentationScope = new SignalScope()
                .On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
            _devUnitGoMirror?.Bind(_sim, _visuals);
            _devUnitGoMirror?.BindProjectileVisual(
                BuildConeCached(),
                ProjectileMaterial(),
                1.8f,
                new Color(1f, 0.95f, 0.35f, 1f));
        }

        /// <summary>
        /// M1 固定房间的两名真实友军。复用现有孢子/菌丝体召唤原型，未受控时继续执行
        /// PlayerMinion AI；与默认本体合计三名可控单位，出生点均在临时信号范围内。
        /// </summary>
        /// <summary>
        /// 可控友军共用的移动原型（2026-09-13 试玩反馈）。取 13「孢子仆从索敌」：accel 8，
        /// 与玩家本体的 <c>BehaviorArchetype.Default</c> 同值，所以三具可控身体的移动手感一致。
        ///
        /// **不去改内容表里 15 号原型的 accel**：那一行是给真·菌丝召唤物用的，
        /// 「固着」正是它的设计意图，为试玩需要去动共享内容会顺带改掉正常召唤物的行为。
        /// </summary>
        public const int ControlAllyArchetypeId = Control.ArchetypeLoadoutTable.SporeArchetypeId;

        /// <summary>可控友军共用的造型 id（2026-09-14）。见 <see cref="SpawnControlAllies"/> 里的说明。</summary>
        public const int ControlAllyVisualId = Control.ArchetypeLoadoutTable.SporeArchetypeId;

        /// <summary>
        /// 第 <paramref name="index"/> 名可控友军的**装配**取哪个原型（2026-09-14）。
        ///
        /// **M2-06 固定试玩里两名友军装配相同**：bin 的原话是「测试不是应该每个友方都一样的
        /// 器官和基因嘛」——这是对的，固定场景就该把变量控死，两具身体只要有任何可观察差异
        /// 就一定是 bug，不必再区分"这是设计还是故障"。移速、造型已经统一，装配是最后一项。
        ///
        /// **正常对局仍然一人一套**：M2-03 的验收点「接管不同单位打出不同的东西」是真能力，
        /// 由 <see cref="Control.ArchetypeLoadoutTable"/> 与自检 [15]/[16] 段守着，不因试玩场景统一而丢。
        /// 想在固定场景里验差异化时，把这里改回按 index 分派即可。
        /// </summary>
        private int AllyLoadoutArchetypeId(int index)
        {
            if (_entryMode == CellStageEntryMode.ConsciousnessPlaytest)
            {
                return Control.ArchetypeLoadoutTable.SporeArchetypeId;
            }
            return index == 0
                ? Control.ArchetypeLoadoutTable.SporeArchetypeId
                : Control.ArchetypeLoadoutTable.MyceliumArchetypeId;
        }

        private void SpawnControlAllies()
        {
            float health = _stats.Get(StatId.MaxHealth);
            float speed = _stats.Get(StatId.MoveSpeed);

            // M2-03a：Spawn 只是入队，实体 id 要等下一次 Step 才存在，
            // 所以装配登记按 LogicId 挂起，由 UnitLoadoutRegistry.ResolvePending 补登记。
            int sporeLogicId = _sim.NextLogicId();
            _sim.Spawn(new SpawnRequest
            {
                Position = new Unity.Mathematics.float2(-4f, 2f),
                Health = health,
                Radius = 0.8f,
                MaxSpeed = speed,
                // 与菌丝体那一具共用同一个移动原型，见 <see cref="ControlAllyArchetypeId"/>。
                ArchetypeId = ControlAllyArchetypeId,
                Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI,
                LogicId = sporeLogicId,
                VisualId = ControlAllyVisualId,
            });
            _unitLoadouts?.RegisterArchetypePending(sporeLogicId, AllyLoadoutArchetypeId(0));

            int myceliumLogicId = _sim.NextLogicId();
            _sim.Spawn(new SpawnRequest
            {
                Position = new Unity.Mathematics.float2(4f, 2f),
                Health = health,
                Radius = 0.8f,
                MaxSpeed = speed,
                // 2026-09-13 试玩反馈：**行为原型只管"怎么动"，不该顺带决定这具身体是谁**。
                // 菌丝体的原型 15「菌丝体固着」是 Stationary + accel 0（内容表里给真·菌丝召唤物用的，
                // 它本来就该是钉在地上的锚），而 JobIntegrate 会把 accel 夹到 0.01 —— 于是接管它时
                // 推方向几乎不产生速度，就是玩家报的「移动奇慢无比」。
                //
                // 可控友军之间的差异按产品口径**只应来自装配的器官**（GDD §6.5/6.6），移动是共有能力
                // （M2-03a 已经立过同一条规矩：Move 不由器官提供）。所以这里两名友军统一用同一个
                // 会动的原型，**只有 VisualId 与装配登记保持各自不同**：外形用来分辨谁是谁，
                // 器官决定能打出什么，移动不再是区分项。
                ArchetypeId = ControlAllyArchetypeId,
                Faction = SimFaction.PlayerMinion,
                IntentSource = IntentSource.AI,
                LogicId = myceliumLogicId,
                // 2026-09-14：**两名友军用同一个模型**。玩家问「怎么模型不一样，装了不一样的
                // 器官基因？」——按产品口径（GDD §6.5/6.6）单位差异只应来自装配的器官，
                // 造型是表型层的事，现在这个阶段让它参与区分只会误导人以为"这两个是不同兵种"。
                // 需要分辨谁是谁走 Hierarchy 的 #UID（DevUnitGoMirror），不靠外形。
                VisualId = ControlAllyVisualId,
            });
            _unitLoadouts?.RegisterArchetypePending(myceliumLogicId, AllyLoadoutArchetypeId(1));

            // 2026-09-14 试玩反馈：「存在友方一直移动的角色，我并未下令他自己移动干嘛」。
            // 友军出生是 IntentSource.AI，原型 13 的 MinionSeekAttack 在索敌半径(8)内找不到
            // Hostile 时会走 `Wander(i, Time) * arc.WanderStrength`（JobSteering.cs:186-188），
            // wander 强度 0.5 —— 于是它**半速随机漫游**。这同时解释了"一直自己动"和"看着很慢"
            // （半速 + 方向乱晃，观感比直控慢得多，但 MaxSpeed 其实一样）。
            // M2-06 固定靶在 (11,-2)，离两名友军都超过 8，所以它们永远进不了索敌、只会一直晃。
            //
            // 口径统一成：**友军没收到命令就待在原地**（和放手后的原地守备同一条规矩）。
            // 守备不接管战斗，进入 AggroRange 的敌人照打。
            _pendingAllyHolds.Add(sporeLogicId);
            _pendingAllyHolds.Add(myceliumLogicId);
            _pendingAllyHoldFrames = 0;
        }

        /// <summary>
        /// 回归测试直调入口：Edit 模式跑不了完整 <see cref="Update"/>（相机/UI/Destroy 语义都不成立），
        /// 但"出生即原地待命"必须有断言守着——它是玩家两轮都报到的问题。
        /// 返回还没解析到实体 id 的友军条数。
        /// </summary>
        public int DebugResolveAllyHolds()
        {
            ResolvePendingAllyHolds();
            return _pendingAllyHolds.Count;
        }

        /// <summary>出生即原地待命的待解析友军 LogicId。Spawn 只是入队，实体 id 要下一次 Step 才存在。</summary>
        private readonly List<int> _pendingAllyHolds = new List<int>(2);
        private int _pendingAllyHoldFrames;
        private readonly SimEntityId[] _allyHoldTarget = new SimEntityId[1];

        /// <summary>出生即待命的解析上限帧数。同 <c>UnitLoadoutRegistry.MaxResolveAttempts</c> 的理由：
        /// 出生即死的项不该被无限扫下去。</summary>
        private const int MaxAllyHoldResolveFrames = 120;

        /// <summary>
        /// 把"出生即原地待命"落到刚解析出实体 id 的友军身上。
        /// 只在待解析表非空时扫一遍快照（正常只有开局那几帧、最多 2 条），
        /// 稳态代价是一次 Count == 0 判断——与 <c>UnitLoadoutRegistry.ResolvePending</c> 同一约定。
        /// </summary>
        private void ResolvePendingAllyHolds()
        {
            if (_pendingAllyHolds.Count == 0 || _sim == null || !_sim.Running)
            {
                return;
            }
            if (++_pendingAllyHoldFrames > MaxAllyHoldResolveFrames)
            {
                _pendingAllyHolds.Clear();
                return;
            }

            BinGames.Sim.SimSnapshot snapshot = _sim.Snapshot;
            for (int i = 0; i < snapshot.Count && _pendingAllyHolds.Count > 0; i++)
            {
                if (!snapshot.IsAlive(i))
                {
                    continue;
                }
                int slot = _pendingAllyHolds.IndexOf(snapshot.LogicId[i]);
                if (slot < 0)
                {
                    continue;
                }

                _allyHoldTarget[0] = snapshot.EntityId[i];
                _sim.IssueCommand(_allyHoldTarget, new UnitCommand
                {
                    Kind = UnitCommandKind.Guard,
                    TargetPosition = snapshot.Position[i],
                    TargetEntity = SimEntityId.None,
                    ArriveRadius = Control.AiHandoffSystem.BufferArriveRadius,
                });
                _pendingAllyHolds.RemoveAt(slot);
            }
        }

        /// <summary>
        /// M2-03a：装配按实体归属。必须排在 <see cref="SpawnControlAllies"/> 之前——
        /// 那里要往注册表里挂延迟登记项。
        ///
        /// 玩家本体取 <c>_sim.ControlledUnitId</c>：<c>SimWorld.Initialize</c> 把槽位 0 的实体
        /// 直接设成受控实体，而本行发生在 <c>RequestControlRestore</c> **之前**，
        /// 所以此刻它必然还是本体，不会误把上一局记住的那具躯体登记成"玩家本体"。
        /// </summary>
        private void SetupUnitLoadouts()
        {
            _unitLoadouts = new Control.UnitLoadoutRegistry();
            _unitLoadouts.Bind(_sim, new Control.MetabolicSlicePlayerLoadoutSource());
            _unitLoadouts.RegisterPlayerBody(_sim.ControlledUnitId);

            // M2-03b：动作集绑在注册表之后，且此刻就编译一次——玩家本体已经登记，
            // 开局第一帧按键就该有反应，不必等到第一次切换控制权才有动作集。
            _directActions?.Bind(_sim, _unitLoadouts, _abilities, _status);

            // M3-05：萌生腔要往 _unitLoadouts 挂延迟登记项，必须排在它创建之后。
            _germinationChambers?.Bind(_sim, _lineages, _biomassLedger, _unitLoadouts);
            // M3-06：回巢改造完成后要重挂 _unitLoadouts 装配、读萌生腔绑定表，同样排在两者之后。
            _homecomingRetrofit?.Bind(_sim, _lineages, _biomassLedger, _unitLoadouts, _germinationChambers);
            // M3-07：临时移植要读/写 _unitLoadouts 的临时器官槽，同样排在它创建之后。
            _wildOrgans?.Bind(_sim, _unitLoadouts);
        }

        /// <summary>
        /// 功能美术运行时覆盖（story-005）：加载完 catalog 后依次处理 player/organ/summon/enemy
        /// 四类槽位，原地改写 <see cref="_visuals"/> 数组元素——<c>SimVisual</c> 是 struct 但
        /// <c>SimVisual[]</c> 是引用类型数组，<see cref="SimRenderer"/> 只存了这个数组的引用（不复制），
        /// 所以这里 <c>arr[i].Mesh = x</c> 原地写完，下一帧 <see cref="SimRenderer.Draw"/> 自动读到新值，
        /// 不需要任何新增 SimRenderer/AOT 公共方法。<see cref="_visuals"/> 判空贯穿每一步 await 之后——
        /// Exit() 可能在任意一次 await 期间把它置 null（阶段切出竞态防护，见 Exit()）。
        /// </summary>
        private async UniTaskVoid ApplyFeatureArtVisualsAsync()
        {
            await FeatureArtResolver.LoadAsync();
            if (_visuals == null)
            {
                return;
            }

            await ApplyPlayerChassisSlots();
            if (_visuals == null)
            {
                return;
            }

            await ApplyOrganSlots();
            if (_visuals == null)
            {
                return;
            }

            await ApplySummonSlot("summon.spore.mesh", ArtBinding.FeatureArtVisualBinder.SummonSporeVisualId);
            if (_visuals == null)
            {
                return;
            }
            await ApplySummonSlot("summon.phage.mesh", ArtBinding.FeatureArtVisualBinder.SummonPhageVisualId);
            if (_visuals == null)
            {
                return;
            }
            await ApplySummonSlot("summon.mycelium.mesh", ArtBinding.FeatureArtVisualBinder.SummonMyceliumVisualId);
            if (_visuals == null)
            {
                return;
            }

            await ApplyEnemySlots();
            if (_visuals == null)
            {
                return;
            }

            // story-006：VFX Prefab 池加载——沿用 _visuals==null 作为"阶段是否已 Exit"哨兵（借用同一判空
            // 信号，不新增字段），加载完成后由 WhiteboxComposeProjectileFeedback.Dispose() 经 OnExit() 自行清理。
            await _composeProjectilePresenter.LoadArtBindingsAsync();
        }

        /// <summary>(a) player：player.chassis.mesh 覆盖 <c>VisualIdForArtId("carrier/base")</c> 下标
        /// （不是数组下标 0——那只是敌人占位段的玩家占位，会被 CarrierBodyVisualPresenter 每帧轮询覆盖回去，
        /// 见 preflight-decisions 已核实事实）；player.chassis.material 要求支持 GPU Instancing 才覆盖。</summary>
        private async UniTask ApplyPlayerChassisSlots()
        {
            if (FeatureArtResolver.TryGetSlot("player.chassis.mesh", out ArtBinding.FeatureArtSlot meshSlot)
                && !string.IsNullOrEmpty(meshSlot.location))
            {
                ArtBinding.FeatureArtVisualBinder.MeshLoadResult result =
                    await ArtBinding.FeatureArtVisualBinder.TryLoadInstancedMesh(meshSlot.location, _loadedArtAssets);
                if (_visuals == null)
                {
                    return;
                }
                if (result.Ok)
                {
                    ApplyLoadedMeshToArtId("carrier/base", result);
                }
            }

            if (FeatureArtResolver.TryGetSlot("player.chassis.material", out ArtBinding.FeatureArtSlot matSlot)
                && !string.IsNullOrEmpty(matSlot.location))
            {
                ArtBinding.FeatureArtVisualBinder.MaterialLoadResult result =
                    await ArtBinding.FeatureArtVisualBinder.TryLoadMaterialOverride(matSlot.location, _loadedArtAssets);
                if (_visuals == null)
                {
                    return;
                }
                if (result.Ok)
                {
                    int vid = VisualIdForArtId("carrier/base");
                    if (vid >= 0)
                    {
                        _visuals[vid].Material = result.Material;
                    }
                }
            }
        }

        /// <summary>(b) organ：遍历 OrganelleCatalog.All，跳过退役/无 ArtId，查 organ.{id}.mesh 槽覆盖。</summary>
        private async UniTask ApplyOrganSlots()
        {
            foreach (var kv in GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.All)
            {
                var def = kv.Value;
                if (def.IsRetired || def.ArtId == null
                    || def.Category == GameLogic.MetabolicSlice.ContentCatalog.OrganelleCategory.Structural)
                {
                    continue;
                }
                if (!FeatureArtResolver.TryGetSlot($"organ.{def.Id}.mesh", out ArtBinding.FeatureArtSlot slot)
                    || string.IsNullOrEmpty(slot.location))
                {
                    continue;
                }

                ArtBinding.FeatureArtVisualBinder.MeshLoadResult result =
                    await ArtBinding.FeatureArtVisualBinder.TryLoadInstancedMesh(slot.location, _loadedArtAssets);
                if (_visuals == null)
                {
                    return;
                }
                if (!result.Ok)
                {
                    continue;
                }

                // 沙盒对比台 / 非 Carrier 路径：按 OrganelleDef.ArtId 覆盖。
                ApplyLoadedMeshToArtId(def.ArtId, result);

                // 局内玩家外形走 CarrierBodyVisualPresenter：org_emitter/cilia 用 carrier/*，
                // 其余 Carrier 用 org/*，并可能带 ::suffix。必须同步覆盖这些 VisualId，
                // 否则只换了沙盒架上的器官白模，正常对局里玩家仍是白模。
                foreach (string carrierArtId in CarrierVisualArtIdsForOrgan(def.Id, def.ArtId))
                {
                    ApplyLoadedMeshToArtId(carrierArtId, result);
                }
            }
        }

        /// <summary>把已加载的 mesh/材质/ScaleMul 写进 <see cref="_visuals"/> 对应 ArtId 槽。</summary>
        private void ApplyLoadedMeshToArtId(string artId, ArtBinding.FeatureArtVisualBinder.MeshLoadResult result)
        {
            if (_visuals == null || string.IsNullOrEmpty(artId) || !result.Ok)
            {
                return;
            }

            int vid = VisualIdForArtId(artId);
            if (vid < 0)
            {
                return;
            }

            _visuals[vid].Mesh = result.Mesh;
            _visuals[vid].ScaleMul = result.ScaleMul;
            if (result.Material != null)
            {
                _visuals[vid].Material = result.Material;
            }
        }

        /// <summary>Carrier 局内实际会切到的 ArtId 列表（含 gene marker 后缀变体）。
        /// 与 <see cref="GameLogic.Battle.Feedback.CarrierBodyVisualPresenter"/> /
        /// <see cref="SimVisualLibrary.AllArtIds"/> 对齐：emitter/cilia 用 carrier/ 前缀，其余用 org ArtId。</summary>
        private static List<string> CarrierVisualArtIdsForOrgan(string organelleId, string artId)
        {
            var ids = new List<string>(5);
            string carrierBase = organelleId switch
            {
                "org_emitter" => "carrier/emitter",
                "org_cilia" => "carrier/cilia",
                _ => artId,
            };

            if (string.IsNullOrEmpty(carrierBase))
            {
                return ids;
            }

            // 自身已在 ApplyLoadedMeshToArtId(def.ArtId) 写过时勿重复也无妨（同 mesh 引用）。
            if (!string.Equals(carrierBase, artId, StringComparison.Ordinal))
            {
                ids.Add(carrierBase);
            }

            string[] suffixes = { "relay", "transform", "edge", "contract" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                ids.Add(carrierBase + "::" + suffixes[i]);
            }

            return ids;
        }

        /// <summary>(c) summon：三个固定 VisualId（13/14/15），不走 VisualIdForArtId——那是沙盒对比台的
        /// 冗余 100+ 段登记，不是运行时真正生成的召唤物用的下标（见 preflight-decisions 已核实事实）。</summary>
        private async UniTask ApplySummonSlot(string slotId, int visualId)
        {
            if (!FeatureArtResolver.TryGetSlot(slotId, out ArtBinding.FeatureArtSlot slot)
                || string.IsNullOrEmpty(slot.location))
            {
                return;
            }

            ArtBinding.FeatureArtVisualBinder.MeshLoadResult result =
                await ArtBinding.FeatureArtVisualBinder.TryLoadInstancedMesh(slot.location, _loadedArtAssets);
            if (_visuals == null || !result.Ok)
            {
                return;
            }
            _visuals[visualId].Mesh = result.Mesh;
            _visuals[visualId].ScaleMul = result.ScaleMul;
            if (result.Material != null)
            {
                _visuals[visualId].Material = result.Material;
            }
        }

        /// <summary>(d) enemy：遍历 FeatureArtVisualBinder.EnemyVisualFamilies（16 条），查
        /// enemy.{key}.mesh，VisualId 直接用表里的值。</summary>
        private async UniTask ApplyEnemySlots()
        {
            foreach (var family in ArtBinding.FeatureArtVisualBinder.EnemyVisualFamilies)
            {
                if (!FeatureArtResolver.TryGetSlot($"enemy.{family.Key}.mesh", out ArtBinding.FeatureArtSlot slot)
                    || string.IsNullOrEmpty(slot.location))
                {
                    continue;
                }

                ArtBinding.FeatureArtVisualBinder.MeshLoadResult result =
                    await ArtBinding.FeatureArtVisualBinder.TryLoadInstancedMesh(slot.location, _loadedArtAssets);
                if (_visuals == null)
                {
                    return;
                }
                if (!result.Ok)
                {
                    continue;
                }
                _visuals[family.VisualId].Mesh = result.Mesh;
                _visuals[family.VisualId].ScaleMul = result.ScaleMul;
                if (result.Material != null)
                {
                    _visuals[family.VisualId].Material = result.Material;
                }
            }
        }

        /// <summary>
        /// 白模视觉。正式美术接入前用颜色分层保证可读性
        /// （Spec §16"万敌规模下可读性"风险项的第一道对策）。
        /// </summary>
        /// <summary>任务二（3D 表现差异化）：器官/代谢模块/基元/Carrier 装配挂件的 VisualId 起点。
        /// 0-99 段是既有敌人/玩家/残块/精英/首领占位，保持不动；召唤物（13/14/15）复用行为原型 id
        /// 本身（EffectSpawn 用 SpawnEnemyId 同时当 ArchetypeId 与 VisualId），也落在这一段内。</summary>
        public const int ArtVisualIdBase = 100;

        /// <summary>按 <see cref="SimVisualLibrary.AllArtIds"/> 顺序查 VisualId；未注册的 ArtId 回退 -1
        /// （渲染层 <see cref="BinGames.Sim.SimRenderer.Draw"/> 对越界 VisualId 会自动回落到槽位 0）。</summary>
        public static int VisualIdForArtId(string artId)
        {
            int idx = System.Array.IndexOf(SimVisualLibrary.AllArtIds, artId);
            return idx < 0 ? -1 : ArtVisualIdBase + idx;
        }

        private static SimVisual[] BuildVisuals()
        {
            Mesh sphereMesh = BuildSphere(8, 12, 0.5f);
            Mesh capsuleMesh = BuildCapsule(0.32f, 0.45f, 6, 10);
            Mesh squashMesh = BuildSquashedSphere(0.5f, 0.15f, 8, 12);
            Material mat = CreateSimMaterial(Color.white);

            var visuals = new SimVisual[ArtVisualIdBase + SimVisualLibrary.AllArtIds.Length];
            for (int i = 0; i < ArtVisualIdBase; i++)
            {
                Mesh mesh = sphereMesh;
                float scaleMul = 1f;
                switch (i)
                {
                    case 0:
                        mesh = capsuleMesh;
                        break;
                    // 召唤机制（任务三）：孢子仆从/噬菌体/菌丝体，行为原型 id 13/14/15 直接复用为 VisualId。
                    case 13:
                        mesh = SimVisualLibrary.BuildForArtId("summon/spore");
                        break;
                    case 14:
                        mesh = SimVisualLibrary.BuildForArtId("summon/phage");
                        break;
                    case 15:
                        mesh = SimVisualLibrary.BuildForArtId("summon/mycelium");
                        break;
                    case 20:
                        mesh = squashMesh;
                        break;
                    case 50:
                    case 51:
                    case 52:
                        mesh = capsuleMesh;
                        scaleMul = 1.3f;
                        break;
                    case 90:
                        mesh = capsuleMesh;
                        scaleMul = 1.8f;
                        break;
                }

                visuals[i] = new SimVisual
                {
                    Mesh = mesh,
                    Material = mat,
                    ScaleMul = scaleMul,
                    BaseColor = ColorFor(i),
                };
            }

            // 100+：24 器官/代谢模块 + 4 基元 + 3 召唤物（冗余登记，供沙盒对比台按 VisualId 直查）
            // + 5 Carrier 装配挂件，按 SimVisualLibrary.AllArtIds 声明序铺开。
            for (int j = 0; j < SimVisualLibrary.AllArtIds.Length; j++)
            {
                string artId = SimVisualLibrary.AllArtIds[j];
                visuals[ArtVisualIdBase + j] = new SimVisual
                {
                    Mesh = SimVisualLibrary.BuildForArtId(artId),
                    Material = mat,
                    ScaleMul = 1f,
                    BaseColor = ArtVisualColor(artId),
                };
            }
            return visuals;
        }

        /// <summary>造型库条目的基线着色——形状是主要区分手段（任务二要求"形状可辨"），
        /// 颜色只按大类粗分，避免 24 种器官强行凑 24 种独立配色反而互相干扰。</summary>
        private static Color ArtVisualColor(string artId)
        {
            if (artId.StartsWith("org/")) { return new Color(0.55f, 0.92f, 0.68f, 1f); }
            if (artId.StartsWith("prim/energy")) { return new Color(1.00f, 0.92f, 0.35f, 1f); }
            if (artId.StartsWith("prim/mass")) { return new Color(0.72f, 0.72f, 0.78f, 1f); }
            if (artId.StartsWith("prim/light")) { return new Color(0.95f, 0.98f, 1.00f, 1f); }
            if (artId.StartsWith("prim/heat")) { return new Color(1.00f, 0.48f, 0.22f, 1f); }
            if (artId.StartsWith("summon/")) { return new Color(0.78f, 0.62f, 1.00f, 1f); }
            if (artId.StartsWith("carrier/")) { return new Color(0.35f, 0.98f, 0.72f, 1f); }
            return new Color(0.70f, 0.78f, 0.72f, 1f);
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
                mat.SetFloat("_ImpactSquash", 0.48f);
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

        /// <summary>弹体锥体。锥尖在局部 +X（DrawProjectiles 的旋转把 +X 转到飞行方向），底面圆环在局部 YZ 平面。</summary>
        private static Mesh BuildCone(float radius, float halfLength, int segments)
        {
            var vertices = new List<Vector3> { new Vector3(halfLength, 0f, 0f), new Vector3(-halfLength, 0f, 0f) };
            var uvs = new List<Vector2> { new Vector2(0.5f, 1f), new Vector2(0.5f, 0f) };

            int ringStart = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments * Mathf.PI * 2f;
                vertices.Add(new Vector3(-halfLength, radius * Mathf.Cos(t), radius * Mathf.Sin(t)));
                uvs.Add(new Vector2(i / (float)segments, 0f));
            }

            var triangles = new List<int>();
            const int tip = 0;
            const int baseCenter = 1;
            for (int i = 0; i < segments; i++)
            {
                int a = ringStart + i;
                int b = ringStart + i + 1;
                // 侧面：锥尖 -> 底环，外法线朝外
                triangles.Add(tip); triangles.Add(a); triangles.Add(b);
                // 底面：圆心 -> 底环，缠绕方向相反，外法线朝 -X
                triangles.Add(baseCenter); triangles.Add(b); triangles.Add(a);
            }

            var m = new Mesh { name = "SimCone" };
            m.SetVertices(vertices);
            m.SetUVs(0, uvs);
            m.SetTriangles(triangles, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>UV 球体（本体/一般敌人）。三轴等比缩放，形状烘焙在 local 顶点里。</summary>
        private static Mesh BuildSphere(int lat, int lon, float radius)
        {
            return BuildEllipsoid(radius, radius, lat, lon, "SimSphere");
        }

        /// <summary>竖高胶囊体（玩家/精英/首领），上下半球 + 中段圆柱。</summary>
        private static Mesh BuildCapsule(float radius, float cylHalfHeight, int lat, int lon)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            int ring = lon + 1;
            int totalRings = 2 * lat + 2;

            for (int r = 0; r < totalRings; r++)
            {
                bool top = r <= lat;
                int y = top ? r : r - lat - 1;
                float phi = top
                    ? y / (float)lat * (Mathf.PI * 0.5f)
                    : Mathf.PI * 0.5f - y / (float)lat * (Mathf.PI * 0.5f);
                float ringRadius = radius * Mathf.Sin(phi);
                float ringY = top
                    ? cylHalfHeight + radius * Mathf.Cos(phi)
                    : -cylHalfHeight - radius * Mathf.Cos(phi);
                float v = r / (float)(totalRings - 1);

                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float theta = u * Mathf.PI * 2f;
                    vertices.Add(new Vector3(ringRadius * Mathf.Cos(theta), ringY, ringRadius * Mathf.Sin(theta)));
                    uvs.Add(new Vector2(u, 1f - v));
                }
            }

            for (int r = 0; r < totalRings - 1; r++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = r * ring + x;
                    int b = a + ring;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }

            var m = new Mesh { name = "SimCapsule" };
            m.SetVertices(vertices);
            m.SetUVs(0, uvs);
            m.SetTriangles(triangles, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>压扁球体（残块），XZ 半径远大于 Y 半径，读作"矮但有体积"。</summary>
        private static Mesh BuildSquashedSphere(float radiusXZ, float radiusY, int lat, int lon)
        {
            return BuildEllipsoid(radiusXZ, radiusY, lat, lon, "SimSquashedSphere");
        }

        private static Mesh BuildEllipsoid(float radiusXZ, float radiusY, int lat, int lon, string name)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            for (int y = 0; y <= lat; y++)
            {
                float v = y / (float)lat;
                float theta = v * Mathf.PI;
                float sinTheta = Mathf.Sin(theta);
                float cosTheta = Mathf.Cos(theta);

                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float phi = u * Mathf.PI * 2f;
                    float sinPhi = Mathf.Sin(phi);
                    float cosPhi = Mathf.Cos(phi);
                    vertices.Add(new Vector3(radiusXZ * sinTheta * cosPhi, radiusY * cosTheta, radiusXZ * sinTheta * sinPhi));
                    uvs.Add(new Vector2(u, 1f - v));
                }
            }

            int ring = lon + 1;
            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = y * ring + x;
                    int b = a + ring;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }

            var m = new Mesh { name = name };
            m.SetVertices(vertices);
            m.SetUVs(0, uvs);
            m.SetTriangles(triangles, 0);
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

            // 召唤共生体（任务三：召唤机制）：旧的 Route/Card→grantAbility 抽卡池已被
            // metabolic-playerization-004 整体 Delist（carddata.py 头注释），28 个旧
            // AbilitySpec 里只有 dash 还能通过正常流程拿到。新技能没有可用的抽卡入口，
            // 照 dash 的先例直接常驻发放，保证"看得到/验得出"可在任意一局立即验收。
            AbilitySpec summon = DataRegistry.Instance.GetAbility(29);
            if (summon != null)
            {
                _abilities.Grant(summon);
            }
        }

        public void Update(float dt)
        {
            if (!_running)
            {
                return;
            }

            // M2-01：镜头必须在暂停早退**之前**驱动。战略视角存在的意义之一就是暂停下选择目标，
            // 而下面的 `_paused` 早退会把整个 _hub 连同原来内联的 FollowCamera 一起冻住。
            // CameraDirector 因此刻意不是 GameModule——它要在玩法冻结时继续工作。
            //
            // 每帧同步暂停态而不是在 6 个 `_paused` 写入点逐个接线：漏掉任何一个（选卡跳过、
            // GM 调试、放弃本局）都会让输入永久卡在让位状态，而那种 bug 只在特定路径下才复现。
            HandleStrategicPauseInput();
            InputRouter.SetGameplayPaused(_paused, _strategicPause);
            _cameraDirector?.Tick(_paused);
            ParkControlOnStrategyView();
            ResolvePendingAllyHolds();
            // M2-02：选择与命令同样要在暂停早退之前——"暂停下令后恢复顺序稳定"是它的验收项，
            // 而下令这件事本身必须在冻结期间还能发生。
            _squadCommands?.Tick(_paused);
            // M2-03a：把"生成时还拿不到实体 id"的装配登记补上。挂起表空时它一行都不扫，
            // 稳态代价是一次 Count == 0 判断——不违反"热更层每帧与敌人数无关"。
            // M2-03b：延迟登记刚落地的那一帧，受控实体的动作集可能还是按"未登记"编译出来的
            // （接管发生在登记落地之前）。只在真的补登记了条目时重建一次，不是每帧重建。
            if (_unitLoadouts != null && _unitLoadouts.ResolvePending(_sim.Snapshot) > 0)
            {
                _directActions?.Rebuild();
            }

            // M2-03c：代谢 / 热债 / 冷却的本地时钟。纯 O(1)（只加一个 float，不遍历实体），
            // 且带着暂停标志——暂停下刷冷却、回代谢是白送的。
            // 放在暂停早退**之前**、由 Tick 内部判暂停，是为了与上面几行保持同一种写法：
            // 暂停语义集中在被调用方，不在这里堆第二个早退分支。
            _directActions?.Tick(dt, _paused);

            // M2-04a：接管缓冲的本地时钟 + 受控单位的安全位置兜底。
            // 与上面几行同一种写法（暂停语义在被调用方），且同样与场上单位数无关：
            // 缓冲计时是"记录时刻 + 惰性判断"（记录数 ≤ 8），安全位置只校验**当前受控的那一个**单位。
            _aiHandoff?.Tick(dt, _paused);

            // 选卡时暂停玩法推进，但不暂停 UI
            if (_paused)
            {
                return;
            }

            _hub.Update(dt);

            TrackPeakEnemies();
            CheckDraft();
            CheckEnd();

            // Dev GO 镜像开启时抑制单位+内核弹道 Instanced，避免双影（区/障碍等本已是 GO）。
            if (!Battle.Feedback.DevUnitGoMirror.SuppressInstancedDraw)
            {
                // combat-primitive-presentation story-004（COMBAT-PRESENTATION §4）：近战整体前冲-回弹，
                // 纯渲染位移写进 SimRenderer，不改 Position/碰撞。零近战攻击历史时 Progress 恒为 0，
                // Draw() 内直接跳过位移分支，零回归。
                if (_composeProjectilePresenter != null && _renderer != null)
                {
                    var (lungeDir, lungeProgress) = _composeProjectilePresenter.GetMeleeLunge();
                    _renderer.SetControlledLunge(lungeDir, lungeProgress);
                }
                _renderer?.Draw(_sim.Snapshot);
                if (_sim.World != null)
                {
                    // 加大加亮 + SimRenderer 内的方向拉长（story-010 V1）：默认半径下的弹体太小太暗。
                    _renderer?.DrawProjectiles(_sim.World.Projectiles, new SimVisual
                    {
                        Mesh = BuildConeCached(),
                        Material = ProjectileMaterial(),
                        ScaleMul = 1.8f,
                        BaseColor = new Color(1f, 0.95f, 0.35f),
                    });

                    // enemy-mechanics-parity：持续区域（毒坑/酸洼/敌人的贴身毒环）。
                    // 没有这一段的话敌人的毒圈就是"看不见的伤害区"——玩家站进去掉血却不知道为什么，
                    // 正是这条线一路在修的那类问题。半径直接取内核的实时值，区域涨多大画多大。
                    _renderer?.DrawZones(_sim.World.Zones, new SimVisual
                    {
                        Mesh = BuildZoneDiscCached(),
                        Material = ProjectileMaterial(),
                        ScaleMul = 1f,
                        BaseColor = new Color(0.45f, 0.9f, 0.5f, 0.55f),
                    });
                }
            }

        }

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            _renderer?.ClearControlledPresentation();
        }

        private Mesh _coneCache;
        private Material _projMat;

        private Mesh BuildConeCached() => _coneCache ??= BuildCone(0.5f, 0.5f, 10);

        /// <summary>持续区域用的圆盘。直接复用球体网格——<see cref="SimRenderer.DrawZones"/>
        /// 会把 Y 压扁成一片，所以不需要为它单独造一个平面网格。</summary>
        private Mesh BuildZoneDiscCached() => _zoneDiscCache ??= BuildSphere(6, 14, 0.5f);

        private Mesh _zoneDiscCache;

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

                    ApplyMetabolicContent(spec);
                }
            }

            PendingOptions = null;
            _paused = false;
        }

        /// <summary>
        /// 代谢化迁移（story-005）：Deck.Acquire 只管卡牌记账（叠层/去重/路线统计），
        /// 真正的玩法效果按 ContentKind 分流到 002 的囊（器官→PartInstance）或
        /// 003 的全局基因契约（Gene→Panel.GeneContracts），两条路都读同一个
        /// MetabolicSlicePanel.Instance——它是唯一的玩家状态持有者。
        /// </summary>
        private static void ApplyMetabolicContent(CardSpec spec)
        {
            if (spec.ContentKind == ContentKind.Organelle)
            {
                MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
                if (panel != null)
                {
                    var part = new PartInstance(System.Guid.NewGuid().ToString("N"), spec.ContentId, PartLocation.Bag());
                    if (panel.AddOrganPart(part) == AddResult.NeedDecision)
                    {
                        // 囊已满：v1 不打断选卡流程弹抉择 UI（那是 002 手动测试按钮的路径），
                        // 先记日志避免玩家困惑「明明选了卡，囊里却没有」。抉择 UI 留后续 story。
                        TEngine.Log.Warning($"[CellStageFlow] 囊已满，抽到的器官 {spec.ContentId} 未能入囊");
                    }
                }
            }
            else if (spec.ContentKind == ContentKind.Structural)
            {
                MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
                if (panel != null)
                {
                    var part = new PartInstance(System.Guid.NewGuid().ToString("N"), spec.ContentId, PartLocation.Bag());
                    if (panel.AddOrganPart(part) == AddResult.NeedDecision)
                    {
                        TEngine.Log.Warning($"[CellStageFlow] 囊已满，抽到的结构器官 {spec.ContentId} 未能入囊");
                    }
                }
            }
            else if (spec.ContentKind == ContentKind.Gene)
            {
                MetabolicSlicePanel.Instance?.AddGene(spec.ContentId);
            }
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

        /// <summary>
        /// GM：灌满体力方便连放测反馈。story-004（combat-visualization R3）收口：
        /// `DataRegistry.AllAbilities` 里 id=2~28（27 条）是敌人 AI 风格技能（骨刺投射/孢子爆发等），
        /// 从未进入正常掉落/选卡池，与 ComposeEngine 基元机制无关，不再强制授予进玩家战斗槽位；
        /// 唯一的 id=1（冲刺）与基元机制无关但保留，且已在 <see cref="GrantStarterAbilities"/>
        /// （<see cref="Enter"/> 时无条件调用）默认授予，这里不需要重复。返回值恒为 0，保留 int
        /// 签名是为了不改 <see cref="DebugGrantAllMetabolicItems"/> 调用处。
        /// </summary>
        public int DebugUnlockAllAbilities()
        {
            if (_abilities == null)
            {
                return 0;
            }

            float staminaMax = _stats != null ? _stats.Get(StatId.StaminaMax) : 100f;
            _wallet?.Add(ResourceKind.Stamina, staminaMax);

            TEngine.Log.Info(
                $"[GM] 体力已灌满（技能槽仅保留冲刺，机制无关技能已按 R3 移除，槽位 {_abilities.SlotCount}/1）");
            return 0;
        }

        /// <summary>
        /// GM：一键灌入全部代谢道具（基因储备 + Carrier 器官）并解锁全部技能（story-002，落地 001 R4）。
        /// 幂等：基因按 geneId 判重跳过（GeneReserve.TryAdd 本身不判重）；Carrier 器官改用稳定
        /// PartId（"gm_"+cardDefId）并在入囊前查 CarrierRegistry 是否已存在，避免重复按键堆叠出多份
        /// 同 cardDefId 的囊内条目（preflight-decisions.md story-002 D2/D3）。
        /// </summary>
        public void DebugGrantAllMetabolicItems()
        {
            MetabolicSlicePanel panel = MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            int genesGranted = 0;
            foreach (string geneId in GameLogic.MetabolicSlice.ContentCatalog.GeneCatalog.AllGeneIds)
            {
                bool alreadyOwned = false;
                IReadOnlyList<GameLogic.MetabolicSlice.Carrier.GeneInstance> reserveItems = panel.GeneReserve.Items;
                for (int i = 0; i < reserveItems.Count; i++)
                {
                    if (reserveItems[i].GeneId == geneId)
                    {
                        alreadyOwned = true;
                        break;
                    }
                }
                if (alreadyOwned)
                {
                    continue;
                }
                panel.AddGene(geneId);
                genesGranted++;
            }

            int carriersGranted = 0;
            foreach (var kv in GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.All)
            {
                GameLogic.MetabolicSlice.ContentCatalog.OrganelleDef def = kv.Value;
                // combat-identity-rework story-007（Required 3）：GM 全量授予改按 AttackMethod==true
                // 过滤（24 个攻击方式），旧修饰已收敛 IsCarrier=false 但显式改判据更贴合意图。
                if (!def.AttackMethod)
                {
                    continue;
                }
                string stablePartId = "gm_" + def.Id;
                if (panel.CarrierRegistry.GetCarrier(stablePartId) != null)
                {
                    continue;
                }
                var part = new PartInstance(stablePartId, def.Id, PartLocation.Bag());
                panel.AddOrganPart(part);
                carriersGranted++;
            }

            // R1③：把玩家当前拥有的每个 Carrier 插槽数补齐到能一次性装下全部 Module 基因，
            // 复用已有 AddSlot/软上限，不新增字段；while 循环 + AddSlot 达软上限 no-op 天然幂等。
            int moduleGeneCount = 0;
            foreach (string _ in GameLogic.MetabolicSlice.ContentCatalog.GeneCatalog.AllModuleIds)
            {
                moduleGeneCount++;
            }
            int slotTarget = System.Math.Min(moduleGeneCount, GameLogic.MetabolicSlice.Carrier.CarrierInstance.SlotSoftCap);
            foreach (var kv in panel.CarrierRegistry.All)
            {
                GameLogic.MetabolicSlice.Carrier.CarrierInstance carrier = kv.Value;
                while (carrier.Slots.Count < slotTarget)
                {
                    if (!carrier.AddSlot())
                    {
                        break;
                    }
                }
            }

            DebugUnlockAllAbilities();
            int totalCarrierCount = 0;
            foreach (var _ in panel.CarrierRegistry.All)
            {
                totalCarrierCount++;
            }
            int carrierOrganCount = 0;
            foreach (var kv in GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.All)
            {
                if (kv.Value.AttackMethod)
                {
                    carrierOrganCount++;
                }
            }

            TEngine.Log.Info(
                $"[GM] 基因 {panel.GeneReserve.Items.Count}/{moduleGeneCount}（本次 +{genesGranted}）"
                + $" ＋攻击器官 {totalCarrierCount}/{carrierOrganCount}（本次 +{carriersGranted}，插槽已补至 {slotTarget}）"
                + " ＋技能：仅冲刺（机制无关技能已按 R3 移除，体力已灌满）");
        }

        /// <summary>
        /// GM：在默认正交俯视态与调试透视态之间切换。
        /// </summary>
        public void DebugToggleCameraVerifyMode()
        {
            if (_camera == null)
            {
                return;
            }

            _cameraVerifyMode = !_cameraVerifyMode;

            Quaternion rot;
            if (_cameraVerifyMode)
            {
                _camera.orthographic = false;
                _camera.fieldOfView = VerifyFieldOfView;
                rot = Quaternion.Euler(VerifyPitchDegrees, 0f, 0f);
                _cameraFollowOffset = rot * new Vector3(0f, 0f, -VerifyViewDistance);
            }
            else
            {
                _camera.orthographic = true;
                _camera.orthographicSize = 16f;
                rot = DefaultCameraRotation;
                _cameraFollowOffset = DefaultCameraOffset;
            }

            _camera.transform.rotation = rot;

            if (_sim != null && _sim.TryGetPresentationAnchor(out Unity.Mathematics.float2 p, out _))
            {
                _camera.transform.position = new Vector3(
                    p.x + _cameraFollowOffset.x, _cameraFollowOffset.y, p.y + _cameraFollowOffset.z);
            }

            string mode = _cameraVerifyMode ? "调试态（透视）" : "默认态（俯视）";
            TEngine.Log.Info($"[GM] 相机切换 → {mode}");
        }

        /// <summary>沙盒木桩用的行为原型 id，对应 Luban 表 cell.BehaviorArchetype 新增第 12 行
        /// （kind=Stationary、attackDamage=0，见 sandbox-skill-editor/002 D1）。不进任何正常刷怪池
        /// （<see cref="SpawnDirector"/> 按内容表自身刷怪池选 id，不会引用该 id）。</summary>
        private const int SandboxDummyArchetypeId = 12;

        /// <summary>
        /// story-002：LookDev 沙盒进局一次性放置木桩。木桩只是一个 <see cref="SimFaction.Hostile"/>
        /// 静态单位，天然被现有 <see cref="MetabolicSliceBridge.ApplyEvent"/>→
        /// <see cref="SimBridge.DamageArea"/> 命中路径命中（该路径固定以 Hostile 为目标阵营、
        /// 发射原点固定 <see cref="SimBridge.PlayerPosition"/>），不新增伤害计算路径、不新增
        /// SimWorld 方法/字段。固定生成在玩家出生点附近（偏移量小于默认命中半径 4），不强求可拖拽。
        /// </summary>
        private void SpawnSandboxDummy()
        {
            Unity.Mathematics.float2 pos = _sim.PlayerPosition + new Unity.Mathematics.float2(0f, 2.5f);
            _sim.Spawn(new SpawnRequest
            {
                Position = pos,
                Health = 999999f,
                Radius = 1f,
                MaxSpeed = 0f,
                ArchetypeId = SandboxDummyArchetypeId,
                Faction = SimFaction.Hostile,
            });
        }

        public const int ConsciousnessPlaytestTargetLogicId = 20601;
        public const float ConsciousnessPlaytestTargetX = 11f;
        public const float ConsciousnessPlaytestTargetY = -2f;

        /// <summary>
        /// M2-06 固定试玩门的唯一敌对目标。它用真实身体接点与静止行为原型：高核心血量避免友军 AI
        /// 在观察结束前把目标击杀，两个低血量接点则保留真实直控精准切离路径。
        /// </summary>
        private void SpawnConsciousnessPlaytestTarget()
        {
            _sim.Spawn(new SpawnRequest
            {
                Position = new Unity.Mathematics.float2(
                    ConsciousnessPlaytestTargetX, ConsciousnessPlaytestTargetY),
                Health = 9999f,
                Radius = 2.2f,
                MaxSpeed = 0f,
                ArchetypeId = SandboxDummyArchetypeId,
                Faction = SimFaction.Hostile,
                IntentSource = IntentSource.AI,
                LogicId = ConsciousnessPlaytestTargetLogicId,
                VisualId = SandboxDummyArchetypeId,
                PrimaryPartMaxHealth = 40f,
                PrimaryPartAimOffset = new Unity.Mathematics.float2(0f, 1.2f),
                PrimaryPartAimRadius = 0.7f,
                SecondaryPartMaxHealth = 40f,
                SecondaryPartAimOffset = new Unity.Mathematics.float2(0f, -1.2f),
                SecondaryPartAimRadius = 0.7f,
            });
            TEngine.Log.Info("[M2-06] 固定意识传递试玩已载入：RTS / 接管 / 双接点手术 / 退出");
        }

#endif

        private void CheckEnd()
        {
            // M1-06：PlayerHealth 读的是**当前受控实体**的血量，没有受控实体时恒为 0。
            // 死亡回弹成功时它会变成新载体的血量，本局照常继续；回弹失败（意识无处可去）
            // 才落到 0 判死——这正是想要的语义，不需要额外分支。
            //
            // 唯一要挡的是 Suspended：恢复请求还挂着、目标尚未 Spawn 完的那几帧同样读到 0，
            // 那不是死亡，是还没接上。不挡这一条，任何一次读档恢复都会立刻误判成本局结束。
            // Suspended：恢复请求还挂着、目标尚未 Spawn 完的那几帧同样读到 0，那不是死亡是还没接上。
            // Released（2026-09-14）：玩家主动放下意识进战略视角，场上仍有可回去的身体。
            //   不挡这一条的后果实测过——**按 M 进战略视角当场判死、直接弹回主菜单**。
            //   一具可接管的身体都没有时 SimBridge 会把状态落回 None，那才是真正的意识无处可去。
            if (_sim.Availability == ControlAvailability.Suspended ||
                _sim.Availability == ControlAvailability.Released)
            {
                return;
            }

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

        /// <summary>上一帧的镜头状态，用来识别"刚进入战略视角"这一次沿。</summary>
        private ViewMode _lastViewMode = ViewMode.Direct;

        /// <summary>放下意识之前控制的那一具。按 M 回直控时优先把它接管回来。</summary>
        private SimEntityId _parkedControlUnit = SimEntityId.None;

        /// <summary>
        /// 2026-09-14（产品决策，bin 拍板）：**进入战略视角 = 放下意识**。
        ///
        /// 玩家报「战术我选不了 #1」。#1 是玩家本体，而内核 <c>MatchesPick</c> 与热更
        /// <c>SquadCommandSystem.IsSelectable</c> 都排除 <c>IntentSource == Player</c> 的那一个
        /// （M2-02 立的规矩，防两套输入抢同一个单位）。那条规矩本身没错，错在**人在战略视角时
        /// 根本没有第二套输入**，却还有一具身体挂着"玩家正在开"的牌子。
        ///
        /// 所以不去松动选择集判据，而是让"进战略视角"这件事把意识彻底放下：
        /// 场上不再有任何 Player 单位 → 连玩家本体都能被框选、被下令。
        /// 放下的那一具走既有交还路径（<see cref="AiHandoffSystem"/> 收到控制变更 → 缓冲 → 原地守备），
        /// 与"没收到命令就不动"这条全局规矩一致。
        ///
        /// 代价（如实记）：战略视角下 <c>SimBridge.ControllingPlayerBody</c> 为 false，
        /// 于是 M2-03b 那道闸门会停掉玩家本体的 Carrier 自动开火——本体改为按行为原型攻击。
        /// 这正是 #5「AI 与直控是两套战斗真相源」的又一次显形，等那条统一后自然消失。
        /// </summary>
        private void ParkControlOnStrategyView()
        {
            if (_cameraDirector == null)
            {
                return;
            }

            ViewMode mode = _cameraDirector.Mode;
            ViewMode previous = _lastViewMode;
            _lastViewMode = mode;

            // 只在"刚切进战略视角"那一帧做一次。放在过渡结束后而不是按下 M 的那一刻：
            // 过渡期间输入本来就全部冻结，提前放手只会让镜头还在飞的时候单位就开始自己动。
            if (mode != ViewMode.Strategy || previous == ViewMode.Strategy)
            {
                return;
            }
            if (_sim == null || !_sim.Running || !_sim.ControlledUnitId.IsValid)
            {
                return;
            }

            ParkControlNow();
        }

        /// <summary>
        /// 真正放下意识的那一步：**先记住是谁，再释放**。
        ///
        /// 记录与释放必须绑在一起——我第一版把记录留在调用点、释放放在这里，结果任何
        /// 不走那个调用点的释放（死亡回弹失败、调试直调）都会让 <see cref="_parkedControlUnit"/>
        /// 停在陈旧值上，按 M 回直控时接管回**上上次**那具身体。自检当场抓到了这条。
        /// </summary>
        private void ParkControlNow()
        {
            if (_sim == null || !_sim.Running || !_sim.ControlledUnitId.IsValid)
            {
                return;
            }
            _parkedControlUnit = _sim.ControlledUnitId;
            _sim.ReleaseControl();
        }

        /// <summary>回归测试直调入口：Edit 模式驱动不了镜头过渡（unscaledDeltaTime 一次 Tick 就收敛），
        /// 但"放下意识 → 回直控接管回原来那具"必须有断言守着。</summary>
        public void DebugParkControlForStrategy() => ParkControlNow();

        /// <summary>
        /// 按 M 回直控时重新拿一具身体（<see cref="CameraDirector.EnsureDirectTarget"/> 的实现）。
        /// 优先回到放下前那一具；它死了/没了就取候选表里的第一个，都没有就如实返回 false，
        /// 镜头会停在战略视角——这比把镜头切进一个空目标诚实。
        /// </summary>
        private bool ReacquireDirectTarget()
        {
            if (_sim == null || !_sim.Running)
            {
                return false;
            }
            if (_sim.ControlledUnitId.IsValid)
            {
                return true;
            }

            // 走 Restore 语义而不是 RequestControlSwitch：**回到自己刚放下的身体不是一次战术接管**，
            // 不该受 0.15 秒接管冷却与信号范围约束。否则在战略/直控之间来回按 M 会随机"没反应"，
            // 而玩家完全不知道自己撞的是一条接管冷却。
            if (_sim.RestoreControlTo(_parkedControlUnit))
            {
                return true;
            }

            SimControlCandidate[] candidates = _sim.GetControlCandidates();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (_sim.RestoreControlTo(candidates[i].EntityId))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsRunning => _running;

        /// <summary>由阶段入口在 Enter 前声明本次进入方式；该配置只消费一次。</summary>
        public void PrepareNextEnter(CellStageEntryMode mode)
        {
            _nextEntryMode = mode;
        }

        public StageOutcome Exit()
        {
            BuildOutcome();

            _hub.Exit();
            _hub.Dispose();

            // M1-06：本局唯一一次控制记忆落盘。必须在 _hub.Exit() 之后——
            // SimBridge.End() 会在内核 Dispose 前把控制状态抄进托管侧，这里读到的才是完整的那一份。
            // 与生涯统计同一条 Reject-to-Safe 纪律：Save 永不 throw，存档异常不阻塞退出流程。
            ControlPersistence.Save(_sim.CurrentHandoff);

            // M2-01/M2-02：解绑镜头与指挥层并复位输入所有权，
            // 否则上一局的 Scope/模态状态、选择集与编组会粘到下一局。
            if (_squadOverlay != null)
            {
                // Edit 模式下 Destroy 会抛「may not be called from edit mode」，
                // 而回归测试就是直接 Enter()/Exit() 本阶段的——必须分路。
                GameObject overlayGo = _squadOverlay.gameObject;
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(overlayGo);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(overlayGo);
                }
                _squadOverlay = null;
                _handoffOverlay = null;
            }
            // M2-04a：先解绑交还系统再解绑指挥层——它订阅着控制权变更信号，
            // 留着会在下一局用上一局的编队字典去记录编队归属。
            _aiHandoff?.Unbind();
            _aiHandoff = null;
            _squadCommands?.Unbind();
            _squadCommands = null;
            // M2-03b：先解绑动作集再解绑注册表——它订阅着控制权变更信号，
            // 留着会在下一局用上一局的注册表引用重建动作集。
            _directActions?.Unbind();
            _directActions = null;
            // M2-03a：装配条目里的键是上一局那个 SimWorld 发的实体 id，跨局一律作废。
            _unitLoadouts?.Unbind();
            _unitLoadouts = null;
            _cameraDirector?.Unbind();
            _cameraDirector = null;
            _strategicPause = false;

            _controlPresentationScope?.Dispose();
            _controlPresentationScope = null;
            _renderer?.Dispose();
            _renderer = null;
            FeatureArtResolver.Unload();
            foreach (UnityEngine.Object asset in _loadedArtAssets)
            {
                GameModule.Resource.UnloadAsset(asset);
            }
            _loadedArtAssets.Clear();
            _visuals = null;
            WhiteboxObstacleVisual.Dispose();
            // EnvFluidBackground.OnExit 已清液体面；此处兜底清回退路径的白模地面。
            WhiteboxGroundAnchor.Dispose();

            Signals.Clear();
            RuleFlags.Current.ClearAll();

            // story-006：此前只有 CheckEnd()（死亡/通关）会翻转 _running，Exit() 本身从不落这个字段。
            // 「退出沙盒」是仓库里第一处在 IsRunning 仍为 true 时主动调 GameRoot.EndRun() 的路径——
            // 不补上这一行，退出后 cell.IsRunning 会卡 true，CellDebugHud.OnGUI() 的
            // "cell==null||!cell.IsRunning" 判断永远走不到 DrawMenu()，主菜单再也回不去。
            _running = false;

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

            // stage-outcome-lifetime-stats story-001：本局唯一一次生涯统计落盘。
            // RecordRun 内部 Load 历史值 → 击杀/吞噬求和、存活时长/等级取最大 → Save 整份覆盖，
            // 返回含本局的最终快照回填到 outcome，结算文案直接读它不再碰磁盘。
            // Reject-to-Safe：RecordRun 永不 throw，存档异常不阻塞退出流程。
            _outcome.Lifetime = LifetimeStatsPersistence.RecordRun(
                _outcome.Statistics.EnemiesKilled,
                _outcome.Statistics.FoodDevoured,
                _outcome.DurationSeconds,
                _outcome.Level);
        }
    }
}
