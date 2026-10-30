using UnityEngine;

namespace TDU2.World
{
    /// <summary>Sichtweite der Objekte (Gebäude, Schilder, Bäume …) relativ zur bisherigen Einstellung.</summary>
    public enum RenderDistance { VeryHigh, High, Normal, Low, VeryLow }

    /// <summary>
    /// Objekt-Sichtweite: Faktor auf die LOD-Wechsel (QualitySettings.lodBias, wirkt sofort auf alle LODGroups) und auf die
    /// Distanzen der Bäume/Vegetation (<see cref="VegetationRenderer.DistanceMultiplier"/>).
    /// Very High ×4, High ×2, Normal ×1 (bisherige Werte), Low ÷2, Very Low ÷4. Wird in PlayerPrefs gespeichert.
    /// Die Grafikoption "LOD" (TDU2Options) setzt ihren Grundwert über <see cref="SetBaseLodBias"/>.
    /// </summary>
    public static class TDU2RenderDistance
    {
        const string PrefKey = "TDU2.RenderDistance";
        public static readonly string[] Names = { "Very High (x4)", "High (x2)", "Normal", "Low (/2)", "Very Low (/4)" };

        static RenderDistance _level = RenderDistance.Normal;
        static float _applied = 1f;   // aktuell in QualitySettings.lodBias eingerechneter Faktor
        static bool _loaded;

        public static float MultiplierOf(RenderDistance l)
        {
            switch (l)
            {
                case RenderDistance.VeryHigh: return 4f;
                case RenderDistance.High: return 2f;
                case RenderDistance.Low: return 0.5f;
                case RenderDistance.VeryLow: return 0.25f;
                default: return 1f;
            }
        }

        public static RenderDistance Level
        {
            get { Load(); return _level; }
            set { Load(); _level = value; Apply(); try { PlayerPrefs.SetInt(PrefKey, (int)value); PlayerPrefs.Save(); } catch { } }
        }

        public static float Multiplier => MultiplierOf(Level);

        /// <summary>Grundwert des LOD-Bias (ohne Sichtweiten-Faktor), z.B. aus den Grafikoptionen.</summary>
        public static void SetBaseLodBias(float bias)
        {
            Load();
            _applied = Multiplier;
            QualitySettings.lodBias = bias * _applied;
        }

        static void Apply()
        {
            float m = MultiplierOf(_level);
            if (!Mathf.Approximately(m, _applied))
            {
                QualitySettings.lodBias = QualitySettings.lodBias / Mathf.Max(0.0001f, _applied) * m;
                _applied = m;
            }
            VegetationRenderer.DistanceMultiplier = m;
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            int v = (int)RenderDistance.Normal;
            try { v = PlayerPrefs.GetInt(PrefKey, v); } catch { }
            _level = v >= 0 && v <= (int)RenderDistance.VeryLow ? (RenderDistance)v : RenderDistance.Normal;
        }

        // Spielstart (auch ohne Domain Reload): gespeicherte Stufe einrechnen
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _loaded = false; _applied = 1f; _level = RenderDistance.Normal; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void ApplyOnStart() { Load(); Apply(); }
    }
}
