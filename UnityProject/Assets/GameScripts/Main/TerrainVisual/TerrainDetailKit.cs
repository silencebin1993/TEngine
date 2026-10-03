using System;
using UnityEngine;
namespace BinGames.TerrainVisual
{
    [CreateAssetMenu(menuName="地球归还/地形/环境建筑素材")]
    public sealed class TerrainDetailKit:ScriptableObject
    {
        public Mesh[] Models=Array.Empty<Mesh>();
        [Range(2,8)]public int MaxRuinFloors=5;
        public float[] BuildingWeights={.55f,.3f,2,1.6f,.55f,.3f,2,1.6f,.55f};
        public Mesh Get(string id){foreach(Mesh mesh in Models)if(mesh&&mesh.name==id)return mesh;return null;}
    }
}
