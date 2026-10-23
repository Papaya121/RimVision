#include "UnityCG.cginc"

sampler2D _MainTex, _Rim3DRoofMask;
float4 _MainTex_ST, _Color, _SurfaceScale, _Rim3DRoofMapSize, _Rim3DWeatherFog;
float3 _Rim3DOutlineCameraPosition, _Rim3DOutlinePositionCorrection;
float4x4 _Rim3DOutlineView, _Rim3DOutlineProjection;
float _Opaque, _WeaponVolume, _SurfaceTile, _WallSurface, _SurfaceRepeat, _Rim3DRoofHeight;
float _Rim3DOutlineFirstPerson;
float _Rim3DOutlineWidth, _Rim3DOutlineWorldPerPixel, _Rim3DOutlineNear;
#include "PlantWind.cginc"

struct OutlineInput
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 center : TEXCOORD2;
    float4 plant : TEXCOORD3;
    float4 color : COLOR;
};

struct OutlineVaryings
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 world : TEXCOORD1;
    float3 normal : TEXCOORD2;
    float distanceToEye : TEXCOORD3;
    float alpha : TEXCOORD4;
    float orientation : TEXCOORD5;
};

OutlineVaryings OutlineVertex(OutlineInput v)
{
    OutlineVaryings o;
    v.vertex.xyz += PlantWindOffset(v.vertex.xyz, v.plant);
    v.center.xyz += PlantWindOffset(v.center.xyz, v.plant);
    float3 translation = unity_ObjectToWorld._m03_m13_m23 - _Rim3DOutlineCameraPosition - _Rim3DOutlinePositionCorrection;
    float3 relative = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz) + translation;
    float3 center = mul((float3x3)unity_ObjectToWorld, v.center.xyz) + translation;
    float3 viewCenter = mul((float3x3)_Rim3DOutlineView, center);
    float depth = max(_Rim3DOutlineNear, -viewCenter.z);
    float3 objectScale = float3(length(unity_ObjectToWorld._m00_m10_m20),
        length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22));
    float radius = max(.0001, abs(v.center.w) * max(objectScale.x, max(objectScale.y, objectScale.z)));
    float width = _Rim3DOutlineWidth * min(1, 6 / max(6, depth));
    float expansion = width * _Rim3DOutlineWorldPerPixel * depth / radius;
    #if defined(RIM3D_SCREEN_OUTLINE)
        float3 view = mul((float3x3)_Rim3DOutlineView, relative);
        o.pos = mul(_Rim3DOutlineProjection, float4(view, 1));
        viewCenter.z = -depth;
        float4 pivot = mul(_Rim3DOutlineProjection, float4(viewCenter, 1));
        o.pos.xy += (o.pos.xy - pivot.xy * (o.pos.w / pivot.w)) * expansion;
    #else
        float3 expanded = center + (relative - center) * (1 + expansion);
        float3 view = mul((float3x3)_Rim3DOutlineView, expanded);
        o.pos = mul(_Rim3DOutlineProjection, float4(view, 1));
    #endif
    if (v.center.w == 0) o.pos = float4(0, 0, 0, 1);
    o.uv = _SurfaceTile > .5 ? v.uv : TRANSFORM_TEX(v.uv, _MainTex);
    o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
    o.normal = UnityObjectToWorldNormal(v.normal);
    o.distanceToEye = length(relative);
    o.alpha = v.color.a;
    o.orientation = sign(v.center.w);
    return o;
}

fixed4 OutlineFragment(OutlineVaryings i, fixed facing : VFACE) : SV_Target
{
    clip(-facing * i.orientation);
    clip(45 - i.distanceToEye);
    if (_WeaponVolume > .5)
        clip(dot(i.normal, i.world - _Rim3DOutlineCameraPosition - _Rim3DOutlinePositionCorrection));
    if (_Opaque < .5 && _WeaponVolume * (1 - step(.75, i.alpha)) < .5)
    {
        float2 uv = i.uv;
        if (_WallSurface > .5)
            uv = abs(i.normal.y) > .5 ? i.world.xz
                : float2(abs(i.normal.x) > .5 ? i.world.z : i.world.x, i.world.y);
        if (_SurfaceTile > .5)
        {
            uv *= _SurfaceScale.xy;
            if (_SurfaceRepeat < .5) uv = frac(uv);
            uv = TRANSFORM_TEX(uv, _MainTex);
        }
        clip(tex2D(_MainTex, uv).a * _Color.a - .25);
    }
    if (_Rim3DOutlineFirstPerson > .5) return fixed4(0, 0, 0, 1);
    float2 roofUV = i.world.xz / _Rim3DRoofMapSize.xy;
    float inside = step(0, roofUV.x) * step(0, roofUV.y) * (1 - step(1, roofUV.x)) * (1 - step(1, roofUV.y));
    float roof = tex2D(_Rim3DRoofMask, roofUV).r * inside * (1 - step(_Rim3DRoofHeight, i.world.y));
    float outdoorFog = _Rim3DWeatherFog.w * (1 - roof);
    float fogDistance = max(0, i.distanceToEye - lerp(8, _Rim3DWeatherFog.z, outdoorFog));
    float visibility = exp(-fogDistance * lerp(.012, _Rim3DWeatherFog.x, outdoorFog)
        - fogDistance * fogDistance * lerp(.00012, _Rim3DWeatherFog.y, outdoorFog));
    visibility *= 1 - smoothstep(18, 45, i.distanceToEye);
    return fixed4(0, 0, 0, visibility);
}
