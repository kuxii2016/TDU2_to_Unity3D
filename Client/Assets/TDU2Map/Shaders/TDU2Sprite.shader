// Billboard-Sprites der Vegetation (Materialien *_Spr, Original-Shader TD_VegetationSPR). Jeder Sprite = 4 Vertices mit
// gleichem Mittelpunkt, der Eckoffset (Meter) steht im zweiten UV-Kanal und wird zur Kamera ausgerichtet.
//
// Beleuchtung wie im Original-Vertexshader:
//   Kronenmitte = Objektursprung + Objekt-oben x RAMPCLR1.x   (Pinie ~11 m, Eiche ~12.5 m, Busch 0.25-0.85 m)
//   N           = normalize(Vertex - Kronenmitte)              (kugelförmige Normale -> Krone oben hell, unten dunkler)
//   x           = 1 + Vertexalpha x (max(N.L, 0) - 1)
//   Farbe       = Textur x Vertexfarbe x lerp(Ambient, lerp(Ambient, Sonne, x), Echtzeitschatten)
// Die Vertexfarbe (~0.6-0.75, dunkler im Kroneninneren) wird direkt multipliziert, nicht x2.
Shader "TDU2/Sprite"
{
    Properties
    {
        _MainTex ("Color", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _TDU2NoClip ("Teil eines Interieurs (nicht ausschneiden)", Float) = 0
        _CrownHeight ("Kronenmitte über Ursprung (RAMPCLR1.x, m)", Float) = 0
        _CrownOn ("Kugel-Normale um die Kronenmitte (Original)", Float) = 0
        _UseVertexColor ("Vertexfarbe (Original: ja, direkt multipliziert)", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        CGPROGRAM
        #pragma surface surf VegSpr alphatest:_Cutoff vertex:vert addshadow
        #pragma multi_compile_instancing
        #pragma target 3.0
        #include "TDU2InteriorClip.cginc"
        sampler2D _MainTex; fixed4 _Color; half _TDU2NoClip, _CrownHeight, _CrownOn, _UseVertexColor;
        float4 _TDU2BillboardRight, _TDU2BillboardUp, _TDU2BillboardFwd;   // xyz, w = aktiv (VegetationRenderer)

        // Unity: Albedo x (Ambient + Licht x Faktor x Schatten) mit Licht = Sonne - Ambient  ==  lerp(Ambient, Sonne, ...)
        half4 LightingVegSpr (SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = max(0.0, dot(s.Normal, lightDir));
            half x = 1.0 + s.Gloss * (ndl - 1.0);   // s.Gloss = Vertexalpha (Anteil N.L)
            half4 c;
            c.rgb = s.Albedo * _LightColor0.rgb * x * atten;
            c.a = s.Alpha;
            return c;
        }

        struct Input { float2 uv_MainTex; float4 color : COLOR; float3 worldPos; };

        void vert (inout appdata_full v)
        {
            float2 off = v.texcoord1.xy;
            float3 vr = UNITY_MATRIX_V[0].xyz, vu = UNITY_MATRIX_V[1].xyz;
        #ifdef UNITY_PASS_SHADOWCASTER
            // Schattenpass: Sprites zur Spielkamera ausrichten wie im Bild, nicht zur Sonne
            if (_TDU2BillboardRight.w > 0.5) { vr = _TDU2BillboardRight.xyz; vu = _TDU2BillboardUp.xyz; }
        #endif
            float3 right = normalize(mul((float3x3)unity_WorldToObject, vr));
            float3 up    = normalize(mul((float3x3)unity_WorldToObject, vu));
            v.vertex.xyz += right * off.x + up * off.y;

            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 center = mul(unity_ObjectToWorld, float4(0, _CrownHeight, 0, 1)).xyz;
            float3 nw = wp - center;
            if (_CrownOn < 0.5 || dot(nw, nw) < 1e-6) nw = float3(0, 1, 0);
            nw = normalize(nw);
            // Welt-Normale -> Objektraum (Unity wandelt mit der Inversen-Transponierten zurück)
            v.normal = normalize(mul(nw, (float3x3)unity_ObjectToWorld));
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            TDU2InteriorClip(IN.worldPos, _TDU2NoClip);   // Vegetations-Billboards der Welt nicht im Interieur
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            fixed3 vc = lerp(fixed3(1,1,1), IN.color.rgb, _UseVertexColor);
            o.Albedo = c.rgb * vc;
            o.Gloss = _CrownOn > 0.5 ? IN.color.a : 0.0;
            o.Alpha = c.a;
        }
        ENDCG
    }
}
