Shader "BinGames/Terrain/ContinuousGround"
{
    Properties { _Detail ("地表细节强度", Range(0,1)) = .7 _Glossiness ("光滑度", Range(0,1)) = .06
        _SoilTex ("Orbis 岩土",2D)="white"{} _CliffTex("Orbis 岩壁",2D)="white"{}
        _SoilNormal("岩土法线",2D)="bump"{} _TextureStrength("岩土纹理",Range(0,1))=.7 _UseOrbis("Orbis 原生网格",Float)=0
        _OrbisSoil("Orbis 土色",Color)=(.42,.35,.28,1) _OrbisRock("Orbis 岩色",Color)=(.48,.47,.44,1)
        _OrbisRestored("Orbis 恢复土地",Color)=(.37,.29,.21,1) _OrbisGrass("Orbis 草色",Color)=(.43,.55,.24,1)
        _OrbisRestoration("Orbis 恢复量",Float)=.65 }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float2 terrainUV; float3 worldPos; float3 worldNormal; INTERNAL_DATA float4 color : COLOR; };
        float _UseOrbis;float4 _TerrainWorldOrigin;
        void vert(inout appdata_full v, out Input o) {
            UNITY_INITIALIZE_OUTPUT(Input,o); o.terrainUV=v.texcoord.xy; o.color=v.color;
            if(_UseOrbis>.5){float3 world=mul(unity_ObjectToWorld,v.vertex).xyz;float2 p=world.xz+_TerrainWorldOrigin.xy;o.terrainUV=p;
                float rock=saturate((world.y-2.5)/12);o.color=float4(lerp(float3(.42,.35,.28),float3(.48,.47,.44),rock),rock);}
        }
        float _Detail, _Glossiness;
        sampler2D _SoilTex,_CliffTex,_SoilNormal;float _TextureStrength;
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
