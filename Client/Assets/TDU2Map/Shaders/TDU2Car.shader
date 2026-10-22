
Shader "TDU2/Car"
{
    Properties
    {
        _Color ("Farbe", Color) = (1,1,1,1)
        _Color2 ("Zweite Farbe (Bicolor-Lack)", Color) = (1,1,1,1)
        _Bicolour ("Bicolor-Lack", Range(0,1)) = 0
        _MainTex ("Textur (DETAIL)", 2D) = "white" {}
        _MainGray ("Textur nur Rotkanal als Helligkeit", Float) = 0

        _Heat ("Hitze", Range(0,1)) = 0
        _HeatColor ("Glühfarbe", Color) = (1,0.25,0.04,1)

        _Glossiness ("Glätte", Range(0,1)) = 0.5
        _Metallic ("Metallisch", Range(0,1)) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0

        _EmissionTex ("Leuchttextur (TEXLIGHT)", 2D) = "black" {}
        _Emission ("Leuchtstärke", Float) = 0
        _EmissionTint ("Leuchtfarbe", Color) = (1,1,1,1)
        _EmissionDiff ("Nur leuchtende Teile", Float) = 0

        _LightMask ("Lichtfunktionen (lgt_g)", 2D) = "white" {}
        _UseMask ("Maske benutzen", Float) = 0

        _LgtLights ("Licht (Scheinwerfer/Rücklicht)", Range(0,1)) = 0
        _LgtBrake ("Bremslicht", Range(0,1)) = 0
        _LgtReverse ("Rückfahrlicht", Range(0,1)) = 0
        _LgtBlink ("Blinker", Range(0,1)) = 0

        _TailLightColor ("Rücklichtfarbe", Color) = (1,0.015,0.01,1)
        _BrakeLightColor ("Bremslichtfarbe", Color) = (1,0.01,0.005,1)
        _ReverseLightColor ("Rückfahrlichtfarbe", Color) = (1,1,1,1)
        _BlinkLightColor ("Blinkerfarbe", Color) = (1,0.25,0.01,1)

        _TailLightIntensity ("Rücklichtstärke", Range(0,10)) = 1
        _BrakeLightIntensity ("Bremslichtstärke", Range(0,10)) = 2
        _ReverseLightIntensity ("Rückfahrlichtstärke", Range(0,10)) = 2
        _BlinkLightIntensity ("Blinkerstärke", Range(0,10)) = 2

        _SelfIllum ("Grundhelligkeit (Interieur)", Range(0,1)) = 0
        _Occlusion ("Umgebungsverdeckung", Range(0,1)) = 1

        _ClearCoat ("Klarlack", Range(0,2)) = 0
        _ClearCoatPower ("Glanzpunkt-Schärfe", Float) = 600

        _Flake ("Metallic-Flakes", Range(0,1)) = 0
        _FlakeScale ("Flake-Dichte (UV1)", Float) = 3000

        _StickerTex ("Werks-Beklebung", 2D) = "black" {}
        _UseSticker ("Beklebung benutzen", Float) = 0
        _StickerUV ("Beklebung UV-Satz (0/1)", Float) = 0
        _StickerAlphaOnly ("Beklebung nur Dekor", Float) = 0

        _DirtTex ("Schmutzmaske", 2D) = "black" {}
        _DirtLevel ("Schmutzstufe", Range(0,1)) = 0
        _DirtColor ("Schmutzfarbe", Color) = (0.42,0.35,0.26,1)
        _ScratchLevel ("Kratzer", Range(0,1)) = 0

        [Enum(UnityEngine.Rendering.CullMode)]
        _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        LOD 300
        Cull [_Cull]

        CGPROGRAM

        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0

        #include "UnityCG.cginc"

        sampler2D _MainTex;
        sampler2D _EmissionTex;
        sampler2D _LightMask;
        sampler2D _StickerTex;
        sampler2D _DirtTex;

        fixed4 _Color;
        fixed4 _Color2;
        fixed4 _EmissionTint;
        fixed4 _HeatColor;
        fixed4 _DirtColor;

        fixed4 _TailLightColor;
        fixed4 _BrakeLightColor;
        fixed4 _ReverseLightColor;
        fixed4 _BlinkLightColor;

        half _Bicolour;
        half _MainGray;
        half _Heat;
        half _Glossiness;
        half _Metallic;
        half _Cutoff;
        half _Emission;
        half _EmissionDiff;
        half _SelfIllum;
        half _Occlusion;

        half _ClearCoat;
        half _ClearCoatPower;
        half _Flake;
        float _FlakeScale;

        half _UseMask;
        half _LgtLights;
        half _LgtBrake;
        half _LgtReverse;
        half _LgtBlink;

        half _TailLightIntensity;
        half _BrakeLightIntensity;
        half _ReverseLightIntensity;
        half _BlinkLightIntensity;

        half _UseSticker;
        half _StickerUV;
        half _StickerAlphaOnly;
        half _DirtLevel;
        half _ScratchLevel;

        float FlakeHash(float2 p)
        {
            return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }

        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_StickerTex;
            float3 worldNormal;
            float3 worldPos;
            float3 worldRefl;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 baseCol = _Color;

            if (_Bicolour > 0.001)
            {
                float ndvB = saturate(dot(
                    normalize(IN.worldNormal),
                    normalize(_WorldSpaceCameraPos - IN.worldPos)
                ));

                baseCol.rgb = lerp(
                    _Color.rgb,
                    _Color2.rgb,
                    smoothstep(0.15, 0.95, 1.0 - ndvB) * _Bicolour
                );
            }

            fixed4 tx = tex2D(_MainTex, IN.uv_MainTex);

            if (_MainGray > 0.5)
                tx.rgb = tx.rrr;

            fixed4 c = tx * baseCol;

            clip(c.a - _Cutoff);

            if (_UseSticker > 0.5)
            {
                half4 st = tex2D(
                    _StickerTex,
                    _StickerUV > 0.5
                        ? IN.uv2_StickerTex
                        : IN.uv_MainTex
                );

                c.rgb = lerp(
                    st.rgb,
                    lerp(c.rgb, st.rgb, st.a),
                    _StickerAlphaOnly
                );
            }

            half dirtK = 0;

            if (_DirtLevel > 0.001 || _ScratchLevel > 0.001)
            {
                half2 dm = tex2D(
                    _DirtTex,
                    _StickerUV > 0.5
                        ? IN.uv2_StickerTex
                        : IN.uv_MainTex
                ).rg;

                dirtK = saturate(
                    (dm.r - (1.0 - _DirtLevel)) * 4.0
                ) * _DirtColor.a;

                c.rgb = lerp(c.rgb, _DirtColor.rgb, dirtK);

                half scr = saturate(
                    (dm.g - (1.0 - _ScratchLevel)) * 4.0
                );

                c.rgb = lerp(c.rgb, c.rgb * 0.55 + 0.25, scr);
            }

            o.Albedo = c.rgb;
            o.Metallic = _Metallic * (1 - dirtK);
            o.Smoothness = _Glossiness * (1 - dirtK * 0.85);
            o.Occlusion = _Occlusion;
            o.Emission = c.rgb * _SelfIllum;

            // Fahrzeuglichter nach Original-Formel (Spec 6.x):
            //   TEXLIGHT * dot((GLOSSLIGHT.rgb, 1-a), GlossFct)
            // Die TEXLIGHT-Textur liefert die Lampenfarbe, die Maske gewichtet die
            // aktive Funktion (R=Bremse, G=Rückfahr, B=Licht, A=0 -> Blinker).
            if (_UseMask > 0.5)
            {
                half4 msk = tex2D(_LightMask, IN.uv_MainTex);

                // Laufzeit-Gewichte je Funktion.
                half fBrake = saturate(_LgtBrake);
                half fReverse = saturate(_LgtReverse);
                half fLight = saturate(_LgtLights);
                half fBlink = saturate(_LgtBlink);

                // Gewichtete Funktionssumme (dot mit (mask.rgb, 1-a)).
                // Die Maske ist praktisch binär; harte Schwellen verhindern, dass
                // benachbarte Funktionen durch Filterung/Kompression mitleuchten.
                half maskW =
                    step(0.5, msk.r) * fBrake +
                    step(0.5, msk.g) * fReverse +
                    step(0.5, msk.b) * fLight +
                    step(0.5, 1.0 - msk.a) * fBlink;

                // Lampenfarbe aus der Leuchttextur (bereits korrekt eingefärbt),
                // mit der Funktionsintensität skaliert.
                fixed3 lampColor = tex2D(_EmissionTex, IN.uv_MainTex).rgb;

                half intensity =
                    fBrake * _BrakeLightIntensity +
                    fReverse * _ReverseLightIntensity +
                    fLight * _TailLightIntensity +
                    fBlink * _BlinkLightIntensity;

                o.Emission += lampColor * maskW * max(intensity, 1.0);
            }
            else if (_Emission > 0.001)
            {
                fixed3 on = tex2D(
                    _EmissionTex,
                    IN.uv_MainTex
                ).rgb;

                half gain = saturate(
                    dot(
                        saturate(on - c.rgb * 0.9),
                        half3(0.3, 0.59, 0.11)
                    ) * 3.0
                );

                fixed3 glow = _EmissionDiff > 0.5
                    ? on * gain * 1.5
                    : on;

                o.Emission += glow * _EmissionTint.rgb * _Emission;
            }

            if (_ClearCoat * (1 - dirtK) > 0.001)
            {
                float3 n = normalize(IN.worldNormal);
                float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
                float ndv = saturate(dot(n, v));

                float fres = 0.05 + 0.95 * pow(1.0 - ndv, 5.0);

                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(
                    unity_SpecCube0,
                    IN.worldRefl,
                    0.5
                );

                half3 env = DecodeHDR(envRaw, unity_SpecCube0_HDR);

                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 h = normalize(l + v);

                float spec = pow(
                    saturate(dot(n, h)),
                    _ClearCoatPower
                ) * saturate(dot(n, l)) * 4.0;

                o.Emission += (
                    env * fres + _LightColor0.rgb * spec
                ) * _ClearCoat * (1 - dirtK);
            }

            if (_Flake > 0.001 && dirtK < 0.99)
            {
                float3 n = normalize(IN.worldNormal);
                float3 v = normalize(_WorldSpaceCameraPos - IN.worldPos);
                float ndv = saturate(dot(n, v));

                o.Albedo *= lerp(
                    1.0,
                    0.78 + 0.22 * ndv,
                    _Flake
                );

                float2 cell = floor(IN.uv2_StickerTex * _FlakeScale);

                float h1 = FlakeHash(cell);
                float h2 = FlakeHash(cell + 17.31);
                float h3 = FlakeHash(cell + 41.7);

                float3 fn = normalize(
                    n + (float3(h1, h2, h3) - 0.5) * 0.3
                );

                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 hv = normalize(l + v);

                float spark = pow(
                    saturate(dot(fn, hv)),
                    260.0
                ) * step(0.82, h3) * saturate(dot(n, l));

                half4 envF = UNITY_SAMPLE_TEXCUBE_LOD(
                    unity_SpecCube0,
                    reflect(-v, fn),
                    2.0
                );

                o.Emission += (
                    _LightColor0.rgb * spark * 0.7 +
                    DecodeHDR(envF, unity_SpecCube0_HDR)
                    * step(0.93, h1) * 0.04
                ) * (o.Albedo * 0.6 + 0.4)
                  * _Flake * (1 - dirtK);
            }

            if (_Heat > 0.001)
            {
                half h = saturate(_Heat);

                o.Emission += _HeatColor.rgb *
                    lerp(
                        half3(0.6, 0.12, 0.02),
                        half3(1.6, 0.7, 0.2),
                        h * h
                    ) * h * 2.0;
            }

            o.Alpha = 1;
        }

        ENDCG
    }

    FallBack "Standard"
}
