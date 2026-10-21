// Objekte (Gebäude, Props, Bäume). Lambert + optionale gebackene Schattenmap (SHADOW-Layer, UV2) + Vertexfarbe.
Shader "TDU2/Object"
{
    Properties
    {
        _MainTex ("Color (COLOR-Layer)", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ShadowTex ("Baked Shadow (SHADOW-Layer, UV2)", 2D) = "white" {}
        _ShadowStrength ("Baked Shadow Strength", Range(0,1)) = 0
        _VertexColorStrength ("Vertex Color Strength (x2)", Range(0,1)) = 0.5
        _VcScale ("Vertexfarbe Faktor (2 = x2-Konvention, 1 = roh wie TD_Gouraud)", Float) = 2
        _DetailTex ("Detail (DETAIL-Layer, UV2) – Straßen", 2D) = "gray" {}
        _DetailMode ("Detail Mode (0 aus, 1 multiplizieren)", Float) = 0
        _DetailUV ("Detail UV (0 = UV-Satz 2, 1 = letzter UV-Satz)", Float) = 0
        _ColorStrength ("Color Strength", Range(0,1)) = 1
        _DetailBoost ("Detail Boost", Float) = 1.2
        _DetailGray ("Detail nur R-Kanal (gepackte d1_/d2_-Texturen)", Float) = 0
        _TintTex ("Farbpalette (DETAIL2, UV-Satz 3)", 2D) = "gray" {}
        _TintMode ("Farbpalette aktiv", Float) = 0
        _MarksTex ("Fahrbahnmarkierung (MARKS, UV-Satz 2)", 2D) = "black" {}
        _MarksMode ("Markierung aktiv", Float) = 0
        _Marks2Tex ("Zweite Markierung (MARKS2, UV-Satz 3: Pfeile, Startraster, Reifenspuren)", 2D) = "black" {}
        _Marks2Mode ("Zweite Markierung aktiv", Float) = 0
        _TintScale ("Farbpalette Skalierung (1/Mittelwert)", Float) = 2
        _Brightness ("Helligkeit", Range(0.5,2)) = 1.15
        _Wrap ("Wrap Lighting (Schattenseiten aufhellen)", Range(0,1)) = 0.35
        _Cutoff ("Alpha Cutoff (0 = opak)", Range(0,1)) = 0
        _Unlit ("Unbeleuchtet (Lightmap lm_* mit eingebackenem Licht, Interieurs)", Range(0,1)) = 0
        _NightTex ("Interieur-Lightmap (NIGHTLIGHTMAP lm_*_night, UV0)", 2D) = "black" {}
        _NightMode ("Interieur-Lightmap aktiv", Float) = 0
        _NightLmap ("nightlmap (x = Anteil Echtzeitlicht, y = Leuchten über Alpha)", Vector) = (0.44, 1, 0, 0)
        _NightLitScale ("Echtzeit-Anteil Sonne (Interieur: 0, die Wände schatten sie ab)", Float) = 0
        _NightAmbient ("Licht-Anteil 'lit' im Interieur (nightlmap.x)", Color) = (0.9,0.9,0.9,1)
        _DetailAlpha ("Alpha aus DETAIL (TD_Building, Interieur-Pflanzen)", Float) = 0
        _ReflMix ("Bodenspiegelung (1 - baseColor.w, TD_Furnitures)", Range(0,1)) = 0
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
        _PlantMode ("Vegetation (V_*): einheitliche Beleuchtung wie Gras/Boden", Float) = 0
        _WetSurface ("Wird bei Regen nass/dunkel (Straßen, Plätze)", Float) = 0
        _NightEmit ("Leuchtet nachts (Lampen TD_Lamp, Neon TD_pubneon)", Float) = 0
        _GlossTex ("GLOSS (Fenstermaske in B, UV-Satz 2)", 2D) = "black" {}
        _WinTex ("WINDOWMAP (Fensterfarben-Rauschen w_bruit)", 2D) = "black" {}
        _WinMode ("Nachtfenster (TD_Building)", Float) = 0
        _WinParams ("luminance_power (x Stärke, y Schwelle der Maske)", Vector) = (9, 0.5, 0, 0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 200
        Cull [_Cull]
        CGPROGRAM
        #pragma surface surf TDU2Wrap alphatest:_Cutoff vertex:vert addshadow fullforwardshadows nolightmap nodynlightmap nodirlightmap
        #pragma multi_compile_instancing
        #pragma target 3.5
        #include "TDU2InteriorClip.cginc"
        sampler2D _MainTex;
        sampler2D _ShadowTex;
        sampler2D _DetailTex;
        sampler2D _TintTex;
        sampler2D _MarksTex;
        sampler2D _Marks2Tex;
        half _Marks2Mode;
        float4 _MarksTex_TexelSize;
        half _TintMode, _MarksMode;
        half _DetailMode, _ColorStrength, _DetailUV, _DetailBoost, _DetailGray;
        fixed4 _Color;
        half _ShadowStrength;
        half _VertexColorStrength;
        half _TintScale, _Brightness, _Wrap, _Unlit, _NightMode, _NightLitScale, _TDU2NoClip;
        half _VcScale;
        sampler2D _NightTex;
        float4 _NightLmap;
        half _ReflMix, _DetailAlpha;
        fixed4 _NightAmbient;
        // Spiegelbild des Bodens (InteriorMirror), im Original gProjectiveMapSampler
        sampler2D _TDU2PlanarRefl;
        half _TDU2PlanarOn;
        float _TDU2PlanarY;   // Höhe der Spiegelebene (Welt) – nur Flächen auf dieser Ebene spiegeln

        // Lambert mit "Wrap": die Farbatlanten der Gebäude enthalten bereits gebackenes Licht,
        // ein reines N·L macht sonnenabgewandte Fassaden zu dunkel.
        half _PlantMode, _WetSurface, _NightEmit, _WinMode;
        float4 _WinParams;
        sampler2D _GlossTex, _WinTex;
        float _TDU2NightK, _TDU2NightWindows;   // global (TDU2Weather): Nacht 0..1, building_nightwindow_frequency
        float _TDU2Wet;
        // Vegetation: Normale = Welt-oben (wie Gras und Boden) -> Büsche/Bäume werden genauso hell beleuchtet wie der Boden,
        // egal wie die Blattkarten im Mesh liegen.
        void vert (inout appdata_full v)
        {
            if (_PlantMode > 0.5) v.normal = normalize(mul((float3x3)unity_WorldToObject, float3(0, 1, 0)));
        }
        half4 LightingTDU2Wrap (SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = dot(s.Normal, lightDir);
            half diff = saturate(ndl * (1.0 - _Wrap) + _Wrap);
            half4 c;
            c.rgb = s.Albedo * _LightColor0.rgb * (diff * atten);
            c.a = s.Alpha;
            return c;
        }
        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_DetailTex;
            float2 uv3_DetailTex;   // TEXCOORD2 = letzter UV-Satz (Straßen)
            float2 uv4_Marks2Tex;   // TEXCOORD3 = 3. UV-Satz bei Straßen mit 4 Sätzen (MARKS2)
            float4 color : COLOR;
            float3 worldPos;
        };
        void surf (Input IN, inout SurfaceOutput o)
        {
            TDU2InteriorClipObject(IN.worldPos, _TDU2NoClip);
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            if (_DetailMode > 0.5)
            {
                float2 duv = _DetailUV > 0.5 ? IN.uv3_DetailTex : IN.uv2_DetailTex;
                fixed4 det4 = tex2D(_DetailTex, duv);
                fixed3 det = det4.rgb;
                // Original (TD_Building): Alpha = min(DETAIL.a, ...) -> Blätter/Pflanzen mit Alpha-Test aus der DETAIL-Textur
                if (_DetailAlpha > 0.5 && _DetailGray < 0.5) c.a = min(c.a, det4.a);
                // d1_/d2_-Detailtexturen sind gepackt (R = Helligkeit, G/B = andere Daten) -> nur R, um 0.5 zentriert
                if (_DetailGray > 0.5) det = det.rrr * 1.8;
                c.rgb = saturate(lerp(fixed3(0.5,0.5,0.5), c.rgb, _ColorStrength) * det * _DetailBoost);
            }
            // SHADOW nutzt denselben UV-Kanal wie DETAIL (TEXCOORD1) -> spart Interpolatoren (max. 10 im ForwardBase-Pass)
            if (_TintMode > 0.5) c.rgb = saturate(c.rgb * tex2D(_TintTex, IN.uv3_DetailTex).rgb * _TintScale);
            if (_MarksMode > 0.5)
            {
                // Die Markierungsmaske ist gerastert (Pixelmuster im 4x4-Raster) -> 3x3 Texel mitteln und
                // anschließend schärfen, so entstehen saubere Linien statt "Ketten".
                float2 uv = IN.uv2_DetailTex;
                float2 t = _MarksTex_TexelSize.xy * 1.5;
                fixed4 mk = 0;
                for (int ix = -1; ix <= 1; ix++)
                    for (int iy = -1; iy <= 1; iy++)
                        mk += tex2D(_MarksTex, uv + float2(ix, iy) * t);
                mk /= 9.0;
                fixed a = smoothstep(0.12, 0.38, mk.a);
                fixed3 mcol = mk.a > 0.001 ? saturate(mk.rgb / max(mk.a, 0.2)) : fixed3(1,1,1);
                c.rgb = lerp(c.rgb, mcol * 0.85, a);
            }
            if (_Marks2Mode > 0.5)
            {
                // TD_Road: Farbe = lerp(lerp(COLOR, MARKS, MARKS.a), MARKS2, MARKS2.a) x DETAIL
                fixed4 m2 = tex2D(_Marks2Tex, IN.uv4_Marks2Tex);
                c.rgb = lerp(c.rgb, m2.rgb * 0.85, m2.a);
            }
            fixed3 vc = lerp(fixed3(1,1,1), saturate(IN.color.rgb * _VcScale), _VertexColorStrength);
            fixed sh = lerp(1.0, tex2D(_ShadowTex, IN.uv2_DetailTex).r, _ShadowStrength);
            o.Albedo = c.rgb * vc * sh * _Brightness;
            // Regen (TDU2Weather, rain_ground_wet): nasser Asphalt/Boden wird dunkler
            o.Albedo *= 1.0 - 0.35 * _TDU2Wet * _WetSurface;
            // Nur Flächen auf der Bodenebene: Wände/Theken mit _ReflMix würden sonst das Bodenspiegelbild über die
            // Bildschirmposition zeigen ("wandert mit der Kamera")
            // (Spiegelbild: am Ende als Eigenleuchten, siehe unten)
            // Interieur-Lightmaps: Licht ist eingebacken -> als Eigenleuchten statt Sonne/Ambient
            o.Emission = o.Albedo * _Unlit;
            o.Albedo *= (1.0 - _Unlit);
            // Nachts: Lampenköpfe/Neon leuchten selbst
            o.Emission += c.rgb * _NightEmit * _TDU2NightK;
            if (_WinMode > 0.5 && _TDU2NightWindows > 0.01)
            {
                // Original TD_Building (gWindowMapTexSampler): Licht = Fensterrauschen(Fensterkachel) x Helligkeit(COLOR) x
                // luminance_power.x, nur wo GLOSS.b über luminance_power.y liegt (Fensterscheiben). Die Kachel kommt im Original
                // aus der Objektposition (nbtilesXYZ); hier aus einem 2.6-m-Raster der Weltposition.
                half g = tex2D(_GlossTex, IN.uv2_DetailTex).b;
                half mask = g > _WinParams.y ? g : 0.0;
                float3 cell = floor(IN.worldPos / 2.6);
                float2 nuv = (cell.xz + cell.y * float2(17.0, 31.0) + 0.5) / 64.0;
                fixed3 win = tex2D(_WinTex, nuv).rgb;
                half lum = dot(tex2D(_MainTex, IN.uv_MainTex).rgb, half3(0.3, 0.59, 0.11));
                o.Emission += win * lum * _WinParams.x * mask * _TDU2NightWindows * 0.5;
            }
            if (_NightMode > 0.5)
            {
                // wie der Original-Pixelshader (TD_Building/TD_Furnitures, NIGHTLIGHTMAP s8):
                //   Farbe = Albedo x lerp(night.rgb, Licht, nightlmap.x) + night.rgb x night.a x nightlmap.y
                // -> die gebackene Beleuchtung wird mit der Oberfläche multipliziert, nur der Alpha-Anteil leuchtet selbst
                fixed4 nl = tex2D(_NightTex, IN.uv_MainTex);
                // lit: im Original lerp(Ambient, Sonne, N.L x Echtzeitschatten) – drinnen fällt die Sonne weg -> fester Raumlicht-Anteil
                o.Emission += o.Albedo * lerp(nl.rgb, _NightAmbient.rgb, _NightLmap.x) + nl.rgb * nl.a * _NightLmap.y;
                o.Albedo *= _NightLmap.x * _NightLitScale;
            }
            if (_ReflMix > 0.001 && _TDU2PlanarOn > 0.5 && abs(IN.worldPos.y - _TDU2PlanarY) < 0.06)
            {
                // TD_Furnitures: Oberfläche = lerp(Spiegelbild, Detail x baseColor x Detail2, baseColor.w) mit Fresnel (biasCoef).
                // Das Spiegelbild ist bereits fertig beleuchtet (Lightmaps der gespiegelten Szene) -> als Eigenleuchten, nicht
                // noch einmal mit der Lightmap des Bodens multiplizieren (sonst ist es nachts fast schwarz)
                float4 cp = mul(UNITY_MATRIX_VP, float4(IN.worldPos, 1.0));
                float2 suv = (cp.xy * float2(1.0, _ProjectionParams.x) / cp.w) * 0.5 + 0.5;
                half ndv = saturate(normalize(_WorldSpaceCameraPos - IN.worldPos).y);
                half w = saturate(_ReflMix + (1.0 - _ReflMix) * pow(1.0 - ndv, 5.0)) * 0.85;
                o.Albedo *= 1.0 - w;
                o.Emission = o.Emission * (1.0 - w) + tex2D(_TDU2PlanarRefl, suv).rgb * w;
            }
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/Diffuse"
}
