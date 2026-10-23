Shader "Recovery/Rim3D/GroundShadow"
{
    Properties
    {
        _MainTex ("Silhouette", 2D) = "white" {}
        _Color ("Source tint", Color) = (1,1,1,1)
        _Opaque ("Solid object", Float) = 1
        _Billboard ("Plant card", Float) = 0
        _ProjectionDirection ("Sun rays", Vector) = (.4,-.7,.4,0)
        _ShadowRight ("Silhouette width direction", Vector) = (1,0,0,0)
        _GroundY ("Ground height", Float) = -9999.988
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Cull Off ZWrite Off ZTest LEqual
        Blend DstColor Zero
        // The projection already sits above the floor. Slope depth bias can
        // pull it through walls at grazing angles; accept only ground pixels.
        Stencil
        {
            Ref 64
            ReadMask 64
            WriteMask 64
            Comp Equal
            // Consume the receiver bit after the first shadow. Overlapping
            // faces and objects must not multiply darkness repeatedly.
            Pass Zero
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float3 _Rim3DCameraPositionCorrection;
            sampler2D _MainTex, _Rim3DRoofMask;
            float4 _Rim3DRoofMapSize;
            float4 _Rim3DWeatherFog;
            float _Rim3DRoofHeight, _Rim3DMapGroundY;
            float4 _MainTex_ST, _Color, _ProjectionDirection, _ShadowRight;
            float _Opaque, _Billboard, _GroundY, _Rim3DShadowOpacity;
            #include "PlantWind.cginc"
            float RoofCoverage(float2 worldXZ)
            {
                float2 uv = worldXZ / _Rim3DRoofMapSize.xy;
                float inside = step(0, uv.x) * step(0, uv.y)
                    * (1 - step(1, uv.x)) * (1 - step(1, uv.y));
                return tex2D(_Rim3DRoofMask, uv).r * inside;
            }
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 center : TEXCOORD1;
                float4 plant : TEXCOORD3;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float distanceToEye : TEXCOORD1;
                float2 worldXZ : TEXCOORD2;
            };
            v2f vert(appdata v)
            {
                if (_Billboard > .5)
                {
                    float3 offset = v.vertex.xyz - v.center;
                    v.vertex.xyz = v.center + _ShadowRight.xyz * offset.x + float3(0,offset.y,0);
                }
                float groundY = _Rim3DMapGroundY + .012;
                v.vertex.xyz += PlantWindOffset(v.vertex.xyz, v.plant);
                float3 origin = unity_ObjectToWorld._m03_m13_m23;
                float3 world = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz)
                    + float3(origin.x, origin.y - groundY, origin.z);
                float height = max(0, world.y);
                world.xz += _ProjectionDirection.xz * height / max(.1, -_ProjectionDirection.y);
                v2f o;
                // Match the surface shader's camera-relative projection so
                // distant world origins do not introduce depth/edge seams.
                float3 relative = float3(world.x - _WorldSpaceCameraPos.x,
                    groundY - _WorldSpaceCameraPos.y, world.z - _WorldSpaceCameraPos.z)
                    - _Rim3DCameraPositionCorrection;
                float3 view = mul((float3x3)UNITY_MATRIX_V, relative);
                o.pos = mul(UNITY_MATRIX_P, float4(view,1));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.distanceToEye = length(relative);
                o.worldXZ = world.xz;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // The surface shader already removes direct sun beneath the
                // roof and in its outdoor shadow; do not darken those twice.
                float roofHeight = max(0, _Rim3DRoofHeight - _Rim3DMapGroundY);
                float2 roofRay = i.worldXZ - _ProjectionDirection.xz
                    * roofHeight / max(.05, -_ProjectionDirection.y);
                clip(.5 - max(RoofCoverage(i.worldXZ), RoofCoverage(roofRay)));
                float alpha = lerp(tex2D(_MainTex,i.uv).a * _Color.a, 1, _Opaque);
                clip(alpha - .15);
                float outdoorFog = _Rim3DWeatherFog.w;
                float distance = max(0, i.distanceToEye - lerp(8, _Rim3DWeatherFog.z, outdoorFog)) * (1 - unity_OrthoParams.w);
                float visibility = exp(-distance * lerp(.012, _Rim3DWeatherFog.x, outdoorFog)
                    - distance * distance * lerp(.00012, _Rim3DWeatherFog.y, outdoorFog));
                float shade = 1 - _Rim3DShadowOpacity * visibility;
                return fixed4(shade,shade,shade,1);
            }
            ENDCG
        }
    }
}
