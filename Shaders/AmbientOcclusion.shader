Shader "Recovery/Rim3D/AmbientOcclusion"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Rim3DAOIntensity ("Intensity", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex, _Rim3DAOTexture;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
        float4 _MainTex_TexelSize, _Rim3DAODepthTexelSize, _Rim3DAOTexelSize;
        float4x4 _Rim3DAOInverseProjection;
        float4 _Rim3DAOProjection;
        float _Rim3DAOIntensity;

        float2 DepthUV(float2 uv)
        {
            float2 depthSize = _Rim3DAODepthTexelSize.zw;
            float2 pixel = clamp(floor(uv * depthSize), float2(0, 0), depthSize - 1);
            return (pixel + .5) * _Rim3DAODepthTexelSize.xy;
        }

        float3 ViewPosition(float2 uv)
        {
            float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            #if !defined(UNITY_REVERSED_Z)
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif
            float4 position = mul(_Rim3DAOInverseProjection, float4(uv * 2 - 1, depth, 1));
            return position.xyz / position.w;
        }

        float4 FragOcclusion(v2f_img i) : SV_Target
        {
            float2 centerUV = DepthUV(i.uv);
            float3 center = ViewPosition(centerUV);
            if (-center.z >= _Rim3DAOProjection.z * .99) return 1;
            float2 texel = _Rim3DAODepthTexelSize.xy;
            if (any(centerUV < texel) || any(centerUV > 1 - texel)) return 1;
            float3 left = center - ViewPosition(centerUV - float2(texel.x, 0));
            float3 right = ViewPosition(centerUV + float2(texel.x, 0)) - center;
            float3 down = center - ViewPosition(centerUV - float2(0, texel.y));
            float3 up = ViewPosition(centerUV + float2(0, texel.y)) - center;
            float3 normal = cross(abs(left.z) < abs(right.z) ? left : right,
                abs(down.z) < abs(up.z) ? down : up);
            normal *= rsqrt(max(dot(normal, normal), 1e-12));
            if (dot(normal, -center) < 0) normal = -normal;
            float radius = .85;
            float2 radiusUV = min(float2(.08, .08), radius * _Rim3DAOProjection.xy * .5 /
                (_Rim3DAOProjection.w > .5 ? 1 : max(-center.z, .15)));
            float2 pixel = floor(i.uv * _Rim3DAOTexelSize.zw);
            float noise = frac(52.9829189 * frac(dot(pixel, float2(.06711056, .00583715))));
            float2 rotation;
            sincos(noise * 6.28318531, rotation.y, rotation.x);
            float radialJitter = frac(noise * 7.13 + .37);
            float occlusion = 0;
            [unroll] for (int sampleIndex = 0; sampleIndex < 32; sampleIndex++)
            {
                float sampleAngle = sampleIndex * 2.39996323;
                float2 direction = float2(cos(sampleAngle), sin(sampleAngle));
                direction = float2(direction.x * rotation.x - direction.y * rotation.y,
                    direction.x * rotation.y + direction.y * rotation.x);
                float2 uv = centerUV + direction * radiusUV * sqrt((sampleIndex + radialJitter) / 32);
                if (any(uv <= 0) || any(uv >= 1)) continue;
                float3 delta = ViewPosition(DepthUV(uv)) - center;
                float distanceSquared = dot(delta, delta);
                float facing = saturate((dot(normal, delta) - .025) * rsqrt(max(distanceSquared, 1e-6)));
                occlusion += facing * saturate(1 - distanceSquared / (radius * radius));
            }
            return saturate(1 - occlusion * (2.4 / 32));
        }

        float4 FragBlur(v2f_img i) : SV_Target
        {
            float centerDepth = ViewPosition(DepthUV(i.uv)).z;
            float2 centerPixel = floor(i.uv * _Rim3DAOTexelSize.zw);
            float sum = 0, weightSum = 0;
            [unroll] for (int y = -2; y <= 2; y++)
            [unroll] for (int x = -2; x <= 2; x++)
            {
                float2 pixel = clamp(centerPixel + float2(x, y), float2(0, 0), _Rim3DAOTexelSize.zw - 1);
                float2 uv = (pixel + .5) * _Rim3DAOTexelSize.xy;
                float difference = abs(ViewPosition(DepthUV(uv)).z - centerDepth);
                float2 offset = (uv - i.uv) * _Rim3DAOTexelSize.zw;
                float weight = exp2(-dot(offset, offset) * .5) *
                    (1 - smoothstep(.025, .1, difference));
                sum += tex2D(_MainTex, uv).r * weight;
                weightSum += weight;
            }
            return weightSum > .0001 ? sum / weightSum : 1;
        }

        float4 FragComposite(v2f_img i) : SV_Target
        {
            float4 color = tex2D(_MainTex, i.uv);
            float2 aoUV = i.uv;
            #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) aoUV.y = 1 - aoUV.y;
            #endif
            color.rgb *= saturate(1 - (1 - tex2D(_Rim3DAOTexture, aoUV).r) * _Rim3DAOIntensity);
            return color;
        }
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragOcclusion
            #pragma target 3.0
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragBlur
            #pragma target 3.0
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragComposite
            #pragma target 3.0
            ENDCG
        }
    }
    Fallback Off
}
