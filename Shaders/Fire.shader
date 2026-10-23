Shader "Recovery/Rim3D/Fire"
{
    Properties
    {
        _MainTex ("Flame A", 2D) = "white" {}
        _FrameB ("Flame B", 2D) = "white" {}
        _FrameC ("Flame C", 2D) = "white" {}
        _Phase ("Animation phase", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
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
            sampler2D _MainTex, _FrameB, _FrameC;
            float _Phase, _Rim3DFireTicks;
            float4 _Rim3DWeatherFog;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float time = _Rim3DFireTicks / 60.0 + _Phase;
                float width = length(unity_ObjectToWorld._m00_m10_m20);
                float height = length(unity_ObjectToWorld._m01_m11_m21);
                float3 center = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                float3 right = UNITY_MATRIX_V._m00_m01_m02;
                float3 up = UNITY_MATRIX_V._m10_m11_m12;
                float sway = sin(time * 8) * .035 * (v.vertex.y + .5);
                float pulse = 1 + sin(time * 11) * .045;
                float3 offset = right * (v.vertex.x + sway) * width * pulse + up * v.vertex.y * height;
                o.world = center + offset;
                float3 relative = (center - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection) + offset;
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float frame = fmod(floor(_Rim3DFireTicks / 9 + _Phase), 3);
                fixed4 flame;
                if (frame < 1) flame = tex2D(_MainTex, i.uv);
                else if (frame < 2) flame = tex2D(_FrameB, i.uv);
                else flame = tex2D(_FrameC, i.uv);
                float fogDistance = max(0, length(i.world - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection) - lerp(8, _Rim3DWeatherFog.z, _Rim3DWeatherFog.w)) * (1 - unity_OrthoParams.w);
                flame.a *= exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, _Rim3DWeatherFog.w)
                    - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, _Rim3DWeatherFog.w));
                clip(flame.a - .003);
                // Emissive flame remains bright at night without extra light passes.
                return flame;
            }
            ENDCG
        }
    }
}
