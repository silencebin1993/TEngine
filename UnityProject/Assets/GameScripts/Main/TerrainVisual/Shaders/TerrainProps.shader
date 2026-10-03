Shader "BinGames/Terrain/MatteProps"
{
    Properties { _Color ("色调", Color) = (1,1,1,1) _RockTex("岩土表面",2D)="white"{} _TextureStrength("风化",Range(0,1))=.6 }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        #pragma multi_compile_instancing
        struct Input { float4 color : COLOR;float3 worldPos;float3 worldNormal;INTERNAL_DATA };
        void vert(inout appdata_full v, out Input o) { UNITY_INITIALIZE_OUTPUT(Input,o); o.color=v.color; }
        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
        UNITY_INSTANCING_BUFFER_END(Props)
        sampler2D _RockTex;float _TextureStrength;float4 _TerrainWorldOrigin;
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float3 n=abs(WorldNormalVector(IN,float3(0,0,1)));float3 blend=pow(n,4);blend/=max(.001,blend.x+blend.y+blend.z);
            float2 p=IN.worldPos.xz+_TerrainWorldOrigin.xy;
            float3 tex=tex2D(_RockTex,float2(p.y,IN.worldPos.y)*.16).rgb*blend.x+
                tex2D(_RockTex,p*.16).rgb*blend.y+tex2D(_RockTex,float2(p.x,IN.worldPos.y)*.16).rgb*blend.z;
            float grey=1-saturate(abs(IN.color.r-IN.color.g)*6+abs(IN.color.g-IN.color.b)*6);
            float tone=lerp(.65,1.45,saturate(dot(tex,float3(.3,.59,.11))*1.5));
            o.Albedo=IN.color.rgb*UNITY_ACCESS_INSTANCED_PROP(Props,_Color).rgb*lerp(1,tone,_TextureStrength*grey);
            o.Emission=IN.color.rgb*saturate((min(IN.color.g,IN.color.b)-IN.color.r-.12)*5)*1.6;
            o.Metallic=.04;o.Smoothness=.09;o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
