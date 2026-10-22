// Reifenspuren: dunkle, halbtransparente Streifen (Alpha aus Vertexfarbe x Textur), leicht vor dem Boden versetzt.
Shader "TDU2/Skidmark"
{
    Properties { _MainTex ("Spur", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Transparent-60" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Offset -1, -1
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _TDU2NightK;   // global (TDU2Weather): 0 = Tag, 1 = Nacht/Straßenlicht an
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_FOG_COORDS(1) };
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color; UNITY_TRANSFER_FOG(o, o.pos); return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                fixed4 c = fixed4(max(t.rgb, i.color.rgb), t.a * i.color.a);   // Vertexfarbe: schwarz = Gummi, braun = Erde
                // Nachts dunkler: sonst wirken die unbeleuchteten Spuren wie Leuchtstreifen auf dem dunklen Asphalt
                c.rgb *= lerp(1.0, 0.12, saturate(_TDU2NightK));
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
