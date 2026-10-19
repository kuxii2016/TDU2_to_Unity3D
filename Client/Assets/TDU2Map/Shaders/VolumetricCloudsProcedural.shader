Shader "TDU2/VolumetricClouds"
{
    Properties
    {
        _Coverage      ("Cloud Coverage", Range(0,1)) = 0.45
        _Density       ("Cloud Density", Range(0,1)) = 0.25
        _Scale         ("Base Scale", Float) = 2.5
        _WorleyScale   ("Worley Scale", Float) = 4.5
        _DetailScale   ("Detail Scale", Float) = 9
        _Octaves       ("FBM Octaves", Range(2,4)) = 3

        _CloudMinY     ("Bottom", Range(0,1)) = 0.0
        _CloudMaxY     ("Top", Range(0,1)) = 1.0
        _HeightFalloff ("Height Falloff", Range(0.8,3)) = 1.6
        _Skew          ("Vertical Skew", Range(0,0.25)) = 0.05

        _WindSpeed     ("Wind Speed", Float) = 0.08
        _WindDir       ("Wind Direction (XZ)", Vector) = (1,0,0.5,0)

        _SunColor      ("Sun Color", Color) = (1,0.995,0.97,1)
        _AmbientColor  ("Ambient Color", Color) = (0.48,0.58,0.72,1)
        _Extinction    ("Extinction", Range(0.8,3.5)) = 1.6
        _Scattering    ("Forward Scattering", Range(0,0.9)) = 0.42
        _PhaseG        ("Anisotropy (HG)", Range(-0.3,0.9)) = 0.65
        _SilverLining  ("Silver Lining", Range(0,0.2)) = 0.05
        _DarkEdge      ("Edge Darkening", Range(0,0.3)) = 0.12

        _Steps         ("Steps", Range(8,14)) = 10
        _StepSize      ("Step Size", Range(0.02,0.08)) = 0.05
        _Opacity       ("Opacity", Range(0,1)) = 1.0
        _Dither        ("Dither", Range(0,1)) = 0.8
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 camPosOS : TEXCOORD0;
                float3 rayDirOS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
            };

            fixed4 _SunColor;
            fixed4 _AmbientColor;

            float _Coverage;
            float _Density;
            float _Scale;
            float _WorleyScale;
            float _DetailScale;
            int   _Octaves;

            float _CloudMinY;
            float _CloudMaxY;
            float _HeightFalloff;
            float _Skew;

            float _WindSpeed;
            float4 _WindDir;

            float _Extinction;
            float _Scattering;
            float _PhaseG;
            float _SilverLining;
            float _DarkEdge;

            int   _Steps;
            float _StepSize;
            float _Opacity;
            float _Dither;

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898,78.233))) * 43758.5453);
            }

            float Hash3(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // 3D Worley Noise (Cellular) - Basis für die zerfetzten Wolkenränder
            float Worley3D(float3 p)
            {
                float3 id = floor(p);
                float3 f  = frac(p);

                float minDist = 1.0;

                for(int x = -1; x <= 1; x++)
                {
                    for(int y = -1; y <= 1; y++)
                    {
                        for(int z = -1; z <= 1; z++)
                        {
                            float3 offset = float3(x,y,z);
                            float3 h = Hash3(id + offset);
                            float3 pos = offset + h;
                            float3 d = pos - f;
                            minDist = min(minDist, dot(d,d));
                        }
                    }
                }
                return sqrt(minDist);
            }

            float FBM3(float3 p)
            {
                float sum = 0.0;
                float amp = 0.5;
                float freq = 1.0;
                for(int i = 0; i < _Octaves; i++)
                {
                    sum += amp * Hash3(floor(p*freq) + frac(p*freq)*0.0);
                    // Besserer Value-Look mit Interpolation
                    sum += amp * Hash3(floor(p*freq) + float3(0.5,0.5,0.5));
                    amp *= 0.5;
                    freq *= 2.0;
                }
                return saturate(sum * 0.5);
            }

            // Einfacheres, stabiles FBM (Value)
            float ValueFBM(float3 p)
            {
                float sum = 0.0;
                float amp = 0.5;
                float freq = 1.0;
                for(int i = 0; i < _Octaves; i++)
                {
                    float3 i3 = floor(p * freq);
                    float3 f3 = frac(p * freq);
                    f3 = f3*f3*(3.0-2.0*f3);

                    float n000 = Hash3(i3 + float3(0,0,0));
                    float n100 = Hash3(i3 + float3(1,0,0));
                    float n010 = Hash3(i3 + float3(0,1,0));
                    float n110 = Hash3(i3 + float3(1,1,0));
                    float n001 = Hash3(i3 + float3(0,0,1));
                    float n101 = Hash3(i3 + float3(1,0,1));
                    float n011 = Hash3(i3 + float3(0,1,1));
                    float n111 = Hash3(i3 + float3(1,1,1));

                    float nx00 = lerp(n000,n100,f3.x);
                    float nx10 = lerp(n010,n110,f3.x);
                    float nx01 = lerp(n001,n101,f3.x);
                    float nx11 = lerp(n011,n111,f3.x);
                    float nxy0 = lerp(nx00,nx10,f3.y);
                    float nxy1 = lerp(nx01,nx11,f3.y);
                    float n    = lerp(nxy0,nxy1,f3.z);

                    sum += amp * n;
                    amp *= 0.5;
                    freq *= 2.0;
                }
                return sum;
            }

            float HeightGrad(float y)
            {
                float t = (y - _CloudMinY) / max(_CloudMaxY - _CloudMinY, 0.0001);
                if(t <= 0.0 || t >= 1.0) return 0.0;
                float bottom = smoothstep(0.0, 0.24, t);
                float top    = smoothstep(1.0, 0.36, t);
                float mid    = pow(t * (1.0-t) * 4.0, _HeightFalloff);
                return saturate(bottom * top * mid);
            }

            float CloudDensity(float3 pOS)
            {
                float time = _Time.y;
                float3 wind = float3(_WindDir.x, 0.0, _WindDir.z) * _WindSpeed;

                // Base Shape (FBM) - Grundstruktur
                float3 pBase = pOS * _Scale + wind * time * 0.8;
                float baseShape = ValueFBM(pBase);
                baseShape = saturate(baseShape);

                // Worley Noise - erzeugt die Löcher und zerfetzten Ränder (wichtig!)
                float3 pWorley = pOS * _WorleyScale - wind * time * 0.4;
                float worley = Worley3D(pWorley);
                worley = 1.0 - worley; // Invertieren, damit Löcher entstehen

                // Detail FBM für feine Wisps
                float3 pDetail = pOS * _DetailScale + wind * time * 0.6;
                float detail = ValueFBM(pDetail) * 0.25;

                // Classic Cloud Shape: Perlin - Worley
                // So entstehen die charakteristischen Wolkenfetzen
                float shape = baseShape * 0.7 + worley * 0.5 + detail;
                shape = saturate(shape);

                // Coverage (Erosion) - reduziert Dichte und reißt Löcher
                float coverage = _Coverage;
                float erode = smoothstep(coverage, coverage + 0.15, shape);
                if(erode <= 0.0001) return 0.0;

                float hg = HeightGrad(pOS.y);
                if(hg <= 0.0001) return 0.0;

                // Vertikale Verzerrung
                float skewNoise = ValueFBM(pOS * (_Scale * 0.3) + wind * time * 0.3);
                float skew = (skewNoise - 0.5) * _Skew;
                hg = saturate(hg + skew);

                // Endgültige Dichte
                float dens = erode * hg * _Density;
                dens = pow(saturate(dens), 0.85); // Etwas knackigere Ränder

                return saturate(dens);
            }

            float HGPhase(float cosTheta, float g)
            {
                float g2 = g*g;
                float denom = 4.0 * UNITY_PI * pow(1.0 + g2 - 2.0*g*cosTheta, 1.5);
                return (1.0 - g2) / denom;
            }

            bool RayAABB(float3 ro, float3 rd, out float t0, out float t1)
            {
                float3 bmin = float3(-0.5,-0.5,-0.5);
                float3 bmax = float3(0.5,0.5,0.5);
                float3 inv = 1.0 / rd;
                float3 tbot = (bmin-ro)*inv;
                float3 ttop = (bmax-ro)*inv;
                float3 tminv = min(tbot,ttop);
                float3 tmaxv = max(tbot,ttop);
                t0 = max(tminv.x,max(tminv.y,tminv.z));
                t1 = min(tmaxv.x,min(tmaxv.y,tmaxv.z));
                return t1 > t0;
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 camWS    = _WorldSpaceCameraPos;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 rayWS    = normalize(worldPos - camWS);
                o.camPosOS      = mul(unity_WorldToObject, float4(camWS,1)).xyz;
                o.rayDirOS      = normalize(mul(unity_WorldToObject, float4(rayWS,0)).xyz);
                o.screenPos     = ComputeScreenPos(o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 ro = i.camPosOS;
                float3 rd = i.rayDirOS;

                float t0,t1;
                if(!RayAABB(ro,rd,t0,t1)) return fixed4(0,0,0,0);
                if(t0 < 0.0) t0 = 0.0;
                float dist = t1-t0;
                if(dist < 0.001) return fixed4(0,0,0,0);

                float3 entry = ro + rd*t0;
                float3 lightDirOS = normalize(mul(unity_WorldToObject, float4(_WorldSpaceLightPos0.xyz,0)).xyz);

                float2 uv = i.screenPos.xy / i.screenPos.w;
                float dither = Hash(uv * _ScreenParams.xy)*2.0-1.0;
                float trav = dither * _StepSize * _Dither;

                float trans = 1.0;
                float3 acc  = float3(0,0,0);

                float cosTheta = dot(rd, lightDirOS);
                float phase    = HGPhase(cosTheta, _PhaseG);

                for(int s=0; s<_Steps; s++)
                {
                    if(trav >= dist) break;
                    float3 p = entry + rd*trav;
                    trav += _StepSize;

                    float d = CloudDensity(p);
                    if(d > 0.0005)
                    {
                        float dt = d * _StepSize * _Extinction;
                        float stepTrans = exp(-dt);
                        float3 sunContrib  = _SunColor.rgb * phase * (1.0 - stepTrans) * _Scattering;
                        float3 ambContrib  = _AmbientColor.rgb * (1.0 - stepTrans) * 0.22;
                        acc += (sunContrib + ambContrib) * trans;
                        trans *= stepTrans;

                        if(trans < 0.01) break;
                    }
                }

                float alpha = 1.0 - trans;
                alpha = saturate(alpha * _Opacity);
                if(alpha < 0.001) return fixed4(0,0,0,0);

                return fixed4(acc, alpha);
            }
            ENDCG
        }
    }
}