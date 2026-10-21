// Meer wie im Original (Shaders/TD_Ocean.sob, nachgebaut):
//   - zwei Normalmaps (fein/grob) in Weltkoordinaten (ocean_NMapHR 0,15 / ocean_NMapLR 0,015 je Meter), mit der Zeit verschoben;
//     in der Ferne flacher (wie der Abstandsterm des Originals)
//   - Grundfarbe = Colormap des Geländes an dieser Stelle: hier sieht man das Gelände (mit derselben Colormap) durch das Wasser
//   - Fresnel: fres = sat(Bias + Scale * (1 - N.V)^Power) aus ocean_fresnel; Ausgabe lerp(Gelände, Reflexion, fres)
//     -> Alpha-Blending mit Alpha = fres (Blend One OneMinusSrcAlpha, Reflexion vormultipliziert)
//   - Reflexion: im Original eine gespiegelte Szene (RenderTarget), hier Reflection Probe/Skybox, Unschärfe ocean_reflectionBluriness
//   - Sonnenglitzern: (R.L)^ocean_specularPower * ocean_specularColorAndCutOff
// Wetterwerte setzt TDU2Weather global (_TDU2Ocean*); ohne Wetter gelten die Material-Werte.
Shader "TDU2/Water"
{
    Properties
    {
        _Color ("Farbe (nicht benutzt: die Farbe kommt aus der Gelände-Colormap)", Color) = (0.05,0.25,0.35,0.1)
        _Glossiness ("Smoothness (nicht benutzt)", Range(0,1)) = 0.25
        _NormalMap ("Normalmap (commonworld water_n)", 2D) = "bump" {}
        _HasNormalMap ("Normalmap vorhanden", Float) = 0
        _NMapScale ("Normalmap-Kacheln je Meter (fein, grob)", Vector) = (0.15, 0.015, 0, 0)
        _NMapWeight ("Gewicht (fein, grob)", Vector) = (1, 1, 0, 0)
        _WaveStrength ("Wellenstärke", Range(0,2)) = 0.6
        _WaveSpeed ("Wellen-Geschwindigkeit", Float) = 1
        _FlatDistance ("Ab dieser Entfernung glatt (m)", Float) = 2500
        _Fresnel ("Fresnel (Bias, Power, Scale)", Vector) = (0.3, 30, 0.5, 0)
        _ReflColor ("Reflexionsfarbe (x2 kodiert)", Color) = (0.498,0.498,0.498,1)
        _ReflBlur ("Reflexions-Unschärfe", Range(0,1)) = 0.05
        _SpecColorCut ("Glanzfarbe (rgb) + Schwelle (a)", Color) = (0.502,0.596,0.674,0)
        _SpecPower ("Glanz-Schärfe", Float) = 20
        _PoolTint ("Pool: eigene Wasserfarbe (a = Mindest-Deckung, 0 = Meer)", Color) = (0,0,0,0)
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" "IgnoreProjector"="True" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Offset 2, 2   // hinter flachem Strand-Terrain zeichnen (kein Z-Fighting)
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "TDU2InteriorClip.cginc"

            sampler2D _NormalMap;
            fixed4 _Color, _ReflColor, _SpecColorCut, _PoolTint;
            float4 _NMapScale, _NMapWeight, _Fresnel;
            half _HasNormalMap, _WaveStrength, _WaveSpeed, _FlatDistance, _ReflBlur, _SpecPower, _TDU2NoClip;
            // global (TDU2MapStreamer / TDU2Weather)
            float4 _TDU2Origin;                                   // Weltposition des Insel-Ursprungs (Floating Origin)
            float _TDU2OceanSet;
            float4 _TDU2OceanFresnel, _TDU2OceanSpec, _TDU2OceanRefl, _TDU2OceanNMap;   // NMap: fein, grob, -, -
            float _TDU2OceanSpecPower, _TDU2OceanBlur;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float2 gamePos : TEXCOORD1;
                UNITY_FOG_COORDS(2)
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.gamePos = o.worldPos.xz - _TDU2Origin.xz;   // Wellen bleiben beim Verschieben der Welt an ihrem Ort
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            // Ersatz ohne Normalmap: Summe gedrehter Sinuswellen (Ableitungen = Neigung)
            float2 WaveSlope(float2 p, float t)
            {
                float2 s = 0;
                const float2 d0 = float2(0.80, 0.60), d1 = float2(-0.45, 0.89), d2 = float2(0.97, -0.24), d3 = float2(-0.70, -0.71);
                s += d0 * cos(dot(p, d0) * 6.0 + t * 1.7) * 0.30;
                s += d1 * cos(dot(p, d1) * 9.5 + t * 2.3) * 0.22;
                s += d2 * cos(dot(p, d2) * 15.0 + t * 3.1) * 0.15;
                s += d3 * cos(dot(p, d3) * 23.0 + t * 3.9) * 0.10;
                return s;
            }

            float3 SampleN(float2 uv)
            {
                float3 n = tex2D(_NormalMap, uv).rgb * 2.0 - 1.0;   // Tangentenraum, Z = oben
                return float3(n.x, n.z, n.y);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                TDU2InteriorClip(i.worldPos, _TDU2NoClip);   // Interieur unter dem Meeresspiegel (Yacht-Garage)
                bool w = _TDU2OceanSet > 0.5;
                float4 fr = w ? _TDU2OceanFresnel : _Fresnel;
                float3 specC = w ? _TDU2OceanSpec.rgb : _SpecColorCut.rgb;
                float specCut = w ? _TDU2OceanSpec.a : _SpecColorCut.a;
                float specPow = max(1.0, w ? _TDU2OceanSpecPower : _SpecPower);
                float3 reflC = (w ? _TDU2OceanRefl.rgb : _ReflColor.rgb) * 2.0;
                float blur = w ? _TDU2OceanBlur : _ReflBlur;
                float2 sc = w && _TDU2OceanNMap.x > 0 ? _TDU2OceanNMap.xy : _NMapScale.xy;

                float t = _Time.y * _WaveSpeed;
                float2 p = i.gamePos;
                float3 n;
                if (_HasNormalMap > 0.5)
                {
                    float3 n1 = SampleN(p * sc.x + t * float2(0.021, 0.013));
                    float3 n2 = SampleN(p * sc.y + t * float2(-0.004, 0.006));
                    n = n1 * _NMapWeight.x + n2 * _NMapWeight.y;
                    n.xz *= _WaveStrength;
                }
                else
                {
                    float2 s = WaveSlope(p * sc.x * 0.6, t) + WaveSlope(p * sc.y * 0.6 + 3.7, t * 0.5) * 1.5;
                    n = float3(-s.x * _WaveStrength * 0.35, 1.0, -s.y * _WaveStrength * 0.35);
                }
                n = normalize(n);
                float3 toCam = _WorldSpaceCameraPos - i.worldPos;
                float dist = length(toCam);
                float3 v = toCam / max(dist, 1e-3);
                n = normalize(lerp(n, float3(0, 1, 0), saturate(dist / max(1.0, _FlatDistance))));

                // Fresnel wie TD_Ocean: Bias + Scale * (1 - N.V)^Power
                float fres = saturate(fr.x + fr.z * pow(1.0 - saturate(dot(v, n)), fr.y));

                float3 r = reflect(-v, n);
                r.y = abs(r.y);   // keine Reflexion "unter" dem Horizont
                half4 envRaw = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, r, blur * 20.0);
                float3 refl = DecodeHDR(envRaw, unity_SpecCube0_HDR) * reflC;

                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float sunUp = saturate(l.y * 4.0);
                float spec = pow(saturate(dot(r, l)), specPow);
                spec = max(0.0, spec - specCut);
                float lum = saturate(dot(_LightColor0.rgb, float3(0.3, 0.59, 0.11)));
                float3 col = refl * fres + specC * spec * lum * sunUp;
                // Pool/Brunnen: Wasserfarbe statt nur Spiegelung (sonst sieht man fast nur den Beckenboden)
                float alpha = max(fres, _PoolTint.a);
                col += _PoolTint.rgb * max(0.0, alpha - fres) * (0.35 + 0.65 * lum);

                fixed4 o = fixed4(col, alpha);
                UNITY_APPLY_FOG_COLOR(i.fogCoord, o, fixed4(unity_FogColor.rgb * alpha, alpha));
                return o;
            }
            ENDCG
        }
    }
}
