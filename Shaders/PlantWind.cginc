float4 _Rim3DPlantWind;

float3 PlantWindOffset(float3 vertex, float4 plant)
{
    if (plant.w >= 0 || _Rim3DPlantWind.z <= 0) return 0;
    float height = max(0, vertex.y - plant.y);
    if (height <= 0) return 0;
    float3 root = mul(unity_ObjectToWorld, float4(plant.xyz, 1)).xyz;
    float2 roofUV = root.xz / _Rim3DRoofMapSize.xy;
    float inside = step(0, roofUV.x) * step(0, roofUV.y)
        * (1 - step(1, roofUV.x)) * (1 - step(1, roofUV.y));
    float shelter = tex2Dlod(_Rim3DRoofMask, float4(roofUV, 0, 0)).r * inside;
    float time = _Time.y;
    float travel = dot(root.xz, _Rim3DPlantWind.xy);
    float phase = dot(root.xz, float2(1.73, 2.39));
    float storm = saturate(_Rim3DPlantWind.w);
    float gust = .65 + .35 * sin(travel * .19 - time * .85);
    float sway = .6 + (.25 + .07 * storm) * sin(time * 1.7 - travel * .43 + sin(phase))
        + (.13 + .05 * storm) * sin(time * 3.1 + phase);
    float flutter = sin(time * 5.3 + phase * 2.7) * (.12 + .14 * storm);
    float bend = -plant.w * height * (.35 + .65 * height / (1 + height))
        * _Rim3DPlantWind.z * gust * (1 - shelter);
    float2 offset = (_Rim3DPlantWind.xy * sway
        + float2(-_Rim3DPlantWind.y, _Rim3DPlantWind.x) * flutter) * bend;
    return mul((float3x3)unity_WorldToObject, float3(offset.x, 0, offset.y));
}
