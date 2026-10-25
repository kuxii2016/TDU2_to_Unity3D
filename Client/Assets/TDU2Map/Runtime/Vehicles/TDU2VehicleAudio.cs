using System.Collections.Generic;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Fahrzeugsound nach CarVSTConfig.xmb: jedes Sample (Motor Last/Schub, Getriebe, Wind, Reifen, Schaltgeräusche ...)
    /// bekommt eine AudioSource; Tonhöhe und Lautstärke folgen den Kurven über Drehzahl, Tempo, Motormoment und Radschlupf.
    /// </summary>
    public sealed class TDU2VehicleAudio : MonoBehaviour
    {
        public TDU2Vehicle Vehicle;
        [Range(0, 2)] public float MasterVolume = 0.05f;
        public bool InteriorView;
        [Tooltip("Motor/Abgas in der Innenansicht (Cockpit) leiser")]
        [Range(0, 1)] public float InteriorEngineFactor = 0.5f;
        public float Wetness;
        /// <summary>Mitspieler-Auto: ohne TDU2Vehicle, Werte kommen aus ExtRpm/ExtKmh/ExtTorque; Klang räumlich (3D).</summary>
        public bool External;
        public float ExtRpm, ExtKmh, ExtTorque;
        /// <summary>Motor läuft (aus: alle Dauergeräusche blenden aus, Hupe/Einzelgeräusche gehen weiter – Händler, Motor abgestellt).</summary>
        public bool EngineOn = true;
        public float MinDistance = 6f, MaxDistance = 160f;

        sealed class Voice
        {
            public SoundSample Sample;
            public AudioSource Source;
            public float Volume;
        }

        readonly List<Voice> _loops = new List<Voice>();
        readonly Dictionary<string, Voice> _triggers = new Dictionary<string, Voice>(System.StringComparer.OrdinalIgnoreCase);
        readonly List<AudioClip> _clips = new List<AudioClip>();
        readonly float[] _phys = new float[64];

        public void Setup(CarAssets a)
        {
            if (a?.Sound == null) return;
            var clipByWave = new Dictionary<string, AudioClip>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var s in a.Sound.Samples)
            {
                if (s.Wave == null || !a.Waves.TryGetValue(s.Wave, out var wav)) continue;
                if (!clipByWave.TryGetValue(s.Wave, out var clip))
                {
                    int frames = wav.data.Length / Mathf.Max(1, wav.channels);
                    if (frames <= 0) continue;
                    clip = AudioClip.Create(s.Wave, frames, wav.channels, wav.rate, false);
                    clip.SetData(wav.data, 0);
                    clipByWave[s.Wave] = clip;
                    _clips.Add(clip);
                }
                var src = gameObject.AddComponent<AudioSource>();
                src.clip = clip;
                src.playOnAwake = false;
                src.spatialBlend = External ? 1f : 0f;
                if (External) { src.rolloffMode = AudioRolloffMode.Logarithmic; src.minDistance = MinDistance; src.maxDistance = MaxDistance; }
                src.dopplerLevel = 0f;
                src.loop = s.Loop && !s.Trigger;
                src.volume = 0f;
                var v = new Voice { Sample = s, Source = src };
                if (s.Trigger) _triggers[s.Name] = v;
                else { _loops.Add(v); src.Play(); }
            }
            if (Vehicle != null) Vehicle.GearChanged += OnGear;
        }

        public void Play(string name, float volume = 1f)
        {
            if (!_triggers.TryGetValue(name, out var v) || v.Source.clip == null) return;
            v.Source.volume = Mathf.Clamp01(v.Sample.BaseVolume * StateFactor(v.Sample) * volume * MasterVolume);
            v.Source.pitch = 1f;
            v.Source.Play();
        }

        public void SetHorn(bool on)
        {
            if (!_triggers.TryGetValue("Horn", out var v)) return;
            if (on && !v.Source.isPlaying) { v.Source.loop = true; v.Source.volume = Mathf.Clamp01(v.Sample.BaseVolume * MasterVolume); v.Source.Play(); }
            else if (!on && v.Source.isPlaying) v.Source.Stop();
        }

        void OnGear(int from, int to)
        {
            if (to == 0 || from == to) return;
            bool up = to > from && from > 0;
            string n = (up ? "GearBoxUp" : "GearBoxDown") + (InteriorView ? "Internal" : "External");
            Play(n);
        }

        [Tooltip("Quergleiten (m/s), ab dem Reifen quietschen")]
        public float SquealLatDeadzone = 2.2f;
        [Tooltip("Längsschlupf (m/s), ab dem Reifen quietschen")]
        public float SquealLongDeadzone = 2.5f;

        static float Deadzone(float v, float dz) => Mathf.Sign(v) * Mathf.Max(0f, Mathf.Abs(v) - dz);

        float StateFactor(SoundSample s)
        {
            var st = s.StateVolume;
            if (st == null || st.Length < 2) return 1f;
            float f = InteriorView ? st[0] : st[1];
            // Reifen-/Rollgeräusche: Oberflächen-Faktor je Untergrund (Index 3 Asphalt, 4 Gras, 5 Erde),
            // gewichtet mit dem Anteil der Räder auf dem jeweiligen Untergrund -> auf Feldwegen das Erd-Rollgeräusch
            if ((s.Panel == 5 || s.Panel == 6) && st.Length > 3)
            {
                var sh = Vehicle != null ? Vehicle.SurfaceShare : null;
                if (sh == null || st.Length < 6) f *= st[3];
                else f *= st[3] * sh[0] + st[4] * sh[1] + st[5] * sh[2];
            }
            return f;
        }

        void Update()
        {
            if (Vehicle == null && !External) return;
            if (Vehicle == null)
            {
                _phys[0] = ExtRpm; _phys[1] = ExtKmh; _phys[3] = ExtTorque;
            }
            else
            {
                // Physikwerte wie in CarVSTConfig
                _phys[0] = Vehicle.Rpm;
                _phys[1] = Vehicle.SpeedKmh;
                _phys[3] = Vehicle.EngineTorque * (Vehicle.Gear == 0 ? 0.3f : 1f);
                var ws = Vehicle.Wheels;
                for (int i = 0; i < 4; i++)
                {
                    var w = i < ws.Length ? ws[i] : null;
                    // Quietschen erst bei echtem Rutschen: normale Kurvenfahrt erzeugt schon 1-2 m/s Quergleiten
                    _phys[26 + i] = w != null && w.Grounded ? Deadzone(w.LatSlipVel, SquealLatDeadzone) : 0f;
                    _phys[34 + i] = w != null && w.Grounded ? Deadzone(w.LongSlipVel, SquealLongDeadzone) : 0f;
                }
            }
            _phys[43] = Mathf.Max(Wetness, TDU2.World.TDU2Weather.GroundWet);   // nasse Fahrbahn (Wetter)

            foreach (var v in _loops)
            {
                float vol = v.Sample.BaseVolume * StateFactor(v.Sample), pitch = 1f;
                bool hasPitch = false;
                foreach (var l in v.Sample.Links)
                {
                    float x = 0f;
                    if (l.Physic.Length == 1) x = Get(l.Physic[0]);
                    else
                    {
                        // Radwerte: stärkstes Rad zählt
                        float best = 0f;
                        foreach (var p in l.Physic) { float val = Get(p); if (Mathf.Abs(val) > Mathf.Abs(best)) best = val; }
                        x = best;
                    }
                    float y = l.Curve.Evaluate(x);
                    if (l.Audio == 0) { pitch *= y; hasPitch = true; }
                    else vol *= y;
                }
                if (!hasPitch) pitch = 1f;
                v.Source.pitch = Mathf.Clamp(pitch, 0.05f, 3f);
                float want = EngineOn ? Mathf.Clamp01(vol * MasterVolume * (InteriorView ? InteriorEngineFactor : 1f)) : 0f;
                v.Volume = Mathf.Lerp(v.Volume, want, Time.deltaTime * (EngineOn ? 20f : 6f));
                v.Source.volume = v.Volume;
            }
        }

        float Get(int id) => id >= 0 && id < _phys.Length ? _phys[id] : 0f;

        void OnDestroy()
        {
            if (Vehicle != null) Vehicle.GearChanged -= OnGear;
            foreach (var c in _clips) if (c != null) Destroy(c);
        }
    }
}
