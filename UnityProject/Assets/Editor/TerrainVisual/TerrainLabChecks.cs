using System;
using System.Collections.Generic;
using System.IO;
using BinGames.TerrainVisual;
using UnityEngine;

namespace BinGames.TerrainVisual.Editor
{
    public static class TerrainLabChecks
    {
        [Serializable] public sealed class Report
        {
            public bool pass;
            public int seeds,edges,lodEdges,streamingEdges,wfcSolutions,negativeChecks,modelSockets,buildingSolutions,entrances;
            public float maxHeightError,maxNormalError,maxColorError;
            public List<string> failures=new List<string>();
        }
        public static void Run(TerrainProfile profile,string destination)
        {
            var r=new Report();int size=profile.ChunkSize;
            foreach(Material material in new[]{profile.GroundMaterial,profile.PropMaterial,profile.WaterMaterial})
                if(!material||!material.shader.isSupported||UnityEditor.ShaderUtil.GetShaderMessages(material.shader).Length>0)
                {
                    if(!material||!material.shader.isSupported)r.failures.Add("场景材质不受支持。");
                    else foreach(var message in UnityEditor.ShaderUtil.GetShaderMessages(material.shader))
                        if(message.severity.ToString()=="Error")r.failures.Add("着色器编译错误："+message.message);
                }
            int[] seeds={41729,1,0,-17,int.MaxValue,int.MinValue,151,90217};
            foreach(int seed in seeds)
            {
                r.seeds++;var field=new TerrainField(profile.Snapshot(seed));
                foreach(int cx in new[]{-3,-1,0,2})
                {
                    TerrainMeshData a=TerrainMeshBuilder.Build(field,cx,-1,size,1,1,1,1,1,profile.WaterLevel);
                    TerrainMeshData b=TerrainMeshBuilder.Build(field,cx+1,-1,size,1,1,1,1,1,profile.WaterLevel);
                    for(int i=0;i<=size;i++)Compare(r,a,i*(size+1)+size,b,i*(size+1));
                    r.edges++;
                    TerrainMeshData fine=TerrainMeshBuilder.Build(field,cx,-1,size,1,1,4,1,1,profile.WaterLevel);
                    TerrainMeshData coarse=TerrainMeshBuilder.Build(field,cx+1,-1,size,4,4,4,4,4,profile.WaterLevel);
                    for(int i=0;i<=size;i++)
                    {
                        int ai=i*(size+1)+size;int ca=i/4;int cb=Math.Min(size/4,ca+1);
                        float expected=Mathf.Lerp(coarse.Vertices[ca*(size/4+1)].y,coarse.Vertices[cb*(size/4+1)].y,(i%4)/4f);
                        r.maxHeightError=Mathf.Max(r.maxHeightError,Mathf.Abs(fine.Vertices[ai].y-expected));
                    }
                    r.lodEdges++;
                    foreach(int step in new[]{1,2,4})
                    {
                        TerrainMeshData current=TerrainMeshBuilder.Build(field,cx,-1,size,step,4,4,4,4,profile.WaterLevel);
                        TerrainMeshData neighbour=TerrainMeshBuilder.Build(field,cx+1,-1,size,4,4,4,4,4,profile.WaterLevel);
                        int stride=size/step+1,coarseStride=size/4+1;
                        for(int p=0;p<=size;p+=step)
                        {
                            int edgeIndex=p/step*stride+stride-1,ca=p/4*coarseStride,cb=Math.Min(size/4,p/4+1)*coarseStride;
                            float t=p%4/4f;
                            r.maxHeightError=Mathf.Max(r.maxHeightError,Mathf.Abs(current.Vertices[edgeIndex].y-Mathf.Lerp(neighbour.Vertices[ca].y,neighbour.Vertices[cb].y,t)));
                            Color d=current.Colors[edgeIndex]-Color.Lerp(neighbour.Colors[ca],neighbour.Colors[cb],t);
                            r.maxColorError=Mathf.Max(r.maxColorError,Mathf.Abs(d.r)+Mathf.Abs(d.g)+Mathf.Abs(d.b));
                        }
                        TerrainMeshData north=TerrainMeshBuilder.Build(field,cx,0,size,4,4,4,4,4,profile.WaterLevel);
                        for(int p=0;p<=size;p+=step)
                        {
                            int edgeIndex=(stride-1)*stride+p/step,ca=p/4,cb=Math.Min(size/4,ca+1);
                            r.maxHeightError=Mathf.Max(r.maxHeightError,Mathf.Abs(current.Vertices[edgeIndex].y-Mathf.Lerp(north.Vertices[ca].y,north.Vertices[cb].y,p%4/4f)));
                        }
                        r.streamingEdges+=2;
                    }
                }
            }
            if(r.maxHeightError>1e-5 || r.maxNormalError>1e-5 || r.maxColorError>1e-5)r.failures.Add("区块接缝误差超限。");
            List<ModuleVariant> variants=profile.Library.Variants();int n=profile.Library.PatchSize;
            if(variants.Count<2)r.failures.Add("模块库没有实际连接变体。");
            r.failures.AddRange(CheckModelSockets(profile.Library,out int checkedPorts));r.modelSockets=checkedPorts;
            for(uint seed=1;seed<=40;seed++)
            {
                WfcResult result=SocketWfc.Solve(variants,n,n,seed);
                if(!SocketWfc.CheckConnections(result,variants,n,n))r.failures.Add("连接解无效："+seed);
                else r.wfcSolutions++;
                WfcResult again=SocketWfc.Solve(variants,n,n,seed);
                if(result.Success!=again.Success || (result.Success && !Equal(result.Cells,again.Cells)))r.failures.Add("连接求解不确定："+seed);
            }
            var impossible=new List<ModuleVariant>{new ModuleVariant(0,0,1,1,1,1,1)};
            foreach(uint seed in new uint[]{0,1,41729,uint.MaxValue,90217})foreach(bool warehouse in new[]{false,true})
            {
                int height=warehouse?1:5;var building=BuildingWfc.Solve(3,2,height,seed,warehouse);
                var repeat=BuildingWfc.Solve(3,2,height,seed,warehouse);
                if(!building.Success||!repeat.Success||!Equal(building.Cells,repeat.Cells)){r.failures.Add("建筑拼装失败或不确定："+seed);continue;}
                for(int y=0;y<height;y++)for(int z=0;z<2;z++)for(int x=0;x<3;x++)
                {
                    var bay=BuildingWfc.Variants[building.Cells[BuildingWfc.Index(x,y,z,3,2)]];
                    if(y==0&&bay.Air)r.failures.Add("建筑底层缺失。");
                    if(y>0&&!bay.Air&&BuildingWfc.Variants[building.Cells[BuildingWfc.Index(x,y-1,z,3,2)]].Air)r.failures.Add("建筑楼层缺少支撑。");
                    bool aboveAir=y==height-1||BuildingWfc.Variants[building.Cells[BuildingWfc.Index(x,y+1,z,3,2)]].Air;
                    if(!bay.Air&&bay.Roof!=aboveAir)r.failures.Add("建筑屋顶封口错误。");
                    if(x<2&&!BuildingWfc.Compatible(building.Cells[BuildingWfc.Index(x,y,z,3,2)],building.Cells[BuildingWfc.Index(x+1,y,z,3,2)],3))r.failures.Add("相邻立面风格不匹配。");
                }
                r.buildingSolutions++;
            }
            foreach(Vector2 lot in TerrainLayout.HomeLots)
            {
                var entrance=lot+new Vector2(0,lot.y>0?-4:4);
                if(TerrainLayout.RoadDistance(entrance.x,entrance.y)>.01f)r.failures.Add("建筑入口未连接道路。");else r.entrances++;
            }
            if(SocketWfc.Solve(impossible,3,3,1).Success)r.failures.Add("矛盾连接没有被拒绝。");else r.negativeChecks++;
            if(SocketWfc.Solve(new List<ModuleVariant>(),3,3,1).Success)r.failures.Add("空库没有被拒绝。");else r.negativeChecks++;
            r.pass=r.failures.Count==0;File.WriteAllText(destination,JsonUtility.ToJson(r,true));
            if(!r.pass)throw new InvalidOperationException(string.Join("; ",r.failures));
            Debug.Log("PCG-TERRAIN-CHECKS: PASS; seeds="+r.seeds+"; seams="+r.edges+"; lod="+r.lodEdges+"; wfc="+r.wfcSolutions);
        }
        public static List<string> CheckModelSockets(TerrainModuleLibrary library,out int checkedPorts)
        {
            var failures=new List<string>();checkedPorts=0;
            foreach(TerrainModule m in library.Modules)
            {
                if(m.Empty)continue;
                if(!m.Mesh){failures.Add("模块缺少网格："+m.Id);continue;}
                if(m.Mesh.triangles.Length/3>1500)failures.Add("模块超出面数预算："+m.Id);
                Bounds b=m.Mesh.bounds;float half=library.CellSize*.5f;
                var ports=new[]{m.North,m.East,m.South,m.West};var ends=new[]{b.max.z,b.max.x,-b.min.z,-b.min.x};
                for(int d=0;d<4;d++)if(ports[d]!=0)
                {checkedPorts++;if(Mathf.Abs(ends[d]-half)>.025f)failures.Add("模型接口没有对齐格边："+m.Id+" / "+d);}
            }
            return failures;
        }
        private static bool Equal(int[]a,int[]b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static void Compare(Report r,TerrainMeshData a,int ai,TerrainMeshData b,int bi)
        {
            r.maxHeightError=Mathf.Max(r.maxHeightError,Mathf.Abs(a.Vertices[ai].y-b.Vertices[bi].y));
            r.maxNormalError=Mathf.Max(r.maxNormalError,(a.Normals[ai]-b.Normals[bi]).magnitude);
            Color d=a.Colors[ai]-b.Colors[bi];r.maxColorError=Mathf.Max(r.maxColorError,Mathf.Abs(d.r)+Mathf.Abs(d.g)+Mathf.Abs(d.b));
        }
    }
}
