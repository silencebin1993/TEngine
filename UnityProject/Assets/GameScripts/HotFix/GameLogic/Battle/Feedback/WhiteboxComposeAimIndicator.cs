using System;
using System.Collections.Generic;
using ComposeEngine.Core;
using Cysharp.Threading.Tasks;
using GameLogic.ArtBinding;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// story-010 J3：白模组合弹道瞄准指示器（玩家装配预览）。
    ///
    /// J2：独立 8 位池（<see cref="FxRecipeCatalog.Global.IndicatorPoolSize"/>），
    /// 禁止与弹道 32 位池共用——密集开火会挤掉指示器，体验极差。
    ///
    /// J4 纪律：**只读** seed，**禁止自增或写回** MetabolicSliceBridge._seed——
    /// 写了会让真实开火的随机数漂掉，不报错、只表现错（见 Preflight J4 决策）。
    ///
    /// 复用 <see cref="WhiteboxComposeProjectileFeedback"/> 的网格（不重建），
    /// 只需 alpha 半透明 + 静态标记（不飞行）。
    /// </summary>
    public sealed class WhiteboxComposeAimIndicator : IComposeAimIndicatorFeedback, IDisposable
    {
        private const int IndicatorPoolSize = FxRecipeCatalog.Global.IndicatorPoolSize;
        private const float MarkerY = FxRecipeCatalog.Global.MarkerY;
        private const float IndicatorAlpha = 0.35f; // J6：半透明，与实弹区分

        private GameObject _poolRoot;
        private Transform[] _tf;
        private MeshFilter[] _mf;
        private MeshRenderer[] _mr;
        private bool[] _active;

        /// <summary>indicator 槽 VFX Prefab 池——与 <see cref="WhiteboxComposeProjectileFeedback"/> 的
        /// muzzle/hit 同手法：绑定了 shape.{Shape}.indicator 就用真实 Prefab，未绑定回退白模网格。</summary>
        private FeatureArtVfxPool _vfxPool;
        private GameObject[] _prefabGo;

        /// <summary>接上角色：缓存最近一次 <see cref="ShowPlayerIndicator"/> 用到的 bridge，供 <see cref="Tick"/>
        /// 逐帧回读玩家当前坐标——指示器不是"射出去"的东西，是"预览你现在站这儿会怎么打"，理应跟着角色走，
        /// 而不是换装那一刻画一次就钉死在原地（原先钉在硬编码的世界 (0,0)，玩家一走就跟丢，见 ShowMarker 改动）。</summary>
        private MetabolicSliceBridge _bridge;

        /// <summary>验收探针（story-010b Acceptance 2）：<see cref="ShowPlayerIndicator"/> 实际重算次数，
        /// 供 Presenter 的脏检查逻辑做「未变则不重复调用」的断言依据。不影响任何表现/结算路径。</summary>
        public int RefreshCount { get; private set; }

        /// <summary>story-007 验收探针：<see cref="ShowMarker"/> 最近一次取到的 Shape 字符串，供断言切 Carrier 后指示器分型变化。</summary>
        public string LastIndicatorShapeKind { get; private set; } = "";

        /// <summary>story-007 验收探针：<see cref="ShowMarker"/> 最近一次取到的配方网格键，供与 <see cref="LastIndicatorShapeKind"/> 联合断言。</summary>
        public FxMeshKind LastIndicatorMeshKind { get; private set; }

        private Material _matTemplate;
        private Mesh _circleMesh;
        private Mesh _streakMesh;
        private Mesh _wedgeMesh;
        private Mesh _ringMesh;
        private Mesh _coneMesh;
        private Mesh _crossMesh;
        private Mesh _bandMesh;

        /// <summary>加载 7 Shape × indicator 共 7 个 VFX 槽的 Prefab 池。由
        /// <see cref="GameLogic.Battle.Feedback.ComposeAimIndicatorPresenter"/> 在阶段进入后调用一次，
        /// 与 <see cref="WhiteboxComposeProjectileFeedback.LoadVfxBindingsAsync"/> 同手法。</summary>
        public async UniTask LoadVfxBindingsAsync()
        {
            _vfxPool ??= new FeatureArtVfxPool();

            var ids = new List<string>();
            foreach (string shape in FxRecipeCatalog.ShapeKeys)
            {
                ids.Add($"shape.{shape}.indicator");
            }

            await _vfxPool.LoadAsync(ids);
        }

        public void ShowPlayerIndicator(int seed)
        {
            RefreshCount++;
            EnsurePool();
            Hide();

            // J4：从 MetabolicSlicePanel.Instance 读取玩家当前装配（Registry/Reserve 由它持有）
            var panel = GameLogic.UI.Battle.MetabolicSlicePanel.Instance;
            if (panel == null)
            {
                return;
            }

            var cell = GameLogic.Stage.GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                return;
            }

            var bridge = cell.MetabolicBridge;
            if (bridge == null)
            {
                return;
            }

            var registry = panel.CarrierRegistry;
            var reserve = panel.GeneReserve;
            if (registry == null || reserve == null)
            {
                return;
            }

            var active = registry.ActiveCarrier;
            if (active == null)
            {
                return;
            }

            // J4：用传入的 seed 编译一次，**不写回** bridge._seed；从 bridge 获取 Engine 和 Environment
            var engine = bridge.GetEngine();
            var env = bridge.GetEnvironment();
            if (engine == null || env == null)
            {
                return;
            }

            // 接上角色：缓存 bridge 供 Tick 逐帧回读玩家坐标（见 _bridge 字段注释）。
            _bridge = bridge;

            var events = CarrierCompiler.Compile(engine, active, reserve, env.State, seed, cellId: null);

            float2 playerPos = bridge.GetPlayerPosition();
            int count = Mathf.Min(events.Count, IndicatorPoolSize);
            for (int i = 0; i < count; i++)
            {
                var evt = events[i];
                ShowMarker(i, evt, playerPos);
            }
        }

        private void ShowMarker(int idx, HitEvent evt, float2 playerPos)
        {
            if (!FxRecipeCatalog.TryGetShapeRecipe(evt.Shape, out var recipe))
            {
                _active[idx] = false;
                _mr[idx].enabled = false;
                ReleasePrefab(idx);
                return;
            }

            LastIndicatorShapeKind = evt.Shape;
            LastIndicatorMeshKind = recipe.Mesh;

            float radius = MetabolicSliceBridge.DamageAreaRadiusFor(evt.Damage, evt.Scale);
            Color baseColor = recipe.Color;
            Color indicatorColor = new Color(baseColor.r, baseColor.g, baseColor.b, IndicatorAlpha);

            _active[idx] = true;

            // indicator 槽绑定了就用真实 Prefab（只同步位置/朝向，缩放由资源自身决定——同
            // WhiteboxComposeProjectileFeedback.SpawnMarker 对 muzzle/hit 的手法），未绑定回退白模网格。
            ReleasePrefab(idx);
            GameObject prefabGo = _vfxPool?.TryAcquire($"shape.{evt.Shape}.indicator");
            _prefabGo[idx] = prefabGo;

            _mr[idx].enabled = prefabGo == null;
            _mr[idx].sharedMaterial.color = indicatorColor;
            _mf[idx].sharedMesh = MeshForRecipe(recipe);

            // J6：静态标记（不飞行），只显示发射原点形状——发射原点＝玩家当前坐标（接上角色，
            // 不再硬编码世界 (0,0)；Tick 里还会逐帧跟着玩家挪，见字段注释）。
            _tf[idx].position = new Vector3(playerPos.x, MarkerY, playerPos.y);
            _tf[idx].rotation = Quaternion.identity;
            // story-005（scene-3d-content）：非等比缩放——网格已烘焙绝对高度（FxRecipeCatalog.Global.MarkerHeight），
            // 等比缩放会让高度被 radius*2f 连带放大/缩小，与既定「高度是绝对量、与 XZ 半径解耦」纪律冲突。
            _tf[idx].localScale = new Vector3(radius * 2f, 1f, radius * 2f);

            if (prefabGo != null)
            {
                prefabGo.transform.position = _tf[idx].position;
                prefabGo.transform.rotation = _tf[idx].rotation;
            }
        }

        private void ReleasePrefab(int idx)
        {
            if (_prefabGo[idx] != null)
            {
                _vfxPool.Release(_prefabGo[idx]);
                _prefabGo[idx] = null;
            }
        }

        private Mesh MeshForRecipe(FxShapeRecipe recipe)
        {
            switch (recipe.Mesh)
            {
                case FxMeshKind.Streak: return _streakMesh;
                case FxMeshKind.Wedge: return _wedgeMesh;
                case FxMeshKind.Ring: return _ringMesh;
                case FxMeshKind.Cone: return _coneMesh;
                case FxMeshKind.Cross: return _crossMesh;
                case FxMeshKind.Band: return _bandMesh;
                case FxMeshKind.Circle:
                default: return _circleMesh;
            }
        }

        public void Hide()
        {
            if (_mr == null) return;
            for (int i = 0; i < _mr.Length; i++)
            {
                _active[i] = false;
                _mr[i].enabled = false;
                ReleasePrefab(i);
            }
        }

        public void Tick(float dt)
        {
            // J6：形状/朝向仍是静态（不飞行、不重算），但接上角色——逐帧把已显示的标记重新钉到玩家
            // 当前坐标，换装那一刻之后玩家只要一走，预览就不会被落在原地（旧行为等价于"钉死不动"）。
            if (_bridge == null || _tf == null)
            {
                return;
            }

            float2 pos = _bridge.GetPlayerPosition();
            for (int i = 0; i < _tf.Length; i++)
            {
                if (!_active[i])
                {
                    continue;
                }
                Vector3 p = _tf[i].position;
                p.x = pos.x;
                p.z = pos.y;
                _tf[i].position = p;
                if (_prefabGo[i] != null)
                {
                    _prefabGo[i].transform.position = p;
                }
            }
        }

        private void EnsurePool()
        {
            if (_poolRoot != null) return;

            // 复用 WhiteboxComposeProjectileFeedback 的网格构建逻辑（不重复造轮子）
            _circleMesh = WhiteboxComposeProjectileFeedback.BuildCircleStatic(FxRecipeCatalog.Global.CircleSegments);
            _streakMesh = WhiteboxComposeProjectileFeedback.BuildStreakStatic();
            _wedgeMesh = WhiteboxComposeProjectileFeedback.BuildWedgeStatic(
                FxRecipeCatalog.Global.ArcHalfAngleDeg, FxRecipeCatalog.Global.ArcSegments);
            _ringMesh = WhiteboxComposeProjectileFeedback.BuildRingStatic(
                FxRecipeCatalog.Global.RingInnerRatio, FxRecipeCatalog.Global.CircleSegments);
            _coneMesh = WhiteboxComposeProjectileFeedback.BuildConeStatic(FxRecipeCatalog.Global.CircleSegments);
            _crossMesh = WhiteboxComposeProjectileFeedback.BuildCrossStatic();
            _bandMesh = WhiteboxComposeProjectileFeedback.BuildBandStatic(
                FxRecipeCatalog.Global.BandInnerRatio, FxRecipeCatalog.Global.BandSegments);

            _matTemplate = new Material(Shader.Find("Unlit/Color"))
            {
                color = Color.white,
                renderQueue = 3001 // 略高于弹道 3000，确保指示器不被遮挡
            };

            // 池对象生命周期随 Presenter.Dispose 管理，不需要 DontDestroyOnLoad（CellStageFlow 不是场景切换，
            // 同骨架的 WhiteboxComposeProjectileFeedback.EnsurePool 也未调用它——保持一致）。
            _poolRoot = new GameObject("[ComposeAimIndicatorPool]");

            _tf = new Transform[IndicatorPoolSize];
            _mf = new MeshFilter[IndicatorPoolSize];
            _mr = new MeshRenderer[IndicatorPoolSize];
            _active = new bool[IndicatorPoolSize];
            _prefabGo = new GameObject[IndicatorPoolSize];

            for (int i = 0; i < IndicatorPoolSize; i++)
            {
                var go = new GameObject($"Indicator{i}");
                go.transform.SetParent(_poolRoot.transform, worldPositionStays: false);
                _tf[i] = go.transform;
                _mf[i] = go.AddComponent<MeshFilter>();
                _mr[i] = go.AddComponent<MeshRenderer>();
                _mr[i].sharedMaterial = new Material(_matTemplate);
                _mr[i].enabled = false;
                _active[i] = false;
            }
        }

        public void Dispose()
        {
            if (_poolRoot != null)
            {
                for (int i = 0; i < _mr.Length; i++)
                {
                    if (_mr[i] != null)
                    {
                        UnityEngine.Object.Destroy(_mr[i].sharedMaterial);
                    }
                }
                UnityEngine.Object.Destroy(_poolRoot);
                _poolRoot = null;
            }

            _vfxPool?.Dispose();
            _vfxPool = null;

            if (_matTemplate != null)
            {
                UnityEngine.Object.Destroy(_matTemplate);
                _matTemplate = null;
            }

            // 网格由 WhiteboxComposeProjectileFeedback 持有，不重复销毁
            _circleMesh = null;
            _streakMesh = null;
            _wedgeMesh = null;
            _ringMesh = null;
            _coneMesh = null;
            _crossMesh = null;
            _bandMesh = null;

            _tf = null;
            _mf = null;
            _mr = null;
            _active = null;
            _prefabGo = null;
            _bridge = null;
        }
    }
}
