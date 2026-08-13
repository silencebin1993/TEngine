using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;
using GameLogic.Spawning;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模血条（story-008 默认实现）。
    ///
    /// 覆盖策略是"合并"而非二选一（D2）：
    ///   - 精英/首领：状态位单位存活期间常显。发现靠节流扫描（<see cref="EliteBossScanInterval"/>=0.5s，
    ///     复用 BossPhaseController 同款"只在没跟够时才付 O(容量) 成本"手法），
    ///     缓存内每帧只做 O(缓存大小) 的存活/掉旗校验。
    ///   - 普通敌人：仅在被命中后 <see cref="HitDisplaySeconds"/> 秒内短显，
    ///     挂在 <see cref="SimSnapshot.Hits"/> 事件流上，O(HitCount)。
    /// 两条路径都不对 Snapshot 做每帧 O(容量) 全量扫描，满足"热更层每帧与敌人数无关"红线。
    ///
    /// 命中普通敌人需要知道它的单位索引才能持续读 Position/Health——HitEvent 只带 LogicId，
    /// 若临时反查会退化成 O(容量) 扫描（SimRenderer 在 AOT 侧可以这么做，但本类是热更层，不行）。
    /// 因此本 story 顺带给 HitEvent 加了 TargetIndex/RemainingHealth 两个只读字段
    /// （JobDamage.TryDamage 结算时已经算出这两个值，只是原来没有写出），不改任何结算数值。
    ///
    /// 池化/材质/坐标手法照抄 <see cref="WhiteboxZoneVisual"/> 已验证可行的路径。
    /// </summary>
    public sealed class WhiteboxHealthBar : IHealthBarFeedback, System.IDisposable
    {
        private const int EliteBossCap = 32;
        private const int HitTrackCap = 48;
        /// <summary>玩家 1 + EliteBossCap 32 + HitTrackCap 48，留余量。</summary>
        private const int PoolSize = 96;

        private const float BarY = 0.02f;
        private const float BarWidth = 1f;
        private const float BarHeight = 0.14f;
        private const float EliteBossScanInterval = 0.5f;
        private const float HitDisplaySeconds = 2.5f;

        private struct HitTrackEntry
        {
            public int UnitIndex;
            public float ExpireAt;
        }

        private GameObject _poolRoot;
        private Transform[] _bgTf;
        private MeshRenderer[] _bgRenderer;
        private Transform[] _fillTf;
        private MeshRenderer[] _fillRenderer;
        private Mesh _quadMesh;
        private Material _bgMatTemplate;
        private Material _fillMatTemplate;
        private int _lastActiveSlots;

        private readonly List<int> _eliteBossCache = new List<int>(EliteBossCap);
        private float _nextScanTime;

        private readonly List<HitTrackEntry> _hitTrack = new List<HitTrackEntry>(HitTrackCap);

        /// <summary>调试计数，供 execute_code 断言"O(缓存) 而非 O(容量)"用，不参与渲染。</summary>
        public int EliteBossActiveCount => _eliteBossCache.Count;
        public int HitTrackActiveCount => _hitTrack.Count;

        public void Sync(in SimSnapshot snap, float playerMaxHealth)
        {
            EnsurePool();

            float now = Time.time;
            RefreshEliteBossCache(in snap, now);
            RefreshHitTrack(in snap, now);

            int slot = 0;
            slot = WriteBar(slot, snap.PlayerPosition, snap.PlayerRadius, snap.PlayerHealth,
                Mathf.Max(1f, playerMaxHealth));

            for (int i = 0; i < _eliteBossCache.Count; i++)
            {
                int idx = _eliteBossCache[i];
                slot = WriteBar(slot, snap.Position[idx], snap.Radius[idx], snap.Health[idx],
                    MaxHealthOf(in snap, idx));
            }

            for (int i = 0; i < _hitTrack.Count; i++)
            {
                int idx = _hitTrack[i].UnitIndex;
                slot = WriteBar(slot, snap.Position[idx], snap.Radius[idx], snap.Health[idx],
                    MaxHealthOf(in snap, idx));
            }

            HideFrom(slot);
        }

        /// <summary>
        /// 每帧 O(缓存大小) 清理掉旗/死亡项；每 <see cref="EliteBossScanInterval"/> 秒做一次
        /// 节流的 O(容量) 扫描，只发现新的精英/首领（不重建已有缓存项）。
        /// </summary>
        private void RefreshEliteBossCache(in SimSnapshot snap, float now)
        {
            for (int i = _eliteBossCache.Count - 1; i >= 0; i--)
            {
                int idx = _eliteBossCache[i];
                if (!snap.IsAlive(idx) || !snap.HasStatus(idx, SimStatus.Elite | SimStatus.Boss))
                {
                    _eliteBossCache.RemoveAt(i);
                }
            }

            if (now < _nextScanTime)
            {
                return;
            }
            _nextScanTime = now + EliteBossScanInterval;

            if (_eliteBossCache.Count >= EliteBossCap)
            {
                return;
            }

            for (int i = 0; i < snap.Count && _eliteBossCache.Count < EliteBossCap; i++)
            {
                if (snap.Alive[i] == 0 || !snap.HasStatus(i, SimStatus.Elite | SimStatus.Boss))
                {
                    continue;
                }
                if (_eliteBossCache.Contains(i))
                {
                    continue;
                }
                _eliteBossCache.Add(i);
            }
        }

        /// <summary>
        /// 每帧 O(追踪表大小) 清过期/死亡项；O(HitCount) 遍历本帧命中事件，
        /// 非精英/首领目标写入/刷新到期时间。容量上限内不扩容，超量丢弃。
        /// </summary>
        private void RefreshHitTrack(in SimSnapshot snap, float now)
        {
            for (int i = _hitTrack.Count - 1; i >= 0; i--)
            {
                HitTrackEntry e = _hitTrack[i];
                if (e.ExpireAt <= now || !snap.IsAlive(e.UnitIndex))
                {
                    _hitTrack.RemoveAt(i);
                }
            }

            if (snap.HitCount <= 0 || !snap.Hits.IsCreated)
            {
                return;
            }

            int n = snap.HitCount;
            for (int h = 0; h < n; h++)
            {
                HitEvent hit = snap.Hits[h];
                int idx = hit.TargetIndex;
                if (idx < 0 || idx >= snap.Count || !snap.IsAlive(idx))
                {
                    continue;
                }
                if (snap.HasStatus(idx, SimStatus.Elite | SimStatus.Boss))
                {
                    // 已由常显覆盖，不重复占用追踪表容量
                    continue;
                }

                float expireAt = now + HitDisplaySeconds;
                int existing = FindHitTrackIndex(idx);
                if (existing >= 0)
                {
                    HitTrackEntry e = _hitTrack[existing];
                    e.ExpireAt = expireAt;
                    _hitTrack[existing] = e;
                }
                else if (_hitTrack.Count < HitTrackCap)
                {
                    _hitTrack.Add(new HitTrackEntry { UnitIndex = idx, ExpireAt = expireAt });
                }
            }
        }

        private int FindHitTrackIndex(int unitIndex)
        {
            for (int i = 0; i < _hitTrack.Count; i++)
            {
                if (_hitTrack[i].UnitIndex == unitIndex)
                {
                    return i;
                }
            }
            return -1;
        }

        private static float MaxHealthOf(in SimSnapshot snap, int idx)
        {
            int enemyId = SpawnDirector.DecodeEnemyId(snap.LogicId[idx]);
            EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);
            return spec != null && spec.Health > 0f ? spec.Health : Mathf.Max(1f, snap.Health[idx]);
        }

        /// <summary>写一条血条到指定槽位，返回下一个空闲槽位。超出池容量时不再写（丢弃多余的）。</summary>
        private int WriteBar(int slot, float2 posXZ, float radius, float health, float maxHealth)
        {
            if (slot >= PoolSize)
            {
                return slot;
            }

            float frac = maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f;
            float worldX = posXZ.x;
            float worldZ = posXZ.y + (radius + 0.3f);
            float halfWidth = BarWidth * 0.5f;

            Transform bg = _bgTf[slot];
            bg.localPosition = new Vector3(worldX, BarY, worldZ);
            bg.localScale = new Vector3(BarWidth, 1f, BarHeight);
            _bgRenderer[slot].enabled = true;

            Transform fill = _fillTf[slot];
            fill.localPosition = new Vector3(worldX - (1f - frac) * halfWidth, BarY + 0.001f, worldZ);
            fill.localScale = new Vector3(frac, 1f, BarHeight);
            _fillRenderer[slot].enabled = true;
            _fillRenderer[slot].sharedMaterial.color = ColorForFrac(frac);

            return slot + 1;
        }

        private static Color ColorForFrac(float frac)
        {
            var green = new Color(0.3f, 0.85f, 0.35f);
            var yellow = new Color(0.95f, 0.85f, 0.2f);
            var red = new Color(0.9f, 0.25f, 0.2f);

            if (frac >= 0.5f)
            {
                return Color.Lerp(yellow, green, (frac - 0.5f) / 0.5f);
            }
            return Color.Lerp(red, yellow, frac / 0.5f);
        }

        private void HideFrom(int activeCount)
        {
            int hideFrom = Mathf.Min(activeCount, PoolSize);
            int hideTo = Mathf.Min(_lastActiveSlots, PoolSize);
            for (int i = hideFrom; i < hideTo; i++)
            {
                _bgRenderer[i].enabled = false;
                _fillRenderer[i].enabled = false;
            }
            _lastActiveSlots = activeCount;
        }

        private void EnsurePool()
        {
            if (_poolRoot != null)
            {
                return;
            }

            _quadMesh = BuildQuad();
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            _bgMatTemplate = new Material(shader) { color = new Color(0f, 0f, 0f, 0.6f) };
            _fillMatTemplate = new Material(shader) { color = Color.white };

            _poolRoot = new GameObject("HealthBar_Pool");
            _bgTf = new Transform[PoolSize];
            _bgRenderer = new MeshRenderer[PoolSize];
            _fillTf = new Transform[PoolSize];
            _fillRenderer = new MeshRenderer[PoolSize];

            for (int i = 0; i < PoolSize; i++)
            {
                CreateSlot($"Bg_{i}", _bgMatTemplate, out _bgTf[i], out _bgRenderer[i]);
                CreateSlot($"Fill_{i}", _fillMatTemplate, out _fillTf[i], out _fillRenderer[i]);
            }
        }

        private void CreateSlot(string name, Material template, out Transform tf, out MeshRenderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_poolRoot.transform, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _quadMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(template);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false;

            tf = go.transform;
            renderer = mr;
        }

        /// <summary>俯视扁平方片，单位边长；实际尺寸靠 localScale 缩放。</summary>
        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "HealthBarQuad" };
            m.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, -0.5f),
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public void Dispose()
        {
            if (_poolRoot != null)
            {
                for (int i = 0; i < PoolSize; i++)
                {
                    if (_bgRenderer[i] != null)
                    {
                        Object.Destroy(_bgRenderer[i].sharedMaterial);
                    }
                    if (_fillRenderer[i] != null)
                    {
                        Object.Destroy(_fillRenderer[i].sharedMaterial);
                    }
                }

                Object.Destroy(_poolRoot);
                _poolRoot = null;
                _bgTf = null;
                _bgRenderer = null;
                _fillTf = null;
                _fillRenderer = null;
            }

            if (_bgMatTemplate != null)
            {
                Object.Destroy(_bgMatTemplate);
                _bgMatTemplate = null;
            }
            if (_fillMatTemplate != null)
            {
                Object.Destroy(_fillMatTemplate);
                _fillMatTemplate = null;
            }
            if (_quadMesh != null)
            {
                Object.Destroy(_quadMesh);
                _quadMesh = null;
            }

            _eliteBossCache.Clear();
            _hitTrack.Clear();
            _lastActiveSlots = 0;
        }
    }
}
