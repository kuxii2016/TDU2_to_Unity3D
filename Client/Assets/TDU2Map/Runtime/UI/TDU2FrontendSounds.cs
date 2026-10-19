using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TDU2.Map.Flash;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Oberflächen-Sounds wie im Spiel: die Flash-Filme melden "FSCommand:sndp:&lt;id&gt;" (z.B. HUD: chptpass beim Checkpoint,
    /// three/two/one/go beim Countdown, time/time2 in den letzten Sekunden; Menüs: move/valid/back ...).
    /// Zuordnung id -> Datei + Lautstärke aus frontend/sounds/fe_snd.bnk/sounds.ini ("AddFESound "id" "datei.wav" vol"),
    /// die WAVs (Microsoft ADPCM) liegen in derselben Bank.
    /// Benutzung: <see cref="Attach"/> für einen Film oder <see cref="Command"/> / <see cref="Play"/> direkt.
    /// </summary>
    public sealed class TDU2FrontendSounds : MonoBehaviour
    {
        public static bool Enabled = true;
        /// <summary>Gesamtlautstärke der Oberflächen-Sounds.</summary>
        public static float Volume = 0.8f;

        static TDU2FrontendSounds _inst;
        static bool _loadTried;
        static BnkArchive _bank;
        static readonly Dictionary<string, (string file, float vol)> _map = new Dictionary<string, (string, float)>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float> _lastPlay = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<VmfPlayer> _attached = new HashSet<VmfPlayer>();
        AudioSource _src;

        static TDU2FrontendSounds Inst
        {
            get
            {
                if (_inst != null) return _inst;
                var go = new GameObject("TDU2 FrontendSounds") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<TDU2FrontendSounds>();
                return _inst;
            }
        }

        void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.dopplerLevel = 0f;
            _src.ignoreListenerPause = true;
            _src.ignoreListenerVolume = false;
        }

        void OnDestroy() { if (_inst == this) _inst = null; }

        static bool EnsureLoaded()
        {
            if (_bank != null) return true;
            if (_loadTried) return false;
            _loadTried = true;
            try
            {
#if UNITY_2023_1_OR_NEWER
                var man = FindFirstObjectByType<TDU2VehicleManager>();
#else
                var man = FindObjectOfType<TDU2VehicleManager>();
#endif
                string dir = man != null ? man.BnkDir : null;
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Application.streamingAssetsPath, "Euro", "Bnk");
                string path = Path.Combine(dir, "frontend", "sounds", "fe_snd.bnk");
                if (!File.Exists(path)) { Debug.LogWarning("[TDU2] Oberflächen-Sounds: " + path + " fehlt"); return false; }
                _bank = BnkArchive.Open(path);
                var ini = _bank.Find("sounds.ini");
                if (ini != null)
                {
                    var raw = _bank.Read(ini); var sb = new StringBuilder(raw.Length); foreach (var b in raw) sb.Append((char)b); var text = sb.ToString();
                    foreach (Match m in Regex.Matches(text, "AddFESound\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s+([0-9.]+)", RegexOptions.IgnoreCase))
                    {
                        float.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v);
                        _map[m.Groups[1].Value.Trim()] = (m.Groups[2].Value.Trim(), v <= 0f ? 1f : v);
                    }
                }
                Debug.Log("[TDU2] Oberflächen-Sounds: " + _map.Count + " Einträge (fe_snd.bnk)");
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Oberflächen-Sounds: " + ex.Message); _bank = null; return false; }
        }

        static AudioClip Clip(string id, out float vol)
        {
            vol = 1f;
            if (!EnsureLoaded() || !_map.TryGetValue(id, out var m)) return null;
            vol = m.vol;
            if (_clips.TryGetValue(m.file, out var c)) return c;
            c = null;
            try
            {
                var e = _bank.Find(m.file) ?? _bank.Find(m.file + ".wav") ?? _bank.Find(m.file.Replace(" ", ""));
                if (e != null && WavDecoder.TryDecode(_bank.Read(e), out var s, out int ch, out int rate) && s != null && s.Length > 0)
                {
                    c = AudioClip.Create("fe_" + id, s.Length / ch, ch, rate, false);
                    c.SetData(s, 0);
                }
            }
            catch (Exception) { c = null; }
            _clips[m.file] = c;   // auch null merken (fehlende Datei nicht ständig suchen)
            return c;
        }

        /// <summary>Clips vorab dekodieren (z.B. vor dem Rennen: Countdown/Checkpoint), damit der erste Ton nicht hakt.</summary>
        public static void Preload(params string[] ids)
        {
            if (!Enabled) return;
            foreach (var id in ids) Clip(id, out _);
        }

        /// <summary>Sound mit der Kennung aus sounds.ini abspielen (z.B. "chptpass").</summary>
        public static void Play(string id)
        {
            if (!Enabled || string.IsNullOrEmpty(id)) return;
            float now = Time.unscaledTime;
            if (_lastPlay.TryGetValue(id, out var last) && now - last < 0.06f) return;   // gleiche Kennung im selben Moment nur einmal
            _lastPlay[id] = now;
            var c = Clip(id, out float vol);
            if (c == null) return;
            Inst._src.PlayOneShot(c, Mathf.Clamp01(vol * Volume));
        }

        /// <summary>FSCommand auswerten: "sndp:&lt;id&gt;[:...]" spielt den Sound. Rückgabe true = war ein Sound-Befehl.</summary>
        public static bool Command(string cmd)
        {
            if (cmd == null) return false;
            var c = cmd.Trim();
            if (!c.StartsWith("sndp:", StringComparison.OrdinalIgnoreCase)) return false;
            var id = c.Substring(5);
            int colon = id.IndexOf(':');
            if (colon >= 0) id = id.Substring(0, colon);
            Play(id);
            return true;
        }

        /// <summary>Sound-Befehle eines Films abspielen (mehrfaches Anmelden wird ignoriert).</summary>
        public static void Attach(VmfPlayer film)
        {
            if (film == null || !_attached.Add(film)) return;
            film.FSCommand += OnFilmCommand;
        }

        public static void Detach(VmfPlayer film)
        {
            if (film == null || !_attached.Remove(film)) return;
            film.FSCommand -= OnFilmCommand;
        }

        static void OnFilmCommand(string cmd, string args) => Command(cmd);
    }
}
