// Avatare (Körper, Kopf, Kleidung, Haare, Augen) – angelehnt an die Original-Shader-Sets
// TD_Skin2 / TD_Skinface (SKIN-Textur x SKINCLR), TD_Clothe2 (BASECLR + LAYER1..3 mit CLRL1..3 über ihr Alpha,
// Normalen aus OCCLUSION (RG) + DETAIL_GRAIN (AG) gekachelt mit UVSCALE.zw), TD_Hair (lerp(PRIMCLR, SCDCLR, HAIR.g),
// Alpha-Test HAIR.a), TD_Eye. Dazu der "Fußraum-Schatten" der Avatar-Sets: t = saturate((y - carinteg.x) / (carinteg.y -
// carinteg.x)) über die gehäutete Höhe, Licht x lerp(atteFact, 1, t) – nur im Auto (_InCar).
Shader "TDU2/AvatarBlend"
{
    Properties
    {
        _Mode ("Modus (0 Haut, 1 Kleidung, 2 Textur, 3 Haare, 4 Auge)", Float) = 0
        _MainTex ("SKIN / TEX / HAIR", 2D) = "white" {}
        _Color ("Tönung", Color) = (1,1,1,1)
        _GlossTex ("GLOSSDETAILCUT (Haut: R Glanz, G Durchscheinen, B Dekal)", 2D) = "black" {}
        _GlossOn ("Glanz/Maske aktiv", Float) = 0
        _RampTex ("SPECTRALSCATTERING (Hautton-Rampe 256x4 über Wrap-N.L)", 2D) = "white" {}
        _RampOn ("Rampe aktiv", Float) = 0
        _RampScale ("Rampe Verstärkung (Rampe endet bei ~0,6)", Float) = 1.5
        _SkinClr ("SKINCLR (Farbe des durchscheinenden Lichts)", Color) = (1,1,1,1)
        _DclClr ("DCLCLR (Dekal)", Color) = (0,0,0,1)
        _Coeffs ("COEFFS (x Wrap, y Dekal, z Zeile der Hautton-Rampe)", Vector) = (0.5,0,0.5,0)
        _BumpMap ("NORMAL (Haut) bzw. OCCLUSION (Kleidung, RG = Normale)", 2D) = "bump" {}
        _BumpOn ("Normalmap aktiv", Float) = 0
        _DetailTex ("DETAIL_GRAIN (Stoff, Normale in AG)", 2D) = "gray" {}
        _DetailOn ("Stoffkorn aktiv", Float) = 0
        _UVScale ("UVSCALE (zw = Kachelung Stoffkorn)", Vector) = (1,1,10,10)
        _BaseClr ("BASECLR", Color) = (0.5,0.5,0.5,1)
        _Layer1 ("LAYER1", 2D) = "black" {}
        _Layer2 ("LAYER2", 2D) = "black" {}
        _Layer3 ("LAYER3", 2D) = "black" {}
        _ClrL1 ("CLRL1", Color) = (1,1,1,1)
        _ClrL2 ("CLRL2", Color) = (1,1,1,1)
        _ClrL3 ("CLRL3", Color) = (1,1,1,1)
        _LayerOn ("Layer aktiv (xyz)", Vector) = (0,0,0,0)
        _LayerUV ("UV-Satz je Layer (xyz: 0..2)", Vector) = (1,2,2,0)
        _AlphaLum ("Alpha aus Helligkeit (DXT1-Masken: Augenbrauen, Wimpern)", Float) = 0
        _Ombre ("Brillenglas: Farbe/Deckkraft lerp(BCOLOR, TCOLOR, TEX.a)", Float) = 0
        _BColor ("BCOLOR (Glas unten)", Color) = (1,1,1,0.5)
        _TColor ("TCOLOR (Glas oben)", Color) = (0,0,0,1)
        _PrimClr ("PRIMCLR (Haare)", Color) = (0,0,0,1)
        _ScdClr ("SCDCLR (Haare)", Color) = (1,1,1,1)
        _Spec ("SPECPARM (x Stärke, y Exponent)", Vector) = (0.2, 8, 0, 0)
        _CarInteg ("CARINTEG (x, y Höhe Fußraum)", Vector) = (0.35, 0.5, 1, 0)
        _AtteFact ("ATTEFACT (Ambient, Diffus, Spekular unten)", Vector) = (0, 0, 0, 0)
        _InCar ("Im Auto (Fußraum-Schatten an)", Float) = 0
        _Cutoff ("Alpha-Test", Range(0,1)) = 0
        _Brightness ("Helligkeit", Range(0.5,2)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 200
        Cull [_Cull]
        ZWrite Off
        CGPROGRAM
        // Geblendete Avatar-Materialien (Original: SrcAlpha/InvSrcAlpha): Augenbrauen, Wimpern, Haar-Strähnen, Haaransätze
        #pragma surface surf TDU2Avatar alpha:fade vertex:vert nolightmap nodynlightmap nodirlightmap noambient
        #define TDU2_BLEND 1
        half _Cutoff;
        #pragma target 3.5

        sampler2D _MainTex, _BumpMap, _DetailTex, _Layer1, _Layer2, _Layer3, _GlossTex, _RampTex;
        fixed4 _Color, _BaseClr, _ClrL1, _ClrL2, _ClrL3, _PrimClr, _ScdClr, _SkinClr, _DclClr;
        float4 _UVScale, _LayerOn, _LayerUV, _Spec, _CarInteg, _AtteFact, _Coeffs;
        half _Mode, _BumpOn, _DetailOn, _InCar, _Brightness, _GlossOn, _RampOn, _RampScale, _AlphaLum, _Ombre;
        fixed4 _BColor, _TColor;
        // Innenräume (TDU2Interiors): Licht ist dort eingebacken, die Sonne scheint nicht hinein. Statt Sonne ein weiches
        // Deckenlicht (_TDU2IndoorLight/-Dir), Umgebung als Verlauf oben -> unten und Abdunkeln zum Boden hin (Kontaktschatten)
        half _TDU2AvatarIndoor;
        half4 _TDU2IndoorLight, _TDU2IndoorDir, _TDU2IndoorAmbTop, _TDU2IndoorAmbBottom;
        // Feinabstimmung aus dem Entwicklermenü (F1 > Avatare > Shader), 0 = wie im Original:
        //   _TDU2AvRampAdd  + Verstärkung der Hautton-Rampe, _TDU2AvRampRow Zeile der Rampe (0 = COEFFS.z)
        //   _TDU2AvAddMix   0 = Original lerp(Umgebung, Sonne), 1 = Umgebung zusätzlich auf der Lichtseite (weicher)
        //   _TDU2AvAmbAdd   zusätzliches Umgebungslicht, _TDU2AvHairSheen Glanzanteil der Haare (0 = wie Original)
        half _TDU2AvRampAdd, _TDU2AvRampRow, _TDU2AvAddMix, _TDU2AvAmbAdd, _TDU2AvHairSheen;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_Layer1;
            float2 uv3_Layer2;
            float footT;
            float hgt;
            float vface : VFACE;
        };

        struct SurfaceOutputAvatar
        {
            fixed3 Albedo;
            fixed3 Normal;
            fixed3 Emission;
            half Specular;
            fixed Gloss;
            fixed Alpha;
            half Foot;
            half Scatter;   // Haut: Maske des durchscheinenden Lichts (GLOSSDETAILCUT.g)
            half Occl;      // Innenraum: Abdunkeln zum Boden hin
            half AO;        // Kleidung: eingebackene Verdeckung (OCCLUSION.b)
            half Aux;       // Haare: HAIR.r (Helligkeit der Strähnen)
        };

        void vert (inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            // gehäutete Höhe im Avatar-Raum (Renderer = Avatar-Wurzel)
            float h = v.vertex.y;
            float d = max(0.001, _CarInteg.y - _CarInteg.x);
            o.footT = _InCar > 0.5 ? saturate((h - _CarInteg.x) / d) : 1.0;
            o.hgt = h;
        }

        static const half3 LUM = half3(0.299, 0.587, 0.114);

        // Lichtmodell der Original-Shader (TD_Skin2/TD_Skinface, TD_Clothe2, TD_Hair, TD_Avatar – aus den Disassemblies):
        //   Licht = lerp(Umgebung x atteFact.x, Sonne x atteFact.y, t), t = (Wrap-)N.L x Schatten
        // also NICHT Umgebung + Sonne: im Schatten nur die Umgebung, im Licht nur die Sonne. Dazu
        //   Haut:     Durchscheinen (GLOSSDETAILCUT.g) tönt alle Lichter Richtung Helligkeit x SKINCLR; Gegenlicht-Saum
        //             saturate(V.-L) x SPECPARM.z x (1 - N.V); Hautton-Rampe SPECTRALSCATTERING(t, COEFFS.z) auf das Licht;
        //             Glanz Phong pow(R.V x Schatten, SPECPARM.y) x GLOSS.r x SPECPARM.x
        //   Kleidung: Licht x OCCLUSION.b (eingebackene Verdeckung), Glanz x DETAIL.b x Verdeckung
        //   Haare:    Wrap 0,75 N.L + 0,25; Ergebnis lerp(Licht x Farbe + Glanz, Farbe + (Licht + Glanz) / 3, Helligkeit)
        half4 LightingTDU2Avatar (SurfaceOutputAvatar s, half3 lightDir, half3 viewDir, half atten)
        {
            half4 c;
            c.a = s.Alpha;
            half3 lc = _LightColor0.rgb;
        #ifdef UNITY_PASS_FORWARDBASE
            half3 att = lerp(_AtteFact.xyz, half3(1,1,1), s.Foot);
            half3 N = s.Normal, V = viewDir;
            half3 amb;
            if (_TDU2AvatarIndoor > 0.5)
            {
                // drinnen: keine Sonne (und kein Sonnenschatten), weiches Deckenlicht; Umgebung oben hell, unten dunkel
                lightDir = normalize(_TDU2IndoorDir.xyz);
                lc = _TDU2IndoorLight.rgb;
                atten = 1;
                amb = lerp(_TDU2IndoorAmbBottom.rgb, _TDU2IndoorAmbTop.rgb, saturate(N.y * 0.5 + 0.5));
            }
            else amb = max(half3(0,0,0), ShadeSH9(half4(N, 1)));
            half3 L = lightDir;
            half nl = dot(N, L);
            half rv = max(dot(reflect(-L, N), V), 1e-4);
            amb = (amb + _TDU2AvAmbAdd) * att.x;
            half3 dif = lc * att.y, spl = lc * att.z;
            half3 light, spec;
            if (_Mode < 0.5)
            {
                half w = _Coeffs.x;
                half u = saturate((nl + w) / (1 + w));
                u = saturate(saturate(dot(V, -L)) * _Spec.z * (1 - saturate(dot(N, V))) + u) * atten;
                amb = lerp(amb, dot(amb, LUM) * _SkinClr.rgb, s.Scatter);
                dif = lerp(dif, dot(dif, LUM) * _SkinClr.rgb, s.Scatter);
                spl = lerp(spl, dot(spl, LUM) * _SkinClr.rgb, s.Scatter);
                half rv2 = _TDU2AvRampRow > 0.001 ? _TDU2AvRampRow : _Coeffs.z;
                half3 ramp = _RampOn > 0.5 ? tex2D(_RampTex, float2(u, rv2)).rgb * (_RampScale + _TDU2AvRampAdd) : half3(1,1,1);
                light = (lerp(amb, dif, u) + amb * u * _TDU2AvAddMix) * ramp;
                spec = spl * pow(max(rv * atten, 1e-4), max(1.0, s.Specular)) * s.Gloss;
                c.rgb = s.Albedo * light + spec;
            }
            else if (_Mode > 2.5 && _Mode < 3.5)
            {
                half u = saturate(0.75 * nl + 0.25) * atten;
                light = (lerp(amb, dif, u) + amb * u * _TDU2AvAddMix) * s.AO;
                half3 h = normalize(L + V);
                spec = spl * pow(saturate(dot(N, h)), max(1.0, s.Specular)) * s.Gloss * atten * saturate(1.75 * nl + 0.25);
                half3 dark = light * s.Albedo + spec;
                // Original: Farbe + (Licht + Glanz) / 3 – das Grau des Lichts würde die Haarfarbe hier ausbleichen;
                // daher farbtreu (Farbe x (1 + Helligkeit / 3)), der Glanz wie im Original
                half3 lit = s.Albedo * (1 + dot(light, LUM) * 0.33) + spec * (0.33 + _TDU2AvHairSheen);
                c.rgb = lerp(dark, lit, saturate(dot(light * s.Aux + spec, LUM)));
            }
            else
            {
                half u = saturate(nl) * atten;
                light = (lerp(amb, dif, u) + amb * u * _TDU2AvAddMix) * s.AO;
                spec = spl * pow(max(rv * atten, 1e-4), max(1.0, s.Specular)) * s.Gloss * s.AO;
                c.rgb = s.Albedo * light + spec;
            }
            c.rgb *= s.Occl;
        #else
            // Zusatzlichter (Punkt/Spot): einfach diffus
            c.rgb = s.Albedo * lc * saturate(dot(s.Normal, lightDir)) * atten * s.Occl * s.AO;
        #endif
            return c;
        }

        float2 SampleUV(Input IN, float which)
        {
            return which < 0.5 ? IN.uv_MainTex : (which < 1.5 ? IN.uv2_Layer1 : IN.uv3_Layer2);
        }

        void surf (Input IN, inout SurfaceOutputAvatar o)
        {
            float2 uv = IN.uv_MainTex;
            fixed4 c = fixed4(1,1,1,1);
            fixed3 n = fixed3(0,0,1);
            o.Scatter = 0;
            o.AO = 1;
            o.Aux = 1;
            o.Occl = _TDU2AvatarIndoor > 0.5 ? lerp(0.55, 1.0, saturate(IN.hgt / 1.0)) : 1.0;
            half specK = _Spec.x, specP = _Spec.y;
            if (_Mode < 0.5)
            {
                // Haut: Farbe = SKIN (Dekal DCLCLR über GLOSSDETAILCUT.b x COEFFS.y), Glanz = .r, Durchscheinen = .g
                c = tex2D(_MainTex, uv) * _Color;
                c.a = 1;
                if (_GlossOn > 0.5)
                {
                    fixed4 g = tex2D(_GlossTex, uv);
                    c.rgb = lerp(c.rgb, _DclClr.rgb, saturate(g.b * _Coeffs.y));
                    specK *= g.r;
                    o.Scatter = saturate(g.g);
                }
                if (_BumpOn > 0.5) { fixed4 nt = tex2D(_BumpMap, uv); fixed2 xy = nt.ag * 2 - 1; n = fixed3(xy, sqrt(saturate(1 - dot(xy, xy)))); }   // DXT5nm (AG)
            }
            else if (_Mode < 1.5)
            {
                // TD_Clothe2: BASECLR, darüber LAYER1 und LAYER2 (CLRLn x Textur über Alpha x CLRLn.a), dann Stoffkorn DETAIL.b
                // (dunkelt ab), dann LAYER3 (Aufdrucke, Knöpfe – nicht abgedunkelt). Deckkraft = max(BASECLR.a, Layer-Alphas)
                c = _BaseClr;
                half alpha = _BaseClr.a;
                fixed4 det = _DetailOn > 0.5 ? tex2D(_DetailTex, uv * _UVScale.zw) : fixed4(0.5, 0.5, 1, 0.5);
                if (_LayerOn.x > 0.5) { fixed4 l = tex2D(_Layer1, SampleUV(IN, _LayerUV.x) * _UVScale.xy); fixed a = l.a * _ClrL1.a; c.rgb = lerp(c.rgb, _ClrL1.rgb * l.rgb, a); alpha = max(alpha, a); }
                if (_LayerOn.y > 0.5) { fixed4 l = tex2D(_Layer2, SampleUV(IN, _LayerUV.y)); fixed a = l.a * _ClrL2.a; c.rgb = lerp(c.rgb, _ClrL2.rgb * l.rgb, a); alpha = max(alpha, a); }
                c.rgb *= det.b;
                if (_LayerOn.z > 0.5) { fixed4 l = tex2D(_Layer3, SampleUV(IN, _LayerUV.z)); fixed a = l.a * _ClrL3.a; c.rgb = lerp(c.rgb, _ClrL3.rgb * l.rgb, a); alpha = max(alpha, a); }
            #ifdef TDU2_BLEND
                c.a = alpha;
            #else
                c.a = 1;
            #endif
                specK *= det.b;
                if (_BumpOn > 0.5)
                {
                    // OCCLUSION: RG = Normale, B = eingebackene Verdeckung; Stoffkorn-Normale (DETAIL.ag) addiert, z = 1
                    fixed4 occ = tex2D(_BumpMap, uv);
                    fixed2 xy = occ.rg * 2 - 1;
                    if (_DetailOn > 0.5) xy += fixed2(det.a, det.g) * 2 - 1;
                    n = normalize(fixed3(xy, 1));
                    o.AO = occ.b;
                }
            }
            else if (_Mode < 2.5)
            {
                // TD_Avatar (Zähne, Wimpern, Augenbrauen, Accessoires, Gurt): Farbe = TEX x COLOR, Deckkraft = TEX.a
                fixed4 t = tex2D(_MainTex, uv);
                c = t * _Color;
                // Augenbrauen/Wimpern: DXT1 ohne Alpha, weiße Härchen auf Schwarz = Maske; Farbe = _Color (Haarfarbe)
                if (_AlphaLum > 0.5) c = fixed4(_Color.rgb, max(t.r, max(t.g, t.b)) * _Color.a);
                // Brillengläser: Verlauf OMBRE2 (Alpha) zwischen BCOLOR (unten) und TCOLOR (oben)
                if (_Ombre > 0.5) { c = lerp(_BColor, _TColor, t.a); specK = max(specK, 0.5); specP = max(specP, 40); }
            }
            else if (_Mode < 3.5)
            {
                // TD_Hair: Farbe lerp(PRIMCLR, SCDCLR, HAIR.g), Glanz x HAIR.b, Helligkeit HAIR.r, Deckkraft HAIR.a
                fixed4 h = tex2D(_MainTex, uv);
                c.rgb = lerp(_PrimClr.rgb, _ScdClr.rgb, h.g);
                c.a = h.a;
                specK *= h.b;
                o.Aux = h.r;
            }
            else
            {
                // Augen
                c = tex2D(_MainTex, uv) * _Color;
                c.a = 1;
                specK = 0.6; specP = 60;
            }
            o.Albedo = c.rgb * _Brightness;
            o.Alpha = c.a;
            // beidseitige Flächen (Haar-Strähnen, Wimpern): Rückseite mit umgedrehter Normale, sonst dunkle Facetten
            if (IN.vface < 0) n.z = -n.z;
            o.Normal = n;
            o.Specular = specP;
            o.Gloss = specK * (_TDU2AvatarIndoor > 0.5 ? 0.5 : 1.0);
            o.Foot = IN.footT;
            o.Emission = 0;   // Umgebungslicht steckt im Lichtmodell (lerp statt Summe)
            clip(o.Alpha - _Cutoff);
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Diffuse"
}
