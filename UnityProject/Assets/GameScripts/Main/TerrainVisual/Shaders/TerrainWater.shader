Shader "BinGames/Terrain/MutedWater"
{
    Properties { _Color ("水色", Color) = (.306,.4,.439,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float2 terrainUV; };
        void vert(inout appdata_full v, out Input o) { UNITY_INITIALIZE_OUTPUT(Input,o); o.terrainUV=v.texcoord.xy; }
        fixed4 _Color;
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 p=IN.terrainUV;
            float wave=sin(p.x*.43+p.y*.29+_Time.y*.45)*sin(p.y*.31-_Time.y*.28);
            o.Albedo=_Color.rgb*(1+wave*.045); o.Smoothness=.48; o.Metallic=.08;
            o.Normal=normalize(float3(wave*.045,cos(p.y*.31-_Time.y*.28)*.025,1)); o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
