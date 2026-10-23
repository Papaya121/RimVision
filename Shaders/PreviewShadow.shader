Shader "Recovery/Rim3D/PreviewShadow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opaque ("Solid", Float) = 1
        _Billboard ("Plant", Float) = 0
        _ViewRight ("Card direction", Vector) = (1,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "DisableBatching"="True" }
        Cull Off ZWrite On
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST, _Color, _ViewRight;
            float _Opaque, _Billboard;
            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float3 center : TEXCOORD1;
            };
            struct v2f { V2F_SHADOW_CASTER; float2 uv : TEXCOORD1; };
            v2f vert(appdata v)
            {
                if (_Billboard > .5)
                {
                    float3 offset = v.vertex.xyz - v.center;
                    v.vertex.xyz = v.center + _ViewRight.xyz * offset.x + float3(0, offset.y, 0);
                    v.normal = cross(float3(0,1,0), _ViewRight.xyz);
                }
                v2f o;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                clip(lerp(tex2D(_MainTex,i.uv).a * _Color.a, 1, _Opaque) - .25);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }
}
