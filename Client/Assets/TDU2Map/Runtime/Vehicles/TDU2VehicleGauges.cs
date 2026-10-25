using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Instrumentennadeln aus der Interieur-Bank: ND_SPD_1 / ND_RPM_1 sind die Nadel-Meshes in Ruhelage (0),
    /// ND_SPD_2 / ND_RPM_2 (ohne Mesh) die Endlage, D_ND_SPD / D_ND_RPM der Drehpunkt.
    /// Skalen aus frontend/&lt;res&gt;/gauges/&lt;_hud_file_name&gt;.bnk -> hud.ini ("AddHud interior_kmh 0 420 0 264.5":
    /// Wert 0..420 entspricht 0..264.5°). Gedreht wird um die Achse Ruhelage -> Referenzlage (_2).
    /// Ohne hud.ini: Drehzahl = rpm_max, Tacho = Vmax (+5 %, 20er-Schritte), 264.5°.
    /// </summary>
    public sealed class TDU2VehicleGauges : MonoBehaviour
    {
        public TDU2Vehicle Vehicle;
        public float SpeedScaleMax = 360f, RpmScaleMax = 10000f;
        public float NeedleDamping = 12f;

        sealed class Needle
        {
            public Transform T; public Quaternion Rest; public Vector3 Axis; public float Value;
            public float ValMin, ValMax = 1f, OriMin, OriMax = 264.5f;   // aus hud.ini
        }
        bool _mph;
        Needle _spd, _rpm;

        public void Setup(TDU2Vehicle v, CarModel model, GaugeConfig cfg = null)
        {
            Vehicle = v;
            _mph = v?.Car != null && !v.Car.Kmh;
            if (v?.Car != null)
            {
                RpmScaleMax = v.Car.RpmMax > 1000 ? v.Car.RpmMax : 8000f;
                float vmax = v.Car.MaxTheoricalSpeed > 50 ? v.Car.MaxTheoricalSpeed : 260f;
                SpeedScaleMax = Mathf.Ceil(vmax * 1.05f / 20f) * 20f;
            }
            if (model == null) return;
            _spd = Make(model, "ND_SPD_1", "ND_SPD_2");
            _rpm = Make(model, "ND_RPM_1", "ND_RPM_2");
            // Skalen: hud.ini (val_min val_max ori_min ori_max), sonst Datenbankwerte
            Configure(_spd, cfg, _mph ? "interior_mph" : "interior_kmh", 0f, _mph ? SpeedScaleMax / 1.609f : SpeedScaleMax);
            Configure(_rpm, cfg, "interior_rpm", 0f, RpmScaleMax);
        }

        static void Configure(Needle n, GaugeConfig cfg, string type, float vmin, float vmax)
        {
            if (n == null) return;
            if (cfg != null && cfg.TryGet(type, out var g)) { n.ValMin = g.valMin; n.ValMax = g.valMax; n.OriMin = g.oriMin; n.OriMax = g.oriMax; }
            else { n.ValMin = vmin; n.ValMax = vmax; n.OriMin = 0f; n.OriMax = 264.5f; }
        }

        static Needle Make(CarModel m, string a, string b)
        {
            var t1 = m.Find(a); var t2 = m.Find(b);
            if (t1 == null) return null;
            var n = new Needle { T = t1, Rest = t1.localRotation, Axis = Vector3.forward };
            if (t2 != null)
            {
                var r2 = t2.localRotation;
                if (t2.parent != t1.parent && t2.parent != null && t1.parent != null)
                    r2 = Quaternion.Inverse(t1.parent.rotation) * t2.rotation;
                var delta = r2 * Quaternion.Inverse(n.Rest);
                delta.ToAngleAxis(out float ang, out Vector3 axis);
                if (ang > 180f) { ang = 360f - ang; axis = -axis; }
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
                // Drehachse/-richtung: von der Ruhelage (_1) zur Referenzlage (_2)
                n.Axis = axis.normalized;
            }
            return n;
        }

        void LateUpdate()
        {
            if (Vehicle == null) return;
            float dt = Time.deltaTime;
            Apply(_spd, _mph ? Vehicle.SpeedKmh / 1.609f : Vehicle.SpeedKmh, dt);
            Apply(_rpm, Vehicle.Rpm, dt);
        }

        void Apply(Needle n, float value, float dt)
        {
            if (n == null || n.T == null) return;
            float t = Mathf.Clamp01((value - n.ValMin) / Mathf.Max(1e-3f, n.ValMax - n.ValMin));
            n.Value = Mathf.Lerp(n.Value, t, 1f - Mathf.Exp(-NeedleDamping * dt));
            n.T.localRotation = Quaternion.AngleAxis(Mathf.Lerp(n.OriMin, n.OriMax, n.Value), n.Axis) * n.Rest;
        }
    }
}
