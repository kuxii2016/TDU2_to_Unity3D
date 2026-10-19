// Stickermap rendern (TDU2LiveryView): Pass 0 = Sticker (Textur x Farbe, Alpha-Blending, Alpha akkumuliert),
// Pass 1 = Grund (deckend), Pass 2 = Kopie mit RGB / Alpha (vormultiplizierte Farbe -> gerade Farbe für den Lack-Shader).
// Vertices in Pixeln der RenderTexture (GL.LoadPixelMatrix).
Shader "Hidden/TDU2/Sticker"
{
    Properties
    {
        _MainTex ("Textur", 2D) = "white" {}
        _Color ("Farbe", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off ZTest Always Cull Off
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        fixed4 _Color;
        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; return o; }
        ENDCG
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target { return tex2D(_MainTex, i.uv) * _Color; }
            ENDCG
        }
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target { fixed4 c = tex2D(_MainTex, i.uv) * _Color; return fixed4(c.rgb, 1); }
            ENDCG
        }
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target { fixed4 c = tex2D(_MainTex, i.uv); return c.a > 0.004 ? fixed4(saturate(c.rgb / c.a), c.a) : fixed4(0, 0, 0, 0); }
            ENDCG
        }
    }
}
