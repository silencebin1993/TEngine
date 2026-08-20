using System;
using System.Collections.Generic;
using ComposeEngine.Core;
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

            var events = CarrierCompiler.Compile(engine, active, reserve, env.State, seed, cellId: null);

            int count = Mathf.Min(events.Count, IndicatorPoolSize);
            for (int i = 0; i < count; i++)
            {
                var evt = events[i];
                ShowMarker(i, evt);
            }
        }

        private void ShowMarker(int idx, HitEvent evt)
        {
            if (!FxRecipeCatalog.TryGetShapeRecipe(evt.Shape, out var recipe))
            {
                _active[idx] = false;
                _mr[idx].enabled = false;
                return;
            }

            LastIndicatorShapeKind = evt.Shape;
            LastIndicatorMeshKind = recipe.Mesh;

            float radius = MetabolicSliceBridge.DamageAreaRadiusFor(evt.Damage, evt.Scale);
            Color baseColor = recipe.Color;
            Color indicatorColor = new Color(baseColor.r, baseColor.g, baseColor.b, IndicatorAlpha);

            _active[idx] = true;
            _mr[idx].enabled = true;
            _mr[idx].sharedMaterial.color = indicatorColor;
            _mf[idx].sharedMesh = MeshForRecipe(recipe);

            // J6：静态标记（不飞行），只显示发射原点形状
            float2 origin = new float2(0f, 0f); // 玩家位置，J6 简化为原点
            _tf[idx].position = new Vector3(origin.x, MarkerY, origin.y);
            _tf[idx].rotation = Quaternion.identity;
            _tf[idx].localScale = Vector3.one * radius * 2f;
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
            }
        }

        public void Tick(float dt)
        {
            // J6：静态指示器，无动画
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
        }
    }
}
