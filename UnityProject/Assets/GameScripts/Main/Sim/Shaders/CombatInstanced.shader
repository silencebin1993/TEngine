// FG0-ARCH-03（FGR-ARC-003 战斗内核）：单位与弹体的程序化实例着色器（Built-in RP，StructuredBuffer + SV_InstanceID）。
// 实例布局与 BinGames.Sim.Combat.CombatInstance 一致：A = (当前 x, 当前 z, 上一步 x, 上一步 z)，B = (半径, 血量比例, 阵营, 种类)。
// 位置 = lerp(上一步, 当前, _Alpha)：60 Hz 内核在任意帧率画面上连续。
// _Kind = 0：单位。圆片按阵营着色（己方青、敌方橙红），外圈血量环（缺血部分变暗）；敌方另叠斜纹，不只靠颜色区分（B15）。
// _Kind = 1：弹体。沿飞行方向拉长的发光短线（长度取本步位移）。美术是占位（B22）。
// _Kind = 2：FG2-FW-03 头顶状态标签图标（FGR-FW-031）。B = (边长, 形状序号, 打包颜色 0xRRGGBB, 30 + 叠层)。形状为主、颜色为辅（B15 色盲安全）：
//            深色底板 + 按形状序号画的符号（▲●◆◇★▼■☆◎○※△□▽ 依次 0～13），底部小点 = 叠层数。
// FG2-VFX-02（DEBT-FG2FW02-02）：区域与无人机（_Kind = 0 那一路里 B.w ≥ 20 的实例）。B = (半径, 剩余时间比例, 打包颜色 0xRRGGBB, 种类)。
//            种类 20 液池（外移的波纹）/ 21 冲击波（随时间外扩的冲击环）/ 22 减速网（网格）/ 23 反应残留（斑点）；外圈一道 = 剩余时间。
//            24 伴飞无人机（X 形机臂 + 四个旋翼环）/ 25 定点哨戒桩（三脚架 + 闪烁信标 + 六边形底座）。颜色 = 区域挂的状态标签色（火 / 酸 / 电……）或阵营色。
//            仍是程序化的占位外观（正式粒子 / 贴图归美术批次），但不再是和单位一样的圆片。
// FG5-RND-03（FGR-RND-031）：_Holo = 1 时（靶场的仿真投影与投影靶）单位与弹体改画全息：己方投影青白、投影靶淡紫，
//            按游戏时间向上滚动的扫描线把圆片镂空成横条（形状差异，不只靠颜色，B15），外圈血量环照画。美术占位（B22）。
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
            // FG2-VFX-02 修复轮：区域 / 无人机动画用游戏时钟（战略暂停时静止、倍速时同步加快），由 CombatRenderer 每帧传入内核时间。
            float _GameTime;
            // FG5-RND-03：全息画法开关（CombatRenderer.Hologram）。
            float _Holo;

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
                    if (abs(it.b.w - 10.0) < 0.5)
                    {
                        // FG2-E2E-01（FG-GAP-043）：引信弹迹——A = (命中点, 炮口)，按两端画一条不插值的细亮线。
                        p = (cur + prev) * 0.5 + dir * (local.y * len) + perp * (local.x * 0.12);
                    }
                    else
                    {
                        p = c + dir * (local.y * max(0.35, len * 2.0)) + perp * (local.x * w);
                    }
                }
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p.x, _Height, p.y, 1.0));
                o.uv = local + 0.5;
                o.data = it.b;
                return o;
            }

            float3 unpackRgb(float v)
            {
                uint packed = (uint)round(v);
                return float3((packed >> 16) & 255, (packed >> 8) & 255, packed & 255) / 255.0;
            }

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // FG2-VFX-02：区域与无人机。
            fixed4 effectFrag(float2 uv, float4 d)
            {
                float2 q = (uv - 0.5) * 2.0;
                float r = length(q);
                float3 col = unpackRgb(d.z);
                float t = _GameTime;
                int kind = (int)round(d.w);
                if (kind == 26)
                {
                    // FG2-E2E-01（FG-GAP-043）：炮口装定闪光——四角星芒 + 亮心，按剩余比例收缩淡出。
                    float fade = saturate(d.y);
                    float2 a2 = abs(q);
                    float star = min(a2.x, a2.y) * 6.0 + max(a2.x, a2.y);
                    if (r < 0.35 * fade + 0.1) return fixed4(lerp(col, float3(1, 1, 1), 0.6), 1);
                    if (star < 0.9 * fade) return fixed4(col, 1);
                    discard;
                    return 0;
                }
                if (kind >= 24)
                {
                    if (kind == 24)
                    {
                        // 伴飞无人机：白色机身 + X 形机臂 + 四个旋转的旋翼环（旋翼环带一道缺口随时间转，看得出在飞）。
                        if (r < 0.2) return fixed4(0.95, 0.97, 1.0, 1);
                        float2 a = abs(q);
                        if (abs(a.x - a.y) * 0.7071 < 0.08 && max(a.x, a.y) < 0.6) return fixed4(col * 0.55, 1);
                        float2 c = float2(q.x > 0 ? 0.52 : -0.52, q.y > 0 ? 0.52 : -0.52);
                        float2 rq = q - c;
                        float rr = length(rq);
                        if (abs(rr - 0.3) < 0.07)
                        {
                            float ang = frac(atan2(rq.x, rq.y) / 6.2831853 + t * 3.0);
                            if (ang > 0.15) return fixed4(col, 1);
                        }
                        discard;
                        return 0;
                    }
                    // 定点哨戒桩：六边形底座 + 三条支腿 + 中心信标（按 1.2 秒闪烁）。
                    float2 h = abs(q);
                    float hex = max(h.x * 0.866 + h.y * 0.5, h.y) - 0.78;
                    if (abs(hex) < 0.07) return fixed4(col * 0.7, 1);
                    for (int k = 0; k < 3; k++)
                    {
                        float a0 = k * 2.0943951 + 1.5707963;
                        float2 dir = float2(cos(a0), sin(a0));
                        float along = dot(q, dir);
                        float across = abs(dot(q, float2(-dir.y, dir.x)));
                        if (along > 0.15 && along < 0.72 && across < 0.06) return fixed4(0.75, 0.78, 0.82, 1);
                    }
                    if (r < 0.2)
                    {
                        float blink = step(0.5, frac(t / 1.2));
                        return fixed4(lerp(col * 0.5, float3(1, 1, 1), blink), 1);
                    }
                    discard;
                    return 0;
                }
                if (r > 1.0)
                {
                    discard;
                }
                float left = saturate(d.y);
                if (r > 0.9)
                {
                    // 外圈：剩余时间（从正上方顺时针，已过去的部分变暗）。
                    float ang = atan2(q.x, q.y) / 6.2831853 + 0.5;
                    return fixed4(ang <= left ? col : col * 0.3, 1);
                }
                int look = kind - 20;
                if (look == 1)
                {
                    // 冲击波：主冲击环随时间从中心外扩到边缘，内侧细密的震荡纹。
                    float front = 1.0 - left;
                    if (abs(r - front * 0.88) < 0.09) return fixed4(lerp(col, float3(1, 1, 1), 0.45), 1);
                    if (r < front * 0.88 && frac(r * 7.0 - t * 3.0) < 0.12) return fixed4(col * 0.8, 1);
                    discard;
                    return 0;
                }
                if (look == 2)
                {
                    // 减速网：斜交网格 + 结点。
                    float2 g = float2(q.x + q.y, q.x - q.y) * 3.0;
                    float2 fg = abs(frac(g) - 0.5);
                    if (min(fg.x, fg.y) < 0.07) return fixed4(col, 1);
                    if (length(fg - 0.5) < 0.12) return fixed4(lerp(col, float3(1, 1, 1), 0.5), 1);
                    discard;
                    return 0;
                }
                if (look == 3)
                {
                    // 反应残留：随机斑点，慢慢闪。
                    float2 cell = floor(q * 7.0);
                    float n = hash21(cell);
                    if (n > 0.62 && length(frac(q * 7.0) - 0.5) < 0.32 * (0.6 + 0.4 * sin(t * 2.0 + n * 6.2831853))) return fixed4(col * 0.9, 1);
                    discard;
                    return 0;
                }
                // 液池：一圈圈向外移动的波纹 + 零星气泡。
                if (frac(r * 4.0 - t * 0.6) < 0.2) return fixed4(col, 1);
                float2 cellB = floor(q * 9.0 + float2(0, t * 0.8));
                if (hash21(cellB) > 0.93 && length(frac(q * 9.0 + float2(0, t * 0.8)) - 0.5) < 0.22) return fixed4(lerp(col, float3(1, 1, 1), 0.35), 1);
                discard;
                return 0;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Kind < 0.5 && i.data.w > 19.5 && i.data.w < 29.5)
                {
                    return effectFrag(i.uv, i.data);
                }
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
                if (_Kind > 0.5 && abs(i.data.w - 10.0) < 0.5)
                {
                    // FG2-E2E-01（FG-GAP-043）：引信弹迹——琥珀白亮线，按剩余比例变细变暗。
                    float edgeT = abs(i.uv.x - 0.5) * 2.0;
                    if (edgeT > saturate(i.data.y) + 0.05)
                    {
                        discard;
                    }
                    return fixed4(lerp(float3(1, 0.95, 0.8), float3(1, 0.82, 0.48), edgeT), 1);
                }
                if (_Holo > 0.5)
                {
                    col = faction < 0.5 ? float3(0.55, 0.95, 1.0) : float3(0.82, 0.62, 1.0);
                }
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
                if (_Holo > 0.5)
                {
                    // 全息：扫描线横条镂空（随游戏时间上移；暂停时静止），中心亮核，边缘一圈细亮线。
                    float scan = frac(i.uv.y * 7.0 - _GameTime * 0.8);
                    if (scan < 0.38 && r > 0.22 && r < 0.7)
                    {
                        discard;
                    }
                    float3 rim = faction < 0.5 ? float3(0.85, 1.0, 1.0) : float3(0.95, 0.85, 1.0);
                    if (faction > 0.5 && frac((i.uv.x + i.uv.y) * 4.0) >= 0.5)
                    {
                        col *= 0.72; // 投影靶保留斜纹（与己方投影的形状差异）
                    }
                    return fixed4(r > 0.7 ? rim : lerp(rim, col, saturate(r * 1.6)), 1);
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
