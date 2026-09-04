Shader "BinGames/EnvWater/Caustics"
{
    // 水体库 · 焦散 + 多涟漪源 + 玩家涟漪
    Properties
    {
        _ArenaHalf ("Arena Half", Float) = 90
        _PlayerWorldXZ ("Player World XZ", Vector) = (0, 0, 0, 0)
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
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

            float hash1(float n)
            {
                return frac(sin(n) * 43758.5453);
            }

            float noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash1(i.x + i.y * 57.0);
                float b = hash1(i.x + 1.0 + i.y * 57.0);
                float c = hash1(i.x + (i.y + 1.0) * 57.0);
                float d = hash1(i.x + 1.0 + (i.y + 1.0) * 57.0);
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float fbmN(float2 p)
            {
                float sum = 0.0;
                float amp = 1.0;
                float freq = 1.0;
                #if defined(ENVFLUID_Q_LOW)
                const int OCT = 3;
                #elif defined(ENVFLUID_Q_HIGH)
                const int OCT = 6;
                #else
                const int OCT = 4;
                #endif
                for (int i = 0; i < OCT; i++)
                {
                    sum += amp * noise(p * freq);
                    amp *= 0.5;
                    freq *= 2.0;
                }
                return sum;
            }

            float ripple(float2 uv, float2 center, float time, float frequency, float amplitude)
            {
                float dist = length(uv - center);
                return sin(dist * frequency - time) * amplitude / (dist + 0.1);
            }

            fixed4 frag(v2f_envwater i) : SV_Target
            {
                float2 p, uv, mouse;
                float halfExtent;
                EnvWaterCoords(i.worldPos, p, uv, mouse, halfExtent);

                float mouseInfluence = (1.0 - smoothstep(0.0, 0.5, length(uv - mouse))) * _RippleStrength;
                float3 deepWater = float3(0.0, 0.1, 0.25);
                float3 shallowWater = float3(0.0, 0.5, 0.7);
                float time = _Time.y * 0.2;

                float displacement = 0.0;
                displacement += fbmN(float2(uv.x * 2.0 + time * 0.1, uv.y * 2.0 - time * 0.15)) * 0.02;
                displacement += fbmN(float2(uv.x * 3.0 - time * 0.2, uv.y * 3.0 + time * 0.1)) * 0.01;

                #if !defined(ENVFLUID_Q_LOW)
                displacement += ripple(uv, float2(0.3, 0.7), time, 15.0, 0.01);
                displacement += ripple(uv, float2(0.7, 0.3), time * 1.2, 12.0, 0.008);
                displacement += ripple(uv, float2(0.5, 0.5), time * 0.8, 10.0, 0.012);
                #endif

                displacement += ripple(uv, mouse, time * 1.5, 20.0, 0.02 * mouseInfluence);

                float2 distortedUV = uv;
                distortedUV += displacement * (1.0 + mouseInfluence);

                float caustics = fbmN(distortedUV * 5.0 + time * 0.1) * fbmN(distortedUV * 3.0 - time * 0.2);
                caustics = pow(max(caustics, 0.0), 1.5) * 0.5;

                float surfacePattern = fbmN(float2(distortedUV.x * 4.0, distortedUV.y * 4.0 + time * 0.1));
                float3 waterColor = lerp(deepWater, shallowWater, surfacePattern + caustics);

                float highlight = pow(max(0.0, 1.0 - abs(surfacePattern - 0.6) * 5.0), 5.0) * 0.5;
                highlight += caustics * 0.3;
                highlight += mouseInfluence * 0.2;

                float3 finalColor = waterColor + highlight * float3(0.8, 0.9, 1.0);
                return fixed4(finalColor, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
