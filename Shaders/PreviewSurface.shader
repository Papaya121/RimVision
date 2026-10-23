Shader "Recovery/Rim3D/PreviewSurface"
{
    Properties
    {
        _MainTex ("Original texture", 2D) = "white" {}
        _MaskTex ("Original recolor mask", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ColorTwo ("Second tint", Color) = (1,1,1,1)
        _UseMask ("Recolor mask", Float) = 0
        [HideInInspector] _WeaponVolume ("Weapon volume vertex tint", Float) = 0
        [HideInInspector] _SrcBlend ("Source blend", Float) = 1
        [HideInInspector] _DstBlend ("Destination blend", Float) = 0
        [HideInInspector] _ZWrite ("Depth write", Float) = 1
        [HideInInspector] _ZTest ("Depth comparison", Float) = 4
        [HideInInspector] _Ghost ("Construction opacity", Float) = 0
        _Opaque ("Solid cube", Float) = 1
        [HideInInspector] _SurfaceOpacity ("Surface opacity", Range(0,1)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Face culling", Float) = 0
        _Unlit ("Pawn card", Float) = 0
        _Billboard ("Plant billboard", Float) = 0
        _Foliage ("Leaf translucency", Float) = 0
        _Ground ("Ground receiver", Float) = 0
        [HideInInspector] _SnowGround ("Receives ground snow", Float) = 0
        [HideInInspector] _Pollution ("Ground pollution", Float) = 0
        [HideInInspector] _PollutedTex ("Polluted ground", 2D) = "white" {}
        [HideInInspector] _PollutionTex ("Pollution overlay", 2D) = "black" {}
        [HideInInspector] _PollutionColor ("Pollution overlay tint", Color) = (1,1,1,0)
        [HideInInspector] _PollutionTint ("Polluted ground tint", Color) = (1,1,1,1)
        [HideInInspector] _PollutionScale ("Pollution overlay scale", Vector) = (1,1,0,0)
        [HideInInspector] _PollutionScroll ("Pollution overlay scroll", Vector) = (0,0,0,0)
        [HideInInspector] _PollutionBlend ("Pollution blend mode", Float) = 0
        [HideInInspector] _TerrainEdge ("Terrain edge alpha", Float) = 0
        [HideInInspector] _TerrainDepthBias ("Terrain edge depth bias", Float) = 0
        [HideInInspector] _TerrainRough ("Rough terrain edge", Float) = 0
        [HideInInspector] _AlphaAddTex ("Terrain edge noise", 2D) = "gray" {}
        [HideInInspector] _StencilWriteMask ("Stencil write mask", Int) = 64
        [HideInInspector] _GroundStencil ("Ground stencil", Int) = 0
        [HideInInspector] _StencilComp ("Stencil comparison", Int) = 8
        [HideInInspector] _StencilReadMask ("Stencil read mask", Int) = 255
        [HideInInspector] _ColorMask ("Color write mask", Int) = 15
        _FlatItem ("Item on ground", Float) = 0
        _Roof ("Roof slab", Float) = 0
        _SurfaceTile ("Seamless solid surface", Float) = 0
        [HideInInspector] _WallSurface ("Wall world UV", Float) = 0
        [HideInInspector] _WallBevel ("Wall bevel lighting", Float) = 0
        [HideInInspector] _SurfaceRepeat ("Standalone repeating surface", Float) = 0
        _SurfaceScale ("Surface repeats per metre", Vector) = (1,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "DisableBatching"="True" "Rim3DOutline"="Surface" }
        Cull [_Cull]
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Offset [_TerrainDepthBias], [_TerrainDepthBias]
        ColorMask [_ColorMask]
        Stencil
        {
            Ref [_GroundStencil]
            WriteMask [_StencilWriteMask]
            ReadMask [_StencilReadMask]
            Comp [_StencilComp]
            Pass Replace
        }
        CGINCLUDE
        #include "UnityCG.cginc"
        float3 _Rim3DCameraPositionCorrection;
        sampler2D _MainTex, _MaskTex, _Rim3DRoofMask, _Rim3DGameLightMap, _Rim3DSnowDepth, _Rim3DSnowTexture;
        sampler2D _Rim3DModelLightPositions;
        float _SnowGround, _Ghost, _WallBevel, _ZWrite;
        sampler2D _Rim3DShorelineMask, _PollutedTex, _PollutionTex, _AlphaAddTex;
        float _Pollution, _PollutionBlend, _TerrainEdge, _TerrainRough;
        float4 _PollutionColor, _PollutionTint, _PollutionScale, _PollutionScroll;
        float _Rim3DMapGroundY;
        float4 _Rim3DRoofMapSize;
        float _Rim3DRoofHeight;
        float RoofCoverage(float2 worldXZ)
        {
            float2 uv = worldXZ / _Rim3DRoofMapSize.xy;
            float inside = step(0, uv.x) * step(0, uv.y) * (1 - step(1, uv.x)) * (1 - step(1, uv.y));
            return tex2D(_Rim3DRoofMask, uv).r * inside;
        }
        float4 _Rim3DIndoorAmbientColor, _Rim3DIndoorFogColor;
        float4 _MainTex_ST, _Color, _ColorTwo, _Rim3DAmbientColor, _Rim3DFogColor, _Rim3DViewRight, _Rim3DSunColor, _Rim3DSunDirection;
        float _UseMask, _Opaque, _Unlit, _Billboard, _Foliage, _Ground, _FlatItem, _Roof, _SurfaceTile, _WallSurface, _SurfaceRepeat, _WeaponVolume;
        float _SurfaceOpacity;
        float4 _SurfaceScale;
        float4 _Rim3DWeatherFog;
        #include "PlantWind.cginc"
        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv : TEXCOORD0;
            float3 center : TEXCOORD1;
            float4 plant : TEXCOORD3;
            float4 color : COLOR;
        };
        void OrientPlant(inout appdata v)
        {
            if (_Billboard > .5)
            {
                float3 offset = v.vertex.xyz - v.center;
                v.vertex.xyz = v.center + _Rim3DViewRight.xyz * offset.x + float3(0, offset.y, 0);
                v.normal = cross(float3(0,1,0), _Rim3DViewRight.xyz);
            }
            v.vertex.xyz += PlantWindOffset(v.vertex.xyz, v.plant);
        }
        void Cutout(float2 uv)
        {
            clip(lerp(tex2D(_MainTex, uv).a * _Color.a, 1, _Opaque) - .25);
        }
        ENDCG
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 world : TEXCOORD2;
                fixed4 color : COLOR;
            };
            v2f vert(appdata v)
            {
                OrientPlant(v);
                v2f o;
                // Subtract the camera before adding local vertices. The preview
                // lives at y=-10000; a full world/view matrix loses enough
                // precision there to open bright cracks along floor triangles.
                float3 relative = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz)
                    + (unity_ObjectToWorld._m03_m13_m23 - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection);
                float3 view = mul((float3x3)UNITY_MATRIX_V, relative);
                o.pos = mul(UNITY_MATRIX_P, float4(view, 1));
                o.uv = _SurfaceTile > .5 ? v.uv : TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = lerp(fixed4(1,1,1,1), v.color, max(_WeaponVolume, _TerrainEdge));
                return o;
            }
            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float2 uv = i.uv;
                if (_WallSurface > .5)
                    uv = abs(i.normal.y) > .5 ? i.world.xz
                        : float2(abs(i.normal.x) > .5 ? i.world.z : i.world.x, i.world.y);
                if (_SurfaceTile > .5)
                {
                    uv *= _SurfaceScale.xy;
                    // Hardware repeat preserves derivatives at tile boundaries.
                    // Atlas/legacy surfaces still require an explicit local wrap.
                    if (_SurfaceRepeat < .5) uv = frac(uv);
                    uv = TRANSFORM_TEX(uv, _MainTex);
                }
                // Zero-alpha vertex tint marks the opaque muzzle, whose colour
                // comes from geometry rather than the side-view sprite.
                float muzzle = _WeaponVolume * (1 - step(.25, i.color.a));
                float volumeSide = _WeaponVolume * (1 - step(.75, i.color.a));
                if (volumeSide < .5) Cutout(uv);
                fixed3 t = tex2D(_MainTex, uv).rgb;
                t = lerp(t, fixed3(1,1,1), muzzle);
                fixed4 mask = tex2D(_MaskTex, uv);
                fixed3 masked = lerp(fixed3(1,1,1), _Color.rgb, mask.r);
                masked = lerp(masked, _ColorTwo.rgb, mask.g);
                fixed3 tint = lerp(_Color.rgb, masked, _UseMask);
                tint = lerp(tint, fixed3(1,1,1), muzzle);
                float3 normal = max(_Ground, _FlatItem) > .5 ? float3(0,1,0)
                    : normalize(i.normal) * (_Roof > .5 && facing < 0 ? -1 : 1);
                float ndl = dot(normal, normalize(_Rim3DSunDirection.xyz));
                // A leaf card rotates for visibility, not because the tree changed
                // orientation. Its brightness must therefore be independent of yaw.
                float foliageDiffuse = .65 + .35 * saturate(_Rim3DSunDirection.y);
                float diffuse = lerp(.3 + .7 * saturate(ndl), foliageDiffuse, _Foliage);
                // Every surface uses world-space sunlight. Camera-dependent shadow
                // map coverage must never alter its brightness; ground silhouettes
                // are rendered separately by GroundShadow.
                float hemisphere = lerp(.62, 1, saturate(normal.y * .5 + .5));
                // Intersect the ray toward the sun with the roof slab. This also
                // casts sunlight through actual openings and beyond roof edges.
                float belowRoof = 1 - step(_Rim3DRoofHeight, i.world.y);
                float3 sunDirection = normalize(_Rim3DSunDirection.xyz);
                float rayLength = max(0, _Rim3DRoofHeight - i.world.y) / max(.05, sunDirection.y);
                // Thin doors sit inside a roofed wall cell. Probe beyond that
                // cell on the facing side, so exterior faces receive outdoor
                // light while their interior faces remain shaded.
                // The visible face is already at the cell boundary. A large
                // offset skips across the neighboring cell and breaks roof
                // shadows into strips on corners and narrow wall sections.
                float diagonalWall = _WallBevel * step(.5, abs(normal.x)) * step(.5, abs(normal.z));
                float roofProbeOffset = _Opaque * (1 - max(_Ground, _FlatItem)) * lerp(.02, .09, diagonalWall);
                float2 roofProbe = i.world.xz + normal.xz * roofProbeOffset;
                float roofShadow = belowRoof * RoofCoverage(roofProbe + sunDirection.xz * rayLength);
                float overheadRoof = max(belowRoof * RoofCoverage(roofProbe), _Roof * step(.5, -normal.y));
                // Roof-only ray projection cannot account for enclosing walls.
                // Keep roofed interiors on indirect light instead of admitting
                // a shifted strip of direct sun through the base of each wall.
                float sunVisibility = (1 - roofShadow) * (1 - overheadRoof);
                float skyVisibility = lerp(1, .32, overheadRoof);
                // Use our explicit sun, not whichever light Unity selected for this
                // view/batch. A camera turn must not change the map's illumination.
                float3 ambient = lerp(_Rim3DAmbientColor.rgb, _Rim3DIndoorAmbientColor.rgb, overheadRoof);
                float3 light = ambient * hemisphere * skyVisibility + _Rim3DSunColor.rgb * diffuse * sunVisibility;
                light += _Rim3DSunColor.rgb * _Foliage * .08 * sunVisibility;
                light = lerp(light, ambient * skyVisibility + _Rim3DSunColor.rgb * .38 * sunVisibility, _Unlit);
                // GlowGrid already handles lamp state, colored light and blockers.
                // Sample the side of a wall facing this surface, in world space.
                float2 lampUV = (i.world.xz + normal.xz * .12) / _Rim3DRoofMapSize.xy;
                float inMap = step(0, lampUV.x) * step(0, lampUV.y)
                    * (1 - step(1, lampUV.x)) * (1 - step(1, lampUV.y));
                float3 lamps = tex2D(_Rim3DGameLightMap, lampUV).rgb;
                float heightFade = exp(-max(0, i.world.y - _Rim3DMapGroundY - 1) * .18);
                float4 lampAnchor = tex2D(_Rim3DModelLightPositions, lampUV);
                float3 lampPosition = lampAnchor.xyz / max(lampAnchor.a, .0001);
                lampPosition.y += _Rim3DMapGroundY;
                float lampDiffuse = .15 + .85 * saturate(dot(normal, normalize(lampPosition - i.world + float3(0, .0001, 0))));
                float lampShading = lerp(1, lampDiffuse, saturate(lampAnchor.a));
                light += min(lamps * 3.6, 1.35) * inMap * heightFade * lampShading;
                float3 surface = t * tint;
                if (_Ground > .5 && _Pollution > .5)
                {
                    float pollution = tex2D(_Rim3DShorelineMask, i.world.xz / _Rim3DRoofMapSize.xy).g;
                    if (pollution > .001)
                    {
                        float3 polluted = tex2D(_PollutedTex, uv).rgb * tint * _PollutionTint.rgb;
                        float4 overlay = tex2D(_PollutionTex, uv * _PollutionScale.xy + _Time.y * _PollutionScroll.xy) * _PollutionColor;
                        float3 blended = saturate(polluted + overlay.rgb - 1);
                        if (_PollutionBlend > 1.5)
                            blended = (1 - 2 * overlay.rgb) * polluted * polluted + 2 * overlay.rgb * polluted;
                        else if (_PollutionBlend > .5)
                            blended = saturate(polluted + overlay.rgb);
                        polluted = lerp(polluted, blended, saturate(overlay.a));
                        surface = lerp(surface, polluted, smoothstep(0, 1, pollution));
                    }
                }
                if (_Ground > .5 && _SnowGround > .5)
                {
                    float depth = tex2D(_Rim3DSnowDepth, i.world.xz / _Rim3DRoofMapSize.xy).r;
                    if (depth > .01)
                    {
                        float3 detail = tex2D(_Rim3DSnowTexture, i.world.xz * .125).rgb;
                        float coverage = smoothstep(.01, .45, depth);
                        // Blend before illumination: snow receives the same lamps,
                        // roof occlusion, projected shadows and weather fog as soil.
                        float3 snow = float3(.90, .94, .98) * (.86 + detail * .14);
                        surface = lerp(surface, snow, coverage);
                    }
                }
                float3 rgb = surface * light * i.color.rgb;
                float distanceToEye = length(i.world - _WorldSpaceCameraPos - _Rim3DCameraPositionCorrection);
                // Dense distance fog blends the forest and map boundary into the sky.
                float outdoorFog = _Rim3DWeatherFog.w * (1 - overheadRoof);
                float fogDistance = max(0, distanceToEye - lerp(8, _Rim3DWeatherFog.z, outdoorFog)) * (1 - unity_OrthoParams.w);
                float fog = 1 - exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, outdoorFog)
                    - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, outdoorFog));
                float3 fogColor = lerp(_Rim3DIndoorFogColor.rgb, _Rim3DFogColor.rgb, outdoorFog);
                float alpha = _Ghost > 0 ? tex2D(_MainTex, uv).a * _Ghost : _SurfaceOpacity;
                if (_TerrainEdge > .5)
                {
                    float edge = i.color.a;
                    float noise = tex2D(_AlphaAddTex, uv * 4).r;
                    alpha *= saturate(edge + (noise - .5) * .6 * 4 * edge * (1 - edge) * _TerrainRough);
                    clip(alpha - .001);
                }
                return fixed4(lerp(rgb, fogColor, fog), alpha);
            }
            ENDCG
        }
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            CGPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag
            #pragma multi_compile_shadowcaster
            #pragma target 3.0
            struct depthV2f
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
            };
            depthV2f depthVert(appdata v)
            {
                OrientPlant(v);
                depthV2f o;
                o.uv = _SurfaceTile > .5 ? v.uv : TRANSFORM_TEX(v.uv, _MainTex);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 depthFrag(depthV2f i) : SV_Target
            {
                clip(_ZWrite - .5);
                if (_Opaque < .5) clip(tex2D(_MainTex, i.uv).a * _Color.a - .25);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }
}
