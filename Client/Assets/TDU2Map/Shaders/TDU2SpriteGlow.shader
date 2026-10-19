// Leucht-Sprites (Halos von Straßen-/Tunnellampen): Billboard wie TDU2/Sprite, aber unbeleuchtet und additiv.
Shader "TDU2/SpriteGlow"
{
    Properties
    {
        _MainTex ("Color", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Intensity ("Intensität", Float) = 1
        _NightOnly ("Nur nachts leuchten (Straßenlampen, Neon) – Wetter/Tageszeit", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "IgnoreProjector"="True" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "TDU2InteriorClip.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color; half _Intensity, _TDU2NoClip, _NightOnly;
            float _TDU2DayDim;   // global (TDU2Weather): 1 = Tag -> Nachtleuchten aus
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 off : TEXCOORD1; float4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_FOG_COORDS(1) float3 wpos : TEXCOORD2; };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v);
                // Eckoffset in View-Space (Kamera rechts/oben) -> Objektraum
                float3 right = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[0].xyz));
                float3 up    = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[1].xyz));
                v.vertex.xyz += right * v.off.x + up * v.off.y;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TDU2InteriorClip(i.wpos, _TDU2NoClip);
                fixed4 c = tex2D(_MainTex, i.uv) * _Color;
                c.rgb *= _Intensity;
                c *= 1.0 - _TDU2DayDim * _NightOnly;
                UNITY_APPLY_FOG_COLOR(i.fogCoord, c, fixed4(0,0,0,0));
                return c;
            }
            ENDCG
        }
    }
}
