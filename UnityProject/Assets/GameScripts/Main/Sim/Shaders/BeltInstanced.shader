// FG0-ARCH-02（FGR-ARC-004 渲染 / FGR-LOG-090）：传送带程序化实例着色器（Built-in RP，StructuredBuffer + SV_InstanceID）。
// _Kind = 0：传送带格。近景 = 带面 + 沿带方向按真实带速滚动的箭头；远景（_Far = 1）= 流动贴图：条纹间距 = 物品间距、
//            按带速滚动、亮度 = 这一格的占用率——不逐物品绘制也能看出“流量多大、往哪流、哪里停了”。
//            堵塞格（B.z != 0）在两种模式下都叠斜纹 + 红色（FGR-LOG-025 图案加颜色，色盲也能区分），条纹 / 箭头停止滚动。
//            FG3-LOG-04：B.w = 等级 + 4 × 节点种类，分流器 / 合流器 / 地下入口 / 地下出口叠加各自的形状（地下段不画）。
// _Kind = 1：带上的物品。位置 = 本步末位置 −（1 − _Alpha）× 上一步位移（插值，20 Hz 内核在 60 帧画面上连续）；
//            颜色按物品种类哈希（占位，物品图标在 FG4 物品表落地后替换，B22）。
// FG3-LOG-08（FGR-LOG-080 叠加层）：_Overlay = 1 物品流向与吞吐（格按占用率上色：蓝 = 空、绿、黄、红 = 满；远景也画滚动箭头表示方向）；
//            2 堵塞（没堵的格压暗去色，堵塞格亮红 + 斜纹）；3 压暗（别的叠加层开着时让位）。只改参数，CPU 开销与格数无关。
// 实例布局与 BinGames.Sim.Logistics.BeltInstance 一致：A、B 两个 float4（32 字节）。
Shader "BinGames/BeltInstanced"
{
    Properties
    {
        _Kind ("Kind (0 belt, 1 item)", Float) = 0
        _Far ("Far view (flow texture)", Float) = 0
        _Alpha ("Interpolation alpha", Float) = 1
        _CellSize ("Cell size", Float) = 1
        _ItemSize ("Item size", Float) = 0.22
        _Height ("Height", Float) = 0.04
        _Overlay ("Overlay mode (0 none, 1 flow, 2 blockage, 3 dim)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Geometry+20" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct BeltInstance
            {
                float4 a;
                float4 b;
            };

            StructuredBuffer<BeltInstance> _Instances;
            float _Kind;
            float _Far;
            float _Alpha;
            float _CellSize;
            float _ItemSize;
            float _Height;
            float _GameTime; // FG3-LOG-03：游戏秒（BeltRenderer.AnimationTime）
            float _Overlay;  // FG3-LOG-08：叠加层画法
            float4 _ItemPalette[64]; // FG4-ECO-01：物品编号 0～63 的颜色（a = 1 表示物品表里有这一种；没有的仍按编号着色）

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
                BeltInstance it = _Instances[iid];
                float2 local = v.vertex.xy;
                float3 wp;
                if (_Kind < 0.5)
                {
                    float2 d = it.a.zw;
                    float2 perp = float2(-d.y, d.x);
                    float2 p = it.a.xy + (local.y * d + local.x * perp) * (_CellSize * 0.96);
                    wp = float3(p.x, _Height, p.y);
                }
                else
                {
                    float2 p = it.a.xy - it.a.zw * (1.0 - _Alpha) + local * _ItemSize * _CellSize;
                    wp = float3(p.x, _Height + 0.03, p.y);
                }
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.uv = local + 0.5;
                o.data = it.b;
                return o;
            }

            float3 Hue(float h)
            {
                float3 k = frac(h + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0;
                return saturate(abs(k) - 1.0);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Kind > 0.5)
                {
                    float2 c = abs(i.uv - 0.5);
                    if (max(c.x, c.y) > 0.46)
                    {
                        discard;
                    }
                    float3 col = lerp(Hue(frac(i.data.x * 0.618034)), float3(1, 1, 1), 0.25);
                    int pid = (int)round(i.data.x);
                    if (pid >= 0 && pid < 64 && _ItemPalette[pid].a > 0.5)
                    {
                        col = _ItemPalette[pid].rgb;
                    }
                    col *= (max(c.x, c.y) > 0.38) ? 0.55 : 1.0;
                    if (_Overlay > 1.5)
                    {
                        col *= 0.45; // 堵塞 / 压暗叠加层：物品也让位，只突出堵塞格。
                    }
                    return fixed4(col, 1);
                }

                float speed = i.data.x;
                float density = saturate(i.data.y);
                float blocked = i.data.z;
                // FG3-LOG-04：B.w = 等级 + 4 × 种类（0 传送带、1 分流器、2 合流器、3 地下入口、4 地下出口）。
                float node = floor((i.data.w + 0.5) / 4.0);
                float tier = i.data.w - node * 4.0;
                float t = blocked > 0.5 ? 0.0 : _GameTime * speed; // FG3-LOG-03：游戏时钟（暂停停住、倍速变快）
                float3 baseCol = lerp(float3(0.16, 0.17, 0.19), float3(0.30, 0.26, 0.16), tier * 0.5);
                float edge = step(0.42, abs(i.uv.x - 0.5));
                float3 col;
                if (_Far > 0.5)
                {
                    float band = frac(i.uv.y * 4.0 - t * 4.0);
                    float lit = smoothstep(0.45, 0.15, abs(band - 0.5)) * density;
                    col = baseCol * 0.8 + lit * float3(0.95, 0.78, 0.35);
                }
                else
                {
                    // 箭头尖朝带的前进方向（uv.y 增大的一侧），并按带速向前滚动。
                    float chev = frac(i.uv.y * 2.0 + abs(i.uv.x - 0.5) - t);
                    float arrow = step(0.8, chev) * (1.0 - edge);
                    col = baseCol + arrow * float3(0.18, 0.18, 0.2);
                }
                col = lerp(col, float3(0.05, 0.05, 0.06), edge * 0.7);
                // FG3-LOG-04：节点叠加形状（颜色之外有形状，色盲也能区分，B15；占位美术 B22）。
                // 分流器 = 从后方到中心的竖条 + 横贯左右的横条（“一进两出”）；合流器 = 横条 + 从中心到前方的竖条（“两进一出”）；
                // 地下入口 = 前半格是黑色洞口；地下出口 = 后半格是黑色洞口。
                float ax = abs(i.uv.x - 0.5);
                if (node > 0.5 && node < 1.5)
                {
                    float shape = max(step(ax, 0.09) * step(i.uv.y, 0.55), step(abs(i.uv.y - 0.5), 0.09));
                    col = lerp(col * 0.7 + float3(0.02, 0.10, 0.10), float3(0.25, 0.85, 0.80), shape);
                }
                else if (node > 1.5 && node < 2.5)
                {
                    float shape = max(step(ax, 0.09) * step(0.45, i.uv.y), step(abs(i.uv.y - 0.5), 0.09));
                    col = lerp(col * 0.7 + float3(0.10, 0.06, 0.0), float3(0.95, 0.65, 0.20), shape);
                }
                else if (node > 2.5)
                {
                    float mouth = node < 3.5 ? step(0.55, i.uv.y) : step(i.uv.y, 0.45);
                    float rim = mouth * step(0.3, ax);
                    col = lerp(col, float3(0.02, 0.02, 0.03), mouth);
                    col = lerp(col, float3(0.55, 0.50, 0.42), rim * 0.6);
                }
                if (_Overlay > 0.5 && _Overlay < 1.5)
                {
                    // 物品流向与吞吐：占用率热度（颜色）+ 方向箭头（形状，远近都画，色盲也能看出往哪流）。
                    float3 heat = density < 0.5 ? lerp(float3(0.15, 0.35, 0.85), float3(0.25, 0.80, 0.35), density * 2.0)
                                                : lerp(float3(0.95, 0.85, 0.20), float3(0.90, 0.25, 0.15), (density - 0.5) * 2.0);
                    float chev2 = frac(i.uv.y * 2.0 + abs(i.uv.x - 0.5) - t);
                    float arrow2 = step(0.75, chev2) * (1.0 - edge);
                    col = lerp(heat * 0.85, float3(0.95, 0.95, 0.95), arrow2 * 0.6);
                    col = lerp(col, float3(0.05, 0.05, 0.06), edge * 0.6);
                }
                else if (_Overlay > 1.5)
                {
                    float grey = dot(col, float3(0.3, 0.59, 0.11));
                    col = float3(grey, grey, grey) * 0.45;
                }
                if (blocked > 0.5)
                {
                    float hatch = step(0.5, frac((i.uv.x + i.uv.y) * 3.0));
                    if (_Overlay > 1.5 && _Overlay < 2.5)
                    {
                        col = lerp(float3(1.0, 0.18, 0.10), float3(1.0, 0.85, 0.20), hatch); // 堵塞叠加层：亮红 / 黄斜纹，一眼可见。
                    }
                    else
                    {
                        col = lerp(col, float3(0.85, 0.22, 0.12), 0.35 + 0.35 * hatch);
                    }
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
