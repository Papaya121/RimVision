Shader "Recovery/Rim3D/Water"
{
    Properties
    {
        _MainTex ("Original water texture", 2D) = "white" {}
        _Color ("Terrain tint", Color) = (1,1,1,1)
        _ShallowColor ("Shallow water", Color) = (.17,.39,.38,1)
        _DeepColor ("Deep water", Color) = (.055,.19,.25,1)
        _Depth ("Depth", Range(0,1)) = .4
        _FlowSpeed ("Ripple speed", Float) = .16
        _RippleStrength ("Ripple strength", Range(0,.3)) = .12
        _WaveHeight ("Wave height", Range(0,.2)) = .09
        [HideInInspector] _Ground ("Ground receiver", Float) = 1
        [HideInInspector] _GroundStencil ("Ground stencil", Int) = 64
        [HideInInspector] _Billboard ("Billboard", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="AlphaTest+1" "Rim3DOutline"="Ground" "DisableBatching"="True" }
        Cull Back
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha
        Stencil { Ref [_GroundStencil] WriteMask 64 Comp Always Pass Replace }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ RIM3D_SIMPLE_WATER
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;

            sampler2D _Rim3DRoofMask, _Rim3DGameLightMap, _Rim3DShorelineMask;
            float4 _Rim3DRoofMapSize, _Rim3DWeatherFog;
            half4 _Color, _ShallowColor, _DeepColor;
            half4 _Rim3DIndoorAmbientColor, _Rim3DIndoorFogColor;
            half4 _Rim3DAmbientColor, _Rim3DSunColor, _Rim3DFogColor;
            float4 _Rim3DSunDirection;
            float _Rim3DRoofHeight, _Depth, _FlowSpeed, _RippleStrength, _WaveHeight;

            float3 SurfaceWave(float2 xz)
            {
                float3 phase = float3(dot(xz, float2(.85,.38)) - _Time.y * 1.05,
                    dot(xz, float2(-.48,1.3)) - _Time.y * 1.32,
                    dot(xz, float2(1.9,1.45)) + _Time.y * 1.76);
                float3 heights = sin(phase) * float3(.58,.28,.14) * _WaveHeight;
                float3 slopes = cos(phase) * float3(.58,.28,.14) * _WaveHeight;
                return float3(heights.x + heights.y + heights.z,
                    dot(slopes, float3(.85,-.48,1.9)), dot(slopes, float3(.38,1.3,1.45)));
            }

            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };
            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, vertex).xyz;
                float height = 0;
                #if !defined(RIM3D_SIMPLE_WATER)
                float coverage = tex2Dlod(_Rim3DShorelineMask, float4(world.xz / _Rim3DRoofMapSize.xy, 0, 0)).r;
                height = (SurfaceWave(world.xz).x + _WaveHeight) * .5 * smoothstep(.75, 1, coverage);
                #endif
                // Match the ground's camera-relative projection at y=-10000.
                float3 relative = mul((float3x3)unity_ObjectToWorld, vertex.xyz)
                    + (unity_ObjectToWorld._m03_m13_m23 - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection);
                relative.y += height;
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                o.world = world + float3(0, height, 0);
                return o;
            }
            half Roof(float2 xz)
            {
                float2 uv = xz / _Rim3DRoofMapSize.xy;
                half inside = step(0, uv.x) * step(0, uv.y) * (1 - step(1, uv.x)) * (1 - step(1, uv.y));
                return tex2D(_Rim3DRoofMask, uv).r * inside;
            }
            half4 frag(v2f i) : SV_Target
            {
                float3 toEye = (_WorldSpaceCameraPos - i.world) + _Rim3DCameraPositionCorrection;
                float distanceToEye = length(toEye);
                half3 view = toEye / max(distanceToEye, .001);
                half coverage = tex2D(_Rim3DShorelineMask, i.world.xz / _Rim3DRoofMapSize.xy).r;
                half shoreBlend = smoothstep(.5, .95, coverage);
                float3 swell = 0;
                float3 wave = 0;
                half2 slope = 0;
                #if !defined(RIM3D_SIMPLE_WATER)
                swell = SurfaceWave(i.world.xz) * (.5 * smoothstep(.75, 1, coverage));
                float t = _Time.y * _FlowSpeed;
                // World coordinates keep ripples continuous across merged tiles.
                float3 phase = float3(dot(i.world.xz, float2(1.7,.8)) - t,
                    dot(i.world.xz, float2(-.9,2.1)) + t * .73,
                    dot(i.world.xz, float2(3.2,2.6)) - t * 1.19);
                wave = sin(phase);
                // Fade subpixel waves before they turn into distant sparkle.
                float3 footprint = abs(ddx(phase)) + abs(ddy(phase));
                wave *= saturate(1 - footprint * .45);
                slope = half2(wave.x + wave.y * -.55 + wave.z * .3,
                    wave.x * .47 + wave.y + wave.z * .24) * _RippleStrength;
                #endif
                half3 normal = normalize(half3(slope.x - swell.y, 1, slope.y - swell.z));
                half3 sun = normalize(_Rim3DSunDirection.xyz);
                half belowRoof = 1 - step(_Rim3DRoofHeight, i.world.y);
                half overhead = belowRoof * Roof(i.world.xz);
                float rayLength = max(0, _Rim3DRoofHeight - i.world.y) / max(.05, sun.y);
                half sunVisible = (1 - overhead) * (1 - belowRoof * Roof(i.world.xz + sun.xz * rayLength));
                half3 lamps = min(tex2D(_Rim3DGameLightMap, i.world.xz / _Rim3DRoofMapSize.xy).rgb * 3.6, 1.35);
                half3 ambient = lerp(_Rim3DAmbientColor.rgb, _Rim3DIndoorAmbientColor.rgb, overhead);
                half3 light = ambient * lerp(1, .32, overhead)
                    + _Rim3DSunColor.rgb * (.3 + .7 * saturate(dot(normal, sun))) * sunVisible + lamps;
                // Bilinear cell coverage rounds shoreline corners and fades into
                // the neighboring ground underlay over roughly half a metre.
                half3 baseColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, _Depth * shoreBlend) * _Color.rgb;
                baseColor *= 1 + (wave.x * wave.y) * .055 + swell.x * .65;
                half fresnel = pow(1 - saturate(dot(normal, view)), 5);
                half3 reflected = reflect(-view, normal);
                half3 sky = lerp(_Rim3DFogColor.rgb * .8,
                    _Rim3DFogColor.rgb * .45 + _Rim3DAmbientColor.rgb * .38, saturate(reflected.y));
                half3 rgb = lerp(baseColor * light, sky, (.06 + fresnel * .68) * (1 - overhead));
                half3 halfway = normalize(sun + view + half3(0,.0001,0));
                // Broader glints in the distance reduce temporal aliasing.
                half gloss = lerp(96, 24, saturate(distanceToEye * .025));
                half specular = pow(saturate(dot(normal, halfway)), gloss);
                rgb += _Rim3DSunColor.rgb * specular * .55 * sunVisible * saturate(sun.y * 8);
                float outdoorFog = _Rim3DWeatherFog.w * (1 - overhead);
                float fogDistance = max(0, distanceToEye - lerp(8, _Rim3DWeatherFog.z, outdoorFog)) * (1 - unity_OrthoParams.w);
                half fog = 1 - exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, outdoorFog)
                    - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, outdoorFog));
                half3 fogColor = lerp(_Rim3DIndoorFogColor.rgb, _Rim3DFogColor.rgb, outdoorFog);
                return half4(lerp(rgb, fogColor, fog), shoreBlend);
            }
            ENDCG
        }
    }
    Fallback Off
}
