Shader "Recovery/Rim3D/CombatTrail"
{
    Properties { _Color ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha One
        ZWrite Off
        ZTest LEqual
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;
            fixed4 _Color;
            float4 _Rim3DWeatherFog;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float3 relative = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz)
                    + (unity_ObjectToWorld._m03_m13_m23 - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection);
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color * _Color;
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float edge = saturate(1 - abs(i.uv.y * 2 - 1));
                float fogDistance = max(0, length(i.world - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection) - lerp(8, _Rim3DWeatherFog.z, _Rim3DWeatherFog.w)) * (1 - unity_OrthoParams.w);
                float visibility = exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, _Rim3DWeatherFog.w)
                    - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, _Rim3DWeatherFog.w));
                return fixed4(i.color.rgb, i.color.a * edge * edge * visibility);
            }
            ENDCG
        }
    }
}
