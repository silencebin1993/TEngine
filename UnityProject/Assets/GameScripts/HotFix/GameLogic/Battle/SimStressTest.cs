using System.Diagnostics;
using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;
// System.Diagnostics 也有 Debug（来自 Stopwatch 的 using），这里要 Unity 的。
using Debug = UnityEngine.Debug;

namespace GameLogic.Battle
{
    /// <summary>
    /// 内核压力测试。验证框架文档 §9 的性能预算，而不是空口声称"能跑 10k"。
    ///
    /// 用法：把本组件挂到场景任意 GameObject 上，或在控制台调用
    /// <see cref="RunHeadless"/>。按 F11 循环切换单位数挡位。
    ///
    /// 测的是**内核 Step 的纯耗时**（不含渲染与热更玩法），
    /// 因为那是"大范围敌人"的决定性瓶颈。
    /// </summary>
    public sealed class SimStressTest : MonoBehaviour
    {
        private static readonly int[] Tiers = { 1000, 5000, 10000, 20000 };

        private SimWorld _world;
        private SimCommandBuffer _cmds;
        private SimRenderer _renderer;

        private int _tierIndex;
        private int _targetCount;

        private readonly Stopwatch _sw = new Stopwatch();
        private double _stepMsAccum;
        private int _stepSamples;
        private double _lastAvgMs;
        private double _worstMs;

        [SerializeField] private bool _render = true;

        private void Start()
        {
            DataRegistry.Instance.Load();
            Begin(Tiers[_tierIndex]);
        }

        private void Begin(int count)
        {
            Dispose();

            _targetCount = count;

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = Mathf.Max(count + 64, 1024);
            cfg.ArenaHalfExtent = Mathf.Sqrt(count) * 1.6f + 30f;
            cfg.HashCellSize = 4f;

            _world = new SimWorld();
            _world.Initialize(cfg);
            _world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            _cmds = default;
            _cmds.Initialize(Unity.Collections.Allocator.Persistent, count + 64);

            _world.SetPlayerStats(1e9f, 1e9f, 1.5f, 8f);

            int archetypeCount = Mathf.Max(1, DataRegistry.Instance.Archetypes.Count);
            float half = cfg.ArenaHalfExtent;

            for (int i = 0; i < count; i++)
            {
                _world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(Random.Range(-half, half), Random.Range(-half, half)),
                    Velocity = float2.zero,
                    Health = 1e9f,   // 不让它们死，保持恒定负载
                    Radius = Random.Range(0.3f, 0.8f),
                    MaxSpeed = Random.Range(2f, 6f),
                    ArchetypeId = Random.Range(0, archetypeCount),
                    Faction = SimFaction.Hostile,
                    LogicId = i + 1,
                    VisualId = Random.Range(1, 12),
                });
            }

            if (_render)
            {
                _renderer = new SimRenderer();
                _renderer.Initialize(BuildVisuals(), cfg.UnitCapacity);
            }

            _stepMsAccum = 0d;
            _stepSamples = 0;
            _worstMs = 0d;

            Debug.Log($"[SimStress] 挡位 {count} 单位，场地半边 {half:F0}");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11))
            {
                _tierIndex = (_tierIndex + 1) % Tiers.Length;
                Begin(Tiers[_tierIndex]);
                return;
            }

            if (_world == null || !_world.IsCreated)
            {
                return;
            }

            // 让玩家绕圈移动，避免所有单位聚成一团后负载失真
            float t = Time.time * 0.35f;
            _world.SetPlayerPosition(new float2(Mathf.Cos(t), Mathf.Sin(t)) * 25f);
            _cmds.SetPlayerIntent(PlayerIntent.Idle);

            _sw.Restart();
            _world.Step(Time.deltaTime, ref _cmds);
            _sw.Stop();

            double ms = _sw.Elapsed.TotalMilliseconds;
            _stepMsAccum += ms;
            _stepSamples++;
            if (ms > _worstMs)
            {
                _worstMs = ms;
            }

            if (_stepSamples >= 60)
            {
                _lastAvgMs = _stepMsAccum / _stepSamples;
                _stepMsAccum = 0d;
                _stepSamples = 0;
            }

            _renderer?.Draw(_world.GetSnapshot());
        }

        private void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            GUILayout.BeginArea(new Rect(12f, 12f, 460f, 190f));
            GUILayout.Label($"<b>内核压力测试</b>　F11 切换挡位", new GUIStyle(style) { richText = true });
            GUILayout.Label($"单位数 {_targetCount}（容量 {_world?.UnitCount ?? 0}）", style);
            GUILayout.Label($"Step 平均 {_lastAvgMs:F2} ms　最差 {_worstMs:F2} ms", style);
            GUILayout.Label($"帧率 {1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F0} FPS", style);
            GUILayout.Label($"预算：Step ≤ 4ms（框架文档 §9）", style);
            string verdict = _lastAvgMs <= 4d ? "达标" : "超预算";
            GUILayout.Label($"结论：{verdict}", style);
            GUILayout.EndArea();
        }

        private static SimVisual[] BuildVisuals()
        {
            var mesh = new Mesh { name = "StressQuad" };
            mesh.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mat = new Material(Shader.Find("BinGames/SimInstancedUnlit") ?? Shader.Find("Unlit/Color"));
            mat.enableInstancing = true;

            var v = new SimVisual[16];
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = new SimVisual
                {
                    Mesh = mesh, Material = mat, ScaleMul = 1f,
                    BaseColor = Color.HSVToRGB(i / 16f, 0.55f, 0.9f),
                };
            }
            return v;
        }

        /// <summary>
        /// 无渲染的纯内核基准。返回平均 Step 毫秒数。
        /// 供自动化测试调用，不依赖场景。
        /// </summary>
        public static double RunHeadless(int unitCount, int frames = 240)
        {
            DataRegistry.Instance.Load();

            SimConfig cfg = SimConfig.Default;
            cfg.UnitCapacity = unitCount + 64;
            cfg.ArenaHalfExtent = Mathf.Sqrt(unitCount) * 1.6f + 30f;

            var world = new SimWorld();
            world.Initialize(cfg);
            world.SetArchetypes(DataRegistry.Instance.ArchetypeArray());

            SimCommandBuffer cmds = default;
            cmds.Initialize(Unity.Collections.Allocator.Persistent, unitCount + 64);

            int archetypes = Mathf.Max(1, DataRegistry.Instance.Archetypes.Count);
            float half = cfg.ArenaHalfExtent;
            for (int i = 0; i < unitCount; i++)
            {
                world.SpawnUnit(new SpawnRequest
                {
                    Position = new float2(Random.Range(-half, half), Random.Range(-half, half)),
                    Health = 1e9f,
                    Radius = Random.Range(0.3f, 0.8f),
                    MaxSpeed = Random.Range(2f, 6f),
                    ArchetypeId = Random.Range(0, archetypes),
                    Faction = SimFaction.Hostile,
                    LogicId = i + 1,
                });
            }

            var sw = new Stopwatch();
            double total = 0d;
            for (int f = 0; f < frames; f++)
            {
                cmds.SetPlayerIntent(PlayerIntent.Idle);
                sw.Restart();
                world.Step(0.0166f, ref cmds);
                sw.Stop();
                // 跳过前 30 帧的预热（Burst JIT / 缓存冷启动）
                if (f >= 30)
                {
                    total += sw.Elapsed.TotalMilliseconds;
                }
            }

            double avg = total / Mathf.Max(1, frames - 30);

            cmds.Dispose();
            world.Dispose();

            Debug.Log($"[SimStress] {unitCount} 单位：Step 平均 {avg:F3} ms");
            return avg;
        }

        private void OnDestroy()
        {
            Dispose();
        }

        private void Dispose()
        {
            _renderer?.Dispose();
            _renderer = null;
            if (_cmds.IsCreated)
            {
                _cmds.Dispose();
            }
            _world?.Dispose();
            _world = null;
        }
    }
}
