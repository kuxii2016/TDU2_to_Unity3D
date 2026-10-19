// Interieur-Ausschnitt: solange man in einem Interieur ist, werden Welt-Pixel (Gelände, Wasser, LOD-/Nachbarobjekte) innerhalb
// des Interieur-Quaders verworfen (TDU2Interiors setzt die globalen Werte). Materialien des Interieurs selbst: _TDU2NoClip = 1.
#ifndef TDU2_INTERIOR_CLIP
#define TDU2_INTERIOR_CLIP
float4x4 _TDU2ClipMatrix;   // Welt -> Quader-Raum (Mitte = Ursprung)
float4 _TDU2ClipExt;        // xyz = halbe Kantenlängen, w = aktiv (1)
inline void TDU2InteriorClip(float3 worldPos, half noClip)
{
    if (_TDU2ClipExt.w > 0.5 && noClip < 0.5)
    {
        float3 l = mul(_TDU2ClipMatrix, float4(worldPos, 1.0)).xyz;
        if (all(abs(l) < _TDU2ClipExt.xyz)) clip(-1);
    }
}
// Welt-Objekte (Gebäude, Glas): nur ausschneiden, wenn TDU2Interiors es für den Raum verlangt (_TDU2ClipObjects = 1),
// sonst fehlen bei Interieurs mit Außenbereich Teile der Außengebäude. Gelände, Wasser, Gras und Vegetation immer.
float _TDU2ClipObjects;
inline void TDU2InteriorClipObject(float3 worldPos, half noClip)
{
    if (_TDU2ClipObjects > 0.5) TDU2InteriorClip(worldPos, noClip);
}
#endif
