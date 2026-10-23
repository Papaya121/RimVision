Shader "Recovery/Rim3D/WeatherParticles"
{
    Properties
    {
        _Volume ("Volume", Vector) = (18,12,0,1)
        _Rates ("Rain snow dust", Vector) = (0,0,0,0)
        _Wind ("Wind", Vector) = (.6,.25,1,0)
        _Tint ("Precipitation tint", Color) = (1,1,1,1)
        _WeatherTime ("Simulation time", Float) = 0
        _WeatherDrift ("Integrated wind drift", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off ZTest LEqual Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;
            float4 _Volume, _Rates, _Wind, _Tint, _WeatherDrift, _Rim3DRoofMapSize, _Rim3DWeatherFog;
            float _WeatherTime, _Rim3DMapGroundY, _Rim3DRoofHeight;
            sampler2D _Rim3DRoofMask;
            struct appdata { float4 vertex : POSITION; float4 seed : TEXCOORD0; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 world : TEXCOORD1;
                float4 data : TEXCOORD2;
                float3 effect : TEXCOORD3;
            };
            v2f vert(appdata v)
            {
                v2f o;
                // Share the existing mesh between active weather types. Dry snow
                // and dust no longer reserve most of the cards during rainfall.
                float3 weights = saturate(_Rates.xyz * float3(.85, .3, .067));
                float total = max(.0001, weights.x + weights.y + weights.z);
                float rainEnd = weights.x / total;
                float snowEnd = (weights.x + weights.y) / total;
                float rain = 1 - step(rainEnd, v.seed.w);
                float snow = step(rainEnd, v.seed.w) * (1 - step(snowEnd, v.seed.w));
                float dust = step(snowEnd, v.seed.w);
                float index = rain * v.seed.w / max(.0001, rainEnd)
                    + snow * (v.seed.w - rainEnd) / max(.0001, snowEnd - rainEnd)
                    + dust * (v.seed.w - snowEnd) / max(.0001, 1 - snowEnd);
                float rate = dot(_Rates.xyz, float3(rain, snow, dust));
                float active = step(index, saturate(total));
                float effect = v.vertex.z;
                active *= effect > .5 ? rain : 1;
                o.effect = float3(effect, 0, frac(v.seed.x * 37 + v.seed.z * 19));
                if (active < .5)
                {
                    o.pos = float4(0,0,0,1); o.uv = 0; o.world = 0; o.data = 0;
                    return o;
                }
                float rainStrength = saturate(rate * .5);
                float speed = rain * (17 + rainStrength * 5) + snow * 1.8 + dust * .65;
                speed *= lerp(1, lerp(.88, 1.12, o.effect.z), rain);
                float3 origin = unity_ObjectToWorld._m03_m13_m23;
                if (effect > .5)
                {
                    float age = frac(_WeatherTime * speed / _Volume.y - v.seed.y - _Volume.z / _Volume.y) * _Volume.y / speed;
                    float lifetime = effect < 1.5 ? .24 : .16;
                    active *= 1 - step(lifetime, age);
                    if (active < .5)
                    {
                        o.pos = float4(0,0,0,1); o.uv = 0; o.world = 0; o.data = 0;
                        return o;
                    }
                    float phase = age / lifetime;
                    float2 impactXZ = frac((v.seed.xz * 8192 - origin.xz) / (_Volume.x * 2) + .5) * (_Volume.x * 2) - _Volume.x;
                    float2 impactUV = (origin.xz + impactXZ) / _Rim3DRoofMapSize.xy;
                    float inside = step(0, impactUV.x) * step(0, impactUV.y) * (1 - step(1, impactUV.x)) * (1 - step(1, impactUV.y));
                    float roof = tex2Dlod(_Rim3DRoofMask, float4(impactUV, 0, 0)).r;
                    active *= inside * (1 - roof);
                    float size = lerp(.16, .24, o.effect.z);
                    float3 impactRelative = origin - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection
                        + float3(impactXZ.x, -_Volume.z + .025, impactXZ.y);
                    float impactDepth = -mul((float3x3)UNITY_MATRIX_V, impactRelative).z;
                    active *= smoothstep(max(.2, _ProjectionParams.y + size), max(.8, _ProjectionParams.y + size * 2), impactDepth);
                    if (active < .001)
                    {
                        o.pos = float4(0,0,0,1); o.uv = 0; o.world = 0; o.data = 0;
                        return o;
                    }
                    float3 offset = effect < 1.5
                        ? float3(v.vertex.x * size, 0, v.vertex.y * size)
                        : UNITY_MATRIX_V._m00_m01_m02 * v.vertex.x * size + float3(0, (v.vertex.y + .5) * size * .7, 0);
                    o.world = float3(origin.x + impactXZ.x, _Rim3DMapGroundY + .025, origin.z + impactXZ.y) + offset;
                    float3 relative = impactRelative + offset;
                    o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                    o.uv = v.vertex.xy + .5;
                    float edge = 1 - smoothstep(_Volume.x * .75, _Volume.x, max(abs(impactXZ.x), abs(impactXZ.y)));
                    o.data = float4(1, 0, 0, active * edge);
                    o.effect.y = phase;
                    return o;
                }
                float y = frac(v.seed.y - _WeatherTime * speed / _Volume.y) * _Volume.y;
                float2 drift = _WeatherDrift.xy * (snow + dust * 2)
                    - _Wind.xy * _Wind.z * rain * 1.5 * (y + _Volume.z) / speed;
                drift += sin(_WeatherTime * .7 + v.seed.yz * 31) * (snow + dust) * .65;
                float2 xz = frac((v.seed.xz * 8192 + drift - origin.xz) / (_Volume.x * 2) + .5) * (_Volume.x * 2) - _Volume.x;
                float3 center = origin + float3(xz.x, y, xz.y);
                float2 mapUV = center.xz / _Rim3DRoofMapSize.xy;
                float inside = step(0, mapUV.x) * step(0, mapUV.y) * (1 - step(1, mapUV.x)) * (1 - step(1, mapUV.y));
                float roof = tex2Dlod(_Rim3DRoofMask, float4(mapUV, 0, 0)).r;
                // Discard whole particles below roofs; no CPU roof query per drop.
                float width = (rain * lerp(.018, .028, rainStrength) * lerp(.8, 1.15, o.effect.z) + snow * .075 + dust * .055) * _Volume.w;
                float height = (rain * lerp(.48, .8, rainStrength) * lerp(.7, 1.2, o.effect.z) + snow * .075 + dust * .055) * _Volume.w;
                float roofMargin = height * .5 + .03;
                active *= inside * (1 - roof * (1 - step(_Rim3DRoofHeight + roofMargin, center.y)));
                float3 centerRelative = origin - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection + float3(xz.x, y, xz.y);
                float depth = -mul((float3x3)UNITY_MATRIX_V, centerRelative).z;
                float nearFade = smoothstep(max(.2, _ProjectionParams.y + height * .5),
                    max(.8, _ProjectionParams.y + height), depth);
                active *= nearFade;
                if (active < .001)
                {
                    o.pos = float4(0,0,0,1); o.uv = 0; o.world = 0; o.data = 0;
                    return o;
                }
                float3 right = UNITY_MATRIX_V._m00_m01_m02;
                float3 up = lerp(UNITY_MATRIX_V._m10_m11_m12, normalize(float3(-_Wind.x * _Wind.z * 1.5, speed, -_Wind.y * _Wind.z * 1.5)), rain);
                float3 offset = right * v.vertex.x * width + up * v.vertex.y * height;
                o.world = center + offset;
                // Camera-relative projection preserves precision at preview y=-10000.
                float3 relative = centerRelative + offset;
                o.pos = mul(UNITY_MATRIX_P, float4(mul((float3x3)UNITY_MATRIX_V, relative), 1));
                o.uv = v.vertex.xy + .5;
                float edge = 1 - smoothstep(_Volume.x * .75, _Volume.x, max(abs(xz.x), abs(xz.y)));
                float ground = saturate((center.y - _Rim3DMapGroundY) * 3);
                o.data = float4(rain, snow, dust, active * edge * ground);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                clip(i.data.w - .001);
                float2 p = i.uv * 2 - 1;
                float roundShape = saturate(1 - dot(p, p));
                float core = exp2(-p.x * p.x * 12);
                float taper = 1 - smoothstep(.5, 1, abs(p.y));
                float streak = core * taper * lerp(.35, .7, i.uv.y) * lerp(.65, 1, i.effect.z);
                float alpha = lerp(roundShape * roundShape * .8, streak, i.data.x);
                if (i.effect.x > .5 && i.effect.x < 1.5)
                {
                    float radius = length(p);
                    float ringRadius = lerp(.12, .86, i.effect.y);
                    float ring = 1 - smoothstep(.035, .10, abs(radius - ringRadius));
                    float impact = exp2(-dot(p, p) * 24) * saturate(1 - i.effect.y * 5);
                    alpha = (ring * .38 + impact * .65) * (1 - i.effect.y) * (1 - i.effect.y);
                }
                else if (i.effect.x > 1.5)
                {
                    float phase = i.effect.y;
                    float lift = 2.8 * phase * (1 - phase);
                    float2 left = float2(-.12 - phase * .62, lift * .75);
                    float2 right = float2(.12 + phase * .58, lift * .9);
                    float2 middle = float2((i.effect.z - .5) * phase * .4, lift);
                    float2 uv = float2(p.x, i.uv.y);
                    float droplets = exp2(-dot(uv - left, uv - left) * 240)
                        + exp2(-dot(uv - right, uv - right) * 240)
                        + exp2(-dot(uv - middle, uv - middle) * 300);
                    alpha = saturate(droplets) * .65 * (1 - phase) * smoothstep(0, .08, phase);
                }
                alpha *= i.data.w;
                if (i.effect.x < .5)
                    alpha *= lerp(1, saturate((i.world.y - _Rim3DMapGroundY) * 30), i.data.x);
                float d = length(i.world - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection);
                alpha *= smoothstep(.2, .8, d);
                float f = max(0, d - _Rim3DWeatherFog.z);
                alpha *= exp(-f * _Rim3DWeatherFog.x - f * f * _Rim3DWeatherFog.y);
                clip(alpha - .003);
                float3 color = _Tint.rgb * lerp(float3(1,1,1), float3(.66,.57,.44), i.data.z);
                return fixed4(color, alpha);
            }
            ENDCG
        }
    }
}
