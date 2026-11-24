Shader "BinGames/Terrain/MatteProps"
{
    Properties { _Color ("色调", Color) = (1,1,1,1) _RockTex("岩土表面",2D)="white"{} _TextureStrength("风化",Range(0,1))=.6 }
    // PL-RP-02 → production/design/presentation-track/handoff/PL-RP-02.md：URP 子着色器（同属性、同算式；Built-in 仍走下面原 surface 子着色器）
    // _Color 留在实例化缓冲里（TerrainStream.CreateProp 每件地物用属性块给色调），所以本子着色器不进 SRP Batcher，靠 GPU 实例化合批。
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        HLSLINCLUDE
        #include "TerrainUrp.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _TextureStrength;
            float4 _RockTex_ST;
        CBUFFER_END
        float4 _TerrainWorldOrigin;
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
            TEXTURE2D(_RockTex); SAMPLER(sampler_RockTex);
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)
            struct A { float4 pos : POSITION; float3 n : NORMAL; float4 c : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs : SV_POSITION; float3 w : TEXCOORD0; float3 n : TEXCOORD1; float4 c : COLOR; half fog : TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID };
            V vert(A a)
            {
                UNITY_SETUP_INSTANCE_ID(a);
                V o = (V)0;
                UNITY_TRANSFER_INSTANCE_ID(a, o);
                o.w = TransformObjectToWorld(a.pos.xyz); o.cs = TransformWorldToHClip(o.w); o.n = TransformObjectToWorldNormal(a.n);
                o.c = a.c; o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }
            float3 rockTex(float2 uv) { return TG(SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, uv).rgb); }
            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 nW = normalize(i.n); float3 n = abs(nW); float3 blend = pow(n, 4); blend /= max(.001, blend.x + blend.y + blend.z);
                float2 p = i.w.xz + _TerrainWorldOrigin.xy;
                float3 tex = rockTex(float2(p.y, i.w.y) * .16) * blend.x + rockTex(p * .16) * blend.y + rockTex(float2(p.x, i.w.y) * .16) * blend.z;
                float3 c = i.c.rgb;
                float grey = 1 - saturate(abs(c.r - c.g) * 6 + abs(c.g - c.b) * 6);
                float tone = lerp(.65, 1.45, saturate(dot(tex, float3(.3, .59, .11)) * 1.5));
                float3 albedo = c * TG(UNITY_ACCESS_INSTANCED_PROP(Props, _Color).rgb) * lerp(1, tone, _TextureStrength * grey);
                float3 emission = c * saturate((min(c.g, c.b) - c.r - .12) * 5) * 1.6;
                return TerrainLit(i.w, nW, i.cs, i.fog, TL(saturate(albedo)), .04, .09, 1, TL(emission));
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
