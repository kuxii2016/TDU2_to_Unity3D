// Nachbau des TDU2-Checkpoint-Shaders "td_checkpoint" (D3D9 vs_3_0/ps_3_0, siehe Docs/tdu2-specs.md §6).
// Original-Konstanten: __World_View_Proj (VS-Transform), __UVA_0 (UV-Animation), __Instance_Color (Tint),
// __Alpha_Ref (Alphatest); Pixel-Sampler: gBaseTexSampler (Basistextur, s0). Kein Normal-/Gloss-Layer.
// Blend/ZWrite/Cull kommen im Original aus dem Render-State der .2DM (hier als Material-Eigenschaften).
Shader "TDU2/Checkpoint"
{
    Properties
    {
        _MainTex ("Color", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Tint ("Grundfarbe (Originalmaterial)", Color) = (1,1,1,1)
        _Cutoff ("Alpha Ref", Range(0,1)) = 0
        _MainTex_ST ("UV Tiling/Offset", Vector) = (1,1,0,0)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1
        [Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 0
        [Enum(Off,0,Front,1,Back,2)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull [_Cull]
        ZWrite [_ZWrite]
        Blend [_SrcBlend] [_DstBlend]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color, _Tint;
            half _Cutoff;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);   // __UVA_0 (Scroll/Puls) fließt über _MainTex_ST
                o.color = v.color * _Color * _Tint;     // __Instance_Color x Vertexfarbe x Materialfarbe
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - _Cutoff);                     // __Alpha_Ref
                return c;
            }
            ENDCG
        }
    }
    FallBack "Unlit/Transparent"
}
