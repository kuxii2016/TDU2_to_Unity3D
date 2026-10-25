using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.World
{
    /// <summary>
    /// Welt-Beleuchtung wie im Original (TD_Building/TD_Vegetation/TD_HeightMap, Gamma-Raum):
    ///   Farbe = Albedo x lerp(Ambient, Sonne, N.L x Schatten)
    /// Unity rechnet Albedo x (Ambient + Licht x N.L x Schatten). Mit Licht = Sonne − Ambient ist das genau dieselbe Formel.
    /// Werte: sonniger Mittag aus den Wetterdaten (sun_diffuse 2.01/1.74/1.36, sun_ambient 0.58/0.67/0.82).
    /// Die Szene stand auf Skybox-Umgebungslicht ohne gebackene Daten -> zur Laufzeit fast kein Ambient (schwarze Schattenseiten).
    /// </summary>
    public sealed class TDU2WorldLighting : MonoBehaviour
    {
        // Die Wetterwerte (Sonne 2.01/1.74/1.36, Ambient 0.58/0.67/0.82) setzen die Farbkorrektur (photoshop_*-Parameter, LUT)
        // des Spiels voraus; ohne sie wird alles überbelichtet und orange. Daher gedämpfte, fast neutrale Werte.
        public Color SunDiffuse = new Color(1.30f, 1.24f, 1.12f, 1f);
        public Color Ambient = new Color(0.46f, 0.49f, 0.56f, 1f);
        [Tooltip("Gesamthelligkeit")]
        [Range(0.3f, 1.5f)] public float Exposure = Renderconfig.Exposure;
        [Tooltip("Ambient-Anteil (1 = Original)")]
        [Range(0f, 2f)] public float AmbientScale = Renderconfig.AmbientScale;

        Light _sun;
        bool _applied;

        void Start() => Apply();

        /// <summary>Umgebungslicht und Sonnenfarbe setzen (Interieurs sichern/stellen das beim Betreten/Verlassen selbst zurück).</summary>
        public void Apply()
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { _sun = l; break; }
            var amb = Ambient * AmbientScale * Exposure; amb.a = 1f;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = amb;
            RenderSettings.ambientIntensity = 1f;
            if (_sun != null)
            {
                var d = (SunDiffuse * Exposure) - amb;
                float k = Mathf.Max(0.001f, Mathf.Max(d.r, Mathf.Max(d.g, d.b)));
                _sun.color = new Color(Mathf.Max(0f, d.r) / k, Mathf.Max(0f, d.g) / k, Mathf.Max(0f, d.b) / k, 1f);
                _sun.intensity = k;
                if (RenderSettings.sun == null) RenderSettings.sun = _sun;
            }
            _applied = true;
        }

#if UNITY_EDITOR
        void OnValidate() { if (_applied && Application.isPlaying) Apply(); }
#endif
    }
}
