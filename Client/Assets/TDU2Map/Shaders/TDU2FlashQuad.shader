// Flash-Overlay (VMF-Player): Textur x Farbtransformation (Mul/Add wie SWF CXFORM), Alpha-Blending, ohne Tiefentest.
Shader "TDU2/FlashQuad"
{
    Properties
    {
        _MainTex ("Textur", 2D) = "white" {}
        _Mul ("Multiplikator", Vector) = (1,1,1,1)
        _Add ("Addend", Vector) = (0,0,0,0)
        _LumAlpha ("Luminanz als Alpha (Fonts)", Float) = 0
        _ClipRect ("Maske (Pixel xMin yMin xMax yMax)", Vector) = (-10000000,-10000000,10000000,10000000)
        _MaskTex ("Maskenform", 2D) = "white" {}
        _MaskRect ("Maskenform-Rechteck (Pixel)", Vector) = (0,0,1,1)
        _MaskOn ("Maskenform aktiv", Float) = 0
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
            sampler2D _MainTex, _MaskTex;
            float4 _Mul, _Add;
            float _LumAlpha;
            float4 _ClipRect, _MaskRect;
            float _MaskOn;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 pix : TEXCOORD1; };
            v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; o.pix = v.vertex.xy; return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                // SWF-Maske (ClipDepth) als Rechteck; Vertices liegen in GUI-Pixeln (GL.LoadPixelMatrix)
                if (i.pix.x < _ClipRect.x || i.pix.y < _ClipRect.y || i.pix.x > _ClipRect.z || i.pix.y > _ClipRect.w) discard;
                if (_MaskOn > 0.5)
                {
                    // Maskenform: gerasterte Shape, oberste Zeile (v = 1) bei _MaskRect.y
                    float2 muv = float2((i.pix.x - _MaskRect.x) / (_MaskRect.z - _MaskRect.x), 1.0 - (i.pix.y - _MaskRect.y) / (_MaskRect.w - _MaskRect.y));
                    if (muv.x < 0 || muv.x > 1 || muv.y < 0 || muv.y > 1 || tex2D(_MaskTex, muv).a < 0.02) discard;
                }
                fixed4 t = tex2D(_MainTex, i.uv);
                if (_LumAlpha > 0.5) t = fixed4(1, 1, 1, max(t.r, max(t.g, t.b)) * t.a);
                return saturate(t * _Mul + _Add);
            }
            ENDCG
        }
    }
}
