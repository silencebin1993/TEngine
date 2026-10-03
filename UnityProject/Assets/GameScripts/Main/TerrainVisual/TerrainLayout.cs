using UnityEngine;
namespace BinGames.TerrainVisual
{
    // Preview art layout. These lots and roads never enter campaign placement or navigation data.
    public static class TerrainLayout
    {
        public static readonly Vector2[] HomeLots={new Vector2(-20,-15),new Vector2(20,-15),new Vector2(-20,15),new Vector2(20,15)};
        public static readonly Vector2[] RoadStart={new Vector2(-39,0),new Vector2(0,-34),new Vector2(-20,-11),new Vector2(20,-11),new Vector2(-20,0),new Vector2(20,0)};
        public static readonly Vector2[] RoadEnd={new Vector2(39,0),new Vector2(0,34),new Vector2(-20,0),new Vector2(20,0),new Vector2(-20,11),new Vector2(20,11)};
        public static float SegmentDistance(Vector2 p,Vector2 a,Vector2 b)
        {Vector2 delta=b-a;float t=Mathf.Clamp01(Vector2.Dot(p-a,delta)/delta.sqrMagnitude);return Vector2.Distance(p,a+delta*t);}
        public static bool Reserved(float x,float z)
        {
            Vector2 p=new Vector2(x,z);if(p.sqrMagnitude<7*7)return true;
            foreach(Vector2 lot in HomeLots)if(Mathf.Abs(x-lot.x)<7&&Mathf.Abs(z-lot.y)<5)return true;
            for(int i=0;i<RoadStart.Length;i++)if(SegmentDistance(p,RoadStart[i],RoadEnd[i])<2.7f)return true;
            return false;
        }
        public static float RoadDistance(float x,float z)
        {float d=float.MaxValue;for(int i=0;i<RoadStart.Length;i++)d=Mathf.Min(d,SegmentDistance(new Vector2(x,z),RoadStart[i],RoadEnd[i]));return d;}
    }
}
