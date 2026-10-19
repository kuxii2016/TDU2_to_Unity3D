// Transparente Objekte: Glas/Fenster (COLOR = Lightmap, DETAIL = Glas mit Alpha), Schilder/Markierungen (Decals),
// Lichthalos (additiv). Einfache Beleuchtung (Sonne + Ambient), Lightmap-Materialien bleiben unbeleuchtet.
Shader "TDU2/ObjectTransparent"
{
    Properties
    {
        _MainTex ("Color (COLOR-Layer, UV0)", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _DetailTex ("Detail (DETAIL-Layer)", 2D) = "white" {}
        _DetailMode ("Detail aktiv", Float) = 0
        _DetailUV ("Detail UV (0 = UV-Satz 2, 1 = letzter UV-Satz)", Float) = 0
        _DetailBoost ("Detail Boost", Float) = 2
        _AlphaSource ("Alpha (0 = COLOR, 1 = DETAIL)", Float) = 0
        _Lit ("Beleuchtung (0 = aus/Lightmap, 1 = an)", Range(0,1)) = 1
        _Intensity ("Intensität", Float) = 1
        _NightOnly ("Nur nachts leuchten (Straßenlampen, Neon) – Wetter/Tageszeit", Float) = 0
        _VcMul ("Vertexfarbe multiplizieren (TD_Gouraud/TD_NEONDECAL: Farbe = Textur x Vertexfarbe)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull [_Cull]
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma target 3.0
            #include "TDU2InteriorClip.cginc"
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Color;
            sampler2D _DetailTex; float4 _DetailTex_ST;
            half _DetailMode, _DetailUV, _DetailBoost, _AlphaSource, _Lit, _Intensity, _TDU2NoClip, _NightOnly, _VcMul;
            float _TDU2DayDim;   // global (TDU2Weather): 1 = Tag -> Nachtleuchten aus
            struct appdata
            {
                float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR;
                float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0;      // xy = COLOR, zw = DETAIL
                fixed4 color : COLOR;
                half3 light : TEXCOORD1;
                float3 wpos : TEXCOORD3;
                UNITY_FOG_COORDS(2)
            };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv.xy = TRANSFORM_TEX(v.uv0, _MainTex);
                o.uv.zw = TRANSFORM_TEX(_DetailUV > 0.5 ? v.uv2 : v.uv1, _DetailTex);
                o.color = v.color;
                half3 n = UnityObjectToWorldNormal(v.normal);
                half ndl = abs(dot(n, _WorldSpaceLightPos0.xyz)); // zweiseitig
                half3 lit = ShadeSH9(half4(n, 1)) + _LightColor0.rgb * ndl;
                o.light = lerp(half3(1,1,1), lit, _Lit);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TDU2InteriorClipObject(i.wpos, _TDU2NoClip);
                fixed4 c = tex2D(_MainTex, i.uv.xy) * _Color;
                if (_DetailMode > 0.5)
                {
                    fixed4 d = tex2D(_DetailTex, i.uv.zw);
                    c.rgb = saturate(c.rgb * d.rgb * _DetailBoost);
                    c.a = lerp(c.a, d.a * _Color.a, _AlphaSource);
                }
                c *= lerp(fixed4(1,1,1,1), i.color, _VcMul);
                c.rgb *= i.light * _Intensity;
                half nightK = 1.0 - _TDU2DayDim * _NightOnly;
                c *= nightK;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
