Shader "BinGames/EnvWater/Waves"
{
    // 水体库 · 多层波峰泡沫 + 简易法线高光 + 玩家涟漪
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
                float n = i.x + i.y * 57.0;
                return lerp(
                    lerp(hash1(n), hash1(n + 1.0), f.x),
                    lerp(hash1(n + 57.0), hash1(n + 58.0), f.x),
                    f.y);
            }

            float fbmN(float2 p)
            {
                float f = 0.0;
                float w = 0.5;
                #if defined(ENVFLUID_Q_LOW)
                const int OCT = 2;
                #elif defined(ENVFLUID_Q_HIGH)
                const int OCT = 5;
                #else
                const int OCT = 3;
                #endif
                for (int i = 0; i < OCT; i++)
                {
                    f += w * noise(p);
                    p *= 2.0;
                    w *= 0.5;
                }
                return f;
            }

            float wave(float2 p, float time, float freq, float amp)
            {
                return sin(p.x * freq + time) * cos(p.y * freq * 0.5 + time * 0.5) * amp;
            }

            float sampleHeight(float2 uv, float time, float mouseInfluence, float mouseDistance)
            {
                float waveHeight1 = wave(uv * 2.0, time, 3.0, 0.05);
                float waveHeight2 = wave(uv * 4.0, time * 1.2, 5.0, 0.03);
                float waveHeight3 = wave(uv * 1.0, time * 0.7, 2.0, 0.07);
                float noiseValue = fbmN(uv * 3.0 + time * 0.1) * 0.1;
                float waterHeight = waveHeight1 + waveHeight2 + waveHeight3 + noiseValue;
                waterHeight += mouseInfluence * 0.15 * sin(mouseDistance * 15.0 - time * 2.0);
                return waterHeight;
            }

            fixed4 frag(v2f_envwater i) : SV_Target
            {
                float2 p, uv, mouse;
                float halfExtent;
                EnvWaterCoords(i.worldPos, p, uv, mouse, halfExtent);

                float mouseDistance = length(uv - mouse);
                float mouseInfluence = smoothstep(0.5, 0.0, mouseDistance) * _RippleStrength;
                float time = _Time.y * 0.3;

                float waterHeight = sampleHeight(uv, time, mouseInfluence, mouseDistance);

                float3 deepColor = float3(0.0, 0.1, 0.2);
                float3 shallowColor = float3(0.0, 0.5, 0.8);
                float3 foamColor = float3(0.9, 0.95, 1.0);

                float3 waterColor = lerp(deepColor, shallowColor, 0.5 + waterHeight * 3.0);
                float foam = smoothstep(0.08, 0.09, waterHeight);
                waterColor = lerp(waterColor, foamColor, foam);

                #if !defined(ENVFLUID_Q_LOW)
                float2 eps = float2(0.01, 0.0);
                float h0 = waterHeight;
                float hx = sampleHeight(uv + eps, time, mouseInfluence, length((uv + eps) - mouse));
                float hy = sampleHeight(uv + eps.yx, time, mouseInfluence, length((uv + eps.yx) - mouse));
                float3 normal = normalize(float3(h0 - hx, h0 - hy, 0.05));
                float3 lightDir = normalize(float3(0.5, 0.5, 1.0));
                float diffuse = max(0.0, dot(normal, lightDir));
                float specular = pow(max(0.0, dot(reflect(-lightDir, normal), float3(0.0, 0.0, 1.0))), 20.0);
                waterColor += diffuse * 0.1 + specular * 0.3;
                #endif

                float mouseRipple = sin(mouseDistance * 20.0 - time * 5.0) * 0.5 + 0.5;
                mouseRipple *= smoothstep(0.5, 0.2, mouseDistance) * _RippleStrength;
                waterColor += mouseRipple * 0.1 * foamColor;

                return fixed4(waterColor, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
