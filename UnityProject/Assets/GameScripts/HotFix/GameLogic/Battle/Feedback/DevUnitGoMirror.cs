using BinGames.Sim;
using GameLogic.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// Dev：把 Sim 里所有走 <c>RenderMeshInstanced</c> 的东西改成可点选 Hierarchy GO
    /// （单位 + 内核弹道）。区/障碍/血条/组合弹道等本就已是 GO，不必镜像。
    /// 开启时抑制 <see cref="SimRenderer"/> 的单位与弹道 Instanced，避免双影。
    /// 不限数量（调试用，关掉即回正式路径）。菜单：<c>BinGames/功能美术/Dev 单位 GO 镜像</c>。
    /// </summary>
    public sealed class DevUnitGoMirror : GameModuleBase
    {
        public const string RootName = "__DevUnitGoMirror";
        const string PrefsKey = "BinGames.DevUnitGoMirror.Enabled";
        /// <summary>与 <see cref="SimRenderer"/> 弹道拉长一致。</summary>
        const float ProjectileStretch = 2.2f;

        public static bool SuppressInstancedDraw => Enabled && _instanceActive;

        static bool _instanceActive;

        public static bool Enabled
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return PlayerPrefs.GetInt(PrefsKey, 0) != 0;
#else
                return false;
#endif
            }
            set
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                PlayerPrefs.SetInt(PrefsKey, value ? 1 : 0);
                PlayerPrefs.Save();
#endif
            }
        }

        public override int Priority => ModulePriority.Presentation;

        SimBridge _sim;
        SimVisual[] _visuals;
        Mesh _projectileMesh;
        Material _projectileMaterial;
        float _projectileScaleMul = 1.8f;
        Color _projectileColor = new Color(1f, 0.95f, 0.35f, 1f);

        Transform _root;
        Transform _unitsRoot;
        Transform _projectilesRoot;

        Entry[] _unitPool;
        int _unitPoolCount;
        int _unitActive;

        Entry[] _projPool;
        int _projPoolCount;
        int _projActive;

        struct Entry
        {
            public GameObject Go;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public int LastKey;
        }

        public void Bind(SimBridge sim, SimVisual[] visuals)
        {
            _sim = sim;
            _visuals = visuals;
        }

        /// <summary>与 CellStageFlow.Update 里 DrawProjectiles 的白模锥一致。</summary>
        public void BindProjectileVisual(Mesh mesh, Material material, float scaleMul, Color color)
        {
            _projectileMesh = mesh;
            _projectileMaterial = material;
            _projectileScaleMul = scaleMul;
            _projectileColor = color;
        }

        public override void OnEnter()
        {
            _instanceActive = true;
            if (Enabled)
            {
                Debug.Log("[DevUnitGoMirror] 已开启：镜像全部单位 + 内核弹道为可点选 GO（无数量上限）。菜单可关。");
            }
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _visuals == null)
            {
                return;
            }

            if (!Enabled)
            {
                if (_unitActive > 0 || _projActive > 0)
                {
                    HideAll();
                }
                return;
            }

            EnsureRoots();
            SyncUnits(_sim.Snapshot);
            SyncProjectiles();
        }

        public override void OnExit()
        {
            _instanceActive = false;
            DestroyRoot();
            _sim = null;
            _visuals = null;
            _projectileMesh = null;
            _projectileMaterial = null;
        }

        void SyncUnits(in SimSnapshot snap)
        {
            _unitActive = 0;
            if (!snap.Position.IsCreated || snap.Count <= 0)
            {
                HideTail(_unitPool, _unitPoolCount, 0);
                return;
            }

            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0)
                {
                    continue;
                }

                Entry e = EnsureEntry(ref _unitPool, ref _unitPoolCount, _unitActive, _unitsRoot, "unit");
                float2 p = snap.Position[i];
                float radius = snap.Radius[i];
                int visualId = snap.VisualId[i];
                if (visualId < 0 || visualId >= _visuals.Length)
                {
                    visualId = 0;
                }

                SimVisual visual = _visuals[visualId];
                float scaleMul = visual.ScaleMul > 0f ? visual.ScaleMul : 1f;
                float s = radius * 2f * scaleMul;

                Transform t = e.Go.transform;
                t.SetPositionAndRotation(new Vector3(p.x, 0f, p.y), Quaternion.identity);
                t.localScale = new Vector3(s, s, s);

                if (e.LastKey != visualId || e.Filter.sharedMesh != visual.Mesh)
                {
                    e.Filter.sharedMesh = visual.Mesh;
                    e.Renderer.sharedMaterial = visual.Material;
                    e.LastKey = visualId;
                    _unitPool[_unitActive] = e;
                }

                int logicId = snap.LogicId[i];
                string label = i == 0
                    ? $"player_L{logicId}_V{visualId}"
                    : $"unit{i}_L{logicId}_V{visualId}";
                if (e.Go.name != label)
                {
                    e.Go.name = label;
                }

                if (!e.Go.activeSelf)
                {
                    e.Go.SetActive(true);
                }

                _unitActive++;
            }

            HideTail(_unitPool, _unitPoolCount, _unitActive);
        }

        void SyncProjectiles()
        {
            _projActive = 0;
            SimWorld world = _sim.World;
            if (world == null || _projectileMesh == null || _projectileMaterial == null)
            {
                HideTail(_projPool, _projPoolCount, 0);
                return;
            }

            NativeArray<ProjectileState> projectiles = world.Projectiles;
            if (!projectiles.IsCreated)
            {
                HideTail(_projPool, _projPoolCount, 0);
                return;
            }

            for (int i = 0; i < projectiles.Length; i++)
            {
                ProjectileState s = projectiles[i];
                if (s.Alive == 0)
                {
                    continue;
                }

                Entry e = EnsureEntry(ref _projPool, ref _projPoolCount, _projActive, _projectilesRoot, "proj");
                float ang = math.atan2(s.Velocity.y, s.Velocity.x) * Mathf.Rad2Deg;
                float sc = s.Radius * 2f * _projectileScaleMul;

                Transform t = e.Go.transform;
                t.SetPositionAndRotation(
                    new Vector3(s.Position.x, 0f, s.Position.y),
                    Quaternion.Euler(0f, -ang, 0f));
                t.localScale = new Vector3(sc * ProjectileStretch, sc, sc);

                if (e.LastKey != 0 || e.Filter.sharedMesh != _projectileMesh)
                {
                    e.Filter.sharedMesh = _projectileMesh;
                    e.Renderer.sharedMaterial = _projectileMaterial;
                    if (e.Renderer.sharedMaterial != null && e.Renderer.sharedMaterial.HasProperty("_Color"))
                    {
                        // 不改共享材质球；颜色靠实例化材质太贵，调试够用即可。
                    }
                    e.LastKey = 0;
                    _projPool[_projActive] = e;
                }

                string label = $"proj{i}_src{s.SourceLogicId}";
                if (e.Go.name != label)
                {
                    e.Go.name = label;
                }

                if (!e.Go.activeSelf)
                {
                    e.Go.SetActive(true);
                }

                _projActive++;
            }

            HideTail(_projPool, _projPoolCount, _projActive);
        }

        static Entry EnsureEntry(
            ref Entry[] pool, ref int poolCount, int index, Transform parent, string defaultName)
        {
            if (pool == null)
            {
                pool = new Entry[64];
            }

            if (index < poolCount && pool[index].Go != null)
            {
                return pool[index];
            }

            var go = new GameObject(defaultName);
            go.transform.SetParent(parent, false);
            go.hideFlags = HideFlags.DontSave;
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var entry = new Entry
            {
                Go = go,
                Filter = filter,
                Renderer = renderer,
                LastKey = int.MinValue,
            };

            if (index >= pool.Length)
            {
                System.Array.Resize(ref pool, Mathf.Max(index + 1, pool.Length * 2));
            }
            pool[index] = entry;
            poolCount = Mathf.Max(poolCount, index + 1);
            return entry;
        }

        static void HideTail(Entry[] pool, int poolCount, int active)
        {
            if (pool == null)
            {
                return;
            }

            for (int i = active; i < poolCount; i++)
            {
                if (pool[i].Go != null && pool[i].Go.activeSelf)
                {
                    pool[i].Go.SetActive(false);
                }
            }
        }

        void EnsureRoots()
        {
            if (_root != null)
            {
                return;
            }

            var go = new GameObject(RootName);
            go.hideFlags = HideFlags.DontSave;
            Object.DontDestroyOnLoad(go);
            _root = go.transform;

            _unitsRoot = new GameObject("Units").transform;
            _unitsRoot.SetParent(_root, false);
            _unitsRoot.gameObject.hideFlags = HideFlags.DontSave;

            _projectilesRoot = new GameObject("Projectiles").transform;
            _projectilesRoot.SetParent(_root, false);
            _projectilesRoot.gameObject.hideFlags = HideFlags.DontSave;
        }

        void HideAll()
        {
            HideTail(_unitPool, _unitPoolCount, 0);
            HideTail(_projPool, _projPoolCount, 0);
            _unitActive = 0;
            _projActive = 0;
        }

        void DestroyRoot()
        {
            HideAll();
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
            _unitsRoot = null;
            _projectilesRoot = null;
            _unitPool = null;
            _projPool = null;
            _unitPoolCount = 0;
            _projPoolCount = 0;
        }
    }
}
