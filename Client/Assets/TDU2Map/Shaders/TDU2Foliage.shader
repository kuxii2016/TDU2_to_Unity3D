// Laub/Blätter (V_*-Materialien mit Alpha-Test): Wind-Animation im Vertex-Shader, weiche Beleuchtung (Wrap + Durchleuchtung),
// Normalen Richtung "oben" geglättet, damit nahe Bäume nicht dunkler wirken als die Billboards in der Ferne.
Shader "TDU2/Foliage"
{
    Properties
    {
        _MainTex ("Color", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _VertexColorStrength ("Vertex Color Strength (x2)", Range(0,1)) = 0.5
        _NormalUp ("Normalen Richtung oben glätten", Range(0,1)) = 1
        _Wrap ("Wrap-Beleuchtung", Range(0,1)) = 0
        _Translucency ("Durchleuchtung", Range(0,1)) = 0
        _SelfShadow ("Eigenschatten (Echtzeit) Stärke", Range(0,1)) = 1
        _UseVertexColor ("Vertexfarbe verwenden (Original: nein)", Float) = 0
        _WindStrength ("Wind Stärke", Range(0,3)) = 1
        _WindSpeed ("Wind Geschwindigkeit", Range(0,5)) = 1
        _FlutterStrength ("Blattflattern", Range(0,3)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 200
        Cull Off
        CGPROGRAM
        #pragma surface surf Foliage alphatest:_Cutoff vertex:vert addshadow fullforwardshadows nolightmap nodynlightmap nodirlightmap
        #pragma multi_compile_instancing
        #pragma target 3.0
        #include "TDU2InteriorClip.cginc"
        half _TDU2NoClip;
        sampler2D _MainTex; fixed4 _Color;
        half _VertexColorStrength, _NormalUp, _Wrap, _Translucency, _SelfShadow, _UseVertexColor;
        half _WindStrength, _WindSpeed, _FlutterStrength;

        struct Input { float2 uv_MainTex; float4 color : COLOR; float3 worldPos; };

        half4 LightingFoliage (SurfaceOutput s, half3 lightDir, half atten)
        {
            // Original TD_Vegetation: Farbe = Textur x lerp(Ambient, Sonne, max(N.L, 0)) – ohne Vertexfarbe.
            // Eigenschatten der Krone (Unity-Kaskadenschatten mit Alpha-Test) nur abgeschwächt, sonst wirken Bäume/Büsche
            // deutlich dunkler als Gras und Boden.
            half ndl = dot(s.Normal, lightDir);
            half wrap = saturate((ndl + _Wrap) / (1.0 + _Wrap));
            half back = saturate(-ndl) * _Translucency;
            atten = lerp(1.0, atten, _SelfShadow);   // Licht scheint durch die Blätter
            half4 c;
            c.rgb = s.Albedo * _LightColor0.rgb * (wrap + back) * atten;
            c.a = s.Alpha;
            return c;
        }

        void vert (inout appdata_full v)
        {
            // Normalen Richtung Objekt-"oben" mischen (Baumkronen wirken wie ein Volumen statt einzelner Karten)
            float3 upObj = normalize(mul((float3x3)unity_WorldToObject, float3(0, 1, 0)));
            v.normal = normalize(lerp(v.normal, upObj, _NormalUp));

            // Wind: großes Schwanken (Höhe über dem Stamm) + schnelles Blattflattern
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 origin = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
            float h = max(0, wp.y - origin.y);
            float phase = dot(origin.xz, float2(0.071, 0.113));
            float t = _Time.y * _WindSpeed;
            float sway = (sin(t * 0.9 + phase) * 0.6 + sin(t * 2.1 + phase * 1.7) * 0.25) * _WindStrength * 0.012 * h;
            float flutter = sin(t * 7.0 + dot(wp, float3(1.3, 0.7, 1.1))) * _FlutterStrength * 0.02 * saturate(h * 0.3);
            float3 disp = float3(sway + flutter, flutter * 0.5, sway * 0.6 - flutter);
            v.vertex.xyz += mul((float3x3)unity_WorldToObject, disp);
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            TDU2InteriorClip(IN.worldPos, _TDU2NoClip);
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            fixed3 vc = lerp(fixed3(1,1,1), saturate(IN.color.rgb * 2.0), _VertexColorStrength * _UseVertexColor);
            o.Albedo = c.rgb * vc;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/Diffuse"
}
