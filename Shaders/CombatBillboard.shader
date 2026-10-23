Shader "Recovery/Rim3D/CombatBillboard"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _DstBlend ("Destination blend", Float) = 10
        _AlphaBoost ("Texture alpha multiplier", Float) = 1
        _BlastMode ("Explosion mode", Float) = 0
        _BlastAge ("Explosion age", Float) = 0
        _BlastSeed ("Explosion seed", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha [_DstBlend]
        ZWrite Off
        ZTest LEqual
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;
            sampler2D _MainTex;
            fixed4 _Color;
            float4 _Rim3DWeatherFog;
            float _BlastMode, _BlastAge, _BlastSeed, _AlphaBoost;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float3 center = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                float width = length(unity_ObjectToWorld._m00_m10_m20);
                float height = length(unity_ObjectToWorld._m01_m11_m21);
                float3 offset = UNITY_MATRIX_V._m00_m01_m02 * v.vertex.x * width
                    + UNITY_MATRIX_V._m10_m11_m12 * v.vertex.y * height;
                o.world = center + offset;
                float3 relative = (center - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection) + offset;
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                o.uv = v.uv;
                return o;
            }
            float noiseHash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }
            float blastNoise(float2 p)
            {
                float2 cell = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(noiseHash(cell), noiseHash(cell + float2(1, 0)), f.x),
                    lerp(noiseHash(cell + float2(0, 1)), noiseHash(cell + 1), f.x), f.y);
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 color;
                if (_BlastMode > 1.5)
                {
                    float radius = length(i.uv * 2 - 1);
                    float glow = pow(saturate(1 - radius), 2);
                    color = float4(_Color.rgb * (1 + 2 * glow), _Color.a * glow);
                }
                else if (_BlastMode > .5)
                {
                    float2 p = i.uv * 2 - 1;
                    float2 drift = float2(_BlastSeed, _BlastAge * -.8);
                    float noise = blastNoise(p * 3 + drift) * .65 + blastNoise(p * 7 + drift * 1.7) * .35;
                    float edge = length(p) + (noise - .5) * .38;
                    float mask = 1 - smoothstep(.62, .98, edge);
                    float heat = saturate(1 - _BlastAge / .65);
                    float core = saturate((1 - length(p)) * heat + (noise - .5) * .6);
                    float3 fire = lerp(float3(1.1, .12, .015), float3(2.4, 1.5, .35), smoothstep(.25, .85, core));
                    float3 smoke = lerp(float3(.085, .075, .065), float3(.32, .29, .25), noise);
                    color = float4(lerp(smoke, fire * _Color.rgb, smoothstep(0, .45, heat)),
                        mask * _Color.a * lerp(.7, 1, heat));
                }
                else
                {
                    color = tex2D(_MainTex, i.uv);
                    color.a = saturate(color.a * _AlphaBoost);
                    color *= _Color;
                }
                float fogDistance = max(0, length(i.world - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection) - lerp(8, _Rim3DWeatherFog.z, _Rim3DWeatherFog.w)) * (1 - unity_OrthoParams.w);
                color.a *= exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, _Rim3DWeatherFog.w)
                    - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, _Rim3DWeatherFog.w));
                clip(color.a - .003);
                return color;
            }
            ENDCG
        }
    }
}
