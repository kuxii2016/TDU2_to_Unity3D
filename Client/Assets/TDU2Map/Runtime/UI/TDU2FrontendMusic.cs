using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Menü-Musik wie im Spiel (FlashSounds::PlayFrontEndMusic, frontend/sounds/fe_snd.bnk sounds.ini "SetMusic"):
    ///   LOGIN -> login.wav (1.0), MAP -> map.wav (0.5), LOBBY -> lobby.wav (1.0), REPLAY -> replay.wav, TDMEDIA -> TDUMedia.wav.
    /// Dateien: &lt;Bnk&gt;/sound/musics/&lt;name&gt;.wav (Schleife, Ein-/Ausblenden).
    /// </summary>
    public sealed class TDU2FrontendMusic : MonoBehaviour
    {
        public static float Volume = 0.7f;
        public static float FadeSeconds = 1.2f;

        static readonly Dictionary<string, (string file, float vol)> Table = new Dictionary<string, (string, float)>(StringComparer.OrdinalIgnoreCase)
        {
            { "LOGIN", ("login", 1f) }, { "MAP", ("map", 0.5f) }, { "LOBBY", ("lobby", 1f) },
            { "REPLAY", ("replay", 1f) }, { "TDMEDIA", ("tdumedia", 1f) },
        };

        static TDU2FrontendMusic _inst;
        AudioSource _src;
        string _current;
        float _target, _vol = 1f;
        Task<(float[] data, int ch, int rate)> _task;
        string _taskName;
        readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        static TDU2FrontendMusic Inst
        {
            get
            {
                if (_inst != null) return _inst;
                var go = new GameObject("TDU2 FrontendMusic") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<TDU2FrontendMusic>();
                return _inst;
            }
        }

        /// <summary>Gerade laufende Musik (Name aus der Tabelle) oder null.</summary>
        public static string Current => _inst != null ? _inst._current : null;

        /// <summary>Musik eines Menüs starten (LOGIN, MAP, LOBBY …). Läuft dieselbe schon, passiert nichts.</summary>
        public static void Play(string bnkDir, string mode)
        {
            if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(mode) || !Table.TryGetValue(mode, out var e)) return;
            var m = Inst;
            if (string.Equals(m._current, mode, StringComparison.OrdinalIgnoreCase) && (m._task != null || (m._src != null && m._src.isPlaying))) { m._target = 1f; return; }
            m._current = mode; m._vol = e.vol;
            if (m._cache.TryGetValue(e.file, out var clip) && clip != null) { m.Begin(clip); return; }
            string path = Path.Combine(bnkDir, "sound", "musics", e.file + ".wav");
            if (!File.Exists(path))
            {
                // Groß-/Kleinschreibung der Dateien ist uneinheitlich (TDUMedia.wav)
                var dir = Path.Combine(bnkDir, "sound", "musics");
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.wav")) if (string.Equals(Path.GetFileNameWithoutExtension(f), e.file, StringComparison.OrdinalIgnoreCase)) { path = f; break; }
            }
            if (!File.Exists(path)) { Debug.LogWarning("Menü-Musik fehlt: " + path); return; }
            m._taskName = e.file;
            m._task = Task.Run(() => WavDecoder.TryDecode(File.ReadAllBytes(path), out var data, out int ch, out int rate) ? (data, ch, rate) : ((float[])null, 0, 0));
        }

        /// <summary>Menü-Musik ausblenden.</summary>
        public static void Stop()
        {
            if (_inst == null) return;
            _inst._current = null; _inst._target = 0f; _inst._task = null;
        }

        void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false; _src.loop = true; _src.spatialBlend = 0f; _src.ignoreListenerPause = true;
        }

        void Begin(AudioClip clip)
        {
            if (_src.clip != clip) { _src.clip = clip; _src.volume = 0f; _src.Play(); }
            else if (!_src.isPlaying) _src.Play();
            _target = 1f;
        }

        void Update()
        {
            if (_task != null && _task.IsCompleted)
            {
                var t = _task; _task = null;
                if (!t.IsFaulted && t.Result.data != null && _current != null)
                {
                    var (data, ch, rate) = t.Result;
                    var clip = AudioClip.Create("FrontendMusic_" + _taskName, data.Length / Math.Max(1, ch), ch, rate, false);
                    clip.SetData(data, 0);
                    _cache[_taskName] = clip;
                    Begin(clip);
                    DebugManager.Info("Menü-Musik: " + _taskName + ".wav");
                }
            }
            float want = _target * _vol * Volume;
            float step = Time.unscaledDeltaTime / Mathf.Max(0.05f, FadeSeconds);
            _src.volume = Mathf.MoveTowards(_src.volume, want, step);
            if (_target <= 0f && _src.volume <= 0.001f && _src.isPlaying) _src.Stop();
        }

        void OnDestroy()
        {
            if (_inst == this) _inst = null;
            foreach (var c in _cache.Values) if (c != null) Destroy(c);
        }
    }
}
