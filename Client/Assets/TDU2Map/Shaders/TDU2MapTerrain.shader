// Große Karte: Gelände-Kacheln (Area-Colormaps) unbeleuchtet wie die Original-Karte – unabhängig von Tageszeit, Wetter,
// Lichtern und Spiegelung der Welt (Cull Off), mit leichter Relief-Schattierung aus den Normalen.
Shader "TDU2/MapTerrain"
{
    Properties
    {
        _MainTex ("Colormap", 2D) = "white" {}
        _Brightness ("Helligkeit", Float) = 1
        _Relief ("Relief-Schattierung", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Brightness, _Relief;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 n : TEXCOORD1; };
            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.n = v.normal;   // Spielkoordinaten der Kachel (Y oben)
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                float3 n = normalize(i.n);
                float shade = saturate(dot(n, normalize(float3(-0.45, 0.8, 0.4))));
                c *= lerp(1.0, 0.55 + 0.6 * shade, _Relief) * _Brightness;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
