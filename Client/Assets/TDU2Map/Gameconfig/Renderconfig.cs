public class Renderconfig
{
    public static float VertexColorStrength = 0.5f;
    public static float ShadowMapStrength = 0.6f;
    /// <summary>Additive Leucht-Materialien (Neon, Halos, Lampenschein). Im Spiel nur nachts sichtbar -> standardmäßig aus.</summary>
    public static bool ShowGlowMaterials = true;
    public static float GlowIntensity = 0.55f;
    /// <summary>Helligkeit der Lampen-Halos (Billboards).</summary>
    public static float HaloIntensity = 1f;
    /// <summary>Wind für Laub (TDU2/Foliage).</summary>
    public static float WindStrength = 1f;
    /// <summary>Gebäude/Objekte: Helligkeitsfaktor und Wrap-Lighting (0 = reines Lambert).</summary>
    public static float ObjectBrightness = 1f, WrapLighting = 0.35f;
    /// <summary>
    /// World Lighning/Lamps
    /// </summary>

    /// <summary>Gesamthelligkeit (Sonne + Ambient) der Szene. Im Spiel wird das über die LUT-Farbkorrektur gesteuert.</summary>
    public static float Exposure = 0.75f;
    /// <summary>Ambient-Anteil (1 = Original). Im Spiel wird das über die LUT-Farbkorrektur gesteuert.</summary>
    public static float AmbientScale = 1f;
}
