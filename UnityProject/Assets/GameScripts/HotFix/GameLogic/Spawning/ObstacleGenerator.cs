using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;
// Unity.Mathematics 也有 Random，与 UnityEngine.Random 冲突。本文件要的是后者（同 SpawnDirector 惯例）。
using Random = UnityEngine.Random;

namespace GameLogic.Spawning
{
    /// <summary>
    /// 局内静态障碍布局生成（story-009）。
    ///
    /// 障碍数量从 <see cref="DataRegistry.Global"/>（CellGlobal.obstacleCount）读取；
    /// 其余布局参数（半径/间隙/留空/重试次数）仍是具名 const，未迁移到表。
    /// 布局本身用参数驱动的随机采样生成（非写死坐标数组），满足 AC"禁止写死唯一一张不可调布局"。
    /// </summary>
    public static class ObstacleGenerator
    {
        private const float MinRadius = 2f;
        private const float MaxRadius = 5f;
        /// <summary>两个障碍边缘之间的最小间隙。</summary>
        private const float MinGap = 3f;
        /// <summary>原点附近留空的半径，保证玩家出生点周围无障碍。</summary>
        private const float CenterClearance = 12f;
        private const float EdgeMargin = 6f;
        private const int MaxAttemptsPerObstacle = 20;

        /// <summary>
        /// 生成布局。候选点落在留空半径内或与已放置障碍重叠（含间隙）则重试；
        /// 多次失败则放弃该个，允许实际数量 &lt; 目标数量。
        /// </summary>
        public static ObstacleSpec[] Generate(float arenaHalf)
        {
            int count = DataRegistry.Instance.Global.ObstacleCount;
            var list = new List<ObstacleSpec>(count);
            float half = math.max(1f, arenaHalf - EdgeMargin);

            for (int i = 0; i < count; i++)
            {
                for (int attempt = 0; attempt < MaxAttemptsPerObstacle; attempt++)
                {
                    float radius = Random.Range(MinRadius, MaxRadius);
                    var pos = new float2(Random.Range(-half, half), Random.Range(-half, half));

                    if (math.lengthsq(pos) < CenterClearance * CenterClearance)
                    {
                        continue;
                    }

                    if (Overlaps(list, pos, radius))
                    {
                        continue;
                    }

                    list.Add(new ObstacleSpec { Position = pos, Radius = radius });
                    break;
                }
            }

            return list.ToArray();
        }

        private static bool Overlaps(List<ObstacleSpec> placed, float2 pos, float radius)
        {
            for (int j = 0; j < placed.Count; j++)
            {
                float reach = radius + placed[j].Radius + MinGap;
                if (math.distancesq(pos, placed[j].Position) < reach * reach)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
