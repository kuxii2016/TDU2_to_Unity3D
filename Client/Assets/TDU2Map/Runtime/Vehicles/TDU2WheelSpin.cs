using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Räder eines Anzeige-Autos (ohne Physik, z.B. in Kinos: Ausfahrt aus der Garage, Titel-Kino) drehen sich mit der gefahrenen
    /// Strecke: Winkel += Weg / Reifenradius, wie TDU2Vehicle (DISC_xx um die lokale X-Achse).
    /// </summary>
    public sealed class TDU2WheelSpin : MonoBehaviour
    {
        sealed class Wheel { public Transform Disc, Spin2; public Quaternion DiscBase, Spin2Base; public float Radius, Angle; }

        Wheel[] _wheels;
        Vector3 _last;
        bool _haveLast;

        public static TDU2WheelSpin Attach(GameObject go, CarModel model, CarAssets assets)
        {
            if (go == null || model == null) return null;
            var list = new System.Collections.Generic.List<Wheel>();
            bool bike = assets?.Car != null && assets.Car.IsBike;
            foreach (var n in bike ? new[] { "F", "R" } : new[] { "FL", "FR", "RL", "RR" })
            {
                bool front = n[0] == 'F';
                Transform disc = bike ? (model.Find("D_RIM_" + n) ?? model.Find("RIM_" + n)) : (model.Find("DISC_" + n) ?? model.Find("D_RIM_" + n));
                Transform spin2 = bike ? model.Find("TIRE_" + n) : null;
                if (disc == null && spin2 == null) continue;
                float r = assets?.Rim != null ? assets.Rim.TireRadius(front) : 0.33f;
                list.Add(new Wheel
                {
                    Disc = disc, Spin2 = spin2,
                    DiscBase = disc != null ? disc.localRotation : Quaternion.identity,
                    Spin2Base = spin2 != null ? spin2.localRotation : Quaternion.identity,
                    Radius = Mathf.Max(0.15f, r),
                });
            }
            if (list.Count == 0) return null;
            var c = go.GetComponent<TDU2WheelSpin>();
            if (c == null) c = go.AddComponent<TDU2WheelSpin>();
            c._wheels = list.ToArray();
            c._haveLast = false;
            return c;
        }

        void OnEnable() { _haveLast = false; FloatingOrigin.Shifted += OnShift; }
        void OnDisable() { FloatingOrigin.Shifted -= OnShift; }
        void OnShift(Vector3 delta) { _last -= delta; }

        void LateUpdate()
        {
            if (_wheels == null) return;
            var p = transform.position;
            if (!_haveLast) { _last = p; _haveLast = true; return; }
            var d = p - _last;
            _last = p;
            // Schnitt/Sprung (Kino-Schnitt, Neustart der Schleife): nicht drehen
            if (d.sqrMagnitude > 25f) return;
            float dist = Vector3.Dot(d, transform.forward);
            if (Mathf.Abs(dist) < 1e-5f) return;
            foreach (var w in _wheels)
            {
                w.Angle = Mathf.Repeat(w.Angle + dist / w.Radius * Mathf.Rad2Deg, 360f);
                if (w.Disc != null) w.Disc.localRotation = w.DiscBase * Quaternion.AngleAxis(w.Angle, Vector3.right);
                if (w.Spin2 != null) w.Spin2.localRotation = w.Spin2Base * Quaternion.AngleAxis(w.Angle, Vector3.right);
            }
        }
    }
}
