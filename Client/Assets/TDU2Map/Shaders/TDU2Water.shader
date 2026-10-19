// Einfache Meeresoberfläche (die Ozean-Meshes aus *-o.3dg werden durch diese Ebene ersetzt).
Shader "TDU2/Water"
{
    Properties
    {
        _Color ("Color", Color) = (0.05,0.25,0.35,0.025)
        _Glossiness ("Smoothness", Range(0,1)) = 0.25
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" }
        ZWrite Off
        Offset 2, 2   // hinter flachem Strand-Terrain zeichnen (kein Z-Fighting)
        CGPROGRAM
        #pragma surface surf Standard alpha:fade
        #pragma target 3.0
        #include "TDU2InteriorClip.cginc"
        fixed4 _Color; half _Glossiness, _TDU2NoClip;
        struct Input { float3 worldPos; };
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            TDU2InteriorClip(IN.worldPos, _TDU2NoClip);   // Interieur unter dem Meeresspiegel (Yacht-Garage)
            o.Albedo = _Color.rgb;
            o.Smoothness = _Glossiness;
            o.Metallic = 0;
            o.Alpha = _Color.a;
        }
        ENDCG
    }
}
