// PL-RP-02 → production/design/presentation-track/handoff/PL-RP-02.md
// 3 个地形 shader 的 URP 子着色器共用：PBR 光照（主光阴影、Forward+ 附加灯、环境 SH、SSAO、雾）+ ShadowCaster / DepthOnly。
// 原 surface shader 的颜色数学是按 Gamma 调的：Linear 下先把贴图 / 材质色换回 Gamma 值（TG），照原式算完再换回线性（TL），观感贴近 Built-in。
#ifndef BINGAMES_TERRAIN_URP_INCLUDED
#define BINGAMES_TERRAIN_URP_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

#define TERRAIN_GAMMA_LIFT 0.85

#if defined(UNITY_COLORSPACE_GAMMA)
    #define TG(c) (c)
    #define TL(c) (c)
#else
    #define TG(c) LinearToSRGB(c)
    #define TL(c) SRGBToLinear(c)
#endif

half4 TerrainLit(float3 positionWS, float3 normalWS, float4 positionCS, half fogFactor, half3 albedo, half metallic, half smoothness, half occlusion, half3 emission)
{
    InputData d = (InputData)0;
    d.positionWS = positionWS;
    d.positionCS = positionCS;
    d.normalWS = normalize(normalWS);
    d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    d.shadowCoord = float4(GetNormalizedScreenSpaceUV(positionCS), 0, 1); // 屏幕空间阴影图按屏幕坐标采（同 URP Lit 的 GetShadowCoord）
#else
    d.shadowCoord = TransformWorldToShadowCoord(positionWS);
#endif
    d.fogCoord = fogFactor;
    d.bakedGI = SampleSH(d.normalWS);
    d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    d.shadowMask = half4(1, 1, 1, 1);

    SurfaceData s = (SurfaceData)0;
    s.albedo = albedo;
    s.metallic = metallic;
    s.smoothness = smoothness;
    s.occlusion = occlusion;
    s.emission = emission;
    s.alpha = 1;
    s.normalTS = half3(0, 0, 1);

    half4 c = UniversalFragmentPBR(d, s);
#if !defined(UNITY_COLORSPACE_GAMMA)
    // 原 shader 的光照在 Gamma 空间里累加（环境 + 主光直接相加）；Linear 下同样的灯相加后暗部压暗约 30%、黑块变多（PL-RP-02 实测 32×32 黑块 1 → 3、夜里 3 → 15）。
    // 受光颜色按显示伽马 TERRAIN_GAMMA_LIFT 提一次暗部，回到 Built-in 的明暗分布。shortcut: 全局常数，PL-TER-04（地形 shader 家族）/ PL-LOOK-01（曝光）接手时去掉
    c.rgb = TL(pow(TG(max(c.rgb, 0)), TERRAIN_GAMMA_LIFT));
#endif
    c.rgb = MixFog(c.rgb, fogFactor);
    c.a = 1;
    return c;
}

// ── ShadowCaster / DepthOnly（只要位置与法线） ──
float3 _LightDirection;
float3 _LightPosition;

struct TerrainDepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float4 TerrainShadowVert(TerrainDepthAttributes a) : SV_POSITION
{
    UNITY_SETUP_INSTANCE_ID(a);
    float3 w = TransformObjectToWorld(a.positionOS.xyz);
    float3 n = TransformObjectToWorldNormal(a.normalOS);
#if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
    float3 l = normalize(_LightPosition - w);
#else
    float3 l = _LightDirection;
#endif
    return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(w, n, l)));
}

float4 TerrainDepthVert(TerrainDepthAttributes a) : SV_POSITION
{
    UNITY_SETUP_INSTANCE_ID(a);
    return TransformObjectToHClip(a.positionOS.xyz);
}

half4 TerrainDepthFrag() : SV_Target
{
    return 0;
}

#endif
