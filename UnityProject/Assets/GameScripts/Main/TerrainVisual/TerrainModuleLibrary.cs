using System.Collections.Generic;
using UnityEngine;

namespace BinGames.TerrainVisual
{
    [CreateAssetMenu(menuName = "地球归还/地形/连接模块库")]
    public sealed class TerrainModuleLibrary : ScriptableObject
    {
        [Min(1)] public float CellSize=4;
        [Range(3,12)] public int PatchSize=7;
        public List<TerrainModule> Modules=new List<TerrainModule>();

        public List<ModuleVariant> Variants()
        {
            var list=new List<ModuleVariant>();
            for(int m=0;m<Modules.Count;m++)
            {
                TerrainModule item=Modules[m];
                if(item.Weight<=0 || (!item.Empty && !item.Mesh))continue;
                int rotations=0;
                for(int r=0;r<4;r++)if(item.Rotations!=null && item.Rotations.Length>r && item.Rotations[r])rotations++;
                for(int r=0;r<4;r++)if(item.Rotations!=null && item.Rotations.Length>r && item.Rotations[r])
                    list.Add(new ModuleVariant(m,r,item.Weight/rotations,item.North,item.East,item.South,item.West));
            }
            return list;
        }
    }
}
