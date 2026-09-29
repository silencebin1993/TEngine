// FG0-ARCH-03（FGR-ARC-003 战斗内核）：单位与弹体的程序化实例着色器（Built-in RP，StructuredBuffer + SV_InstanceID）。
// 实例布局与 BinGames.Sim.Combat.CombatInstance 一致：A = (当前 x, 当前 z, 上一步 x, 上一步 z)，B = (半径, 血量比例, 阵营, 种类)。
// 位置 = lerp(上一步, 当前, _Alpha)：60 Hz 内核在任意帧率画面上连续。
// _Kind = 0：单位。圆片按阵营着色（己方青、敌方橙红），外圈血量环（缺血部分变暗）；敌方另叠斜纹，不只靠颜色区分（B15）。
// _Kind = 1：弹体。沿飞行方向拉长的发光短线（长度取本步位移）。美术是占位（B22）。
// _Kind = 2：FG2-FW-03 头顶状态标签图标（FGR-FW-031）。B = (边长, 形状序号, 打包颜色 0xRRGGBB, 30 + 叠层)。形状为主、颜色为辅（B15 色盲安全）：
//            深色底板 + 按形状序号画的符号（▲●◆◇★▼■☆◎○※△□▽ 依次 0～13），底部小点 = 叠层数。
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

            // 形状距离场（p 在 [-1, 1]，< 0 = 在形状内）。
            float sdTri(float2 p, float r)
            {
                const float k = 1.7320508;
                p.x = abs(p.x) - r;
                p.y = p.y + r / k;
                if (p.x + k * p.y > 0.0) p = float2(p.x - k * p.y, -k * p.x - p.y) / 2.0;
                p.x -= clamp(p.x, -2.0 * r, 0.0);
                return -length(p) * sign(p.y);
            }

            float sdStar(float2 p, float r, float rf)
            {
                const float2 k1 = float2(0.809016994375, -0.587785252292);
                const float2 k2 = float2(-k1.x, k1.y);
                p.x = abs(p.x);
                p -= 2.0 * max(dot(k1, p), 0.0) * k1;
                p -= 2.0 * max(dot(k2, p), 0.0) * k2;
                p.x = abs(p.x);
                p.y -= r;
                float2 ba = rf * float2(-k1.y, k1.x) - float2(0, 1);
                float h = clamp(dot(p, ba) / dot(ba, ba), 0.0, r);
                return length(p - ba * h) * sign(p.y * ba.x - p.x * ba.y);
            }

            float shapeSd(float2 p, int shape)
            {
                float ring = 0.12;
                if (shape == 0) return sdTri(float2(p.x, p.y + 0.12), 0.8);
                if (shape == 1) return length(p) - 0.72;
                if (shape == 2) return (abs(p.x) + abs(p.y)) - 0.8;
                if (shape == 3) return abs((abs(p.x) + abs(p.y)) - 0.7) - ring;
                if (shape == 4) return sdStar(p, 0.85, 0.45);
                if (shape == 5) return sdTri(float2(p.x, -p.y + 0.12), 0.8);
                if (shape == 6) return max(abs(p.x), abs(p.y)) - 0.62;
                if (shape == 7) return abs(sdStar(p, 0.8, 0.45)) - ring * 0.8;
                if (shape == 8) return min(abs(length(p) - 0.66) - ring * 0.8, length(p) - 0.26);
                if (shape == 9) return abs(length(p) - 0.64) - ring;
                if (shape == 10)
                {
                    float2 a = abs(p);
                    float xs = abs(a.x - a.y) * 0.7071 - 0.1;
                    xs = max(xs, max(a.x, a.y) - 0.62);
                    float dots = min(min(length(p - float2(0, 0.62)), length(p + float2(0, 0.62))), min(length(p - float2(0.62, 0)), length(p + float2(0.62, 0)))) - 0.12;
                    return min(xs, dots);
                }
                if (shape == 11) return abs(sdTri(float2(p.x, p.y + 0.12), 0.72)) - ring * 0.8;
                if (shape == 12) return abs(max(abs(p.x), abs(p.y)) - 0.56) - ring;
                return abs(sdTri(float2(p.x, -p.y + 0.12), 0.72)) - ring * 0.8;
            }

            v2f vert(appdata v, uint iid : SV_InstanceID)
            {
                CombatInstance it = _Instances[iid];
                float2 cur = it.a.xy;
                float2 prev = it.a.zw;
                float2 c = lerp(prev, cur, _Alpha);
                float2 local = v.vertex.xy;
                float2 p;
                if (_Kind > 1.5)
                {
                    p = c + local * max(0.1, it.b.x);
                }
                else if (_Kind < 0.5)
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
                if (_Kind > 1.5)
                {
                    float2 q2 = (i.uv - 0.5) * 2.0;
                    uint packed = (uint)round(i.data.z);
                    float3 tint = float3((packed >> 16) & 255, (packed >> 8) & 255, packed & 255) / 255.0;
                    int stacks = (int)round(i.data.w - 30.0);
                    // 底部叠层小点（1～3 个）。
                    for (int s = 0; s < 3; s++)
                    {
                        if (s < stacks)
                        {
                            float2 dotC = float2((s - (stacks - 1) * 0.5) * 0.36, -0.86);
                            if (length(q2 - dotC) < 0.11) return fixed4(1, 1, 1, 1);
                        }
                    }
                    float sd = shapeSd(float2(q2.x, q2.y + 0.1) * 1.12, (int)round(i.data.y));
                    if (sd < 0.0) return fixed4(tint, 1);
                    if (sd < 0.1) return fixed4(0.06, 0.06, 0.07, 1);
                    // 深色圆角底板，保证在任何地面颜色上都看得清。
                    float plate = max(abs(q2.x), abs(q2.y + 0.05)) - 0.92;
                    if (plate < 0.0) return fixed4(0.1, 0.11, 0.13, 1);
                    discard;
                    return 0;
                }
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
