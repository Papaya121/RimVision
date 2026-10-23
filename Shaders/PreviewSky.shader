Shader "Recovery/Rim3D/PreviewSky"
{
    Properties
    {
        _Zenith ("Upper sky", Color) = (.22,.43,.68,1)
        _Horizon ("Horizon", Color) = (.76,.82,.86,1)
        _SunDirection ("Sun direction", Vector) = (0,1,0,0)
        _Daylight ("Daylight", Float) = 1
        _Overcast ("Overcast", Range(0,1)) = 0
        _Night ("Night visibility", Range(0,1)) = 0
        _MoonTex ("Full moon", 2D) = "black" {}
        _MoonLight ("Moon light direction", Vector) = (0,0,1,0)
        _CloudNoise ("Cloud noise", 2D) = "gray" {}
        _CloudColor ("Cloud color", Color) = (.91,.94,.98,1)
        _CloudOffset ("Cloud layer offsets", Vector) = (0,0,0,0)
        _Space ("Orbital background", Float) = 0
        _OrbitalClouds ("Orbital cloud map", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float4 _Zenith, _Horizon, _SunDirection, _CloudColor, _CloudOffset;
            float _Daylight, _Overcast, _Night;
            float _Space;
            float4x4 _OrbitalFrame;
            sampler2D _OrbitalClouds;
            sampler2D _CloudNoise;
            sampler2D _MoonTex;
            float4 _MoonTex_TexelSize, _MoonLight;
            float4 _PlanetDirections[8], _PlanetSurfaces[8], _PlanetPrimary[8], _PlanetSecondary[8], _PlanetRings[8];
            struct v2f { float4 pos : SV_POSITION; float3 direction : TEXCOORD0; };
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.direction = v.vertex.xyz;
                return o;
            }
            float starHash(float3 p)
            {
                p = frac(p * .1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }
            float stars(float3 direction)
            {
                // A 3D field has neither a panorama seam nor stretched polar stars.
                float3 p = direction * 115;
                float3 cell = floor(p);
                float seed = starHash(cell);
                float3 center = float3(starHash(cell + 17.1), starHash(cell + 43.7), starHash(cell + 91.3));
                float distanceToStar = length(frac(p) - lerp(.2, .8, center));
                float radius = lerp(.08, .17, seed);
                float aa = max(length(fwidth(p)) * .5, .015);
                float disk = 1 - smoothstep(max(0, radius - aa), radius + aa, distanceToStar);
                return disk * step(.965, seed) * lerp(.45, 1.1, center.x)
                    * min(1, radius / aa);
            }
            float planetNoise(float3 p, float seed)
            {
                float a = tex2Dlod(_CloudNoise, float4(p.xy * .31 + seed, 0, 0)).r;
                float b = tex2Dlod(_CloudNoise, float4(p.yz * .73 - seed * .37, 0, 0)).r;
                float c = tex2Dlod(_CloudNoise, float4(p.zx * 1.57 + seed * .19, 0, 0)).r;
                return a * .55 + b * .3 + c * .15;
            }
            float3 planets(float3 sky, float3 background, float3 direction, float visibility)
            {
                float angularAA = max(length(fwidth(direction)), .00002);
                background *= lerp(.78, 1, _Night);
                [loop]
                for (int index = 0; index < 8; index++)
                {
                    float4 body = _PlanetDirections[index];
                    float4 rings = _PlanetRings[index];
                    float facing = dot(direction, body.xyz);
                    float extent = body.w * max(1.14, rings.x);
                    [branch]
                    if (body.y < -extent || facing < rsqrt(1 + extent * extent)) continue;
                    float3 right = normalize(cross(float3(0,1,0), body.xyz));
                    float3 up = cross(body.xyz, right);
                    float2 disk = float2(dot(direction, right), dot(direction, up)) / (body.w * facing);
                    float radiusSquared = dot(disk, disk);
                    float radius = sqrt(radiusSquared);
                    float edgeAA = max(angularAA / body.w, .002);
                    float4 surface = _PlanetSurfaces[index];
                    float3 primary = _PlanetPrimary[index].rgb;
                    float3 secondary = _PlanetSecondary[index].rgb;
                    float3 sunlight = normalize(_SunDirection.xyz);
                    float3 light = float3(dot(sunlight, right), dot(sunlight, up), -dot(sunlight, body.xyz));
                    float sphereDepth = sqrt(saturate(1 - radiusSquared));
                    float ringAlpha = 0;
                    float3 ringColor = 0;
                    float ringDepth = -2;
                    [branch]
                    if (rings.x > 0)
                    {
                        float tilt = radians(rings.z);
                        float rotation = radians(rings.w);
                        float3 ringNormal = float3(sin(tilt) * cos(rotation), sin(tilt) * sin(rotation), cos(tilt));
                        ringDepth = -dot(ringNormal.xy, disk) / ringNormal.z;
                        float3 ringPoint = float3(disk, ringDepth);
                        float ringRadius = length(ringPoint);
                        float ringAA = max(edgeAA / ringNormal.z, .008);
                        ringAlpha = smoothstep(rings.y - ringAA, rings.y + ringAA, ringRadius)
                            * (1 - smoothstep(rings.x - ringAA, rings.x + ringAA, ringRadius));
                        float bands = .65 + .2 * sin(ringRadius * 85 + surface.y) + .15 * sin(ringRadius * 173);
                        float gap = 1 - .8 * (1 - smoothstep(.015, .04, abs(ringRadius - lerp(rings.y, rings.x, .64))));
                        ringAlpha *= bands * gap * .85;
                        float shadowDistance = length(ringPoint - light * dot(ringPoint, light));
                        float shadow = lerp(1, smoothstep(.94, 1.06, shadowDistance), step(dot(ringPoint, light), 0));
                        ringColor = lerp(primary, float3(.83,.85,.89), .35)
                            * (.2 + .8 * abs(dot(ringNormal, light))) * lerp(.18, 1, shadow);
                        if (ringDepth <= sphereDepth || radiusSquared >= 1)
                            sky = lerp(sky, background + ringColor, ringAlpha * visibility);
                    }
                    [branch]
                    if (radius < 1.14)
                    {
                        float3 normal = float3(disk, sphereDepth);
                        float spinSin, spinCos;
                        sincos(surface.z, spinSin, spinCos);
                        float3 p = float3(normal.x * spinCos + normal.z * spinSin, normal.y,
                            normal.z * spinCos - normal.x * spinSin);
                        float noise = planetNoise(p, surface.y);
                        float detail = planetNoise(p * 3.7, surface.y + 5.3);
                        float3 albedo;
                        float emission = 0;
                        if (surface.x < .5)
                        {
                            float craters = smoothstep(.39, .44, detail) - smoothstep(.46, .5, detail);
                            albedo = lerp(secondary, primary, smoothstep(.3, .68, noise)) * (1 - craters * .28);
                        }
                        else if (surface.x < 1.5)
                        {
                            float bands = sin((p.y + (noise - .5) * .22) * 42 + surface.y);
                            float storm = exp(-dot((p.xy - float2(.32, -.24)) * float2(5, 11),
                                (p.xy - float2(.32, -.24)) * float2(5, 11))) * smoothstep(0, .3, p.z);
                            albedo = lerp(secondary, primary, saturate(.5 + bands * .32 + (detail - .5) * .6));
                            albedo = lerp(albedo, secondary * 1.2, storm * .8);
                        }
                        else if (surface.x < 2.5)
                        {
                            float land = smoothstep(.49, .55, noise);
                            albedo = lerp(primary, secondary, land);
                            float clouds = smoothstep(.53, .64, detail + noise * .12);
                            albedo = lerp(albedo, float3(.88,.94,1), clouds * .85);
                            albedo = lerp(albedo, float3(.78,.9,.96), smoothstep(.8, .97, abs(p.y)));
                        }
                        else if (surface.x < 3.5)
                        {
                            emission = 1 - smoothstep(.005, .018, abs(noise - .5) + (detail - .5) * .04);
                            albedo = lerp(primary, secondary, emission * .35);
                        }
                        else
                        {
                            float cracks = 1 - smoothstep(.008, .035, abs(noise - .5));
                            albedo = lerp(primary, secondary, saturate(cracks * .7 + (detail - .4) * .6));
                            albedo += pow(saturate(detail), 5) * .3;
                        }
                        float illumination = .055 + .9 * pow(saturate(dot(normal, light)), .75);
                        float3 color = albedo * illumination + secondary * emission * .12;
                        float limb = pow(1 - sphereDepth, 3) * surface.w;
                        color += lerp(primary, float3(.34,.65,1), .55) * limb * (.15 + illumination * .5);
                        float mask = (1 - smoothstep(1 - edgeAA, 1 + edgeAA, radius)) * visibility;
                        sky = lerp(sky, background + color, mask);
                        float halo = exp(-max(0, radius - 1) * 38) * smoothstep(1 - edgeAA, 1 + edgeAA, radius);
                        sky += lerp(primary, float3(.25,.55,1), .6) * halo * surface.w * visibility * .12;
                    }
                    if (ringDepth > sphereDepth && radiusSquared < 1)
                        sky = lerp(sky, background + ringColor, ringAlpha * visibility);
                }
                return sky;
            }
            float3 orbitalSky(float3 direction)
            {
                float3 light = normalize(_SunDirection.xyz);
                float sunDot = dot(direction, light);
                float sunAA = max(fwidth(sunDot), .000005);
                float3 sky = float3(.001,.002,.006) + stars(direction) * float3(.75,.84,1);
                sky += float3(1,.94,.82) * smoothstep(.99996 - sunAA, .99996 + sunAA, sunDot);
                sky += float3(.42,.53,.75) * pow(saturate(sunDot), 256) * .12;
                float3 center = float3(0,-1.035,0);
                float facing = dot(direction, center);
                float closestSquared = dot(center, center) - facing * facing;
                float limbAA = max(fwidth(closestSquared), .00008);
                float atmosphere = exp(-max(0, closestSquared - 1) * 220)
                    * (1 - smoothstep(1.002, 1.035, closestSquared)) * step(0, facing);
                float3 tangentNormal = normalize(direction * max(0, facing) - center + float3(0,.00001,0));
                float rimDay = smoothstep(-.15, .2, dot(tangentNormal, light));
                sky += float3(.06,.28,.7) * atmosphere * (.035 + rimDay * .8);
                [branch]
                if (facing > 0 && closestSquared < 1 + limbAA)
                {
                    float distanceToSurface = facing - sqrt(max(0, 1 - closestSquared));
                    float3 normal = normalize(direction * distanceToSurface - center);
                    float3 p = mul((float3x3)_OrbitalFrame, normal);
                    float terrain = planetNoise(p * 11.5, 4.73);
                    float detail = planetNoise(p * 43.7, 11.9);
                    float land = smoothstep(.49, .515, terrain + (detail - .5) * .08);
                    float latitude = abs(p.y);
                    float arid = smoothstep(.38, .65, detail) * (1 - smoothstep(.3, .65, latitude));
                    float3 ground = lerp(float3(.17,.32,.12), float3(.50,.39,.23), arid);
                    ground *= lerp(.7, 1.18, detail);
                    float snow = smoothstep(.82, .94, latitude + (terrain - .5) * .2);
                    ground = lerp(ground, float3(.72,.8,.84), snow);
                    float shallow = smoothstep(.44, .51, terrain);
                    float3 ocean = lerp(float3(.015,.08,.20), float3(.04,.25,.34), shallow);
                    float3 albedo = lerp(ocean, ground, land);
                    float3 orbitalUp = mul((float3x3)_OrbitalFrame, float3(0,1,0));
                    float2 uv = float2(atan2(orbitalUp.x, -orbitalUp.z) / (2 * UNITY_PI) + .5,
                        asin(clamp(orbitalUp.y, -1, 1)) / UNITY_PI + .5);
                    uv += normal.xz * float2(2,4);
                    uv.x += _CloudOffset.x;
                    float clouds = tex2D(_OrbitalClouds, uv).r;
                    float cloudShadow = tex2D(_OrbitalClouds, uv + float2(.0015,-.001)).r;
                    albedo *= 1 - cloudShadow * .28;
                    albedo = lerp(albedo, float3(.84,.89,.95), saturate(clouds * 1.4));
                    float sunlight = dot(normal, light);
                    float day = smoothstep(-.08, .12, sunlight);
                    float3 planet = albedo * (lerp(.04, .28, day) + .72 * saturate(sunlight));
                    float specular = pow(saturate(dot(reflect(-light, normal), -direction)), 110)
                        * (1 - land) * (1 - clouds) * day;
                    planet += float3(.85,.86,.78) * specular * .45;
                    float limb = pow(1 - saturate(dot(normal, -direction)), 3);
                    planet = lerp(planet, float3(.10,.34,.68) * (.05 + day * .65), limb * .72);
                    float mask = 1 - smoothstep(1 - limbAA, 1 + limbAA, closestSquared);
                    sky = lerp(sky, planet, mask);
                }
                return sky;
            }
            float4 frag(v2f i) : SV_Target
            {
                float3 direction = normalize(i.direction);
                [branch]
                if (_Space > .5) return float4(orbitalSky(direction), 1);
                float height = pow(saturate(direction.y), .55);
                float3 sky = lerp(_Horizon.rgb, _Zenith.rgb, height);
                sky = lerp(sky, _Horizon.rgb * (1 - height * .2), _Overcast * .8);
                float aboveHorizon = smoothstep(0, .08, direction.y);
                float sunDot = dot(direction, normalize(_SunDirection.xyz));
                float sun = saturate(sunDot);
                sky += float3(1,.70,.35) * pow(sun, 18) * .18 * _Daylight * (1 - _Overcast);
                float sunAA = max(fwidth(sunDot), .00004);
                sky += float3(1,.92,.72) * smoothstep(.99965 - sunAA, .99965 + sunAA, sunDot)
                    * _Daylight * aboveHorizon;
                float3 nightBackground = sky;
                if (_Night > .001)
                    sky += stars(direction) * float3(.8,.88,1) * _Night * aboveHorizon;
                sky = planets(sky, nightBackground, direction, aboveHorizon * (1 - _Overcast) * lerp(.65, 1, _Night));
                if (_Night > .001)
                {
                    float3 moonDirection = -normalize(_SunDirection.xyz);
                    float moonDot = dot(direction, moonDirection);
                    float3 moonRight = normalize(cross(abs(moonDirection.y) > .999 ? float3(0,0,1) : float3(0,1,0), moonDirection));
                    float3 moonUp = cross(moonDirection, moonRight);
                    float2 disk = float2(dot(direction, moonRight), dot(direction, moonUp)) / .03162;
                    float radiusSquared = dot(disk, disk);
                    float illuminatedFraction = saturate((1 + _MoonLight.z) * .5);
                    if (moonDot > 0 && radiusSquared < 1.1)
                    {
                        float2 uv = disk * float2(.49, .49 * _MoonTex_TexelSize.z / _MoonTex_TexelSize.w) + .5;
                        float4 moon = tex2D(_MoonTex, uv);
                        float3 normal = float3(disk, sqrt(saturate(1 - radiusSquared)));
                        float light = dot(normal, _MoonLight.xyz);
                        float terminatorAA = max(fwidth(light), .015);
                        float illumination = pow(saturate(light), .65) * smoothstep(0, terminatorAA, light);
                        float earthshine = .018 * (1 - illuminatedFraction * .7) * lerp(.35, 1, normal.z);
                        float diskAA = max(fwidth(radiusSquared), .001);
                        float mask = moon.a * (1 - smoothstep(1 - diskAA, 1 + diskAA, radiusSquared))
                            * _Night * aboveHorizon;
                        sky = lerp(sky, nightBackground + moon.rgb * float3(.75,.82,1)
                            * (illumination * .85 + earthshine), mask);
                    }
                    sky += float3(.65,.73,.88) * pow(saturate(moonDot), 64) * .025
                        * illuminatedFraction * _Night * aboveHorizon;
                }
                // Two texture samples replace expensive per-pixel fractal noise/ray marching.
                // Curved cloud shell: a plane with a clamped denominator freezes UVs
                // near the horizon and stretches cloud edges into vertical curtains.
                float cloudHeight = max(0, direction.y);
                float shellProjection = cloudHeight + sqrt(cloudHeight * cloudHeight + .04);
                float2 cloudUV = direction.xz / shellProjection * .28;
                float cloudHorizonFade = smoothstep(.015, .14, direction.y);
                float broad = tex2D(_CloudNoise, cloudUV + _CloudOffset.xy).r;
                float detail = tex2D(_CloudNoise, cloudUV * 2.17 + _CloudOffset.zw).r;
                float noise = broad * .75 + detail * .25;
                float threshold = lerp(.59, .24, _Overcast);
                float coverage = smoothstep(threshold - .07, threshold + .09, noise);
                coverage = lerp(coverage, 1, _Overcast * _Overcast) * cloudHorizonFade;
                float shade = lerp(.6, 1, smoothstep(.3, .75, noise));
                float3 cloudColor = _CloudColor.rgb * shade * lerp(1, .65, _Overcast);
                sky = lerp(sky, cloudColor, coverage);
                return float4(sky, 1);
            }
            ENDCG
        }
    }
}
