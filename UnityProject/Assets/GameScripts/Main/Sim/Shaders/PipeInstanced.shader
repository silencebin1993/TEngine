// FG3-LOG-05（FGR-LOG-040“管线按所载流体上色，并有图标”）：管线程序化实例着色器（Built-in RP，StructuredBuffer + SV_InstanceID）。
// 实例布局与 BinGames.Sim.Logistics.PipeInstance 一致：A = (x, y, 种类 + 4 × 方向, 连接掩码)，B = (流体编号, 等级, 储罐液位 0～1, 标志)。
// 种类：0 管线（中心块 + 朝相连方向的管臂，T2 更粗）、1 泵（圆盘）、2 储罐（方罐 + 液位）、3 阀门（沿流向的蝶形，关着 = 横杠，两侧流体不同 = 红色斜纹）。
// 颜色 = _Palette[流体]（没有流体 = 灰）；标志 bit0 = 画图标：按流体的形状（_Glyphs：0 水滴、1 圆点、2 十字、3 三角、4 菱形），颜色之外还有形状（B15）。
// 占位美术（B22），美术批次替换。
Shader "BinGames/PipeInstanced"
{
    Properties
    {
        _CellSize ("Cell size", Float) = 1
        _Height ("Height", Float) = 0.05
    }

    SubShader
    {
        Tags { "Queue" = "Geometry+19" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct PipeInstance
            {
                float4 a;
                float4 b;
            };

            StructuredBuffer<PipeInstance> _Instances;
            float4 _Palette[32];
            float _Glyphs[32];
            float _CellSize;
            float _Height;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 p : TEXCOORD0;
                float4 a : TEXCOORD1;
                float4 b : TEXCOORD2;
            };

            v2f vert(appdata v, uint iid : SV_InstanceID)
            {
                PipeInstance it = _Instances[iid];
                float2 local = v.vertex.xy;
                float3 wp = float3(it.a.x + local.x * _CellSize, _Height, it.a.y + local.y * _CellSize);
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.p = local;
                o.a = it.a;
                o.b = it.b;
                return o;
            }

            float Bit(float mask, float bit)
            {
                return fmod(floor(mask / exp2(bit)), 2.0);
            }

            // 方向 d（0 北 1 东 2 南 3 西）的单位向量。
            float2 DirVec(float d)
            {
                return d < 0.5 ? float2(0, 1) : d < 1.5 ? float2(1, 0) : d < 2.5 ? float2(0, -1) : float2(-1, 0);
            }

            float Arms(float2 p, float mask, float w)
            {
                float body = step(abs(p.x), w) * step(abs(p.y), w);
                body = max(body, Bit(mask, 0) * step(abs(p.x), w) * step(0, p.y));
                body = max(body, Bit(mask, 1) * step(abs(p.y), w) * step(0, p.x));
                body = max(body, Bit(mask, 2) * step(abs(p.x), w) * step(p.y, 0));
                body = max(body, Bit(mask, 3) * step(abs(p.y), w) * step(p.x, 0));
                return body;
            }

            float Glyph(float2 p, float g)
            {
                float r = 0.13;
                if (g < 0.5)
                {
                    // 水滴：圆 + 上方尖角
                    float c = step(length(p + float2(0, 0.03)), r * 0.8);
                    float tri = step(abs(p.x), (0.16 - p.y) * 0.55) * step(-0.03, p.y) * step(p.y, 0.16);
                    return max(c, tri);
                }
                if (g < 1.5)
                {
                    return step(length(p), r);
                }
                if (g < 2.5)
                {
                    return max(step(abs(p.x), 0.04) * step(abs(p.y), r), step(abs(p.y), 0.04) * step(abs(p.x), r));
                }
                if (g < 3.5)
                {
                    return step(abs(p.x), (r - p.y) * 0.6) * step(-r, p.y) * step(p.y, r);
                }
                return step(abs(p.x) + abs(p.y), r * 1.2);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.p;
                float kindDir = i.a.z;
                // FG4-ECO-04：编码 = 种类（0～7）+ 8 × 方向（地下管线口是第 5 种）。
                float dir = floor((kindDir + 0.5) / 8.0);
                float kind = kindDir - dir * 8.0;
                float mask = i.a.w;
                float fluid = i.b.x;
                float tier = i.b.y;
                float level = saturate(i.b.z);
                float flags = i.b.w;
                int fi = (int)clamp(fluid + 0.5, 0, 31);
                float3 fcol = fluid > 0.5 ? _Palette[fi].rgb : float3(0.42, 0.42, 0.45);
                float3 dark = fcol * 0.45;
                float inside = 0;
                float3 col = fcol;

                if (kind < 0.5)
                {
                    float w = tier > 0.5 ? 0.2 : 0.14;
                    inside = Arms(p, mask, w);
                    float rim = inside * (1.0 - Arms(p, mask, w - 0.045));
                    col = lerp(fcol, dark, rim);
                }
                else if (kind < 1.5)
                {
                    float d = length(p);
                    float disc = step(d, 0.38);
                    inside = max(disc, Arms(p, mask, 0.13));
                    col = lerp(fcol, dark, step(0.3, d) * disc);
                    col = lerp(col, float3(0.15, 0.15, 0.17), step(abs(d - 0.2), 0.025));
                }
                else if (kind < 2.5)
                {
                    float2 ap = abs(p);
                    inside = step(max(ap.x, ap.y), 0.46);
                    float wall = step(0.39, max(ap.x, ap.y)) * inside;
                    float filled = step((p.y + 0.39) / 0.78, level);
                    col = lerp(float3(0.10, 0.10, 0.12), fcol, filled);
                    col = lerp(col, float3(0.55, 0.56, 0.58), wall);
                }
                else if (kind > 3.5)
                {
                    // FG4-ECO-04（FG-GAP-082）地下管线口：地面一侧伸出管臂，朝地下的一侧是带深色口沿的方形井盖 + 三道横纹（形状区分，B15）；
                    // 没配对（flags 第 3 位）叠红色斜纹。
                    float2 fwd = DirVec(dir);
                    float2 side = float2(fwd.y, -fwd.x);
                    float u = dot(p, fwd);
                    float s = dot(p, side);
                    float w = tier > 0.5 ? 0.2 : 0.14;
                    float arm = step(abs(s), w) * step(u, 0.0);
                    float hatch = step(abs(s), 0.36) * step(-0.1, u) * step(u, 0.36);
                    inside = max(arm, hatch);
                    col = lerp(fcol, dark, hatch * step(0.28, max(abs(s), abs(u - 0.13) + 0.05)));
                    float bands = hatch * step(0.5, frac(u * 9.0)) * step(abs(s), 0.24);
                    col = lerp(col, float3(0.12, 0.12, 0.14), bands * 0.8);
                    if (Bit(flags, 3) > 0.5)
                    {
                        float stripe = step(0.5, frac((p.x - p.y) * 6.0));
                        col = lerp(col, float3(0.9, 0.15, 0.1), stripe * 0.7);
                    }
                }
                else
                {
                    float2 fwd = DirVec(dir);
                    float2 side = float2(fwd.y, -fwd.x);
                    float u = dot(p, fwd);
                    float s = dot(p, side);
                    float axis = step(abs(s), 0.14);
                    float bow = step(abs(s), abs(u) * 0.9 + 0.05) * step(abs(u), 0.36);
                    inside = max(axis, bow);
                    col = lerp(fcol, dark, bow * step(0.28, abs(u)));
                    // 流向箭头：前半截的亮尖
                    col = lerp(col, float3(0.95, 0.95, 0.9), step(abs(s), (0.36 - u) * 0.35) * step(0.12, u) * step(u, 0.36) * 0.6);
                    if (Bit(flags, 1) > 0.5)
                    {
                        // 关着：横杠
                        float bar = step(abs(u), 0.05) * step(abs(s), 0.42);
                        inside = max(inside, bar);
                        col = lerp(col, float3(0.08, 0.08, 0.08), bar);
                    }
                    if (Bit(flags, 2) > 0.5)
                    {
                        float stripe = step(0.5, frac((p.x + p.y) * 6.0));
                        col = lerp(col, float3(0.9, 0.15, 0.1), stripe * 0.7);
                    }
                }
                if (inside < 0.5)
                {
                    discard;
                }
                if (Bit(flags, 0) > 0.5 && fluid > 0.5)
                {
                    float g = Glyph(p, _Glyphs[fi]);
                    float lum = dot(fcol, float3(0.299, 0.587, 0.114));
                    float3 gc = lum > 0.5 ? float3(0.05, 0.05, 0.07) : float3(0.97, 0.97, 0.95);
                    col = lerp(col, gc, g);
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
