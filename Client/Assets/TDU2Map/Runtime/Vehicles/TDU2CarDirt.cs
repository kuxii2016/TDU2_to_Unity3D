using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Verschmutzung der Karosserie wie im Spiel (DBCarDirtData, Fälle DirtSun / DirtRain / NoDirtRain):
    ///   abseits der Straße steigt die Schmutzstufe mit _bodydirtfactor je Sekunde (Sonne 0.003, Regen 0.02),
    ///   auf der Straße im Regen wäscht _watercleanfactor (0.003/s) sie wieder ab.
    /// Darstellung: common_car.bnk/dirt_scratch.2db (R) über die Lack-Materialien (TDU2/Car: _DirtLevel).
    /// "Stufen" (C_DIRT_%02d im Spiel) = optional gerastert über <see cref="Steps"/>.
    /// Wird von TDU2VehicleManager an jedes Auto gehängt; Stand wird je Fahrzeug (Datei) gemerkt.
    /// </summary>
    public sealed class TDU2CarDirt : MonoBehaviour
    {
        [Range(0f, 1f)] public float Level;
        [Range(0f, 1f)] public float Scratches;
        [Tooltip("Anzahl Schmutzstufen (0 = stufenlos)")]
        public int Steps = 0;
        [Tooltip("Regen: schneller schmutzig abseits der Straße, sauber auf der Straße")]
        public bool Raining;
        [Tooltip("Faktor auf die Original-Raten (1 = wie im Spiel)")]
        public float RateScale = 1f;
        public Color DryColor = new Color(0.45f, 0.38f, 0.28f, 1f);
        public Color WetColor = new Color(0.26f, 0.21f, 0.15f, 1f);

        // DBCarDirtData (db_data.cpr): _bodydirtfactor / _watercleanfactor
        const float DirtSun = 0.003f, DirtRain = 0.02f, CleanRain = 0.003f;

        static readonly Dictionary<string, float> Saved = new Dictionary<string, float>();

        TDU2Vehicle _veh;
        CarModel _model;
        string _key;
        float _shown = -1f, _shownScr = -1f;
        bool _wetShown;

        public void Setup(TDU2Vehicle veh, CarModel model)
        {
            _veh = veh; _model = model;
            _key = veh != null && veh.Car != null ? veh.Car.FileName + "#" + veh.Car.Id : null;
            if (_key != null && Saved.TryGetValue(_key, out var l)) Level = l;
            Apply(true);
        }

        /// <summary>Mitspieler-Auto: Stand kommt aus dem Netz (TDU2Multiplayer), keine eigene Berechnung.</summary>
        public void SetExternal(float level, float scratches, bool wet)
        {
            Level = Mathf.Clamp01(level); Scratches = Mathf.Clamp01(scratches); Raining = wet;
            Apply(false);
        }

        static string KeyOf(CarPhysics car) => car != null ? car.FileName + "#" + car.Id : null;

        /// <summary>Gemerkte Schmutzstufe eines Fahrzeugs (0 = sauber/unbekannt).</summary>
        public static float GetSaved(CarPhysics car)
        {
            var k = KeyOf(car);
            return k != null && Saved.TryGetValue(k, out var l) ? l : 0f;
        }

        /// <summary>Schmutzstufe eines Fahrzeugs setzen (z.B. Waschanlage: 0), live = auch an gerade gefahrenen Autos.</summary>
        public static void SetSaved(CarPhysics car, float level, bool live = false)
        {
            var k = KeyOf(car);
            if (k == null) return;
            Saved[k] = Mathf.Clamp01(level);
            if (!live) return;
            foreach (var d in FindObjectsByType<TDU2CarDirt>(FindObjectsSortMode.None))
                if (d != null && d._key == k) { d.Level = Saved[k]; if (level <= 0f) d.Scratches = 0f; d.Apply(true); }
        }

        /// <summary>Auto waschen (Stufe 0).</summary>
        [ContextMenu("Waschen")]
        public void Wash() { Level = 0f; Scratches = 0f; Apply(true); }

        [ContextMenu("Voll verschmutzt")]
        public void FullDirt() { Level = 1f; Apply(true); }

        void Update()
        {
            if (_veh == null) return;
            float dt = Time.deltaTime;
            if (dt > 0f && _veh.AnyWheelGrounded)
            {
                float speed = Mathf.Abs(_veh.SpeedKmh);
                int off = 0, n = 0;
                foreach (var w in _veh.Wheels)
                {
                    if (!w.Grounded) continue;
                    n++;
                    if (w.Surface != 0 || IsOffroad(w.GroundCollider)) off++;
                }
                float moving = Mathf.Clamp01(speed / 40f);
                if (n > 0 && off > 0)
                    Level += (Raining ? DirtRain : DirtSun) * RateScale * moving * off / n * dt;
                else if (Raining)
                    Level -= CleanRain * RateScale * dt;
                Level = Mathf.Clamp01(Level);
            }
            Apply(false);
            if (_key != null) Saved[_key] = Level;
        }

        /// <summary>Gelände = Terrain-Mesh des Streamers (Straßen liegen als eigene Sektor-Meshes darüber).</summary>
        static bool IsOffroad(Collider c)
        {
            if (c == null) return false;
            var n = c.name;
            return n.IndexOf("Terrain", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Apply(bool force)
        {
            if (_model == null) return;
            float lvl = Steps > 0 ? Mathf.Ceil(Level * Steps - 0.001f) / Steps : Level;
            if (!force && Mathf.Abs(lvl - _shown) < 0.002f && Mathf.Abs(Scratches - _shownScr) < 0.002f && _wetShown == Raining) return;
            _shown = lvl; _shownScr = Scratches; _wetShown = Raining;
            var col = Raining ? WetColor : DryColor;
            foreach (var m in _model.PaintMaterials)
            {
                if (m == null || !m.HasProperty("_DirtLevel")) continue;
                m.SetFloat("_DirtLevel", lvl);
                m.SetFloat("_ScratchLevel", Scratches);
                m.SetColor("_DirtColor", col);
            }
        }
    }
}
