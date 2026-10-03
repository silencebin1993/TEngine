Shader "BinGames/Terrain/WastelandSky"
{
    Properties {_SkyTint("天空",Color)=(.45,.55,.65,1) _GroundColor("地平线",Color)=(.72,.66,.54,1) _Exposure("亮度",Float)=1}
    SubShader {Tags {"Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox"} Cull Off ZWrite Off
        Pass {CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f{float4 vertex:SV_POSITION;float3 direction:TEXCOORD0;};
            float4 _SkyTint,_GroundColor;float _Exposure;
            v2f vert(appdata_base v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.direction=v.vertex.xyz;return o;}
            fixed4 frag(v2f i):SV_Target{float3 d=normalize(i.direction);float t=pow(saturate(d.y),.55);
                return float4(lerp(_GroundColor.rgb,_SkyTint.rgb,t)*_Exposure,1);}
            ENDCG}
    }
}
