using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace BinGames.Sim.WorldGen
{
    /// <summary>一个已调度的区块生成任务（工作线程）。主线程轮询 <see cref="IsCompleted"/>，完成后
    /// <see cref="CopyResult"/> 取结果，再 <see cref="Release"/> 释放原生内存（成对释放）。</summary>
    public sealed class WorldGenJob
    {
        public int ChunkX { get; private set; }
        public int ChunkY { get; private set; }
        /// <summary>调度者的标记（热更层用它区分表面 / 请求批次）。</summary>
        public int Tag { get; private set; }
        public bool Released { get; private set; }

        private JobHandle _handle;
        private NativeArray<WorldGenRect> _rects;
        private NativeArray<WorldGenZone> _zones;
        private NativeArray<byte> _terrain;
        private NativeArray<byte> _pollution;

        internal void Start(in WorldGenParams p, int cx, int cy, int tag, WorldGenRect[] rects, WorldGenZone[] zones)
        {
            ChunkX = cx;
            ChunkY = cy;
            Tag = tag;
            Released = false;
            int n = p.ChunkSize * p.ChunkSize;
            _rects = WorldGenKernel.ToNative(rects, Allocator.Persistent);
            _zones = WorldGenKernel.ToNative(zones, Allocator.Persistent);
            _terrain = new NativeArray<byte>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _pollution = new NativeArray<byte>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var job = new JobGenerateChunk
            {
                Params = p,
                ChunkX = cx,
                ChunkY = cy,
                Rects = _rects,
                RectCount = rects?.Length ?? 0,
                Zones = _zones,
                ZoneCount = zones?.Length ?? 0,
                Terrain = _terrain,
                Pollution = _pollution,
            };
            _handle = job.Schedule();
        }

        public bool IsCompleted => Released || _handle.IsCompleted;

        /// <summary>等待完成（已完成时立即返回）。</summary>
        public void Complete()
        {
            if (!Released)
            {
                _handle.Complete();
            }
        }

        public void CopyResult(byte[] terrain, byte[] pollution)
        {
            if (Released)
            {
                throw new InvalidOperationException("WorldGenJob 已释放");
            }
            _handle.Complete();
            _terrain.CopyTo(terrain);
            _pollution.CopyTo(pollution);
        }

        /// <summary>完成（必要时等待）并释放全部原生内存。幂等。</summary>
        public void Release()
        {
            if (Released)
            {
                return;
            }
            _handle.Complete();
            if (_rects.IsCreated) _rects.Dispose();
            if (_zones.IsCreated) _zones.Dispose();
            if (_terrain.IsCreated) _terrain.Dispose();
            if (_pollution.IsCreated) _pollution.Dispose();
            Released = true;
            WorldGenKernel.Untrack(this);
        }
    }

    /// <summary>一个已调度的叠加层贴图绘制任务（工作线程）。完成后 <see cref="Upload"/> 到贴图，再 <see cref="Release"/>。</summary>
    public sealed class WorldPaintJob
    {
        public int ChunkX { get; private set; }
        public int ChunkY { get; private set; }
        public int Stamp { get; private set; }
        public bool Released { get; private set; }

        private JobHandle _handle;
        private NativeArray<byte> _terrain;
        private NativeArray<byte> _pollution;
        private NativeArray<byte> _explored;
        private NativeArray<Color32> _palette;
        private NativeArray<byte> _patterns;
        private NativeArray<Color32> _pixels;

        internal void Start(in TilePaintParams q, int cx, int cy, int stamp, byte[] terrain, byte[] pollution, byte[] explored,
            Color32[] palette, byte[] patterns)
        {
            ChunkX = cx;
            ChunkY = cy;
            Stamp = stamp;
            Released = false;
            _terrain = new NativeArray<byte>(terrain, Allocator.Persistent);
            _pollution = new NativeArray<byte>(pollution, Allocator.Persistent);
            _explored = new NativeArray<byte>(explored, Allocator.Persistent);
            _palette = new NativeArray<Color32>(palette, Allocator.Persistent);
            _patterns = new NativeArray<byte>(patterns, Allocator.Persistent);
            int size = q.ChunkSize * q.PixelsPerCell;
            _pixels = new NativeArray<Color32>(size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var job = new JobPaintTile
            {
                Params = q,
                Terrain = _terrain,
                Pollution = _pollution,
                Explored = _explored,
                Palette = _palette,
                Patterns = _patterns,
                Pixels = _pixels,
            };
            _handle = job.Schedule();
        }

        public bool IsCompleted => Released || _handle.IsCompleted;

        public void Complete()
        {
            if (!Released)
            {
                _handle.Complete();
            }
        }

        /// <summary>把结果写进贴图并上传（主线程）。贴图尺寸必须是 区块边长 × 每格像素。</summary>
        public void Upload(Texture2D texture)
        {
            if (Released)
            {
                throw new InvalidOperationException("WorldPaintJob 已释放");
            }
            _handle.Complete();
            texture.SetPixelData(_pixels, 0);
            texture.Apply(false, false);
        }

        public void Release()
        {
            if (Released)
            {
                return;
            }
            _handle.Complete();
            if (_terrain.IsCreated) _terrain.Dispose();
            if (_pollution.IsCreated) _pollution.Dispose();
            if (_explored.IsCreated) _explored.Dispose();
            if (_palette.IsCreated) _palette.Dispose();
            if (_patterns.IsCreated) _patterns.Dispose();
            if (_pixels.IsCreated) _pixels.Dispose();
            Released = true;
            WorldGenKernel.Untrack(this);
        }
    }

    /// <summary>
    /// FG0-ARCH-05（FGR-ARC-013）：世界生成内核的门面——热更层只经这里调度 / 同步运行 Burst 任务，不直接接触
    /// Unity.Jobs 的泛型调度（HybridCLR 下泛型值类型实例化只放在 AOT 里）。
    /// 原生内存全部由 <see cref="WorldGenJob"/> / <see cref="WorldPaintJob"/> 持有，调用方负责 Release；
    /// 进程退出与脚本重载时 <see cref="ReleaseAll"/> 兜底，防止泄漏。
    /// </summary>
    public static class WorldGenKernel
    {
        private static readonly List<object> Live = new List<object>();
        private static bool _hooked;

        /// <summary>尚未释放的任务数（自检：关停后必须为 0）。</summary>
        public static int LiveJobCount => Live.Count;

        /// <summary>在工作线程上调度一个区块的生成（Burst）。</summary>
        public static WorldGenJob Schedule(in WorldGenParams p, int chunkX, int chunkY, int tag, WorldGenRect[] rects, WorldGenZone[] zones)
        {
            Hook();
            var job = new WorldGenJob();
            job.Start(in p, chunkX, chunkY, tag, rects, zones);
            Live.Add(job);
            return job;
        }

        /// <summary>调度后立即把任务交给工作线程（否则要等下一次 Complete / 帧末才开始）。</summary>
        public static void Kick() => JobHandle.ScheduleBatchedJobs();

        /// <summary>同步生成（玩法查询碰到还没生成的区块时用）：<paramref name="burst"/>=true 用 Burst 在当前线程运行；
        /// false 走托管 Execute（自检用来证明两条路径逐字节一致）。</summary>
        public static void GenerateNow(in WorldGenParams p, int chunkX, int chunkY, WorldGenRect[] rects, WorldGenZone[] zones,
            byte[] terrain, byte[] pollution, bool burst = true)
        {
            int n = p.ChunkSize * p.ChunkSize;
            var r = ToNative(rects, Allocator.TempJob);
            var z = ToNative(zones, Allocator.TempJob);
            var t = new NativeArray<byte>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var pol = new NativeArray<byte>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                var job = new JobGenerateChunk
                {
                    Params = p,
                    ChunkX = chunkX,
                    ChunkY = chunkY,
                    Rects = r,
                    RectCount = rects?.Length ?? 0,
                    Zones = z,
                    ZoneCount = zones?.Length ?? 0,
                    Terrain = t,
                    Pollution = pol,
                };
                if (burst)
                {
                    job.Run();
                }
                else
                {
                    job.Execute();
                }
                t.CopyTo(terrain);
                pol.CopyTo(pollution);
            }
            finally
            {
                r.Dispose();
                z.Dispose();
                t.Dispose();
                pol.Dispose();
            }
        }

        /// <summary>在工作线程上调度一个区块贴图的绘制（Burst）。</summary>
        public static WorldPaintJob SchedulePaint(in TilePaintParams q, int chunkX, int chunkY, int stamp, byte[] terrain, byte[] pollution,
            byte[] explored, Color32[] palette, byte[] patterns)
        {
            Hook();
            var job = new WorldPaintJob();
            job.Start(in q, chunkX, chunkY, stamp, terrain, pollution, explored, palette, patterns);
            Live.Add(job);
            return job;
        }

        /// <summary>单个格子（自检抽查用）。</summary>
        public static void SampleCell(in WorldGenParams p, int x, int y, WorldGenRect[] rects, WorldGenZone[] zones, out byte terrain, out byte pollution)
        {
            var r = ToNative(rects, Allocator.Temp);
            var z = ToNative(zones, Allocator.Temp);
            try
            {
                WorldGenMath.Sample(in p, x, y, r, rects?.Length ?? 0, z, zones?.Length ?? 0, out terrain, out pollution);
            }
            finally
            {
                r.Dispose();
                z.Dispose();
            }
        }

        /// <summary>区块内容的 64 位 FNV-1a 哈希（地形层 + 污染层），生成器版本回归基准用（FGR-GEN-061）。</summary>
        public static ulong Hash64(byte[] terrain, byte[] pollution)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                for (int i = 0; i < terrain.Length; i++)
                {
                    h = (h ^ terrain[i]) * 1099511628211UL;
                }
                h = (h ^ 0xFF) * 1099511628211UL;
                for (int i = 0; i < pollution.Length; i++)
                {
                    h = (h ^ pollution[i]) * 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>完成并释放所有未释放的任务（区域卸载、进程退出、脚本重载时调用）。</summary>
        public static void ReleaseAll()
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (i >= Live.Count)
                {
                    continue;
                }
                switch (Live[i])
                {
                    case WorldGenJob g: g.Release(); break;
                    case WorldPaintJob p: p.Release(); break;
                }
            }
            Live.Clear();
        }

        internal static void Untrack(object job) => Live.Remove(job);

        internal static NativeArray<T> ToNative<T>(T[] src, Allocator allocator) where T : struct
        {
            if (src == null || src.Length == 0)
            {
                return new NativeArray<T>(1, allocator); // Burst 任务里的 NativeArray 不能是未创建状态，给一个空占位。
            }
            return new NativeArray<T>(src, allocator);
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            Application.quitting += ReleaseAll;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
#endif
        }
    }
}
