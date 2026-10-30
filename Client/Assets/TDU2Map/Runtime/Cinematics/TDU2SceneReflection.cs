using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Cinematics
{
    /// <summary>
    /// Echtzeit-Reflection-Probe an der Kamera für Szenen ohne Spieler-Auto (Titel-Kino, Story-Szenen). Sonst gibt es keine Probe
    /// in der Welt (die des Spieler-Autos fehlt) und Lack, Glas und Wasser (Meer/Pool spiegeln unity_SpecCube0) werden schwarz
    /// bzw. glanzlos. Referenzgezählt: <see cref="Acquire"/> / <see cref="Release"/>.
    /// </summary>
    public sealed class TDU2SceneReflection : MonoBehaviour
    {
        static TDU2SceneReflection _inst;
        static int _refs;
        ReflectionProbe _probe;

        public static void Acquire()
        {
            _refs++;
            if (_inst != null) return;
            var go = new GameObject("TDU2 Szenen-Reflexion");
            _inst = go.AddComponent<TDU2SceneReflection>();
            var p = go.AddComponent<ReflectionProbe>();
            p.mode = ReflectionProbeMode.Realtime;
            p.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
            p.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            p.resolution = 128;
            p.size = new Vector3(3000f, 1000f, 3000f);   // groß: auch das Meer bis zum Horizont liegt darin
            p.boxProjection = false;
            p.importance = 5;                             // unter der Probe des Spieler-Autos (10)
            p.nearClipPlane = 0.3f;
            p.farClipPlane = 2000f;
            p.cullingMask = ~(1 << 2);
            _inst._probe = p;
        }

        public static void Release()
        {
            _refs = Mathf.Max(0, _refs - 1);
            if (_refs > 0 || _inst == null) return;
            Destroy(_inst.gameObject);
            _inst = null;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) transform.position = cam.transform.position;
        }

        void OnDestroy() { if (_inst == this) { _inst = null; _refs = 0; } }
    }
}
