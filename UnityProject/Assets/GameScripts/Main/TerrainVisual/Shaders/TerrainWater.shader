Shader "BinGames/Terrain/MutedWater"
{
    Properties { _Color ("水色", Color) = (.306,.4,.439,1) _CampaignMode("正式世界",Float)=0 }
    // PL-RP-02 → production/design/presentation-track/handoff/PL-RP-02.md：URP 子着色器（同属性、同算式；Built-in 仍走下面原 surface 子着色器）
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" "RenderPipeline"="UniversalPipeline" }
        HLSLINCLUDE
        #include "TerrainUrp.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _CampaignMode;
        CBUFFER_END
        ENDHLSL
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            struct A { float4 pos : POSITION; float3 n : NORMAL; float4 c : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs : SV_POSITION; float3 w : TEXCOORD0; float3 n : TEXCOORD1; float4 c : COLOR; float2 uv : TEXCOORD2; half fog : TEXCOORD3; };
            V vert(A a)
            {
                UNITY_SETUP_INSTANCE_ID(a);
                V o = (V)0;
                o.w = TransformObjectToWorld(a.pos.xyz); o.cs = TransformWorldToHClip(o.w); o.n = TransformObjectToWorldNormal(a.n);
                o.c = a.c; o.uv = a.uv; o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                float2 p = i.uv;
                float wave = sin(p.x * .43 + p.y * .29 + _Time.y * .45) * sin(p.y * .31 - _Time.y * .28);
                float3 albedo = lerp(TG(_Color.rgb), i.c.rgb, step(.5, _CampaignMode)) * (1 + wave * .045);
                float3 tn = normalize(float3(wave * .045, cos(p.y * .31 - _Time.y * .28) * .025, 1));
                float3 nb = normalize(float3(1,0,0) * tn.x + float3(0,0,1) * tn.y + normalize(i.n) * tn.z);
                return TerrainLit(i.w, nb, i.cs, i.fog, TL(saturate(albedo)), .08, .48, 1, 0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex TerrainShadowVert
            #pragma fragment TerrainDepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex TerrainDepthVert
            #pragma fragment TerrainDepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float2 terrainUV; float4 color : COLOR; };
        void vert(inout appdata_full v, out Input o) { UNITY_INITIALIZE_OUTPUT(Input,o); o.terrainUV=v.texcoord.xy; o.color=v.color; }
        fixed4 _Color;float _CampaignMode;
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 p=IN.terrainUV;
            float wave=sin(p.x*.43+p.y*.29+_Time.y*.45)*sin(p.y*.31-_Time.y*.28);
            o.Albedo=lerp(_Color.rgb,IN.color.rgb,step(.5,_CampaignMode))*(1+wave*.045); o.Smoothness=.48; o.Metallic=.08;
            o.Normal=normalize(float3(wave*.045,cos(p.y*.31-_Time.y*.28)*.025,1)); o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
