using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using TDU2.Map.Racing;
using TDU2.Map.Vehicles;
using TDU2.Map.World;
using UnityEngine;

namespace TDU2.Map
{
    /// <summary>
    /// Diagnose-Log für Rennschule/Interieur/Fahrzeug in eine Datei im Projekt (für die Fehlersuche ohne Unity-Konsole):
    ///   &lt;Projekt&gt;/Docs/Logs/tdu2_session.log   (aktuelle Sitzung, beim Start neu angelegt; vorige Sitzung -> tdu2_session_prev.log)
    /// Inhalt:
    ///   - alle Unity-Logs mit "[TDU2]", alle Warnungen/Fehler/Exceptions (mit Stacktrace)
    ///   - Ereignisse (TDU2DebugLog.Event)
    ///   - Zustandsbild jede Sekunde, wenn sich etwas geändert hat (sonst alle 10 s): timeScale, Cursor, Interieur,
    ///     aktuelles Auto, Lade-Status, Rennzustand, offene Menüs, Kamera (Position, aktive Steuer-Komponenten), Mitspieler/Verkehr.
    /// F8 = sofort ein Zustandsbild schreiben (Markierung "MANUELL").
    /// </summary>
    public sealed class TDU2DebugLog : MonoBehaviour
    {
        public static bool Enabled = true;
        static TDU2DebugLog _inst;
        static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        static string _path;
        static int _frame;
        static float _time;

        string _lastState = "";
        float _nextState, _nextForced;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!Enabled || _inst != null) return;
            var go = new GameObject("TDU2 DebugLog") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<TDU2DebugLog>();
        }

        static string LogDir()
        {
            // Assets -> Client -> Projekt; Logs neben Docs (vom Assistenten lesbar)
            try
            {
                var client = Directory.GetParent(Application.dataPath);
                var project = client?.Parent;
                var dir = project != null ? Path.Combine(project.FullName, "Docs", "Logs") : Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception) { return Application.persistentDataPath; }
        }

        void Awake()
        {
            var dir = LogDir();
            _path = Path.Combine(dir, "tdu2_session.log");
            try
            {
                if (File.Exists(_path)) File.Copy(_path, Path.Combine(dir, "tdu2_session_prev.log"), true);
                File.WriteAllText(_path, $"=== TDU2 Sitzung {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Unity {Application.unityVersion}  {(Application.isEditor ? "Editor" : "Player")} ===\n");
            }
            catch (Exception) { }
            Application.logMessageReceivedThreaded += OnLog;
            CopyData(dir);
        }

        /// <summary>
        /// Spieldateien, die der Assistent zur Analyse braucht, aber wegen der Ordnertiefe nicht selbst holen kann,
        /// einmalig nach Docs/Logs/data kopieren (nur wenn noch nicht vorhanden).
        /// </summary>
        static void CopyData(string logDir)
        {
            try
            {
                string bnk = Path.Combine(Application.streamingAssetsPath, "Euro", "Bnk", "islands");
                if (!Directory.Exists(bnk)) return;
                string outDir = Path.Combine(logDir, "data");
                foreach (var isl in Directory.GetDirectories(bnk))
                {
                    string name = Path.GetFileName(isl);
                    foreach (var rel in new[] { Path.Combine("scenarics", "packs", "pack_cp.bnk"), Path.Combine("exterior", "ers_a.bnk"), Path.Combine("exterior", "ers_b.bnk"), Path.Combine("exterior", "ers_c.bnk") })
                    {
                        var src = Path.Combine(isl, rel);
                        if (!File.Exists(src)) continue;
                        Directory.CreateDirectory(outDir);
                        var dst = Path.Combine(outDir, name + "_" + Path.GetFileName(rel));
                        if (!File.Exists(dst)) File.Copy(src, dst);
                    }
                }
            }
            catch (Exception ex) { _queue.Enqueue(Stamp() + "WARN Datenkopie: " + ex.Message); }
        }

        void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Flush();
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            bool tdu = msg != null && msg.StartsWith("[TDU2]", StringComparison.Ordinal);
            if (!tdu && type == LogType.Log) return;
            var sb = new StringBuilder();
            sb.Append(Stamp()).Append(type == LogType.Log ? "LOG  " : type == LogType.Warning ? "WARN " : "ERR  ").Append(msg);
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                sb.Append('\n').Append(string.IsNullOrEmpty(stack) ? "" : "    " + stack.Trim().Replace("\n", "\n    "));
            _queue.Enqueue(sb.ToString());
        }

        static string Stamp() => $"[{DateTime.Now:HH:mm:ss.fff} f{Volatile.Read(ref _frame)} t{_time:0.0}] ";

        /// <summary>Ereignis ins Log schreiben (thread-sicher).</summary>
        public static void Event(string text)
        {
            if (!Enabled) return;
            _queue.Enqueue(Stamp() + "EVT  " + text);
        }

        void Update()
        {
            Volatile.Write(ref _frame, Time.frameCount);
            _time = Time.unscaledTime;
#if ENABLE_LEGACY_INPUT_MANAGER
            bool manual = Input.GetKeyDown(KeyCode.F8);
#else
            bool manual = false;
#endif
            if (manual || Time.unscaledTime >= _nextState)
            {
                _nextState = Time.unscaledTime + 1f;
                string st = State();
                if (manual || st != _lastState || Time.unscaledTime >= _nextForced)
                {
                    _queue.Enqueue(Stamp() + (manual ? "MANUELL " : "STATE ") + st);
                    _lastState = st;
                    _nextForced = Time.unscaledTime + 10f;
                }
            }
            Flush();
        }

        static void Flush()
        {
            if (_path == null || _queue.IsEmpty) return;
            var sb = new StringBuilder();
            while (_queue.TryDequeue(out var l)) sb.Append(l).Append('\n');
            try { File.AppendAllText(_path, sb.ToString()); } catch (Exception) { }
        }

        static T Find<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<T>();
#else
            return FindObjectOfType<T>();
#endif
        }

        static string State()
        {
            var sb = new StringBuilder();
            sb.Append($"ts={Time.timeScale:0.##} cur={Cursor.lockState}/{(Cursor.visible ? "v" : "h")}");
            try
            {
                var man = Find<TDU2VehicleManager>();
                if (man != null)
                {
                    var c = man.Current;
                    sb.Append($" | car={(c != null && c.Car != null ? c.Car.FileName + "#" + c.Car.Id : "-")} loading={man.IsLoadingCar} susp={man.Suspended}");
                    if (c != null) { var p = c.transform.position; var fw = c.transform.forward; sb.Append($" carPos=({p.x:0},{p.y:0.0},{p.z:0}) fwd=({fw.x:0.00},{fw.z:0.00}) kmh={c.SpeedKmh:0}"); }
                    var inter = man.GetComponent<TDU2Interiors>();
                    if (inter != null) sb.Append($" | inside={inter.Inside} spot={(inter.CurrentSpot != null ? inter.CurrentSpot.Code : "-")} ready={inter.SceneReady}");
                    var mm = man.Minimap;
                    if (mm != null) sb.Append($" | minimap={(mm.enabled ? "an" : "aus")}{(mm.HudPlayer != null ? (mm.HudPlayer.Visible ? "/hudV" : "/hudH") : "")}");
                }
                var menu = Find<RacingSchoolMenu>();
                if (menu != null)
                {
                    sb.Append($" | school open={menu.IsOpen} inTest={menu.InTest} block={RacingSchoolMenu.BlocksPauseMenu} res={RaceResults.AnyOpen}");
                    if (menu.Race != null) sb.Append($" race={menu.Race.State} next={menu.Race.NextCheckpointIndex}");
                }
                var pause = Find<TDU2PauseMenu>();
                if (pause != null) sb.Append($" | pause={pause.IsOpen}");
                sb.Append($" | mpHidden={Online.TDU2Multiplayer.Hidden}");
                var tr = Find<Traffic.TDU2Traffic>();
                if (tr != null) sb.Append($" traffic={tr.EnableTraffic}");
                var cam = Camera.main;
                if (cam != null)
                {
                    var p = cam.transform.position;
                    sb.Append($" | cam=({p.x:0},{p.y:0.0},{p.z:0}) parent={(cam.transform.parent != null ? cam.transform.parent.name : "-")} ctl=");
                    foreach (var b in cam.GetComponents<Behaviour>())
                        if (b != null && b.enabled && !(b is Camera) && !(b is AudioListener) && !(b is FlareLayer)) sb.Append(b.GetType().Name).Append(',');
                }
                else sb.Append(" | cam=KEINE");
                var fp = Find<TDU2FirstPerson>();
                if (fp != null) sb.Append($" fp={(fp.enabled ? "an" : "aus")}");
            }
            catch (Exception ex) { sb.Append(" | state-Fehler: " + ex.Message); }
            return sb.ToString();
        }
    }
}
