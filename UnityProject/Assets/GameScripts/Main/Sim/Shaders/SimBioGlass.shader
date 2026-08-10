Shader "BinGames/SimBioGlass"
{
    // 阳光培养皿 · 软边 + 游动/受击方向形变（单 Pass · GPU Instancing）
    // _Motion.xy = 归一化速度方向（XZ），_Motion.z = 速度强度 0..1
    // _Impact.xy = 受击压缩方向，_Impact.z = 强度 0..1
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.98, 0.72, 1)
        _RimColor ("Outline Color", Color) = (1, 1, 0.95, 0.75)
        _BodyRadius ("Body Radius", Range(0.5, 0.95)) = 0.80
        _OutlineWidth ("Outline Width", Range(0.02, 0.25)) = 0.11
        _CoreBright ("Core Bright", Range(1, 2.5)) = 1.2
        _CoreRadius ("Core Radius", Range(0.05, 0.55)) = 0.34
        _BodyAlpha ("Body Alpha", Range(0.5, 1)) = 0.90
        _EdgeSoft ("Edge Softness", Range(0.02, 0.2)) = 0.08
        _IdleWobble ("Idle Wobble", Range(0, 0.12)) = 0.028
        _WobbleSpeed ("Wobble Speed", Range(0, 8)) = 2.4
        _SwimStretch ("Swim Stretch", Range(0, 0.45)) = 0.22
        _SwimCompress ("Swim Side Compress", Range(0, 0.35)) = 0.14
        _ImpactSquash ("Impact Squash", Range(0, 0.55)) = 0.32
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Motion)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Impact)
            UNITY_INSTANCING_BUFFER_END(Props)

            float4 _RimColor;
            float _BodyRadius;
            float _OutlineWidth;
            float _CoreBright;
            float _CoreRadius;
            float _BodyAlpha;
            float _EdgeSoft;
            float _IdleWobble;
            float _WobbleSpeed;
            float _SwimStretch;
            float _SwimCompress;
            float _ImpactSquash;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // 把圆盘坐标按游动/受击做各向异性软形变
            float2 Deform(float2 p, float4 motion, float4 impact)
            {
                float2 mdir = motion.xy;
                float mlen2 = dot(mdir, mdir);
                float mspd = saturate(motion.z);
                if (mlen2 > 1e-4)
                {
                    mdir *= rsqrt(mlen2);
                    float along = dot(p, mdir);
                    float2 side = p - mdir * along;
                    // 前进方向拉长，侧向收窄；尾部略收缩 → 游动液滴感
                    float stretch = 1.0 + mspd * _SwimStretch * (along > 0.0 ? 1.0 : -0.35);
                    float compress = 1.0 - mspd * _SwimCompress;
                    p = mdir * (along * stretch) + side * compress;
                }

                float2 idir = impact.xy;
                float iamt = saturate(impact.z);
                float ilen2 = dot(idir, idir);
                if (iamt > 1e-3 && ilen2 > 1e-4)
                {
                    idir *= rsqrt(ilen2);
                    float ia = dot(p, idir);
                    float2 iperp = p - idir * ia;
                    // 受击侧压扁，垂直方向略鼓，体积感更软
                    float squash = 1.0 - iamt * _ImpactSquash * (ia > 0.0 ? 1.0 : 0.25);
                    float bulge = 1.0 + iamt * _ImpactSquash * 0.45;
                    p = idir * (ia * squash) + iperp * bulge;
                }

                return p;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 baseCol = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float4 motion = UNITY_ACCESS_INSTANCED_PROP(Props, _Motion);
                float4 impact = UNITY_ACCESS_INSTANCED_PROP(Props, _Impact);

                float2 p0 = i.uv * 2.0 - 1.0;
                float2 p = Deform(p0, motion, impact);

                float r0 = length(p);
                float ang = atan2(p.y, p.x);

                // 静止时轻柔 idle；游动越快 idle 越小，避免和形变抢戏
                float idleMul = (1.0 - saturate(motion.z) * 0.85) * (1.0 - saturate(impact.z) * 0.7);
                float t = _Time.y * _WobbleSpeed;
                float idle =
                    sin(ang * 3.0 + t) * 0.55 +
                    sin(ang * 5.0 - t * 1.1) * 0.30 +
                    sin(ang * 8.0 + t * 0.6) * 0.15;
                float outlineR = _BodyRadius + idle * _IdleWobble * idleMul;
                float innerR = outlineR - _OutlineWidth;

                // 软外缘（比硬描边环柔和）
                float outer = 1.0 - smoothstep(outlineR, outlineR + _EdgeSoft, r0);
                clip(outer - 0.004);

                float outline = smoothstep(innerR - _EdgeSoft, innerR + _EdgeSoft * 0.35, r0)
                              * (1.0 - smoothstep(outlineR - _EdgeSoft, outlineR + _EdgeSoft, r0));
                outline = smoothstep(0.0, 1.0, outline); // 再软一点

                float body = 1.0 - smoothstep(innerR - _EdgeSoft * 1.2, innerR + _EdgeSoft * 0.2, r0);
                float core = 1.0 - smoothstep(_CoreRadius * 0.45, _CoreRadius, r0);

                float3 col = baseCol.rgb * lerp(1.0, _CoreBright, core * body);
                float3 outlineCol = lerp(baseCol.rgb, _RimColor.rgb, 0.55);
                col = lerp(col, outlineCol, outline * _RimColor.a);

                float alpha = body * _BodyAlpha * max(baseCol.a, 0.85);
                alpha = max(alpha, outline * 0.72);
                alpha *= outer;
                // 最外一圈再柔化
                alpha *= smoothstep(0.0, 0.2, outer);

                return fixed4(col, saturate(alpha));
            }
            ENDCG
        }
    }

    FallBack Off
}
