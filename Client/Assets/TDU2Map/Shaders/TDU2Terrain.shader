// Terrain: Colormap (<kachel>-c.2db) über die ganze Kachel.
// Splatmap (<sektor>-s.2db): R = gebackener Schatten, G = Straßenlicht (Nacht), B = Mischung DETAIL1/DETAIL2.
Shader "TDU2/Terrain"
{
    Properties
    {
        _MainTex ("Colormap", 2D) = "white" {}
        _SplatTex ("Splat/Shadow Map", 2D) = "white" {}
        _UseSplat ("Use Splat", Float) = 0
        _ShadowStrength ("Baked Shadow Strength", Range(0,1)) = 0.75
        _Detail1 ("Detail 1 (R)", 2D) = "gray" {}
        _Detail2 ("Detail 2 (R)", 2D) = "gray" {}
        _DetailTiling ("Detail Tiling (pro Meter)", Float) = 0.098
        _DetailStrength ("Detail Strength", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows
        #pragma target 3.0
        #include "TDU2InteriorClip.cginc"
        sampler2D _MainTex, _SplatTex, _Detail1, _Detail2;
        half _UseSplat, _ShadowStrength, _DetailTiling, _DetailStrength;
        float _TDU2Wet, _TDU2StreetLight;   // global (TDU2Weather): rain_ground_wet, outside_street_lightmap
        struct Input { float2 uv_MainTex; float3 worldPos; };
        void surf (Input IN, inout SurfaceOutput o)
        {
            TDU2InteriorClip(IN.worldPos, 0);
            fixed3 col = tex2D(_MainTex, IN.uv_MainTex).rgb;
            fixed4 splat = _UseSplat > 0.5 ? tex2D(_SplatTex, IN.uv_MainTex) : fixed4(1,0,0,1);
            float2 duv = IN.worldPos.xz * _DetailTiling;
            // Detailtexturen (d1_cliff_rocks, d2_earth) sind gepackt: nur R = Helligkeitsdetail
            half d1 = tex2D(_Detail1, duv).r;
            half d2 = tex2D(_Detail2, duv).r;
            half det = lerp(d2, d1, _UseSplat > 0.5 ? splat.b : 0.5) * 1.8;
            col *= lerp(1.0, det, _DetailStrength);
            col *= lerp(1.0, splat.r, _ShadowStrength * _UseSplat);
            // Regen: nasser Boden dunkler; Nacht: Straßenlicht aus der Splatmap (G) wie im Original (outside_street_lightmap)
            col *= 1.0 - 0.45 * _TDU2Wet;
            o.Albedo = col;
            o.Emission = col * splat.g * _UseSplat * _TDU2StreetLight * half3(0.97, 0.84, 0.65);
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
