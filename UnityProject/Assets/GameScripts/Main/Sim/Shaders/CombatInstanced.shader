// FG0-ARCH-03（FGR-ARC-003 战斗内核）：单位与弹体的程序化实例着色器（Built-in RP，StructuredBuffer + SV_InstanceID）。
// 实例布局与 BinGames.Sim.Combat.CombatInstance 一致：A = (当前 x, 当前 z, 上一步 x, 上一步 z)，B = (半径, 血量比例, 阵营, 种类)。
// 位置 = lerp(上一步, 当前, _Alpha)：60 Hz 内核在任意帧率画面上连续。
// _Kind = 0：单位。圆片按阵营着色（己方青、敌方橙红），外圈血量环（缺血部分变暗）；敌方另叠斜纹，不只靠颜色区分（B15）。
// _Kind = 1：弹体。沿飞行方向拉长的发光短线（长度取本步位移）。美术是占位（B22）。
Shader "BinGames/CombatInstanced"
{
    Properties
    {
        _Kind ("Kind (0 unit, 1 projectile)", Float) = 0
        _Alpha ("Interpolation alpha", Float) = 1
        _Height ("Height", Float) = 0.6
    }

    SubShader
    {
        Tags { "Queue" = "Geometry+30" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct CombatInstance
            {
                float4 a;
                float4 b;
            };

            StructuredBuffer<CombatInstance> _Instances;
            float _Kind;
            float _Alpha;
            float _Height;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 data : TEXCOORD1;
            };

            v2f vert(appdata v, uint iid : SV_InstanceID)
            {
                CombatInstance it = _Instances[iid];
                float2 cur = it.a.xy;
                float2 prev = it.a.zw;
                float2 c = lerp(prev, cur, _Alpha);
                float2 local = v.vertex.xy;
                float2 p;
                if (_Kind < 0.5)
                {
                    float r = max(0.2, it.b.x);
                    p = c + local * (r * 2.0);
                }
                else
                {
                    float2 d = cur - prev;
                    float len = length(d);
                    float2 dir = len > 1e-4 ? d / len : float2(0, 1);
                    float2 perp = float2(-dir.y, dir.x);
                    float w = max(0.08, it.b.x * 1.6);
                    p = c + dir * (local.y * max(0.35, len * 2.0)) + perp * (local.x * w);
                }
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p.x, _Height, p.y, 1.0));
                o.uv = local + 0.5;
                o.data = it.b;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float faction = i.data.z;
                float3 friendCol = float3(0.25, 0.85, 0.95);
                float3 foeCol = float3(0.98, 0.45, 0.18);
                float3 col = faction < 0.5 ? friendCol : foeCol;
                if (_Kind > 0.5)
                {
                    float edge = abs(i.uv.x - 0.5) * 2.0;
                    if (edge > 1.0)
                    {
                        discard;
                    }
                    return fixed4(lerp(float3(1, 1, 0.85), col, edge), 1);
                }
                float2 q = i.uv - 0.5;
                float r = length(q) * 2.0;
                if (r > 1.0)
                {
                    discard;
                }
                float hp = saturate(i.data.y);
                if (r > 0.78)
                {
                    // 血量环：从正上方顺时针，缺血部分变暗。
                    float ang = atan2(q.x, q.y) / 6.2831853 + 0.5;
                    return fixed4(ang <= hp ? float3(0.35, 0.95, 0.35) : float3(0.18, 0.18, 0.18), 1);
                }
                if (faction > 0.5)
                {
                    float stripe = frac((i.uv.x + i.uv.y) * 4.0);
                    col *= stripe < 0.5 ? 1.0 : 0.72;
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
