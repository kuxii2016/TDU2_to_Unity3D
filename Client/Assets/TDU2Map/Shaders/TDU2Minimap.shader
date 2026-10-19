// Minimap: Straßen-Mesh (Vertexfarbe) in eine RenderTexture, Anzeige als runde Scheibe (_Circle = 1: Kreismaske über UV).
Shader "TDU2/Minimap"
{
    Properties
    {
        _MainTex ("Textur", 2D) = "white" {}
        _Circle ("Kreismaske", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Circle;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 col : COLOR; };
            v2f vert (appdata_full v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; o.col = v.color; return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.col;
                if (_Circle > 0.5)
                {
                    float d = length(i.uv - 0.5) * 2.0;
                    c.a *= saturate((1.0 - d) * 60.0);
                }
                return c;
            }
            ENDCG
        }
    }
}
