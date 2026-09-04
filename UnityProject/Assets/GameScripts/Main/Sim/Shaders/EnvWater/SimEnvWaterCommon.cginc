#ifndef SIM_ENV_WATER_COMMON_INCLUDED
#define SIM_ENV_WATER_COMMON_INCLUDED

// 水体库共用：世界 XZ 顶点、竞技场归一化、玩家涟漪坐标
// 质量档 keyword：ENVFLUID_Q_LOW / MED / HIGH（由 EnvFluidBackground 统一开关）

#include "UnityCG.cginc"

float _ArenaHalf;
float4 _PlayerWorldXZ;
float _RippleStrength;

struct appdata_envwater
{
    float4 vertex : POSITION;
};

struct v2f_envwater
{
    float4 vertex : SV_POSITION;
    float3 worldPos : TEXCOORD0;
};

v2f_envwater vert_envwater(appdata_envwater v)
{
    v2f_envwater o;
    float4 wp = mul(unity_ObjectToWorld, v.vertex);
    o.worldPos = wp.xyz;
    o.vertex = mul(UNITY_MATRIX_VP, wp);
    return o;
}

// p ≈ 竞技场归一化世界坐标（中心 0，边缘约 ±1）
// uv ≈ 同空间平移到 0..1 附近，供原 WebGL 水体公式使用
void EnvWaterCoords(float3 worldPos, out float2 p, out float2 uv, out float2 playerP, out float halfExtent)
{
    halfExtent = max(_ArenaHalf, 1.0);
    p = worldPos.xz / halfExtent;
    uv = p * 0.5 + 0.5;
    playerP = _PlayerWorldXZ.xy / halfExtent * 0.5 + 0.5;
}

#endif
