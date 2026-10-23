Shader "Recovery/Rim3D/Lightning"
{
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha One
        ZWrite Off ZTest LEqual Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;
            float4 _Rim3DWeatherFog;
            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float3 relative : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float3 origin = unity_ObjectToWorld._m03_m13_m23;
                o.relative = origin - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection + mul((float3x3)unity_ObjectToWorld, v.vertex.xyz);
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, o.relative), 1));
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float edge = abs(i.uv.y * 2 - 1);
                float core = exp(-edge * edge * 220);
                float halo = pow(saturate(1 - edge), 3);
                float distanceToCamera = length(i.relative);
                float fogDistance = max(0, distanceToCamera - _Rim3DWeatherFog.z) * (1 - unity_OrthoParams.w);
                float visibility = exp(-fogDistance * _Rim3DWeatherFog.x * .07);
                float3 color = float3(.24, .45, 1) * halo * .65 + float3(.86, .94, 1) * core * 3;
                return float4(color * i.color.rgb, i.color.a * visibility);
            }
            ENDCG
        }
    }
}
