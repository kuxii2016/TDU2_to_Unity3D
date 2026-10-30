
Shader "TDU2/Grass"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.4
        _Wind ("Wind", Float) = 0.08
        _GroundTex ("Bodenfarbe (Colormap des Sektors, UV2)", 2D) = "white" {}
        _GroundOn ("Bodenfarbe aktiv", Float) = 0
        _GroundBoost ("Bodenfarbe Verstärkung", Float) = 1.0
        _GroundTipMix ("Halmspitzen weniger bodenfarben (Original: lerp(Boden, 1, v))", Range(0,1)) = 0.3
        _SplatTex ("Splatmap (R = gebackener Schatten, UV2)", 2D) = "white" {}
        _UseSplat ("Splatmap aktiv", Float) = 0
        _ShadowStrength ("Schatten-Stärke", Range(0,1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        Cull Off
        ZWrite On

        CGPROGRAM

        #pragma surface surf Plant alphatest:_Cutoff vertex:vert addshadow
        #pragma target 3.0
        #include "TDU2InteriorClip.cginc"

        sampler2D _MainTex, _GroundTex, _SplatTex;
        fixed4 _Color;
        half _Wind, _GroundOn, _GroundBoost, _UseSplat, _ShadowStrength, _GroundTipMix;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_GroundTex;
            float4 color : COLOR;
            float facing : VFACE;
            float3 worldPos;
        };

        // Gleiche Beleuchtung wie Bäume/Büsche (TDU2/Foliage, TDU2/Sprite, TDU2/Object mit _PlantMode):
        // Normale = oben, Echtzeitschatten wie der Boden -> alle Pflanzen passen zueinander und zum Boden.
        half4 LightingPlant (SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = saturate(dot(s.Normal, lightDir));
            half4 c;
            c.rgb = s.Albedo * _LightColor0.rgb * ndl * atten;
            c.a = s.Alpha;
            return c;
        }

        float _TDU2TerrainLower;   // global (TDU2MapStreamer)
        void vert(inout appdata_full v)
        {
            v.normal = normalize(mul((float3x3)unity_WorldToObject, float3(0, 1, 0)));
            // Windbewegung an der Spitze stärker als an der Basis
            float top = 1.0 - saturate(v.texcoord.y);

            // Weltposition vor der Bewegung
            float3 worldPos =
                mul(unity_ObjectToWorld, v.vertex).xyz;

            float wind =
                sin(_Time.y * 1.7
                + worldPos.x * 0.3
                + worldPos.z * 0.2)
                * _Wind * top;

            // Gras seitlich bewegen
            v.vertex.x += wind;
            v.vertex.z += wind * 0.5;
            // mit dem dargestellten Gelände absenken (TDU2/Terrain: _TDU2TerrainLower), sonst schwebt die Grasbasis
            v.vertex.xyz += mul((float3x3)unity_WorldToObject, float3(0, -_TDU2TerrainLower, 0));
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            TDU2InteriorClip(IN.worldPos, 0);   // kein Gras im Interieur
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            fixed4 c = tex * _Color;

            // wie TD_TallGrass: Grastextur x Bodenfarbe (Colormap) -> Gras so hell wie der Boden darunter, nicht heller als Bäume
            // Original TD_TallGrass: Gras x lerp(Bodenfarbe, 1, v) x (Ambient + Sonne x Schatten) – ohne Verstärkung
            fixed3 ground = _GroundOn > 0.5 ? lerp(saturate(tex2D(_GroundTex, IN.uv2_GroundTex).rgb * _GroundBoost), fixed3(1,1,1), _GroundTipMix) : fixed3(1,1,1);
            fixed sh = _UseSplat > 0.5 ? lerp(1.0, tex2D(_SplatTex, IN.uv2_GroundTex).r, _ShadowStrength) : 1.0;
            o.Albedo = c.rgb * IN.color.rgb * ground * sh;

            // Alpha-Test für transparente Bereiche
            o.Alpha = c.a;

            // Keine zusätzliche Emission
            o.Emission = fixed3(0, 0, 0);
        }

        ENDCG
    }

    FallBack "Transparent/Cutout/VertexLit"
}
