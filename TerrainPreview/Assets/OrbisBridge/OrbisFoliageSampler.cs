using System.Collections.Generic;
using BinGames.TerrainVisual;
using Orbis;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
public static class OrbisFoliageSampler
{
    public const float Distance=2.6f;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install(){TerrainDetailBuilder.FoliageSampler=Sample;}
    public static Vector2[] Sample(Vector2Int cell,int size,int seed)
    {
        float2 origin=new float2(cell.x*size,cell.y*size),margin=new float2(Distance*.5f);
        var accepted=new List<Vector2>();
        using(var output=new NativeList<float2>(128,Allocator.TempJob))
        {
            var job=new FastPoisonDiskSampling.SamplingJob{output=output,
                bottomLeft=origin+margin,topRight=origin+size-margin,minDistance=Distance,
                iterationsPerPoint=FastPoisonDiskSampling.DefaultIterationPerPoint,
                random=new Unity.Mathematics.Random(TerrainField.Hash(cell.x,cell.y,seed+1301)|1)};
            job.Schedule().Complete();
            // Spacing guard: upstream's neighbour search excludes a grid-row endpoint.
            foreach(float2 point in output)
            {
                var p=new Vector2(point.x,point.y);bool close=false;
                foreach(Vector2 previous in accepted)if((p-previous).sqrMagnitude<Distance*Distance){close=true;break;}
                if(!close)accepted.Add(p);
            }
        }
        return accepted.ToArray();
    }
}
