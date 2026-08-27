using System.Collections.Generic;
using BinGames.Sim;
using Cysharp.Threading.Tasks;
using GameLogic.Ability;
using GameLogic.Ability.Executors;
using GameLogic.ArtBinding;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Digestion;
using GameLogic.Progression;
using GameLogic.Spawning;
using GameLogic.Stats;
using GameLogic.UI.Battle;
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
        private ZoneVisualPresenter _zoneVisual;
        private HealthBarPresenter _healthBars;
        private MetabolicSliceBridge _metabolicBridge;
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
        private MetabolicDigestionSystem _digestion;
        private CarrierBodyVisualPresenter _carrierBodyVisual;
        private ComposeProjectilePresenter _composeProjectilePresenter;

        private SimRenderer _renderer;
        /// <summary>story-005：持有 BuildVisuals() 返回的同一个数组引用，供 ApplyFeatureArtVisualsAsync
        /// 原地覆盖 Mesh/Material（SimRenderer.Initialize 只存引用不复制，见 preflight-decisions R3）。</summary>
        private SimVisual[] _visuals;
        /// <summary>story-005：追踪本次 Enter 期间加载的功能美术 Prefab/Material 资源，Exit 时配对释放。</summary>
        private readonly List<UnityEngine.Object> _loadedArtAssets = new();
        private Camera _camera;
        private bool _cameraVerifyMode;
        private Vector3 _cameraFollowOffset = DefaultCameraOffset;

        private bool _running;
        private bool _paused;
        private string _deathCause;

        /// <summary>story-006：LookDev 沙盒态。只读标记，实际抑制逻辑在 <see cref="DebugSetSandboxMode"/>。</summary>
        private bool _sandboxMode;
        public bool IsSandboxMode => _sandboxMode;

        /// <summary>
        /// 把 <see cref="_sandboxMode"/> 落到当前模块实例上（三处 Suppressed + 验证态相机）。
        /// 不放在 <c>#if</c> 门禁里——<see cref="Enter"/> 每次都要调用它（哪怕 _sandboxMode 恒为 false 的
        /// 正常入局也要跑一遍，保证语义一致），只有内部的相机分支才门禁到编辑器/开发构建。
        /// </summary>
        private void ApplySandboxState()
        {
            if (_director != null)
            {
                _director.Suppressed = _sandboxMode;
            }
            if (_timeline != null)
            {
                _timeline.Suppressed = _sandboxMode;
            }
            if (_metabolicBridge != null)
            {
                _metabolicBridge.Suppressed = _sandboxMode;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_sandboxMode && _cameraVerifyMode)
            {
                DebugToggleCameraVerifyMode();
            }
#endif
        }

        /// <summary>选卡暂停时的待选项。UI 读它显示进化选择界面。</summary>
        public System.Collections.Generic.List<CardSpec> PendingOptions { get; private set; }
        public DraftKind PendingDraftKind { get; private set; }

        public bool Paused => _paused;

        /// <summary>story-005：暂停菜单最小公开入口，复用 Draft 已验证的冻结语义（不碰 Time.timeScale）。</summary>
        public void SetPaused(bool paused)
        {
            if (!_running)
            {
                return;
            }
            _paused = paused;
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
        public HealthBarPresenter HealthBars => _healthBars;
        public MetabolicSliceBridge MetabolicBridge => _metabolicBridge;
        public MetabolicDigestionSystem Digestion => _digestion;

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
            // story-006：StageDirector.GoTo 是延迟切换（下一帧 Update 才真正调用本方法），
            // 所以 DebugSetSandboxMode(true) 完全可能在 RegisterModules() 重建新模块实例之前就已调用过——
            // 那次调用时 _director/_timeline/_metabolicBridge 还是旧实例甚至 null，Suppressed 白设。
            // 这里按 _sandboxMode 重新落一次，保证不管调用时序如何，新建的模块实例总能拿到正确的抑制态。
            ApplySandboxState();
            SetupSim();
            ApplyFeatureArtVisualsAsync().Forget();
            GrantStarterAbilities();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_sandboxMode)
            {
                SpawnSandboxDummy();
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
            _metabolicBridge.Bind(_sim, _stats, _abilities);
            _zones.Bind(_sim, _status);
            _carrierBodyVisual.Bind(_sim);
            _zoneVisual.Bind(_zones);
            _healthBars.Bind(_sim, _stats);
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

            // 轻障碍（story-009）：数据驱动随机布局，白模一次性生成。
            ObstacleSpec[] obstacles = ObstacleGenerator.Generate(cfg.ArenaHalfExtent);
            _sim.SetObstacles(obstacles);
            WhiteboxObstacleVisual.Spawn(obstacles);
            WhiteboxGroundAnchor.Spawn(cfg.ArenaHalfExtent);

            _renderer = new SimRenderer();
            _visuals = BuildVisuals();
            _renderer.Initialize(_visuals, cfg.UnitCapacity);
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
                    int vid = VisualIdForArtId("carrier/base");
                    if (vid >= 0)
                    {
                        _visuals[vid].Mesh = result.Mesh;
                        if (result.Material != null)
                        {
                            _visuals[vid].Material = result.Material;
                        }
                    }
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
                if (def.IsRetired || def.ArtId == null)
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
                int vid = VisualIdForArtId(def.ArtId);
                if (vid < 0)
                {
                    continue;
                }
                _visuals[vid].Mesh = result.Mesh;
                if (result.Material != null)
                {
                    _visuals[vid].Material = result.Material;
                }
            }
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
                // 加大加亮 + SimRenderer 内的方向拉长（story-010 V1）：默认半径下的弹体太小太暗。
                _renderer?.DrawProjectiles(_sim.World.Projectiles, new SimVisual
                {
                    Mesh = BuildConeCached(),
                    Material = ProjectileMaterial(),
                    ScaleMul = 1.8f,
                    BaseColor = new Color(1f, 0.95f, 0.35f),
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
            var want = new Vector3(
                p.x + _cameraFollowOffset.x, _cameraFollowOffset.y, p.y + _cameraFollowOffset.z);
            _camera.transform.position = Vector3.Lerp(
                _camera.transform.position, want, 1f - Mathf.Exp(-8f * dt));
        }

        private Mesh _coneCache;
        private Material _projMat;

        private Mesh BuildConeCached() => _coneCache ??= BuildCone(0.5f, 0.5f, 10);

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

            if (_sim != null)
            {
                Unity.Mathematics.float2 p = _sim.PlayerPosition;
                _camera.transform.position = new Vector3(
                    p.x + _cameraFollowOffset.x, _cameraFollowOffset.y, p.y + _cameraFollowOffset.z);
            }

            string mode = _cameraVerifyMode ? "调试态（透视）" : "默认态（俯视）";
            TEngine.Log.Info($"[GM] 相机切换 → {mode}");
        }

        /// <summary>
        /// LookDev 沙盒开关（story-006）：抑制刷怪/阶段推进/玩家真实网格常规装配 Tick 三处噪声源
        /// （<see cref="_director"/>/<see cref="_timeline"/>/<see cref="_metabolicBridge"/> 各自的 Suppressed），
        /// 不影响 <see cref="MetabolicSliceBridge.ApplyEvent"/>/<see cref="MetabolicSliceBridge.TickPendingMotion"/>。
        /// 进沙盒默认切验证态相机（复用 005），给可读 3D 视角，不强制玩家再按 F12。
        /// </summary>
        public void DebugSetSandboxMode(bool on)
        {
            _sandboxMode = on;
            ApplySandboxState();
            TEngine.Log.Info($"[GM] LookDev 沙盒 → {(on ? "开启" : "关闭")}");
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
            FeatureArtResolver.Unload();
            foreach (UnityEngine.Object asset in _loadedArtAssets)
            {
                GameModule.Resource.UnloadAsset(asset);
            }
            _loadedArtAssets.Clear();
            _visuals = null;
            WhiteboxObstacleVisual.Dispose();
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
        }
    }
}
