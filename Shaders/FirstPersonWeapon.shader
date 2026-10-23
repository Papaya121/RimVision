Shader "Recovery/Rim3D/FirstPersonWeapon"
{
    Properties
    {
        _MainTex ("Weapon sprite", 2D) = "white" {}
        _MaskTex ("Recolor mask", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ColorTwo ("Second tint", Color) = (1,1,1,1)
        _HandShading ("Hand shading", Float) = 0
        _Flash ("Muzzle flash", Float) = 0
        _UseMask ("Use recolor mask", Float) = 0
        _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="TransparentCutout" }
        Pass
        {
            Cull [_Cull]
            ZTest LEqual
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _MaskTex;
            float4 _MainTex_ST, _MainTex_TexelSize, _Color, _ColorTwo;
            float _UseMask, _Flash, _HandShading;
            float4x4 _Rim3DFirstPersonProjection;
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct Output { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; };
            Output vert(Input v)
            {
                Output o;
                float4 view = mul(unity_ObjectToWorld, v.vertex);
                view.z = -view.z;
                o.vertex = mul(_Rim3DFirstPersonProjection, view);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }
            fixed4 frag(Output i) : SV_Target
            {
                float2 halfTexel = abs(_MainTex_TexelSize.xy) * .5;
                float2 uvMin = min(_MainTex_ST.zw, _MainTex_ST.zw + _MainTex_ST.xy) + halfTexel;
                float2 uvMax = max(_MainTex_ST.zw, _MainTex_ST.zw + _MainTex_ST.xy) - halfTexel;
                float2 uv = clamp(i.uv, uvMin, uvMax);
                fixed4 sprite = tex2D(_MainTex, uv);
                if (_Flash > .5)
                {
                    clip(sprite.a - .001);
                    return fixed4(sprite.rgb * _Color.rgb, saturate(sprite.a * 8) * _Color.a);
                }
                clip(sprite.a - .1);
                fixed3 tint = _Color.rgb;
                if (_UseMask > .5)
                {
                    fixed3 mask = tex2D(_MaskTex, uv).rgb;
                    tint = lerp(fixed3(1,1,1), _Color.rgb, mask.r);
                    tint = lerp(tint, _ColorTwo.rgb, mask.g);
                }
                if (_HandShading > .5)
                    tint *= .55 + .45 * saturate(dot(normalize(i.normal), normalize(float3(-.4, .7, -.6))));
                return fixed4(sprite.rgb * tint, 1);
            }
            ENDCG
        }
    }
}

