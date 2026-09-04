Shader "BinGames/EnvWater/InkGold"
{
    // 水体库 · 万塔黑底 + 金纹水彩（与蓝水气质不同，作风格变体）
    Properties
    {
        _ArenaHalf ("Arena Half", Float) = 90
        _PlayerWorldXZ ("Player World XZ", Vector) = (0, 0, 0, 0)
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
        _Gold ("Gold", Color) = (1.0, 0.8, 0.2, 1)
        _Ink ("Ink", Color) = (0.005, 0.005, 0.01, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Geometry-10" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_envwater
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local ENVFLUID_Q_MED ENVFLUID_Q_LOW ENVFLUID_Q_HIGH
            #include "SimEnvWaterCommon.cginc"

            float4 _Gold;
            float4 _Ink;

            float random(float2 st)
            {
                return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
            }

            float noise(float2 st)
            {
                float2 i = floor(st);
                float2 f = frac(st);
                float a = random(i);
                float b = random(i + float2(1.0, 0.0));
                float c = random(i + float2(0.0, 1.0));
                float d = random(i + float2(1.0, 1.0));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
            }

            float fbmN(float2 st)
            {
                float value = 0.0;
                float amplitude = 0.5;
                #if defined(ENVFLUID_Q_LOW)
                const int OCT = 3;
                #elif defined(ENVFLUID_Q_HIGH)
                const int OCT = 6;
                #else
                const int OCT = 4;
                #endif
                for (int i = 0; i < OCT; i++)
                {
                    value += amplitude * noise(st);
                    st *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            fixed4 frag(v2f_envwater i) : SV_Target
            {
                float2 p, uv, mouse;
                float halfExtent;
                EnvWaterCoords(i.worldPos, p, uv, mouse, halfExtent);

                float time = _Time.y;
                float mouseDistance = distance(uv, mouse);
                float mouseInfluence = smoothstep(0.5, 0.0, mouseDistance) * _RippleStrength;

                float flow = fbmN(uv * 3.0 + time * 0.1);
                flow += fbmN(uv * 6.0 - time * 0.15) * 0.5;
                flow += mouseInfluence * 0.3;

                float pattern1 = fbmN(uv * 2.0 + flow * 0.8);
                float pattern2 = fbmN(uv * 4.0 - flow * 1.2);

                float blend = smoothstep(0.3, 0.7, pattern1 * pattern2);
                blend = pow(blend, 1.5 + mouseInfluence);

                float highlight = smoothstep(0.5, 0.51, pattern1) * smoothstep(0.49, 0.5, pattern2);
                highlight *= 0.8 + 0.2 * sin(time * 0.5);

                float veins = smoothstep(0.75, 0.8, fbmN(uv * 8.0 + time * 0.05));
                veins *= 0.7 + 0.3 * mouseInfluence;

                float3 color = lerp(_Ink.rgb, _Gold.rgb, blend * 0.3 + highlight + veins * 0.5);
                color += _Gold.rgb * mouseInfluence * 0.15;
                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
