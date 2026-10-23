Shader "Recovery/Rim3D/OutlineHull"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opaque ("Solid", Float) = 1
        _WeaponVolume ("Weapon volume", Float) = 0
        _SurfaceTile ("Tiled surface", Float) = 0
        _WallSurface ("Wall surface", Float) = 0
        _SurfaceRepeat ("Repeating surface", Float) = 0
        _SurfaceScale ("Surface scale", Vector) = (1,1,0,0)
        _GroundStencil ("Stencil reference", Int) = 0
        _StencilReadMask ("Stencil read mask", Int) = 255
        _StencilComp ("Stencil comparison", Int) = 8
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "DisableBatching"="True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha
        Stencil
        {
            Ref [_GroundStencil]
            ReadMask [_StencilReadMask]
            WriteMask 0
            Comp [_StencilComp]
            Pass Keep
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma target 3.0
            #include "OutlineSurface.cginc"
            ENDCG
        }
    }
}
