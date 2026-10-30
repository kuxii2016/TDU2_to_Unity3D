using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TDU2
{
    /// <summary>
    /// Floating Origin gegen Zittern bei großen Koordinaten (Inseln ~35 km, float hat bei 16 km nur noch ~2 mm Auflösung):
    /// Entfernt sich die Kamera mehr als <see cref="Threshold"/> vom Ursprung, wird die ganze Szene (alle Root-Objekte,
    /// Rigidbodies) so verschoben, dass die Kamera wieder bei (0, y, 0) steht. Komponenten mit eigenen Weltkoordinaten
    /// hängen sich an <see cref="Shifted"/> (Argument: Verschiebung, die von allen Weltpositionen abgezogen wurde).
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class FloatingOrigin : MonoBehaviour
    {
        public float Threshold = 1000f;
        public Transform Focus;

        /// <summary>delta = alte Position − neue Position (wurde von allen Weltkoordinaten abgezogen).</summary>
        public static event Action<Vector3> Shifted;
        /// <summary>Summe aller Verschiebungen (Weltposition "wie ohne Floating Origin" = aktuelle + Offset).</summary>
        public static Vector3 TotalOffset { get; private set; }

        // Interpolation kurz aus, sonst interpoliert Unity einen Frame lang zwischen alter und neuer Position
        static readonly System.Collections.Generic.List<(Rigidbody rb, RigidbodyInterpolation mode)> _restore =
            new System.Collections.Generic.List<(Rigidbody, RigidbodyInterpolation)>();
        static bool _restorePending;

        void FixedUpdate()
        {
            if (!_restorePending) return;
            _restorePending = false;
            foreach (var (rb, mode) in _restore) if (rb != null) rb.interpolation = mode;
            _restore.Clear();
        }

        // In Update (Reihenfolge -10000, vor allen anderen Skripten) statt LateUpdate: Bäume/Gras werden in ihrem Update per
        // Graphics.DrawMesh mit Weltmatrizen eingereiht – eine Verschiebung erst in LateUpdate zeichnete sie für ein Bild
        // ~1 km daneben (Flackern/Blitzen beim Verschieben). Kamera, Effekte, Spuren laufen danach mit den neuen Koordinaten.
        void Update()
        {
            var f = Focus != null ? Focus : (Camera.main != null ? Camera.main.transform : null);
            if (f == null) return;
            var p = f.position;
            p.y = 0f;
            if (p.magnitude < Threshold) return;
            Shift(p);
        }

        public static void Shift(Vector3 delta)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                root.transform.position -= delta;
#if UNITY_2023_1_OR_NEWER
            var bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
#else
            var bodies = FindObjectsOfType<Rigidbody>();
#endif
            foreach (var rb in bodies)
            {
                if (rb.gameObject.scene != scene) continue;
                if (rb.interpolation != RigidbodyInterpolation.None) { _restore.Add((rb, rb.interpolation)); rb.interpolation = RigidbodyInterpolation.None; }
                rb.position = rb.transform.position;
            }
            Physics.SyncTransforms();
            _restorePending = _restore.Count > 0;
            TotalOffset += delta;
            ResetCameraHistory();
            Shifted?.Invoke(delta);
        }

        /// <summary>
        /// Kamera springt um die Verschiebung: Verlauf von TAA/Bewegungsunschärfe (PostProcessLayer.ResetHistory) verwerfen,
        /// sonst blitzt ein Bild mit Bewegungsvektoren über 1 km auf. Per Reflexion – kein Verweis auf das Post-Processing-Paket.
        /// </summary>
        static void ResetCameraHistory()
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                foreach (var mb in cam.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || mb.GetType().Name != "PostProcessLayer") continue;
                    try { mb.GetType().GetMethod("ResetHistory", Type.EmptyTypes)?.Invoke(mb, null); } catch { }
                }
            }
        }
    }
}
