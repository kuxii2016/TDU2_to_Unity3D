using System;
using System.Text;
using UnityEngine;

namespace TDU2
{
    /// <summary>
    /// Gezielte Diagnose in das Sitzungs-Log (Docs/Logs/tdu2_session.log, Zeilen "EVT  DIAG …"), damit Fehler ohne Screenshots
    /// nachvollziehbar sind: Avatar-Materialien und Gesichtsknochen, Rahmen/Platzierungen der Story-Szenen, Wetter/Uhrzeit,
    /// Kamera (pro Bild nach einem Kino-Start; Änderungen der Kamera nach dem Setzen durch andere Skripte).
    /// </summary>
    public static class TDU2Diag
    {
        public static bool Enabled = true;

        public static void Log(string topic, string text)
        {
            if (!Enabled) return;
            try { TDU2DebugLog.Event("DIAG " + topic + ": " + text); } catch (Exception) { }
        }

        public static string F(Vector3 v) => $"({v.x:0.000}, {v.y:0.000}, {v.z:0.000})";
        /// <summary>Hohe Auflösung (0,01 mm / 0,0001°) für Bild-zu-Bild-Vergleiche der Kamera.</summary>
        public static string F5(Vector3 v) => $"({v.x:0.00000}, {v.y:0.00000}, {v.z:0.00000})";
        public static string F5(Quaternion q) { var e = q.eulerAngles; return $"[{e.x:0.0000}, {e.y:0.0000}, {e.z:0.0000}]"; }

        /// <summary>Datei neben dem Sitzungs-Log (Docs/Logs/&lt;name&gt;), z.B. Gesichtsbilder der Avatare.</summary>
        public static string FilePath(string name)
        {
            string dir;
            try
            {
                var project = System.IO.Directory.GetParent(Application.dataPath)?.Parent;
                dir = project != null ? System.IO.Path.Combine(project.FullName, "Docs", "Logs") : System.IO.Path.Combine(Application.persistentDataPath, "Logs");
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception) { dir = Application.persistentDataPath; }
            return System.IO.Path.Combine(dir, name);
        }
        public static string F(Quaternion q) { var e = q.eulerAngles; return $"[{e.x:0.00}, {e.y:0.00}, {e.z:0.00}]"; }

        // ------------------------------------------------------------------ Kamera
        static Vector3 _setPos;
        static Quaternion _setRot;
        static int _setFrame = -1, _traceFrames, _changes;
        static string _owner = "";
        static bool _hooked;
        static float _lastT = -1f;

        /// <summary>Kino-Skript hat die Kamera gesetzt (in LateUpdate). Protokolliert nach <see cref="TraceCamera"/> jedes Bild.</summary>
        public static void CameraSet(Camera cam, string owner, float cinTime)
        {
            if (!Enabled || cam == null) return;
            if (!_hooked) { _hooked = true; Camera.onPreCull += OnPreCull; }
            _setPos = cam.transform.position; _setRot = cam.transform.rotation; _setFrame = Time.frameCount; _owner = owner;
            if (_traceFrames > 0)
            {
                _traceFrames--;
                Log("CAM", $"{owner} f{Time.frameCount} dt={Time.unscaledDeltaTime:0.00000} t={cinTime:0.00000} dtCin={(_lastT >= 0f ? cinTime - _lastT : 0f):0.00000} pos={F5(_setPos)} rot={F5(_setRot)} fov={cam.fieldOfView:0.000}");
            }
            _lastT = cinTime;
        }

        /// <summary>Die nächsten <paramref name="frames"/> Bilder der Kino-Kamera protokollieren (dazu einmal die Kamera-Komponenten).</summary>
        public static void TraceCamera(int frames, string why)
        {
            if (!Enabled) return;
            _traceFrames = frames; _changes = 0; _lastT = -1f;
            var cam = Camera.main;
            if (cam == null) { Log("CAM", why + ": keine Main Camera"); return; }
            var sb = new StringBuilder();
            foreach (var b in cam.GetComponents<Behaviour>())
            {
                if (b == null) continue;
                sb.Append(b.GetType().Name).Append(b.enabled ? "" : "(aus)");
                var aa = b.GetType().GetField("antialiasingMode");
                if (aa != null) sb.Append("{AA=").Append(aa.GetValue(b)).Append('}');
                sb.Append(", ");
            }
            Log("CAM", $"{why}: Main Camera '{cam.name}' parent={(cam.transform.parent != null ? cam.transform.parent.name : "-")} near={cam.nearClipPlane} far={cam.farClipPlane} vSync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} Bildschirm={Screen.currentResolution.refreshRateRatio.value:0.##} Hz Komponenten: {sb}");
        }

        static void OnPreCull(Camera c)
        {
            if (!Enabled || c == null || c != Camera.main || _setFrame != Time.frameCount) return;
            float d = (c.transform.position - _setPos).magnitude, a = Quaternion.Angle(c.transform.rotation, _setRot);
            if ((d > 1e-4f || a > 0.01f) && _changes < 60)
            {
                _changes++;
                Log("CAM", $"f{Time.frameCount} Kamera nach {_owner} noch verändert: Δpos={d:0.00000} m Δrot={a:0.0000}° jetzt {F(c.transform.position)} {F(c.transform.rotation)}");
            }
        }
    }
}
