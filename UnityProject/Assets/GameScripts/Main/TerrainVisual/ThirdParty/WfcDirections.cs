// Adapted from marian42/wavefunctioncollapse/Assets/Code/WaveFunctionCollapse/Orientations.cs.
// Copyright (c) 2018 Marian Kleineberg. MIT; see LICENSE.marian42.txt.
// Kept the original direction ordering; removed unused editor/gizmo rotations and mutable lazy caches.
using UnityEngine;
namespace BinGames.TerrainVisual
{
    internal static class WfcDirections
    {
        public const int LEFT=0,DOWN=1,BACK=2,RIGHT=3,UP=4,FORWARD=5;
        public static readonly Vector3Int[] Direction={Vector3Int.left,Vector3Int.down,Vector3Int.back,
            Vector3Int.right,Vector3Int.up,new Vector3Int(0,0,1)};
        public static readonly int[] HorizontalDirections={0,2,3,5};
        public static int Opposite(int d)=>(d+3)%6;
        public static bool IsHorizontal(int d)=>d!=DOWN&&d!=UP;
    }
}
