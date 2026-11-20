Shader "BinGames/Terrain/ContinuousGround"
{
    Properties { _Detail ("地表细节强度", Range(0,1)) = .7 _Glossiness ("光滑度", Range(0,1)) = .06
        _CampaignMode("正式世界",Float)=0 _MainTex("正式地形与迷雾",2D)="white"{}
        _SoilTex ("Orbis 岩土",2D)="white"{} _CliffTex("Orbis 岩壁",2D)="white"{}
        _SoilNormal("岩土法线",2D)="bump"{} _TextureStrength("岩土纹理",Range(0,1))=.7 _UseOrbis("Orbis 原生网格",Float)=0
        _OrbisSoil("Orbis 土色",Color)=(.42,.35,.28,1) _OrbisRock("Orbis 岩色",Color)=(.48,.47,.44,1)
        _OrbisRestored("Orbis 恢复土地",Color)=(.37,.29,.21,1) _OrbisGrass("Orbis 草色",Color)=(.43,.55,.24,1)
        _OrbisRestoration("Orbis 恢复量",Float)=.65 }
    // PL-RP-02 → production/design/presentation-track/handoff/PL-RP-02.md：URP 子着色器（同属性、同算式；Built-in 仍走下面原 surface 子着色器）
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 200
        HLSLINCLUDE
        #include "TerrainUrp.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _Detail, _Glossiness, _CampaignMode, _TextureStrength, _UseOrbis, _OrbisRestoration;
            float4 _MainTex_ST, _SoilTex_ST, _CliffTex_ST, _SoilNormal_ST;
            float4 _OrbisSoil, _OrbisRock, _OrbisRestored, _OrbisGrass;
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
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_SoilTex); SAMPLER(sampler_SoilTex);
            TEXTURE2D(_CliffTex); SAMPLER(sampler_CliffTex);
            TEXTURE2D(_SoilNormal); SAMPLER(sampler_SoilNormal);
            struct A { float4 pos : POSITION; float3 n : NORMAL; float4 c : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 cs : SV_POSITION; float3 w : TEXCOORD0; float3 n : TEXCOORD1; float4 c : COLOR; float2 tuv : TEXCOORD2; float2 cuv : TEXCOORD3; half fog : TEXCOORD4; };
            V vert(A a)
            {
                UNITY_SETUP_INSTANCE_ID(a);
                V o = (V)0;
                o.w = TransformObjectToWorld(a.pos.xyz); o.cs = TransformWorldToHClip(o.w); o.n = TransformObjectToWorldNormal(a.n);
                o.c = a.c; o.tuv = a.uv; o.cuv = a.uv;
                if (_CampaignMode > .5) o.tuv = o.w.xz;
                if (_UseOrbis > .5) { o.tuv = o.w.xz + _TerrainWorldOrigin.xy; float rock = saturate((o.w.y - 2.5) / 12); o.c = float4(lerp(float3(.42,.35,.28), float3(.48,.47,.44), rock), rock); }
                o.fog = ComputeFogFactor(o.cs.z);
                return o;
            }
            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float noise2(float2 p) { float2 i = floor(p), f = frac(p); f = f * f * (3 - 2 * f); return lerp(lerp(hash21(i), hash21(i + float2(1,0)), f.x), lerp(hash21(i + float2(0,1)), hash21(i + 1), f.x), f.y); }
            float3 cliffTex(float2 uv) { return TG(SAMPLE_TEXTURE2D(_CliffTex, sampler_CliffTex, uv).rgb); }
            half4 frag(V i) : SV_Target
            {
                float2 p = i.tuv;
                float broad = noise2(p * .31), grain = noise2(p * 3.1);
                float crack = pow(saturate(1 - abs(noise2(p * 1.7) * 2 - 1)), 18) * .1;
                float shade = 1 + ((broad - .5) * .25 + (grain - .5) * .10 - crack) * _Detail;
                float3 nW = normalize(i.n); float3 n = abs(nW); float3 blend = pow(n, 4); blend /= max(.001, blend.x + blend.y + blend.z);
                float3 soil = TG(SAMPLE_TEXTURE2D(_SoilTex, sampler_SoilTex, p * .14).rgb);
                float3 rock = cliffTex(float2(p.y, i.w.y) * .09) * blend.x + cliffTex(p * .09) * blend.y + cliffTex(float2(p.x, i.w.y) * .09) * blend.z;
                float cliff = saturate((1 - n.y) * 2.8); float3 surfaceTex = lerp(soil, rock, cliff);
                float luminance = dot(surfaceTex, float3(.2126, .7152, .0722));
                float3 palette = i.c.rgb;
                if (_CampaignMode > .5) palette = TG(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.cuv).rgb);
                if (_UseOrbis > .5)
                {
                    float altitude = saturate((i.w.y - 2.5) / 7.5);
                    palette = lerp(TG(_OrbisSoil.rgb), TG(_OrbisRock.rgb), altitude);
                    float restored = 1 - smoothstep(12, 24, length(p)); palette = lerp(palette, TG(_OrbisRestored.rgb), restored * .6);
                    palette = lerp(palette, TG(_OrbisGrass.rgb), restored * smoothstep(.46, .66, noise2(p / 8)) * _OrbisRestoration * .6);
                }
                float3 albedo = palette * shade * lerp(1, lerp(luminance.xxx, surfaceTex, .18) * 1.9, _TextureStrength);
                float3 tn = normalize(lerp(float3(0,0,1), UnpackNormal(SAMPLE_TEXTURE2D(_SoilNormal, sampler_SoilNormal, p * .14)), .42 * (1 - cliff)));
                float3 nb = normalize(float3(1,0,0) * tn.x + float3(0,0,1) * tn.y + nW * tn.z);
                return TerrainLit(i.w, nb, i.cs, i.fog, TL(saturate(albedo)), 0, _Glossiness, 1 - crack * _Detail, 0);
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
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float2 terrainUV; float2 campaignUV; float3 worldPos; float3 worldNormal; INTERNAL_DATA float4 color : COLOR; };
        float _UseOrbis,_CampaignMode;float4 _TerrainWorldOrigin;
        void vert(inout appdata_full v, out Input o) {
            UNITY_INITIALIZE_OUTPUT(Input,o); o.terrainUV=v.texcoord.xy; o.campaignUV=v.texcoord.xy; o.color=v.color;
            if(_CampaignMode>.5){float3 world=mul(unity_ObjectToWorld,v.vertex).xyz;o.terrainUV=world.xz;}
            if(_UseOrbis>.5){float3 world=mul(unity_ObjectToWorld,v.vertex).xyz;float2 p=world.xz+_TerrainWorldOrigin.xy;o.terrainUV=p;
                float rock=saturate((world.y-2.5)/12);o.color=float4(lerp(float3(.42,.35,.28),float3(.48,.47,.44),rock),rock);}
        }
        float _Detail, _Glossiness;
        sampler2D _SoilTex,_CliffTex,_SoilNormal,_MainTex;float _TextureStrength;
        float4 _OrbisSoil,_OrbisRock,_OrbisRestored,_OrbisGrass;float _OrbisRestoration;
        float hash21(float2 p) { p=frac(p*float2(123.34,456.21)); p+=dot(p,p+45.32); return frac(p.x*p.y); }
        float noise2(float2 p) { float2 i=floor(p),f=frac(p); f=f*f*(3-2*f); return lerp(lerp(hash21(i),hash21(i+float2(1,0)),f.x),lerp(hash21(i+float2(0,1)),hash21(i+1),f.x),f.y); }
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 p=IN.terrainUV;
            float broad=noise2(p*.31), grain=noise2(p*3.1);
            float crack=pow(saturate(1-abs(noise2(p*1.7)*2-1)),18)*.1;
            float shade=1+((broad-.5)*.25+(grain-.5)*.10-crack)*_Detail;
            float3 n=abs(WorldNormalVector(IN,float3(0,0,1)));float3 blend=pow(n,4);blend/=max(.001,blend.x+blend.y+blend.z);
            float3 soil=tex2D(_SoilTex,p*.14).rgb;
            float3 rock=tex2D(_CliffTex,float2(p.y,IN.worldPos.y)*.09).rgb*blend.x+
                tex2D(_CliffTex,p*.09).rgb*blend.y+tex2D(_CliffTex,float2(p.x,IN.worldPos.y)*.09).rgb*blend.z;
            float cliff=saturate((1-n.y)*2.8);float3 surfaceTex=lerp(soil,rock,cliff);
            // Preserve the documented soil palette while using the imported asset's fine relief.
            float luminance=dot(surfaceTex,float3(.2126,.7152,.0722));
            float3 palette=IN.color.rgb;
            // 正式地图的动态贴图包含地形、污染和迷雾；世界 UV 只用于细节纹理。
            if(_CampaignMode>.5)palette=tex2D(_MainTex,IN.campaignUV).rgb;
            // Orbis vertex colours encode a biome scalar; they cannot be consumed as RGB.
            if(_UseOrbis>.5){float altitude=saturate((IN.worldPos.y-2.5)/7.5);
                palette=lerp(_OrbisSoil.rgb,_OrbisRock.rgb,altitude);
                float restored=1-smoothstep(12,24,length(p));palette=lerp(palette,_OrbisRestored.rgb,restored*.6);
                palette=lerp(palette,_OrbisGrass.rgb,restored*smoothstep(.46,.66,noise2(p/8))*_OrbisRestoration*.6);}
            o.Albedo=palette*shade*lerp(1,lerp(luminance.xxx,surfaceTex,.18)*1.9,_TextureStrength);
            o.Normal=normalize(lerp(float3(0,0,1),UnpackNormal(tex2D(_SoilNormal,p*.14)),.42*(1-cliff)));
            o.Metallic=0; o.Smoothness=_Glossiness;
            o.Occlusion=1-crack*_Detail; o.Alpha=1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
