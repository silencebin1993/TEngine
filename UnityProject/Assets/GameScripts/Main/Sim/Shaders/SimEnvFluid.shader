Shader "BinGames/SimEnvFluid"
{
    // 阳光培养皿 · 环境液体地面（单 Pass · 世界 XZ 程序化）
    // 涟漪跟随 _PlayerWorldXZ；质量档 ENVFLUID_Q_LOW / MED / HIGH
    Properties
    {
        _ArenaHalf ("Arena Half", Float) = 90
        _PlayerWorldXZ ("Player World XZ", Vector) = (0, 0, 0, 0)
        _ColorDeep ("Deep", Color) = (0.10, 0.30, 0.50, 1)
        _ColorCyan ("Cyan", Color) = (0.20, 0.60, 0.70, 1)
        _ColorLight ("Light", Color) = (0.40, 0.80, 0.90, 1)
        _ColorMid ("Mid", Color) = (0.15, 0.45, 0.55, 1)
        _FlowSpeed ("Flow Speed", Float) = 0.15
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-10"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local ENVFLUID_Q_MED ENVFLUID_Q_LOW ENVFLUID_Q_HIGH
            #include "UnityCG.cginc"

            float _ArenaHalf;
            float4 _PlayerWorldXZ;
            float4 _ColorDeep;
            float4 _ColorCyan;
            float4 _ColorLight;
            float4 _ColorMid;
            float _FlowSpeed;
            float _RippleStrength;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 wp = mul(unity_ObjectToWorld, v.vertex);
                o.worldPos = wp.xyz;
                o.vertex = mul(UNITY_MATRIX_VP, wp);
                return o;
            }

            float2 hash2(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453123);
            }

            float hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
            }

            float3 voronoi(float2 x, float t)
            {
                float2 n = floor(x);
                float2 f = frac(x);

                float minDist = 1.0;
                float2 minPoint = 0;

                for (int j = -1; j <= 1; j++)
                {
                    for (int i = -1; i <= 1; i++)
                    {
                        float2 g = float2((float)i, (float)j);
                        float2 o = hash2(n + g);
                        o = 0.5 + 0.5 * sin(t * 0.3 + 6.2831 * o);
                        float2 r = g + o - f;
                        float d = dot(r, r);
                        if (d < minDist)
                        {
                            minDist = d;
                            minPoint = o;
                        }
                    }
                }

                return float3(sqrt(minDist), minPoint);
            }

            float noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = hash(i);
                float b = hash(i + float2(1.0, 0.0));
                float c = hash(i + float2(0.0, 1.0));
                float d = hash(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float fbm2(float2 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < 2; i++)
                {
                    value += amplitude * noise(p * frequency);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            float fbm3(float2 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < 3; i++)
                {
                    value += amplitude * noise(p * frequency);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            float fbm5(float2 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                float frequency = 1.0;
                for (int i = 0; i < 5; i++)
                {
                    value += amplitude * noise(p * frequency);
                    frequency *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float halfExtent = max(_ArenaHalf, 1.0);
                float2 worldXZ = i.worldPos.xz;
                float2 p = worldXZ / halfExtent;
                float2 playerP = _PlayerWorldXZ.xy / halfExtent;

                float2 delta = p - playerP;
                float distToPlayer = length(delta);
                float2 direction = distToPlayer > 1e-4 ? delta / distToPlayer : float2(0, 1);

                float t = _Time.y * _FlowSpeed;

                #if defined(ENVFLUID_Q_LOW)
                float2 flowUV = (worldXZ * 0.02) + float2(fbm2(p * 2.0 + t * 0.2), fbm2(p * 2.0 + t * 0.2 + 100.0)) * 0.2;
                float cells = fbm2(flowUV * 3.0);
                float cellsDistorted = cells;
                float flowLines = fbm2(p * 3.0 + float2(t * 0.3, 0.0));
                float ripple = 0.0;
                if (distToPlayer < 2.0)
                {
                    float radialWave = sin(distToPlayer * 6.0 - t * 2.0) * 0.5 + 0.5;
                    ripple = radialWave * exp(-distToPlayer * 1.5) * smoothstep(2.0, 0.0, distToPlayer);
                    ripple *= _RippleStrength * 0.7;
                }
                float pattern = saturate(cells * 0.7 + flowLines * 0.3 + ripple * 0.3);

                #elif defined(ENVFLUID_Q_HIGH)
                float2 uv = worldXZ * 0.015 + 0.5;
                float2 flowUV = uv * 3.0;
                flowUV += float2(fbm5(uv * 2.0 + t * 0.2), fbm5(uv * 2.0 + t * 0.2 + 100.0)) * 0.3;

                float3 vor = voronoi(flowUV, _Time.y);
                float cells = vor.x;

                float ripple = 0.0;
                if (distToPlayer < 2.0)
                {
                    float radialWave = sin(distToPlayer * 8.0 - t * 2.0) * 0.5 + 0.5;
                    float forwardBias = dot(direction, normalize(p + 1e-4)) * 0.5 + 0.5;
                    ripple = radialWave * exp(-distToPlayer * 1.5) * (0.5 + 0.5 * forwardBias);
                    ripple *= smoothstep(2.0, 0.0, distToPlayer) * _RippleStrength;
                }

                float2 fluidDistort = float2(
                    fbm5(p * 2.0 + t * 0.1),
                    fbm5(p * 2.0 + t * 0.1 + float2(5.3, 2.1))
                ) * 0.4;
                fluidDistort += direction * ripple * 0.3;

                float3 vorDistorted = voronoi(flowUV + fluidDistort * 2.0, _Time.y);
                float cellsDistorted = vorDistorted.x;

                float pattern = cells * 0.5 + cellsDistorted * 0.5;
                pattern = saturate(pattern);
                float flowLines = fbm5(p * 4.0 + fluidDistort * 3.0 + float2(t * 0.3, 0.0));
                pattern = lerp(pattern, flowLines, 0.3);
                pattern += ripple * 0.4;

                #else
                // MED default
                float2 uv = worldXZ * 0.015 + 0.5;
                float2 flowUV = uv * 3.0;
                flowUV += float2(fbm3(uv * 2.0 + t * 0.2), fbm3(uv * 2.0 + t * 0.2 + 100.0)) * 0.25;

                float3 vor = voronoi(flowUV, _Time.y);
                float cells = vor.x;

                float ripple = 0.0;
                if (distToPlayer < 2.0)
                {
                    float radialWave = sin(distToPlayer * 8.0 - t * 2.0) * 0.5 + 0.5;
                    ripple = radialWave * exp(-distToPlayer * 1.5);
                    ripple *= smoothstep(2.0, 0.0, distToPlayer) * _RippleStrength;
                }

                float2 fluidDistort = float2(
                    fbm3(p * 2.0 + t * 0.1),
                    fbm3(p * 2.0 + t * 0.1 + float2(5.3, 2.1))
                ) * 0.3;
                fluidDistort += direction * ripple * 0.25;

                float3 vorDistorted = voronoi(flowUV + fluidDistort * 1.5, _Time.y);
                float cellsDistorted = vorDistorted.x;

                float pattern = saturate(cells * 0.5 + cellsDistorted * 0.5);
                float flowLines = fbm3(p * 4.0 + fluidDistort * 2.0 + float2(t * 0.3, 0.0));
                pattern = lerp(pattern, flowLines, 0.25);
                pattern += ripple * 0.35;
                #endif

                float3 color = lerp(_ColorDeep.rgb, _ColorCyan.rgb, pattern);
                color = lerp(color, _ColorLight.rgb, cells * 0.5);
                color = lerp(color, _ColorMid.rgb, flowLines * 0.3);
                color += float3(0.3, 0.5, 0.6) * ripple * 0.6;

                float cellHighlight = smoothstep(0.15, 0.0, cellsDistorted);
                color += float3(0.2, 0.4, 0.5) * cellHighlight * 0.4;

                float vignette = 1.0 - length(p) * 0.22;
                color *= saturate(vignette);

                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
