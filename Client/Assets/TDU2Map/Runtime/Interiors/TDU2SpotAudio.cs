using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Ton in Interieurs wie im Spiel: Autoradio aus, stattdessen die Spot-Musik aus sound/musics (MngMusic, "%sSound/Musics/%s").
    ///   ERS_A schoolasphalt, ERS_B schooldirt, ERS_C schoolclassic, ECD_* cardealer, EPH_* playerhouse01..03,
    ///   EAS_* (Kleidungsläden) clothesshop01/02, ...
    /// Zuerst wird im Exterior-Bank des Spots (entities.xmb) nach einem Verweis auf eine der Musikdateien gesucht
    /// (MusicDesc o.ä.), sonst gilt die Tabelle unten. Beim Verlassen läuft das Radio weiter.
    /// Wird von <see cref="TDU2Interiors"/> automatisch angehängt (Option SpotAudio).
    /// </summary>
    public sealed class TDU2SpotAudio : MonoBehaviour
    {
        public TDU2Interiors Interiors;
        [Range(0f, 1f)] public float Volume = 0.6f;
        [Tooltip("Autoradio im Interieur stumm schalten")]
        public bool MuteRadio = true;
        [Tooltip("Ein-/Ausblendzeit in Sekunden")]
        public float Fade = 1.0f;

        /// <summary>Spot-Code-Präfix → Musikdatei (ohne .wav). Längere Präfixe zuerst.</summary>
        static readonly (string prefix, string music)[] Table =
        {
            ("ERS_A", "schoolasphalt"), ("ERS_B", "schooldirt"), ("ERS_C", "schoolclassic"),
            ("ESTOL", "stolencardealer"), ("ESTICK", "stickershop"), ("EPHOT", "photograph"),
            ("ECDC", "cardealer"), ("ECD", "cardealer"), ("EBD", "cardealer"),
            ("EPH", "playerhouse"), ("ETUN", "tunershop"), ("ESUR", "surgeryclinic"),
            ("EHD", "hairdresser"), ("EGAM", "gamblinghall"), ("EAIR", "airport"),
            ("ERE", "realtor"), ("ECH", "clothesshop"), ("EAS", "clothesshop"), ("EUC", "club"),
        };

        AudioSource _src;
        Task<(float[] data, int ch, int rate, string name)> _task;
        TDU2Interiors _bound;
        float _target;
        bool _inside;

        void OnEnable() => Bind();
        void OnDisable() { Unbind(); Stop(true); }

        void Bind()
        {
            if (Interiors == null || _bound == Interiors) return;
            Unbind();
            _bound = Interiors;
            Interiors.Entered += OnEntered;
            Interiors.Exited += OnExited;
            if (Interiors.Inside) OnEntered(Interiors.CurrentSpot);
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.Entered -= OnEntered;
            _bound.Exited -= OnExited;
            _bound = null;
        }

        void OnEntered(Spot s)
        {
            _inside = true;
            if (MuteRadio && TDU2Radio.Instance != null) TDU2Radio.Instance.Muted = true;
            if (s == null || Interiors == null || Interiors.Manager == null || Interiors.Manager.Streamer == null) return;
            var idx = Interiors.Manager.Streamer.Index;
            if (idx == null || string.IsNullOrEmpty(idx.BnkDir)) return;
            string bnkDir = idx.BnkDir, extDir = idx.ExteriorDir, code = s.Code ?? "";
            int salt = s.Index;
            _task = Task.Run(() => LoadMusic(bnkDir, extDir, code, salt));
        }

        void OnExited(Spot s)
        {
            _inside = false;
            _task = null;
            _target = 0f;
            if (MuteRadio && TDU2Radio.Instance != null) TDU2Radio.Instance.Muted = false;
        }

        static (float[], int, int, string) LoadMusic(string bnkDir, string extDir, string code, int salt)
        {
            string dir = Path.Combine(bnkDir, "sound", "musics");
            if (!Directory.Exists(dir)) return (null, 0, 0, null);
            var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(dir, "*.wav")) available[Path.GetFileNameWithoutExtension(f)] = f;

            string pick = FromExterior(extDir, code, available);
            if (pick == null)
            {
                string up = code.ToUpperInvariant();
                foreach (var (prefix, music) in Table)
                {
                    if (!up.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (available.ContainsKey(music)) { pick = music; break; }
                    // Varianten (playerhouse01..03, clothesshop01/02, clubhawai/clubibiza): je Spot fest gewählt
                    var vars = new List<string>();
                    foreach (var k in available.Keys) if (k.StartsWith(music, StringComparison.OrdinalIgnoreCase)) vars.Add(k);
                    vars.Sort(StringComparer.OrdinalIgnoreCase);
                    if (vars.Count > 0) { pick = vars[Math.Abs(salt) % vars.Count]; break; }
                }
            }
            if (pick == null || !available.TryGetValue(pick, out var path)) return (null, 0, 0, null);
            if (!WavDecoder.TryDecode(File.ReadAllBytes(path), out var data, out int ch, out int rate)) return (null, 0, 0, pick);
            return (data, ch, rate, pick);
        }

        /// <summary>Exterior-Bank des Spots nach einem String durchsuchen, der einer Musikdatei entspricht.</summary>
        static string FromExterior(string extDir, string code, Dictionary<string, string> available)
        {
            try
            {
                if (string.IsNullOrEmpty(extDir) || string.IsNullOrEmpty(code)) return null;
                var bankPath = CarDatabase.FindFile(extDir, code.ToLowerInvariant() + ".bnk");
                if (bankPath == null) return null;
                var bank = BnkArchive.Open(bankPath);
                var e = bank.Find("entities.xmb");
                if (e == null) return null;
                var root = XmbFile.Parse(bank.Read(e));
                string found = null;
                void Walk(XmbNode n, int depth)
                {
                    if (found != null || n == null || depth > 40) return;
                    if (n.Value is string sv && sv.Length > 2)
                    {
                        var stem = Path.GetFileNameWithoutExtension(sv.Replace('\\', '/'));
                        if (available.ContainsKey(stem)) { found = stem; return; }
                    }
                    foreach (var c in n.Children) Walk(c, depth + 1);
                    foreach (var c in n.Items) Walk(c, depth + 1);
                }
                Walk(root, 0);
                return found;
            }
            catch (Exception) { return null; }
        }

        void Update()
        {
            if (Interiors != null && _bound != Interiors) Bind();
            if (_task != null && _task.IsCompleted)
            {
                var t = _task; _task = null;
                if (!t.IsFaulted && _inside && t.Result.data != null)
                {
                    var (data, ch, rate, name) = t.Result;
                    var clip = AudioClip.Create("SpotMusic_" + name, data.Length / ch, ch, rate, false);
                    clip.SetData(data, 0);
                    Play(clip);
                    DebugManager.Info("Spot-Musik: " + name + ".wav");
                }
                else if (!t.IsFaulted && _inside && t.Result.name != null) DebugManager.Error("Spot-Musik " + t.Result.name + ".wav konnte nicht dekodiert werden");
            }
            if (_src != null)
            {
                float step = Fade > 0.01f ? Time.unscaledDeltaTime / Fade : 1f;
                _src.volume = Mathf.MoveTowards(_src.volume, _target, step * Mathf.Max(Volume, 0.01f));
                if (_target <= 0f && _src.volume <= 0.001f && _src.isPlaying) Stop(true);
            }
        }

        void Play(AudioClip clip)
        {
            if (_src == null)
            {
                _src = gameObject.AddComponent<AudioSource>();
                _src.playOnAwake = false;
                _src.spatialBlend = 0f;
                _src.loop = true;
                _src.ignoreListenerPause = true;
            }
            if (_src.clip != null && _src.clip != clip) Destroy(_src.clip);
            _src.clip = clip;
            _src.volume = 0f;
            _target = Volume;
            _src.Play();
        }

        void Stop(bool hard)
        {
            if (_src == null) return;
            _src.Stop();
            if (hard && _src.clip != null) { Destroy(_src.clip); _src.clip = null; }
        }

        void OnDestroy()
        {
            Unbind();
            if (TDU2Radio.Instance != null && MuteRadio && _inside) TDU2Radio.Instance.Muted = false;
            Stop(true);
        }
    }
}