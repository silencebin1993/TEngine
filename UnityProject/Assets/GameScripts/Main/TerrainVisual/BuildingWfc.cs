using System;
using System.Collections.Generic;
namespace BinGames.TerrainVisual
{
    // Finite building volume: six-face propagation, load-bearing vertical sockets, matching
    // facade families and bounded backtracking. Direction convention comes from marian42.
    public static class BuildingWfc
    {
        public readonly struct Bay
        {
            public readonly bool Air,Ground,Roof;
            public readonly int Theme;
            public readonly float Weight;
            public Bay(bool air,bool ground,bool roof,int theme,float weight)
            {Air=air;Ground=ground;Roof=roof;Theme=theme;Weight=weight;}
            public int Socket(int d)=>WfcDirections.IsHorizontal(d)?(Air?-1:Theme):
                d==WfcDirections.DOWN?(Air||Ground?0:10):(Air||Roof?0:10);
        }
        public static readonly Bay[] Variants={new Bay(true,false,false,0,.55f),
            new Bay(false,true,true,1,.3f),new Bay(false,true,false,1,2),
            new Bay(false,false,true,1,1.6f),new Bay(false,false,false,1,.55f),
            new Bay(false,true,true,2,.3f),new Bay(false,true,false,2,2),
            new Bay(false,false,true,2,1.6f),new Bay(false,false,false,2,.55f)};
        public static WfcResult Solve(int width,int depth,int floors,uint seed,bool warehouse=false,float[] weights=null)
        {
            if(width<1||depth<1||floors<1||width*depth*floors>512)
                return new WfcResult{Error="建筑体积无效或超出 512 格预算。"};
            var domains=new ulong[width*depth*floors];
            seed|=1;
            for(int y=0;y<floors;y++)for(int z=0;z<depth;z++)for(int x=0;x<width;x++)
            {
                ulong mask=0;
                for(int v=0;v<Variants.Length;v++)
                {
                    Bay bay=Variants[v];
                    if(y==0&&(bay.Air||!bay.Ground))continue;
                    if(y>0&&bay.Ground)continue;
                    if(y==floors-1&&!bay.Air&&!bay.Roof)continue;
                    // A warehouse is a complete one-storey shed; ruins may step down in height.
                    if(warehouse&&(bay.Air||!bay.Roof||bay.Theme!=2))continue;
                    mask|=1UL<<v;
                }
                domains[Index(x,y,z,width,depth)]=mask;
            }
            int observations=0,backtracks=0;
            bool success=Search(domains,width,depth,floors,ref seed,ref observations,ref backtracks,weights);
            var result=new WfcResult{Success=success,Attempts=backtracks+1,Observations=observations,
                Error=success?null:"建筑连接约束无法满足，回溯预算耗尽。"};
            if(success){result.Cells=new int[domains.Length];for(int i=0;i<domains.Length;i++)result.Cells[i]=First(domains[i]);}
            return result;
        }
        public static int Index(int x,int y,int z,int width,int depth)=>(y*depth+z)*width+x;
        private static int First(ulong mask){for(int i=0;i<Variants.Length;i++)if((mask&(1UL<<i))!=0)return i;return -1;}
        private static float Random(ref uint seed){seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;return(seed&0xffffff)/16777216f;}
        private static float Weight(int v,float[] weights)=>weights!=null&&weights.Length==Variants.Length?Math.Max(.001f,weights[v]):Variants[v].Weight;
        private static bool Search(ulong[] domains,int w,int d,int h,ref uint random,ref int observations,ref int backtracks,float[] weights)
        {
            if(!Propagate(domains,w,d,h))return false;
            int cell=-1;double min=double.MaxValue;
            for(int i=0;i<domains.Length;i++)
            {
                ulong mask=domains[i];if((mask&(mask-1))==0)continue;
                double sum=0,log=0;
                for(int v=0;v<Variants.Length;v++)if((mask&(1UL<<v))!=0)
                {double weight=Weight(v,weights);sum+=weight;log+=weight*Math.Log(weight);}
                double entropy=Math.Log(sum)-log/sum+Random(ref random)*.000001;
                if(entropy<min){min=entropy;cell=i;}
            }
            if(cell<0)return true;
            observations++;ulong remaining=domains[cell];
            while(remaining!=0&&backtracks<512)
            {
                float total=0;for(int v=0;v<Variants.Length;v++)if((remaining&(1UL<<v))!=0)total+=Weight(v,weights);
                float draw=Random(ref random)*total;int pick=First(remaining);
                for(int v=0;v<Variants.Length;v++)if((remaining&(1UL<<v))!=0)
                {draw-=Weight(v,weights);if(draw<=0){pick=v;break;}}
                ulong[] branch=(ulong[])domains.Clone();branch[cell]=1UL<<pick;
                if(Search(branch,w,d,h,ref random,ref observations,ref backtracks,weights))
                {Array.Copy(branch,domains,domains.Length);return true;}
                remaining&=~(1UL<<pick);backtracks++;
            }
            return false;
        }
        private static bool Propagate(ulong[] a,int w,int d,int h)
        {
            var queue=new Queue<int>();for(int i=0;i<a.Length;i++)queue.Enqueue(i);
            while(queue.Count>0)
            {
                int i=queue.Dequeue();if(a[i]==0)return false;
                int x=i%w,z=i/w%d,y=i/(w*d);
                for(int face=0;face<6;face++)
                {
                    var delta=WfcDirections.Direction[face];int nx=x+delta.x,ny=y+delta.y,nz=z+delta.z;
                    if(nx<0||nx>=w||ny<0||ny>=h||nz<0||nz>=d)continue;
                    int next=Index(nx,ny,nz,w,d);ulong keep=0;
                    for(int b=0;b<Variants.Length;b++)if((a[next]&(1UL<<b))!=0)
                        for(int v=0;v<Variants.Length;v++)if((a[i]&(1UL<<v))!=0&&Compatible(v,b,face))
                        {keep|=1UL<<b;break;}
                    if(keep!=a[next]){a[next]=keep;if(keep==0)return false;queue.Enqueue(next);}
                }
            }
            return true;
        }
        public static bool Compatible(int a,int b,int face)
        {
            int s=Variants[a].Socket(face),t=Variants[b].Socket(WfcDirections.Opposite(face));
            return s==t||(WfcDirections.IsHorizontal(face)&&(s==-1||t==-1));
        }
    }
}
