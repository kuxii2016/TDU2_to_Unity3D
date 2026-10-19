// Fahrzeugglas / Scheinwerfergläser: transparent mit Spiegelung (premultiplied, Reflexion bleibt sichtbar).
Shader "TDU2/CarGlass"
{
    Properties
    {
        _Color ("Farbe (A = Deckkraft)", Color) = (0.1,0.1,0.1,0.35)
        _MainTex ("Textur (DETAIL)", 2D) = "white" {}
        _Glossiness ("Glätte", Range(0,1)) = 0.95
        _MaxAlpha ("Maximale Deckkraft (gleichmäßige Tönung)", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 300
        Cull [_Cull]
        ZWrite Off
        CGPROGRAM
        #pragma surface surf Standard alpha:premul
        #pragma target 3.0
        sampler2D _MainTex;
        fixed4 _Color;
        half _Glossiness, _MaxAlpha;
        struct Input { float2 uv_MainTex; };
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = 0;
            o.Smoothness = _Glossiness;
            // Manche Scheibentexturen tragen im Alpha keine Deckkraft, sondern Schmutz/Tönungsverläufe (fast 1) ->
            // Deckkraft begrenzen, sonst sind Frontscheiben schwarz
            o.Alpha = min(c.a, _MaxAlpha);
        }
        ENDCG
    }
    FallBack "Transparent/Diffuse"
}
