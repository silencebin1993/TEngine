using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using UnityEngine;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>区块的活跃等级（FGR-GEN-052）。</summary>
    public enum ChunkActivity : byte
    {
        /// <summary>纯地形：不需要模拟，可以被回收、需要时按种子重新生成。</summary>
        TerrainOnly = 0,
        /// <summary>正在被观察（镜头窗口内）：完整模拟。</summary>
        Observed = 1,
        /// <summary>有己方建筑 / 传送带 / 管线 / 机器：无论是否被观察都完整模拟（FGR-BASE-021）。</summary>
        Owned = 2,
    }

    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-052 第 1、2 条）：按“有没有己方实体 / 是否被观察”给区块分级，供世界模拟调度（FG0-ARCH-01）与区块回收使用。
    /// 敌方据点与巡逻的休眠 / 唤醒（第 3 条）属于 FG0-ARCH-06（FGR-ARC-016）。
    /// 机器位置取实时登记表（MachineRegistry，含区域里的实时位置），没有登记时退回存档里的机器记录。
    /// 开销 O(已加载区块 + 机器数)，只在调度需要时算，不每帧逐区块算。
    /// </summary>
    public static class WorldActivity
    {
        public static ChunkActivity Classify(CampaignState state, HomeGridMap map, int cx, int cy, GridCell focus, int viewRadiusChunks)
        {
            if (IsOwned(state, map, cx, cy))
            {
                return ChunkActivity.Owned;
            }
            ChunkAddress f = GridMath.Address(focus, map.ChunkSize);
            return Math.Max(Math.Abs(cx - f.ChunkX), Math.Abs(cy - f.ChunkY)) <= viewRadiusChunks ? ChunkActivity.Observed : ChunkActivity.TerrainOnly;
        }

        private static bool IsOwned(CampaignState state, HomeGridMap map, int cx, int cy)
        {
            HomeGridMap.Chunk c = map.TryGetLoaded(cx, cy);
            if (c != null && c.HasStructures())
            {
                return true;
            }
            foreach (Vector2 p in HomeMachinePositions(state))
            {
                ChunkAddress a = GridMath.Address(GridCell.FromWorld(p), map.ChunkSize);
                if (a.ChunkX == cx && a.ChunkY == cy)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>家园里存活机器的位置（实时登记表优先）。</summary>
        public static IEnumerable<Vector2> HomeMachinePositions(CampaignState state)
        {
            IEnumerable<MachineRecord> records = MachineRegistry.RecordCount > 0
                ? MachineRegistry.AllRecords
                : (IEnumerable<MachineRecord>)(state?.MachineRecords ?? Array.Empty<MachineRecord>());
            foreach (MachineRecord m in records)
            {
                if (m == null || m.RegionId != Regions.HomeValleyLayout.RegionId || m.Health <= 0f)
                {
                    continue;
                }
                yield return MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
            }
        }

        /// <summary>需要完整模拟的全部区块（有己方实体的 ∪ 镜头窗口），键 = <see cref="HomeGridMap.Key"/>。</summary>
        public static void ActiveChunks(CampaignState state, HomeGridMap map, GridCell focus, int viewRadiusChunks, HashSet<long> into)
        {
            into.Clear();
            foreach (HomeGridMap.Chunk c in map.LoadedChunks)
            {
                if (c.HasStructures())
                {
                    into.Add(HomeGridMap.Key(c.ChunkX, c.ChunkY));
                }
            }
            foreach (Vector2 p in HomeMachinePositions(state))
            {
                ChunkAddress a = GridMath.Address(GridCell.FromWorld(p), map.ChunkSize);
                into.Add(HomeGridMap.Key(a.ChunkX, a.ChunkY));
            }
            ChunkAddress f = GridMath.Address(focus, map.ChunkSize);
            for (int dy = -viewRadiusChunks; dy <= viewRadiusChunks; dy++)
            {
                for (int dx = -viewRadiusChunks; dx <= viewRadiusChunks; dx++)
                {
                    into.Add(HomeGridMap.Key(f.ChunkX + dx, f.ChunkY + dy));
                }
            }
        }
    }
}
