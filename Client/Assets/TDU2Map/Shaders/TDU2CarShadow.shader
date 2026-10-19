// Bodenschatten unter dem Fahrzeug: Alpha der Schattentextur (shadow.2db) dunkelt den Untergrund ab (multiplikativ).
Shader "TDU2/CarShadow"
{
    Properties
    {
        _MainTex ("Schatten (Alpha)", 2D) = "white" {}
        _Strength ("Stärke", Range(0,1)) = 0.75
    }
    SubShader
    {
        Tags { "Queue"="Transparent-50" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend DstColor Zero
        ZWrite Off
        Offset -2, -2
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            half _Strength;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata_base v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord.xy; return o; }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed a = tex2D(_MainTex, i.uv).a * _Strength;
                return fixed4(1 - a, 1 - a, 1 - a, 1);
            }
            ENDCG
        }
    }
}
