Shader "BinGames/EnvWater/Displace"
{
    // 水体库 · 经典位移水面（FBM + 正弦波 + 玩家涟漪）
    Properties
    {
        _ArenaHalf ("Arena Half", Float) = 90
        _PlayerWorldXZ ("Player World XZ", Vector) = (0, 0, 0, 0)
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
        _Speed ("Speed", Float) = 0.5
        _WaveHeight ("Wave Height", Float) = 0.05
        _WaveFrequency ("Wave Frequency", Float) = 15
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

            float _Speed;
            float _WaveHeight;
            float _WaveFrequency;

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

            float fbmN(float2 st, float time)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                #if defined(ENVFLUID_Q_LOW)
                const int OCT = 2;
                #elif defined(ENVFLUID_Q_HIGH)
                const int OCT = 4;
                #else
                const int OCT = 3;
                #endif
                for (int i = 0; i < OCT; i++)
                {
                    value += amplitude * noise(st * frequency);
                    st = st * 2.0 + time * 0.05;
                    amplitude *= 0.5;
                    frequency *= 2.0;
                }
                return value;
            }

            float2 waterDisplacement(float2 uv, float time)
            {
                float2 displacedUv = uv;
                displacedUv.y += sin(uv.x * _WaveFrequency + time * _Speed) * _WaveHeight;
                displacedUv.x += cos(uv.y * _WaveFrequency * 0.8 + time * _Speed * 0.7) * _WaveHeight * 0.8;
                displacedUv += fbmN(uv * 3.0 + time * 0.1, time) * 0.03;
                return displacedUv;
            }

            float3 waterColor(float2 uv, float time)
            {
                float3 deepWater = float3(0.0, 0.1, 0.2);
                float3 shallowWater = float3(0.0, 0.5, 0.7);
                float3 surfaceHighlight = float3(0.7, 0.9, 1.0);
                float2 displacedUv = waterDisplacement(uv, time);
                float waterPattern = fbmN(displacedUv * 5.0, time);
                float surface = smoothstep(0.4, 0.6, waterPattern);
                float3 waterBase = lerp(deepWater, shallowWater, waterPattern);
                return lerp(waterBase, surfaceHighlight, surface * 0.3);
            }

            fixed4 frag(v2f_envwater i) : SV_Target
            {
                float2 p, uv, playerUv;
                float halfExtent;
                EnvWaterCoords(i.worldPos, p, uv, playerUv, halfExtent);

                float mouseDist = distance(uv, playerUv);
                float mouseInfluence = smoothstep(0.5, 0.0, mouseDist) * _RippleStrength;
                float adjustedTime = _Time.y + mouseInfluence * 2.0;
                float2 distortedUv = uv;

                if (mouseDist < 0.5)
                {
                    float rippleStrength = (0.5 - mouseDist) * 2.0 * _RippleStrength;
                    float ripplePhase = mouseDist * 20.0 - adjustedTime * 2.0;
                    float2 dir = mouseDist > 1e-4 ? normalize(uv - playerUv) : float2(0, 1);
                    distortedUv += dir * sin(ripplePhase) * rippleStrength * 0.03;
                }

                float3 color = waterColor(distortedUv, adjustedTime);
                float vignette = 1.0 - smoothstep(0.5, 1.5, length(uv - 0.5) * 2.0);
                color *= vignette * 1.1;
                color += float3(0.0, 0.2, 0.3) * mouseInfluence * 0.3;
                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
