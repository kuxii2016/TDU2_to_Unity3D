Shader "Debug/VertexPoints"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 0.02
        _Color ("Color", Color) = (1,0,0,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2g
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            float _PointSize;
            fixed4 _Color;

            v2g vert (appdata v)
            {
                v2g o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            [maxvertexcount(4)]
            void geom (point v2g input[1], inout TriangleStream<g2f> triStream)
            {
                float4 center = input[0].pos;
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp    = UNITY_MATRIX_V[1].xyz;

                float size = _PointSize * 0.01;

                // View-space size stabiler (optional) - falls zu groﬂ/klein
                // size *= center.w; // entkommentieren falls perspektivisch skalieren willst

                float4 p0 = center + float4(-camRight - camUp, 0) * size;
                float4 p1 = center + float4( camRight - camUp, 0) * size;
                float4 p2 = center + float4( camRight + camUp, 0) * size;
                float4 p3 = center + float4(-camRight + camUp, 0) * size;

                g2f o;
                o.uv = float2(-1,-1); o.pos = p0; triStream.Append(o);
                o.uv = float2( 1,-1); o.pos = p1; triStream.Append(o);
                o.uv = float2(-1, 1); o.pos = p3; triStream.Append(o);
                o.uv = float2( 1, 1); o.pos = p2; triStream.Append(o);
                triStream.RestartStrip();
            }

            fixed4 frag (g2f i) : SV_Target
            {
                float d = length(i.uv);
                // Kreis statt Quadrat (glatterer Punkt)
                float alpha = smoothstep(1.0, 0.8, d);
                fixed4 c = _Color;
                c.a *= alpha;
                return c;
            }
            ENDCG
        }
    }
}