using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG0-ARCH-03：有 GameObject 表现的单位（Demo 的机器、具名敌人）的画面位置同步。表现对象只在地点被观察时存在；
    /// 绑定后每帧一次 <see cref="Apply"/>（Burst 并行写 Transform），按统一时钟的步内比例在上一步与本步位置之间插值——
    /// 热更层每帧只调一次，开销与单位数无关（FGR-SYS-042）。模拟位置的真相在内核，Transform 只是画面（DEBT-FG0ARCH01-03）。
    /// FG6-DEF-01 审查修复：绑定时可要求同时写朝向（<see cref="Bind(int, Transform, float, bool)"/> 的 orient）——炮塔头按内核里的炮口朝向
    /// （装甲朝向 Armor.zw）转动，与位置同一个 Burst 作业，热更层不再逐炮塔每帧写 Transform。
    /// </summary>
    public sealed class CombatTransformSync : IDisposable
    {
        private TransformAccessArray _transforms;
        private NativeList<int> _ids;
        private NativeList<float> _heights;
        private NativeList<byte> _orient;

        public CombatTransformSync(int capacity = 16)
        {
            _transforms = new TransformAccessArray(math.max(4, capacity));
            _ids = new NativeList<int>(math.max(4, capacity), Allocator.Persistent);
            _heights = new NativeList<float>(math.max(4, capacity), Allocator.Persistent);
            _orient = new NativeList<byte>(math.max(4, capacity), Allocator.Persistent);
        }

        public int Count => _ids.IsCreated ? _ids.Length : 0;

        public bool IsBound(int unitId)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                if (_ids[i] == unitId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>绑定一个表现对象（同一单位重复绑定会先解绑旧的）。<paramref name="height"/> = 画面高度（y）。</summary>
        public void Bind(int unitId, Transform transform, float height) => Bind(unitId, transform, height, false);

        /// <summary>绑定一个表现对象；<paramref name="orient"/> = 每帧同时按内核朝向（Armor.zw，xz 平面）写旋转（炮塔头）。</summary>
        public void Bind(int unitId, Transform transform, float height, bool orient)
        {
            if (transform == null || unitId <= 0)
            {
                return;
            }
            Unbind(unitId);
            _transforms.Add(transform);
            _ids.Add(unitId);
            _heights.Add(height);
            _orient.Add(orient ? (byte)1 : (byte)0);
        }

        public void Unbind(int unitId)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                if (_ids[i] == unitId)
                {
                    _transforms.RemoveAtSwapBack(i);
                    _ids.RemoveAtSwapBack(i);
                    _heights.RemoveAtSwapBack(i);
                    _orient.RemoveAtSwapBack(i);
                    return;
                }
            }
        }

        public void Clear()
        {
            while (_ids.Length > 0)
            {
                Unbind(_ids[_ids.Length - 1]);
            }
        }

        /// <summary>把绑定的 Transform 放到插值位置（<paramref name="alpha"/> = 距上一步的步内比例 0～1）。坐标相对 <paramref name="origin"/>。</summary>
        public void Apply(CombatKernel kernel, float alpha, double2 origin)
        {
            if (kernel == null || kernel.IsDisposed || _ids.Length == 0)
            {
                return;
            }
            ref CombatData d = ref kernel.Data;
            var job = new CombatTransformJob
            {
                Ids = _ids.AsArray(),
                Heights = _heights.AsArray(),
                Orient = _orient.AsArray(),
                SlotOfId = d.SlotOfId.AsArray(),
                Pos = d.Pos.AsArray(),
                Prev = d.Prev.AsArray(),
                Armor = d.Armor.AsArray(),
                Alpha = math.saturate(alpha),
                Origin = origin,
            };
            job.Schedule(_transforms).Complete();
        }

        public void Dispose()
        {
            if (_transforms.isCreated)
            {
                _transforms.Dispose();
            }
            if (_ids.IsCreated)
            {
                _ids.Dispose();
            }
            if (_heights.IsCreated)
            {
                _heights.Dispose();
            }
            if (_orient.IsCreated)
            {
                _orient.Dispose();
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct CombatTransformJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<int> Ids;
        [ReadOnly] public NativeArray<float> Heights;
        [ReadOnly] public NativeArray<byte> Orient;
        [ReadOnly] public NativeArray<int> SlotOfId;
        [ReadOnly] public NativeArray<double2> Pos;
        [ReadOnly] public NativeArray<double2> Prev;
        [ReadOnly] public NativeArray<float4> Armor;
        public float Alpha;
        public double2 Origin;

        public void Execute(int index, TransformAccess transform)
        {
            int id = Ids[index];
            int slot = id > 0 && id < SlotOfId.Length ? SlotOfId[id] : -1;
            if (slot < 0)
            {
                return;
            }
            double2 p = math.lerp(Prev[slot], Pos[slot], Alpha) - Origin;
            transform.position = new Vector3((float)p.x, Heights[index], (float)p.y);
            if (Orient[index] != 0 && slot < Armor.Length)
            {
                float2 f = Armor[slot].zw;
                if (math.lengthsq(f) > 1e-6f)
                {
                    transform.rotation = quaternion.LookRotationSafe(new float3(f.x, 0f, f.y), new float3(0f, 1f, 0f));
                }
            }
        }
    }
}
