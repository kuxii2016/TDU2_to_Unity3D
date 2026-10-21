using UnityEngine;

namespace TDU2.Map.World
{
    /// <summary>
    /// World Actor als umfallendes Objekt wie im Spiel (worldactors_*.dhk: dynamischer Körper an einer zerbrechlichen
    /// Verankerung; Schilder/Poller 11-20 kg, Ampeln 120 kg, Bänke 3500 kg). Bis zum Anstoß kinematisch (steht fest).
    /// Gelöst wird beim Aufprall (OnCollisionEnter, relativeVelocity = Geschwindigkeit VOR dem Stoß) und schon vorher
    /// über einen etwas größeren Auslöser. Weil ein kinematischer Körper für PhysX unendlich schwer ist, bekommt das
    /// Fahrzeug nach dem Lösen seine Geschwindigkeit von vor dem Stoß zurück (abzüglich des Impulses, den ein
    /// 15-120-kg-Teil wirklich aufnimmt) – sonst steht das Auto an einem Poller wie an einer Mauer.
    /// </summary>
    public sealed class WorldActorBody : MonoBehaviour
    {
        public float BreakSpeed = 2f;
        Rigidbody _rb;
        Collider _trigger;
        bool _free;

        /// <summary>Körper für eine Actor-Instanz anlegen. Bänke und große Teile bleiben statisch.</summary>
        public static void Physicalize(GameObject go, string actorName)
        {
            if (go == null) return;
            string n = (actorName ?? "").ToUpperInvariant();
            Bounds? lb = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                var wb = r.bounds;
                var c = go.transform.InverseTransformPoint(wb.center);
                var e = go.transform.InverseTransformVector(wb.extents);
                var b = new Bounds(c, new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * 2f);
                if (lb == null) lb = b; else { var x = lb.Value; x.Encapsulate(b); lb = x; }
            }
            if (lb == null) return;
            var bb = lb.Value;
            // Hohe Teile (Ampeln mit Ausleger, Schilderbrücken): Kollision nur so breit wie der Mast unten (Fußabdruck der
            // untersten 1.2 m) – sonst sperrt der Ausleger über der Straße die ganze Fahrspur.
            if (bb.size.y > 2.2f && Footprint(go, bb.min.y + 1.2f, out var fp))
            {
                var c = bb.center; var sz = bb.size;
                c.x = fp.center.x; c.z = fp.center.z;
                sz.x = Mathf.Max(0.12f, fp.size.x + 0.05f); sz.z = Mathf.Max(0.12f, fp.size.z + 0.05f);
                bb = new Bounds(c, sz);
            }
            var size = new Vector3(Mathf.Max(0.08f, bb.size.x), Mathf.Max(0.08f, bb.size.y), Mathf.Max(0.08f, bb.size.z));

            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;   // Radstrahlen treffen sie nicht
            // Unterkante 8 cm anheben: steckt sonst im Boden-Collider und springt beim Lösen hoch.
            // Konvexer Box-MeshCollider statt BoxCollider: die Karte ist gespiegelt (negative Skalierung), das kann BoxCollider
            // nicht ("BoxCollider does not support negative scale") -> falsche/fehlende Kollision.
            float lift = Mathf.Min(0.08f, size.y * 0.2f);
            var sc = bb.center + new Vector3(0f, lift * 0.5f, 0f);
            var ss = new Vector3(size.x, size.y - lift, size.z);
            var solid = go.AddComponent<MeshCollider>();
            solid.sharedMesh = BoxMesh(sc, ss);
            solid.convex = true;

            bool heavy = n.StartsWith("SF_BEN") || n.StartsWith("SF_BNC") || size.x * size.y * size.z > 6f;
            if (heavy) return;

            var body = go.AddComponent<WorldActorBody>();
            var trig = go.AddComponent<MeshCollider>();
            trig.sharedMesh = BoxMesh(sc, ss + new Vector3(1.2f, 0.2f, 1.2f));
            trig.convex = true;
            trig.isTrigger = true;
            body._trigger = trig;
            body._rb = go.AddComponent<Rigidbody>();
            body._rb.isKinematic = true;
            body._rb.interpolation = RigidbodyInterpolation.None;
            body._rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            float vol = size.x * size.y * size.z;
            body._rb.mass = n.StartsWith("SF_XIN") ? 120f : n.StartsWith("RS_") || n.StartsWith("SF_PANOR") || n.StartsWith("SF_STUD") ? 15f : Mathf.Clamp(vol * 80f, 5f, 150f);
            body.BreakSpeed = n.StartsWith("SF_XIN") ? 4f : 1.5f;
        }

        static readonly System.Collections.Generic.Dictionary<(Vector3Int, Vector3Int), Mesh> _boxes = new System.Collections.Generic.Dictionary<(Vector3Int, Vector3Int), Mesh>();

        /// <summary>Quader als Mesh (für konvexe MeshCollider), nach Mitte/Größe auf 2 cm gerastert geteilt.</summary>
        static Mesh BoxMesh(Vector3 c, Vector3 s)
        {
            const float q = 50f;
            var k = new Vector3Int(Mathf.RoundToInt(c.x * q), Mathf.RoundToInt(c.y * q), Mathf.RoundToInt(c.z * q));
            var k2 = new Vector3Int(Mathf.RoundToInt(s.x * q), Mathf.RoundToInt(s.y * q), Mathf.RoundToInt(s.z * q));
            var key = (k, k2);
            if (_boxes.TryGetValue(key, out var m) && m != null) return m;
            var h = s * 0.5f;
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++)
                v[i] = c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
            m = new Mesh { name = "TDU2_ActorBox", hideFlags = HideFlags.DontSave };
            m.vertices = v;
            m.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            m.RecalculateBounds();
            _boxes[key] = m;
            return m;
        }

        /// <summary>x/z-Ausdehnung (im Raum von go) aller Mesh-Vertices unterhalb der Höhe yMax.</summary>
        static bool Footprint(GameObject go, float yMax, out Bounds fp)
        {
            fp = default; bool any = false;
            var toLocal = go.transform.worldToLocalMatrix;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var r = mf.GetComponent<Renderer>();
                if (r != null && !r.enabled) continue;
                var m = toLocal * mf.transform.localToWorldMatrix;
                var v = mesh.vertices;
                for (int i = 0; i < v.Length; i++)
                {
                    var p = m.MultiplyPoint3x4(v[i]);
                    if (p.y > yMax) continue;
                    if (!any) { fp = new Bounds(p, Vector3.zero); any = true; } else fp.Encapsulate(p);
                }
            }
            return any;
        }

        static Vector3 Vel(Rigidbody r)
        {
#if UNITY_6000_0_OR_NEWER
            return r.linearVelocity;
#else
            return r.velocity;
#endif
        }
        static void SetVel(Rigidbody r, Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            r.linearVelocity = v;
#else
            r.velocity = v;
#endif
        }

        void OnTriggerEnter(Collider other) => TryTrigger(other);
        void OnTriggerStay(Collider other) => TryTrigger(other);

        void TryTrigger(Collider other)
        {
            if (_free || _rb == null || other == null) return;
            var orb = other.attachedRigidbody;
            if (orb == null || orb.isKinematic || orb == _rb) return;
            var v = Vel(orb);
            if (v.magnitude < BreakSpeed) return;
            // nur, wenn das Fahrzeug auf das Teil zu fährt
            var to = _rb.worldCenterOfMass - orb.worldCenterOfMass; to.y = 0f;
            if (to.sqrMagnitude > 1e-4f && Vector3.Dot(v, to) <= 0f) return;
            Break(v);
        }

        void OnCollisionEnter(Collision c)
        {
            if (_free || _rb == null || c == null) return;
            var orb = c.rigidbody;
            if (orb == null || orb.isKinematic || orb == _rb) return;
            // Vor-Stoß-Geschwindigkeit: relativeVelocity, Korrektur über den Impuls (Vorzeichen: die betragsgrößere Variante)
            var post = Vel(orb);
            var dv = c.impulse / Mathf.Max(1f, orb.mass);
            var pre = (post + dv).sqrMagnitude > (post - dv).sqrMagnitude ? post + dv : post - dv;
            float rel = c.relativeVelocity.magnitude;
            if (Mathf.Max(rel, pre.magnitude) < BreakSpeed) return;
            // Impuls, den das leichte Teil tatsächlich aufnimmt: m_teil / (m_auto + m_teil) des Stoßes
            float k = _rb.mass / (orb.mass + _rb.mass);
            var n = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.zero;
            float into = Mathf.Abs(Vector3.Dot(pre, n));
            SetVel(orb, pre - n * (into * k * Mathf.Sign(Vector3.Dot(pre, n))));
            Break(pre);
        }

        void Break(Vector3 hitVel)
        {
            _free = true;
            if (_trigger != null) Destroy(_trigger);
            _rb.isKinematic = false;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Teil wird mitgerissen und kippt (Drehachse quer zur Stoßrichtung)
            var h = hitVel; h.y = 0f;
            SetVel(_rb, h * 0.9f + Vector3.up * Mathf.Min(2f, h.magnitude * 0.15f));
            var axis = Vector3.Cross(Vector3.up, h.sqrMagnitude > 1e-4f ? h.normalized : transform.forward);
            _rb.angularVelocity = axis * Mathf.Clamp(h.magnitude * 0.4f, 2f, 10f) + Random.insideUnitSphere;
            _rb.WakeUp();
            Destroy(gameObject, 90f);   // umgefahrene Teile nach einer Minute aufräumen (Sektor lädt sie neu)
        }
    }
}
