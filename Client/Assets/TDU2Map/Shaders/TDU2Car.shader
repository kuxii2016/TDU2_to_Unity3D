// Fahrzeug-Materialien (Lack, Kunststoff, Chrom, Interieur): Standard-PBR (Reflexionen über Reflection Probes/Skybox).
Shader "TDU2/Car"
{
    Properties
    {
        _Color ("Farbe", Color) = (1,1,1,1)
        _Color2 ("Zweite Farbe (Bicolor-Lack, Farbwechsel nach Blickwinkel)", Color) = (1,1,1,1)
        _Bicolour ("Bicolor-Lack", Range(0,1)) = 0
        _MainTex ("Textur (DETAIL)", 2D) = "white" {}
        _MainGray ("Textur nur Rotkanal als Helligkeit (gepackte AO-Maske, z.B. Reifenprofil lr_ambocc)", Float) = 0
        _Heat ("Hitze (Bremsscheibe glüht, 0..1)", Range(0,1)) = 0
        _HeatColor ("Glühfarbe", Color) = (1,0.25,0.04,1)
        _Glossiness ("Glätte", Range(0,1)) = 0.5
        _Metallic ("Metallisch", Range(0,1)) = 0
        _Cutoff ("Alpha Cutoff (0 = aus)", Range(0,1)) = 0
        _EmissionTex ("Leuchttextur (TEXLIGHT, z.B. lgt_on)", 2D) = "black" {}
        _Emission ("Leuchtstärke", Float) = 0
        _EmissionTint ("Leuchtfarbe", Color) = (1,1,1,1)
        _EmissionDiff ("Nur leuchtende Teile (an - aus)", Float) = 0
        _LightMask ("Lichtfunktionen (lgt_g: R Bremse, G Rückfahr, B Licht, A=0 Blinker)", 2D) = "white" {}
        _UseMask ("Maske benutzen", Float) = 0
        _LgtLights ("Licht an (Scheinwerfer/Rücklicht)", Float) = 0
        _LgtBrake ("Bremse", Float) = 0
        _LgtReverse ("Rückfahrlicht", Float) = 0
        _LgtBlink ("Blinker", Float) = 0
        _SelfIllum ("Grundhelligkeit (Interieur)", Range(0,1)) = 0
        _Occlusion ("Umgebungsverdeckung (Interieur < 1)", Range(0,1)) = 1
        _ClearCoat ("Klarlack (Reflexion + Glanzpunkt)", Range(0,2)) = 0
        _ClearCoatPower ("Glanzpunkt-Schärfe", Float) = 600
        _Flake ("Metallic-Flakes (Glitzern + Farbverlauf)", Range(0,1)) = 0
        _FlakeScale ("Flake-Dichte (UV1)", Float) = 3000
        _StickerTex ("Werks-Beklebung (Stickermap)", 2D) = "black" {}
        _UseSticker ("Beklebung benutzen", Float) = 0
        _StickerUV ("Beklebung UV-Satz (0/1)", Float) = 0
        _StickerAlphaOnly ("Beklebung nur Dekor (Alpha)", Float) = 0
        _DirtTex ("Schmutzmaske (R Schmutz, G Kratzer)", 2D) = "black" {}
        _DirtLevel ("Schmutzstufe", Range(0,1)) = 0
        _DirtColor ("Schmutzfarbe", Color) = (0.42,0.35,0.26,1)
        _ScratchLevel ("Kratzer", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300
        Cull [_Cull]
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0
        sampler2D _MainTex, _EmissionTex, _LightMask, _StickerTex;
        sampler2D _DirtTex;
        half _UseSticker, _StickerUV, _StickerAlphaOnly, _DirtLevel, _ScratchLevel;
        fixed4 _DirtColor;
        fixed4 _Color, _Color2, _EmissionTint;
        half _Bicolour;
        half _Emission, _EmissionDiff, _SelfIllum, _ClearCoat, _ClearCoatPower, _UseMask, _LgtLights, _LgtBrake, _LgtReverse, _LgtBlink;
        half _Glossiness, _Metallic, _Cutoff, _Occlusion, _Flake, _MainGray, _Heat;
        fixed4 _HeatColor;
        float _FlakeScale;
        float FlakeHash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
        struct Input { float2 uv_MainTex; float2 uv2_StickerTex; float3 worldNormal; float3 worldPos; float3 worldRefl; };
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 baseCol = _Color;
            if (_Bicolour > 0.001)
            {
                // Bicolor-Lack (Sticker-Shop "Peinture bicolore"): Farbe 1 frontal, Farbe 2 zum flachen Blickwinkel hin
                float ndvB = saturate(dot(normalize(IN.worldNormal), normalize(_WorldSpaceCameraPos - IN.worldPos)));
                baseCol.rgb = lerp(_Color.rgb, _Color2.rgb, smoothstep(0.15, 0.95, 1.0 - ndvB) * _Bicolour);
            }
            fixed4 tx = tex2D(_MainTex, IN.uv_MainTex);
            if (_MainGray > 0.5) tx.rgb = tx.rrr;   // R = scharfe Verschattung (G/B dieselbe weicher) – keine Farbe
            fixed4 c = tx * baseCol;
            clip(c.a - _Cutoff);
            if (_UseSticker > 0.5)
            {
                // Werks-Beklebung: RGB enthält auch die Grundfarbe der Lackierung (z.B. Grün der Classic-Schule, Alpha 0 = Lack),
                // daher ersetzt die Stickermap den Lack vollständig; _StickerAlphaOnly = 1 -> nur Dekor (Alpha) über die Lackfarbe
                half4 st = tex2D(_StickerTex, _StickerUV > 0.5 ? IN.uv2_StickerTex : IN.uv_MainTex);
                c.rgb = lerp(st.rgb, lerp(c.rgb, st.rgb, st.a), _StickerAlphaOnly);
            }
            half dirtK = 0;
            if (_DirtLevel > 0.001 || _ScratchLevel > 0.001)
            {
                // Schmutzmaske: R wächst von unten/außen nach oben/innen -> Stufe = Schwelle (wie DIRT ALPHA im Spiel)
                half2 dm = tex2D(_DirtTex, _StickerUV > 0.5 ? IN.uv2_StickerTex : IN.uv_MainTex).rg;
                dirtK = saturate((dm.r - (1.0 - _DirtLevel)) * 4.0) * _DirtColor.a;
                c.rgb = lerp(c.rgb, _DirtColor.rgb, dirtK);
                half scr = saturate((dm.g - (1.0 - _ScratchLevel)) * 4.0);
                c.rgb = lerp(c.rgb, c.rgb * 0.55 + 0.25, scr);
            }
            o.Albedo = c.rgb;
            o.Metallic = _Metallic * (1 - dirtK);
            o.Smoothness = _Glossiness * (1 - dirtK * 0.85);
            o.Occlusion = _Occlusion;   // Himmelslicht/Reflexionen im Innenraum abschwächen (keine Ambient Occlusion im Original-Licht)
            o.Emission = c.rgb * _SelfIllum;
            if (_UseMask > 0.5)
            {
                // wie im Spiel: lgt_g ordnet jedem Texel seine Lichtfunktion zu, lgt_on liefert die Leuchtfarbe
                half4 msk = tex2D(_LightMask, IN.uv_MainTex);
                half k = saturate(msk.r * _LgtBrake + msk.b * _LgtLights + msk.g * _LgtReverse + (1.0 - msk.a) * _LgtBlink);
                fixed3 onc = tex2D(_EmissionTex, IN.uv_MainTex).rgb;
                o.Emission += onc * k * _Emission;
                o.Albedo = lerp(c.rgb, onc, saturate(k));
            }
            else if (_Emission > 0.001)
            {
                // lgt_on enthält das ganze Glas (auch Chrom/Reflektor) -> nur der Unterschied zu lgt_off leuchtet
                fixed3 on = tex2D(_EmissionTex, IN.uv_MainTex).rgb;
                // Maske = Helligkeitsgewinn an -> leuchtende Stellen; Farbe = Originalfarbe der lgt_on-Textur
                // (die reine Differenz an - aus ergibt bei orange/rot gemischten Gläsern Grünstiche)
                half gain = saturate(dot(saturate(on - c.rgb * 0.9), half3(0.3, 0.59, 0.11)) * 3.0);
                fixed3 glow = _EmissionDiff > 0.5 ? on * gain * 1.5 : on;
                o.Emission += glow * _EmissionTint.rgb * _Emission;
            }
            if (_ClearCoat * (1 - dirtK) > 0.001)
            {
                // Klarlack wie im Spiel: Umgebungsreflexion (Reflection Probe) mit Fresnel + scharfer Sonnen-Glanzpunkt
                float3 n = normalize(IN.worldNormal);
                float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
                float ndv = saturate(dot(n, v));
                float fres = 0.05 + 0.95 * pow(1.0 - ndv, 5.0);
                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, IN.worldRefl, 0.5);
                half3 env = DecodeHDR(envRaw, unity_SpecCube0_HDR);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 h = normalize(l + v);
                float spec = pow(saturate(dot(n, h)), _ClearCoatPower) * saturate(dot(n, l)) * 4.0;
                o.Emission += (env * fres + _LightColor0.rgb * spec) * _ClearCoat * (1 - dirtK);
            }
            if (_Flake > 0.001 && dirtK < 0.99)
            {
                // Metallic-Lack: winkelabhängiger Farbverlauf (dunkler zum Rand hin) + feine Flakes, die das Licht einzeln einfangen
                float3 n = normalize(IN.worldNormal);
                float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
                float ndv = saturate(dot(n, v));
                o.Albedo *= lerp(1.0, 0.78 + 0.22 * ndv, _Flake);
                float2 cell = floor(IN.uv2_StickerTex * _FlakeScale);
                float h1 = FlakeHash(cell), h2 = FlakeHash(cell + 17.31), h3 = FlakeHash(cell + 41.7);
                float3 fn = normalize(n + (float3(h1, h2, h3) - 0.5) * 0.3);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 hv = normalize(l + v);
                float spark = pow(saturate(dot(fn, hv)), 260.0) * step(0.82, h3) * saturate(dot(n, l));
                half4 envF = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflect(-v, fn), 2.0);
                o.Emission += (_LightColor0.rgb * spark * 0.7 + DecodeHDR(envF, unity_SpecCube0_HDR) * step(0.93, h1) * 0.04)
                              * (o.Albedo * 0.6 + 0.4) * _Flake * (1 - dirtK);
            }
            if (_Heat > 0.001)
            {
                // glühende Bremsscheibe: erst dunkelrot, mit mehr Hitze heller/orange (nach Vollbremsungen, kühlt langsam ab)
                half h = saturate(_Heat);
                o.Emission += _HeatColor.rgb * lerp(half3(0.6, 0.12, 0.02), half3(1.6, 0.7, 0.2), h * h) * h * 2.0;
            }
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Standard"
}
