using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace BinGames.TerrainVisual
{
    public sealed class TerrainDetailResult
    {
        public int Buildings,Bays,Failures;
        public readonly List<Mesh> OwnedMeshes=new List<Mesh>();
    }
    // Chunk ownership uses the object's absolute centre. Buildings are solved from world seeds,
    // so any chunk order obtains identical bays. Batches retain local coordinates after origin shifts.
    public sealed class TerrainDetailBuilder
    {
        public static Func<Vector2Int,int,int,Vector2[]> FoliageSampler;
        private readonly TerrainProfile p;private readonly TerrainField field;private readonly int seed;
        private readonly Transform parent;private readonly int x0,z0,size;
        private readonly List<CombineInstance> parts=new List<CombineInstance>();
        private readonly TerrainDetailResult result=new TerrainDetailResult();
        private TerrainDetailBuilder(TerrainProfile profile,TerrainField surface,int worldSeed,Vector2Int key,Transform root)
        {p=profile;field=surface;seed=worldSeed;parent=root;size=p.ChunkSize;x0=key.x*size;z0=key.y*size;}
        public static TerrainDetailResult Build(TerrainProfile p,TerrainField f,int seed,Vector2Int key,Transform root)
        {
            var b=new TerrainDetailBuilder(p,f,seed,key,root);
            if(!p.DetailKit||f==null)return b.result;
            b.Home();b.Settlements();b.Landmarks();b.Foliage();b.Flush();return b.result;
        }
        private bool Own(float x,float z)=>x>=x0&&x<x0+size&&z>=z0&&z<z0+size;
        private void Add(string id,float x,float y,float z,float angle=0,Vector3? scale=null)
        {
            if(!Own(x,z))return;Mesh mesh=p.DetailKit.Get(id);if(!mesh)throw new InvalidOperationException("环境素材缺失："+id);
            parts.Add(new CombineInstance{mesh=mesh,transform=Matrix4x4.TRS(new Vector3(x-x0,y,z-z0),Quaternion.Euler(0,angle,0),scale??Vector3.one)});
        }
        private void Home()
        {
            Add("pcg_home_core",0,field.Height(0,0),0);
            for(int i=0;i<TerrainLayout.HomeLots.Length;i++)
            {
                Vector2 lot=TerrainLayout.HomeLots[i];Building(lot,3,2,1,true,seed+i*17);
                Add("pcg_tank",lot.x-8,field.Height(lot.x-8,lot.y),lot.y);
                Add("pcg_tank",lot.x-8,field.Height(lot.x-8,lot.y+3),lot.y+3);
                for(int j=0;j<3;j++)Add("pcg_crate",lot.x+7+(j%2)*1.35f,field.Height(lot.x+7,lot.y),lot.y-2+j*1.5f);
            }
            for(int i=0;i<TerrainLayout.RoadStart.Length;i++)Road(TerrainLayout.RoadStart[i],TerrainLayout.RoadEnd[i],4.6f);
            // Supply lines terminate at the factories on the same metre grid as the old pipe kit.
            for(int side=-1;side<=1;side+=2)for(int x=4;x<=16;x+=4)
                AddLegacyPipe(side*x,1.0f,5,90);
        }
        private void AddLegacyPipe(float x,float y,float z,float angle)
        {
            if(!Own(x,z))return;
            if(!p.Library)return;
            foreach(TerrainModule m in p.Library.Modules)if(m.Mesh&&m.Mesh.name=="pcg_pipe_straight")
            {parts.Add(new CombineInstance{mesh=m.Mesh,transform=Matrix4x4.TRS(new Vector3(x-x0,field.Height(x,z)+y,z-z0),Quaternion.Euler(0,angle,0),Vector3.one)});break;}
        }
        private void Settlements()
        {
            for(int pz=Mathf.FloorToInt((z0-28)/96f);pz<=Mathf.CeilToInt((z0+size+28)/96f);pz++)
                for(int px=Mathf.FloorToInt((x0-28)/96f);px<=Mathf.CeilToInt((x0+size+28)/96f);px++)
                {
                    if(!field.HasSite(px,pz))continue;Vector2 c=field.SiteCenter(px,pz);
                    if(c.sqrMagnitude<55*55||field.RiverDistance(c.x,c.y)<p.RiverWidth*3.5f)continue;
                    for(int z=-1;z<=1;z+=2)for(int x=-1;x<=1;x+=2)
                    {
                        Vector2 lot=c+new Vector2(x*9,z*10);uint hash=TerrainField.Hash(px*4+x,pz*4+z,seed+119);
                        int levels=2+(int)(hash%(uint)Mathf.Max(1,p.DetailKit.MaxRuinFloors-1));Building(lot,2,3,levels,false,unchecked((int)hash));
                    }
                    Road(c+new Vector2(-21,0),c+new Vector2(21,0),3.6f);
                    Road(c+new Vector2(0,-23),c+new Vector2(0,23),3.6f);
                }
        }
        private void Building(Vector2 c,int w,int d,int floors,bool factory,int salt)
        {
            if(c.x+w*2<x0||c.x-w*2>=x0+size||c.y+d*2<z0||c.y-d*2>=z0+size)return;
            WfcResult solution=BuildingWfc.Solve(w,d,floors,TerrainField.Hash((long)c.x,(long)c.y,salt),factory,p.DetailKit.BuildingWeights);
            if(!solution.Success){result.Failures++;Debug.LogWarning(solution.Error);return;}
            if(Own(c.x,c.y))result.Buildings++;
            float baseY=field.Height(c.x,c.y);
            for(int y=0;y<floors;y++)for(int z=0;z<d;z++)for(int x=0;x<w;x++)
            {
                var bay=BuildingWfc.Variants[solution.Cells[BuildingWfc.Index(x,y,z,w,d)]];if(bay.Air)continue;
                string style=factory||bay.Theme==2?"factory":"ruin";
                float wx=c.x+(x-(w-1)*.5f)*4,wz=c.y+(z-(d-1)*.5f)*4,height=baseY+y*3.4f;
                if(Own(wx,wz))result.Bays++;
                Add("pcg_"+style+"_bay",wx,height,wz);
                if(bay.Roof)Add("pcg_"+style+"_roof",wx,height+3.4f,wz);
                int[]dx={0,1,0,-1},dz={1,0,-1,0};
                for(int side=0;side<4;side++)
                {
                    int nx=x+dx[side],nz=z+dz[side];bool outer=nx<0||nx>=w||nz<0||nz>=d;
                    if(!outer)outer=BuildingWfc.Variants[solution.Cells[BuildingWfc.Index(nx,y,nz,w,d)]].Air;
                    if(!outer)continue;
                    bool entrance=y==0&&x==w/2&&side==(c.y>0?2:0);
                    string facade=entrance?"door":factory?"window":TerrainField.Random01(x+y*17,z+salt,seed+533)<.72f?"broken":"window";
                    Add("pcg_"+style+"_"+facade,wx,height,wz,side*90);
                }
            }
        }
        private void Landmarks()
        {
            // A continuous highway with one deliberate collapsed span; both remaining ends align.
            for(int i=-5;i<=8;i++)
            {
                if(i==4||i==5)continue;float x=i*12,z=91,deck=21;
                Add("pcg_bridge_deck",x,deck,z,90);
                float ground=field.Height(x,z),h=deck-.6f-ground;
                if(h>1)Add("pcg_bridge_pier",x,ground,z,90,new Vector3(1,h/8f,1));
            }
            for(int gz=Mathf.FloorToInt(z0/8f)-1;gz<=Mathf.CeilToInt((z0+size)/8f);gz++)
                for(int side=-1;side<=1;side+=2)
                {
                    float z=gz*8+TerrainField.Random01(gz,side,seed+871)*3;
                    float x=side<0?-78-9*Mathf.Sin(z*.021f):109+7*Mathf.Sin(z*.025f);
                    x+=(TerrainField.Random01(gz,side,seed+878)-.5f)*7;
                    float y=field.Height(x,z);int v=(int)(TerrainField.Hash(gz,side,seed+873)%3);
                    float height=(side<0?1.5f:2.2f)*Mathf.Lerp(.62f,1.25f,TerrainField.Random01(gz,side,seed+879));
                    Add("pcg_cliff_"+v,x,y-2,z,TerrainField.Random01(gz,side,seed+875)*360,new Vector3(1.8f,height,1.8f));
                    if(gz%3==0)Add("pcg_cliff_"+((v+1)%3),x+side*7,y+3,z+2,35,new Vector3(1.3f,height*.85f,1.4f));
                }
        }
        private void Foliage()
        {
            if(FoliageSampler!=null)
            {
                foreach(Vector2 point in FoliageSampler(new Vector2Int(x0/size,z0/size),size,seed))Plant(point.x,point.y);
                return;
            }
            const float spacing=2.6f;
            for(int gz=Mathf.FloorToInt(z0/spacing)-1;gz<=Mathf.CeilToInt((z0+size)/spacing);gz++)
                for(int gx=Mathf.FloorToInt(x0/spacing)-1;gx<=Mathf.CeilToInt((x0+size)/spacing);gx++)
                {
                    float x=(gx+TerrainField.Random01(gx,gz,seed+331))*spacing,z=(gz+TerrainField.Random01(gx,gz,seed+332))*spacing;
                    Plant(x,z);
                }
        }
        private void Plant(float x,float z)
        {
            if(!Own(x,z)||TerrainLayout.Reserved(x,z))return;
            float radius=Mathf.Sqrt(x*x+z*z),chance=radius<42?p.Restoration*.7f:.07f;
            int gx=Mathf.FloorToInt(x*10),gz=Mathf.FloorToInt(z*10);
            if(TerrainField.Random01(gx,gz,seed+333)>chance||field.Height(x,z)<p.WaterLevel+.6f)return;
            float s=Mathf.Lerp(.75f,1.65f,TerrainField.Random01(gx,gz,seed+334));
            Add("pcg_scrub",x,field.Height(x,z),z,gx*63,new Vector3(s,s,s));
        }
        private void Road(Vector2 a,Vector2 b,float width)
        {
            Vector2 dir=(b-a).normalized,perp=new Vector2(-dir.y,dir.x)*width*.5f;int steps=Mathf.CeilToInt(Vector2.Distance(a,b)/2);
            var vertices=new List<Vector3>();var triangles=new List<int>();var colors=new List<Color>();
            for(int s=0;s<steps;s++)
            {
                Vector2 c0=Vector2.Lerp(a,b,s/(float)steps),c1=Vector2.Lerp(a,b,(s+1f)/steps),mid=(c0+c1)*.5f;
                if(!Own(mid.x,mid.y))continue;
                int n=vertices.Count;
                foreach(Vector2 v in new[]{c0-perp,c0+perp,c1-perp,c1+perp})
                {vertices.Add(new Vector3(v.x-x0,field.Height(v.x,v.y)+.035f,v.y-z0));colors.Add(new Color(.37f,.32f,.25f,1));}
                triangles.AddRange(new[]{n,n+2,n+1,n+1,n+2,n+3});
            }
            if(vertices.Count==0)return;
            var mesh=new Mesh{name="道路"};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.SetColors(colors);mesh.RecalculateNormals();
            result.OwnedMeshes.Add(mesh);parts.Add(new CombineInstance{mesh=mesh,transform=Matrix4x4.identity});
        }
        private void Flush()
        {
            if(parts.Count==0)return;
            var mesh=new Mesh{name="分块合并建筑与环境",indexFormat=IndexFormat.UInt32};mesh.CombineMeshes(parts.ToArray(),true,true);mesh.RecalculateBounds();
            result.OwnedMeshes.Add(mesh);var go=new GameObject(mesh.name);go.transform.SetParent(parent,false);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=p.PropMaterial;
            renderer.shadowCastingMode=ShadowCastingMode.On;
        }
    }
}
