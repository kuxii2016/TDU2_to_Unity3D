using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TDU2.Formats;
using TDU2.IO;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Cinematics
{
    /// <summary>
    /// Kino-Töne einer Szenen-Bank (Unity-Port von OpenTDU2 CinematicSounds, game/cinematic/cinematic_audio.cpp):
    ///   Fx__Root{SourceData} -> Fx__SourceData{Fx[0] = .bfx, SoundList[].SoundNode} -> Fx__Sound{SoundFile[0] = .wav}
    ///   bzw. Fx__Sound__&lt;LANG&gt;{LocalizedSoundFile[0]} in der Sprachbank "&lt;bank&gt;__&lt;sprache&gt;.bnk" (Stimmen).
    ///   .bfx: SND-Knoten (" DNS" bei Knoten+0xe0, Klasse 0x8540076c bei +0x58): +0x5c Schleifen (-1 = Schleife), +0x78 Name,
    ///   +0xa0 Ton-rsc, +0xac Lautstärke, +0xb0 Tonhöhe, +0xb4/+0xb8 Zufall, +0xc8/+0xd0 Nah/Fern; "LSGL" bei 0xb4 = Sprachweiche
    ///   (Knoten nach Präfix "GE_", sonst "US_", sonst irgendeiner; "_W"-Zweitaufnahmen nur ohne normale).
    ///   Entity__Fx (Name -> Fx__Root, Position) sind die Emitter für "snd:music"/"snd:timestop" und PlayEmitter("C_OUTRO").
    /// Die Positionen sind im Rahmen der Bank (Spielkoordinaten der Szene) und werden über <see cref="Frame"/> in die Welt gebracht.
    /// </summary>
    public sealed class TDU2CinematicAudio
    {
        public sealed class Sound
        {
            public string Name = "";
            public ulong Rsc;
            public float Volume = 1f, Pitch = 1f, VolumeRandom, PitchRandom;
            public float ClipNear = 4f, ClipFar = 40f;
            public bool Loop;
            public bool Voice;         // Stimme aus der Sprachbank
            public bool HasPosition;
            public Vector3 Position;   // Bankrahmen
        }

        /// <summary>Lautstärke aller Kino-Töne (zusätzlich zu AudioListener.volume).</summary>
        public static float MasterVolume = 1f;
        /// <summary>Mischung wie die Klienten des Originals (Stimmen vor Musik): Stimmen, Musik-Emitter (snd:music), übrige Effekte.</summary>
        public static float VoiceVolume = 1f, MusicVolume = 0.35f, EffectsVolume = 0.8f;
        /// <summary>.cin/.sce-Fx-Flag 0x10: nicht positioniert (Musik, Stimmen, Logo).</summary>
        public const uint Flag2D = 0x10;

        /// <summary>Bankrahmen -> Welt (Szenen-Holder).</summary>
        public Transform Frame;
        public string LanguageBankPath { get; private set; }

        BnkArchive _bank, _lang;
        readonly Dictionary<ulong, BnkEntry> _rsc = new Dictionary<ulong, BnkEntry>();
        readonly Dictionary<ulong, BnkEntry> _langRsc = new Dictionary<ulong, BnkEntry>();
        readonly Dictionary<ulong, ulong> _rootSources = new Dictionary<ulong, ulong>();
        readonly Dictionary<ulong, string> _rootNames = new Dictionary<ulong, string>();
        readonly Dictionary<ulong, (ulong bfx, List<ulong> nodes)> _sources = new Dictionary<ulong, (ulong, List<ulong>)>();
        readonly Dictionary<ulong, ulong> _soundFiles = new Dictionary<ulong, ulong>();
        readonly Dictionary<string, (ulong root, Vector3 pos)> _emitters = new Dictionary<string, (ulong, Vector3)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<ulong, List<Sound>> _resolved = new Dictionary<ulong, List<Sound>>();
        readonly Dictionary<ulong, (float[] data, int ch, int rate)> _waves = new Dictionary<ulong, (float[], int, int)>();
        readonly Dictionary<ulong, AudioClip> _clips = new Dictionary<ulong, AudioClip>();
        string _prefix = "US_";
        uint _seed = 12345;

        GameObject _host;
        readonly List<AudioSource> _voices = new List<AudioSource>();

        public bool HasSounds => _rootSources.Count > 0;

        // ------------------------------------------------------------------ Laden (thread-sicher, ohne Unity-Objekte)
        /// <summary>Bank und (falls vorhanden) Sprachbank lesen, Töne vorab dekodieren. lang: "ge", "us" …</summary>
        public static TDU2CinematicAudio Load(string bankPath, BnkArchive bank, string lang)
        {
            var a = new TDU2CinematicAudio { _bank = bank };
            try { a.LoadInternal(bankPath, lang); }
            catch (Exception ex) { DebugManager.Warning("[TDU2] Kino-Töne " + Path.GetFileName(bankPath) + ": " + ex.Message); }
            return a;
        }

        static string Prefix(string lang)
        {
            switch ((lang ?? "us").ToLowerInvariant())
            {
                case "us": case "en": return "US_";
                case "ge": case "de": return "GE_";
                case "fr": return "FR_";
                case "it": return "IT_";
                case "sp": case "es": return "SP_";
                case "ru": return "RU_";
                case "pl": case "po": return "PL_";
                case "ja": case "jp": return "JP_";
                default: return lang.ToUpperInvariant() + "_";
            }
        }

        static void ReadHashcodes(BnkArchive bank, BnkEntry hc, Dictionary<ulong, BnkEntry> into)
        {
            if (bank == null || hc == null) return;
            var groups = XmbFile.Parse(bank.Read(hc))?.Get("___CmdSaveFileInfoArray");
            if (groups == null) return;
            var byIndex = new Dictionary<int, BnkEntry>();
            foreach (var e in bank.Entries) if (!byIndex.ContainsKey(e.Index)) byIndex[e.Index] = e;
            foreach (var g in groups.Items)
            {
                var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                if (idx == null || refs == null) continue;
                int n = Math.Min(idx.Items.Count, refs.Items.Count);
                for (int i = 0; i < n; i++)
                {
                    int fi = idx.Items[i].AsInt(-1);
                    if (fi < 0) continue;
                    if (!byIndex.TryGetValue(fi, out var be)) be = fi < bank.Entries.Count ? bank.Entries[fi] : null;
                    if (be != null) into[refs.Items[i].AsULong()] = be;
                }
            }
        }

        void LoadInternal(string bankPath, string lang)
        {
            if (_bank == null) return;
            ReadHashcodes(_bank, _bank.Find("hashcodes.xmb"), _rsc);

            // Sprachbank "<bank>__<lang>.bnk", Rückfall "__us", dann jede vorhandene
            lang = string.IsNullOrEmpty(lang) ? "us" : lang.ToLowerInvariant();
            if (!string.IsNullOrEmpty(bankPath) && bankPath.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
            {
                string stem = bankPath.Substring(0, bankPath.Length - 4);
                var tries = new List<string> { lang, "us" };
                try
                {
                    string dir = Path.GetDirectoryName(bankPath), name = Path.GetFileName(stem) + "__";
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, name + "*.bnk"))
                        {
                            string l = Path.GetFileNameWithoutExtension(f).Substring(name.Length);
                            if (!tries.Contains(l)) tries.Add(l);
                        }
                }
                catch { }
                foreach (var l in tries)
                {
                    string p = stem + "__" + l + ".bnk";
                    if (!File.Exists(p)) continue;
                    try
                    {
                        _lang = BnkArchive.Open(p);
                        BnkEntry hc = null;
                        foreach (var e in _lang.Entries)
                            if (e.Extension == ".xmb" && e.Name.StartsWith("hashcodes", StringComparison.OrdinalIgnoreCase)) { hc = e; break; }
                        ReadHashcodes(_lang, hc, _langRsc);
                        LanguageBankPath = p; lang = l;
                        break;
                    }
                    catch (Exception ex) { DebugManager.Warning("[TDU2] Sprachbank " + p + ": " + ex.Message); _lang = null; }
                }
            }
            _prefix = Prefix(lang);

            var ent = _bank.Find("entities.xmb");
            if (ent == null) return;
            var root = XmbFile.Parse(_bank.Read(ent));
            var fx = root.Get("ExportType_Fx")?.Get("ArrayOf_Fx__Root");
            if (fx != null)
                foreach (var it in fx.Items) { ulong hc = it.U("__Hc"); _rootSources[hc] = it.U("SourceData"); _rootNames[hc] = it.S("__Name", ""); }
            var sd = root.Get("ExportType_FxSourceData")?.Get("ArrayOf_Fx__SourceData");
            if (sd != null)
                foreach (var it in sd.Items)
                {
                    ulong bfx = 0;
                    var f = it.Get("Fx");
                    if (f != null && f.Items.Count > 0) bfx = f.Items[0].U("rsc");
                    var nodes = new List<ulong>();
                    var sl = it.Get("SoundList");
                    if (sl != null) foreach (var s in sl.Items) nodes.Add(s.U("SoundNode"));
                    _sources[it.U("__Hc")] = (bfx, nodes);
                }
            var snd = root.Get("ExportType_FxSound");
            if (snd != null)
                foreach (var arr in snd.Children)
                {
                    if (!arr.IsArray || !arr.Name.StartsWith("ArrayOf_Fx__Sound", StringComparison.Ordinal)) continue;
                    foreach (var it in arr.Items)
                        foreach (var field in new[] { "SoundFile", "LocalizedSoundFile" })
                        {
                            var l = it.Get(field);
                            if (l != null && l.Items.Count > 0) { _soundFiles[it.U("__Hc")] = l.Items[0].U("rsc"); break; }
                        }
                }
            var em = root.Get("ExportType_Entity")?.Get("ArrayOf_Entity__Fx");
            if (em != null)
                foreach (var it in em.Items)
                {
                    var p = it.Get("orient_pos_scale")?.Get("pos");
                    var pos = p != null ? new Vector3(p.F("X"), p.F("Y"), p.F("Z")) : Vector3.zero;
                    _emitters[it.S("__Name", "")] = (it.U("Fx"), pos);
                }

            // alle Wurzeln auflösen und die Töne vorab dekodieren (AudioClips entstehen erst beim Abspielen im Hauptthread)
            foreach (var hc in new List<ulong>(_rootSources.Keys))
                foreach (var s in Resolve(hc))
                {
                    if (_waves.ContainsKey(s.Rsc)) continue;
                    var e = FindWav(s.Rsc, out var b);
                    if (e != null && WavDecoder.TryDecode(b.Read(e), out var data, out int ch, out int rate) && data != null && ch > 0 && rate > 0)
                        _waves[s.Rsc] = (data, ch, rate);
                }
            DebugManager.Info($"[TDU2] Kino-Töne {Path.GetFileName(bankPath)}: {_rootSources.Count} Fx, {_waves.Count} Töne, {_emitters.Count} Emitter" +
                              (_lang != null ? ", Stimmen " + Path.GetFileName(LanguageBankPath) : ""));
        }

        BnkEntry FindWav(ulong rsc, out BnkArchive bank)
        {
            bank = _bank;
            if (_rsc.TryGetValue(rsc, out var e) && e.Extension == ".wav") return e;
            bank = _lang;
            if (_langRsc.TryGetValue(rsc, out e) && e.Extension == ".wav") return e;
            bank = null;
            return null;
        }

        sealed class SndNode { public string Name; public ulong Rsc; public Sound Params; }

        static List<SndNode> ParseBfx(byte[] d, out bool languageSwitch)
        {
            var list = new List<SndNode>();
            languageSwitch = d != null && d.Length >= 0xb8 && d[0xb4] == 'L' && d[0xb5] == 'S' && d[0xb6] == 'G' && d[0xb7] == 'L';
            if (d == null || d.Length < 0xe4) return list;
            for (int tag = 0xe0; tag + 4 <= d.Length; tag += 4)
            {
                if (d[tag] != ' ' || d[tag + 1] != 'D' || d[tag + 2] != 'N' || d[tag + 3] != 'S') continue;
                int n = tag - 0xe0;
                if (BitConverter.ToUInt32(d, n + 0x58) != 0x8540076cu) continue;
                int len = 0; while (len < 32 && d[n + 0x78 + len] != 0) len++;
                var s = new Sound
                {
                    Name = Encoding.ASCII.GetString(d, n + 0x78, len),
                    Rsc = BitConverter.ToUInt64(d, n + 0xa0),
                    Volume = BitConverter.ToSingle(d, n + 0xac),
                    Pitch = BitConverter.ToSingle(d, n + 0xb0),
                    VolumeRandom = BitConverter.ToSingle(d, n + 0xb4),
                    PitchRandom = BitConverter.ToSingle(d, n + 0xb8),
                    ClipNear = BitConverter.ToSingle(d, n + 0xc8),
                    ClipFar = BitConverter.ToSingle(d, n + 0xd0),
                    Loop = BitConverter.ToUInt32(d, n + 0x5c) == 0xffffffffu,
                };
                if (float.IsNaN(s.Pitch) || s.Pitch <= 0f) s.Pitch = 1f;
                if (float.IsNaN(s.Volume) || s.Volume < 0f) s.Volume = 1f;
                if (!(s.ClipNear > 0f)) s.ClipNear = 4f;
                if (!(s.ClipFar > s.ClipNear)) s.ClipFar = s.ClipNear + 36f;
                list.Add(new SndNode { Name = s.Name, Rsc = s.Rsc, Params = s });
                tag += 0xe0 - 4;
            }
            return list;
        }

        List<Sound> Resolve(ulong rootHc)
        {
            lock (_resolved)
            {
                if (_resolved.TryGetValue(rootHc, out var cached)) return cached;
                var list = new List<Sound>();
                _resolved[rootHc] = list;
                if (!_rootSources.TryGetValue(rootHc, out var src) || !_sources.TryGetValue(src, out var source)) return list;
                List<SndNode> nodes = null; bool ls = false;
                if (source.bfx != 0 && _rsc.TryGetValue(source.bfx, out var be)) nodes = ParseBfx(_bank.Read(be), out ls);
                nodes = nodes ?? new List<SndNode>();
                ulong langRsc = 0;
                if (ls)
                {
                    SndNode chosen = null;
                    foreach (var prefix in new[] { _prefix, "US_", "" })
                    {
                        for (int pass = 0; pass < 2 && chosen == null; pass++)
                            foreach (var n in nodes)
                                if (n.Name.StartsWith(prefix, StringComparison.Ordinal) && n.Name.EndsWith("_W", StringComparison.Ordinal) == (pass == 1)
                                    && FindWav(n.Rsc, out _) != null) { chosen = n; break; }
                        if (chosen != null) break;
                    }
                    if (chosen == null) return list;
                    langRsc = chosen.Rsc;
                }
                foreach (var sn in source.nodes)
                {
                    if (!_soundFiles.TryGetValue(sn, out ulong rsc)) continue;
                    if (ls && rsc != langRsc) continue;
                    var e = FindWav(rsc, out _);
                    if (e == null) continue;
                    Sound s = null;
                    foreach (var n in nodes) if (n.Rsc == rsc) { s = n.Params; break; }
                    s = s != null ? Copy(s) : new Sound();
                    s.Rsc = rsc; s.Name = e.Name;
                    FindWav(rsc, out var wb); s.Voice = wb != null && wb == _lang;
                    list.Add(s);
                }
                return list;
            }
        }

        static Sound Copy(Sound s) => new Sound
        {
            Name = s.Name, Rsc = s.Rsc, Volume = s.Volume, Pitch = s.Pitch, VolumeRandom = s.VolumeRandom, PitchRandom = s.PitchRandom,
            ClipNear = s.ClipNear, ClipFar = s.ClipFar, Loop = s.Loop, Voice = s.Voice, HasPosition = s.HasPosition, Position = s.Position,
        };

        /// <summary>Fx__Root nach Name (z.B. "EVTE_to_player_01"), 0 = keiner.</summary>
        public ulong FindRootByName(string name)
        {
            foreach (var kv in _rootNames) if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase) && Resolve(kv.Key).Count > 0) return kv.Key;
            return 0;
        }

        public bool HasEmitter(string name) => _emitters.ContainsKey(name ?? "");

        // ------------------------------------------------------------------ Abspielen (Hauptthread)
        AudioClip Clip(ulong rsc)
        {
            if (_clips.TryGetValue(rsc, out var c)) return c;
            c = null;
            if (_waves.TryGetValue(rsc, out var w))
            {
                c = AudioClip.Create("cin_" + rsc.ToString("X16"), w.data.Length / w.ch, w.ch, w.rate, false);
                c.SetData(w.data, 0);
            }
            _clips[rsc] = c;
            return c;
        }

        float Rand() { _seed = _seed * 0x7ff8a3edu + 0x2aa01d31u; return (float)(_seed / 4294967296.0); }

        float RandVolume(float r, float v) => r == 0f ? v : Mathf.Clamp01(v + r * Rand() - 0.5f * r);

        float RandPitch(float r, float p)
        {
            if (r == 0f) return p;
            float x = 2f * Rand() - 1f;
            return x >= 0f ? p * (1f + x * r) : (1f + x) * p - x * p / (1f + r);
        }

        /// <summary>Töne einer Fx-Wurzel abspielen. worldPos: Weltposition (null = Emitterposition bzw. 2D). Rückgabe: gestartete Quellen.</summary>
        public List<AudioSource> PlayRoot(ulong rootHc, float offset = 0f, bool loop = false, Vector3? worldPos = null, uint flags = 0)
            => Play(Resolve(rootHc), offset, loop, worldPos, flags);

        /// <summary>Entity__Fx (z.B. "C_OUTRO", "S_TimeStart") an seiner Position abspielen.</summary>
        public List<AudioSource> PlayEmitter(string name, float offset = 0f, bool loop = false, bool music = false)
        {
            if (!_emitters.TryGetValue(name ?? "", out var em)) return null;
            var list = new List<Sound>();
            foreach (var s in Resolve(em.root)) { var c = Copy(s); c.HasPosition = true; c.Position = em.pos; list.Add(c); }
            return Play(list, offset, loop, null, 0, music);
        }

        public List<AudioSource> Play(List<Sound> sounds, float offset, bool loop, Vector3? worldPos, uint flags, bool music = false)
        {
            var started = new List<AudioSource>();
            if (sounds == null || sounds.Count == 0) return started;
            if (_host == null) _host = new GameObject("TDU2 Kino-Töne");
            _voices.RemoveAll(v => v == null);
            foreach (var s in sounds)
            {
                var clip = Clip(s.Rsc);
                if (clip == null) continue;
                var go = new GameObject(s.Name);
                go.transform.SetParent(_host.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.clip = clip;
                float mix = s.Voice ? VoiceVolume : music ? MusicVolume : EffectsVolume;
                src.volume = Mathf.Clamp01(RandVolume(s.VolumeRandom, s.Volume) * MasterVolume * mix);
                src.priority = s.Voice ? 0 : music ? 200 : 128;
                src.pitch = Mathf.Clamp(RandPitch(s.PitchRandom, s.Pitch), 0.1f, 3f);
                src.loop = loop || s.Loop;
                src.dopplerLevel = 0f;
                Vector3? pos = worldPos;
                if (pos == null && s.HasPosition) pos = Frame != null ? Frame.TransformPoint(s.Position) : s.Position;
                // Stimmen immer verständlich: 2D (die Kino-Kamera ist oft weit von der Figur weg, 3D wäre kaum hörbar)
                bool spatial = pos.HasValue && (flags & Flag2D) == 0 && !s.Voice;
                src.spatialBlend = spatial ? 1f : 0f;
                if (pos.HasValue) go.transform.position = pos.Value;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = Mathf.Max(0.1f, s.ClipNear);
                src.maxDistance = Mathf.Max(src.minDistance + 1f, s.ClipFar);
                float start = offset * src.pitch;
                if (src.loop && clip.length > 0f) start %= clip.length;
                if (start >= clip.length) { UnityEngine.Object.Destroy(go); continue; }
                src.time = Mathf.Max(0f, start);
                src.Play();
                if (!src.loop) UnityEngine.Object.Destroy(go, (clip.length - start) / Mathf.Max(0.1f, src.pitch) + 0.25f);
                _voices.Add(src);
                started.Add(src);
            }
            return started;
        }

        public static void Stop(List<AudioSource> sources)
        {
            if (sources == null) return;
            foreach (var s in sources) if (s != null) UnityEngine.Object.Destroy(s.gameObject);
            sources.Clear();
        }

        /// <summary>Fx-Ereignisse des .cin im Intervall (from, to] (Spätstart: Versatz im Ton). Position: Zeile 3 der Ereignismatrix.</summary>
        public void UpdateCin(CinData cin, float from, float to, List<AudioSource> outVoices)
        {
            if (cin == null || _rootSources.Count == 0) return;
            foreach (var e in cin.FxEvents)
            {
                bool hit = (e.Time > from && e.Time <= to) || (from < 0f && e.Time <= 0f && to >= 0f);
                if (!hit || e.Reference < 0 || e.Reference >= cin.References.Count) continue;
                var m = e.World;
                var local = new Vector3(m[3, 0], m[3, 1], m[3, 2]);
                Vector3 world = Frame != null ? Frame.TransformPoint(local) : local;
                var v = PlayRoot(cin.References[e.Reference].Hashcode, Mathf.Max(to - e.Time, 0f), false, world, e.Flags);
                outVoices?.AddRange(v);
            }
        }

        /// <summary>Fx-Spuren (0x7846) der Darsteller einer .sce (z.B. Stimmen von Tess) im Intervall (from, to] der Szenenzeit.</summary>
        public void UpdateScene(CinScene sc, float from, float to, List<AudioSource> outVoices)
        {
            if (sc == null || _rootSources.Count == 0) return;
            foreach (var o in sc.Objects)
                foreach (var tr in o.Tracks)
                {
                    if (tr.Id != CinIds.TrackFx || tr.Components < 21) continue;
                    for (int k = 0; k < tr.Times.Length; k++)
                    {
                        float t = tr.Times[k];
                        bool hit = (t > from && t <= to) || (from < 0f && t <= 0f && to >= 0f);
                        if (!hit) continue;
                        int b = k * tr.Components;
                        uint reference = tr.Raw.Length > b ? tr.Raw[b] : 0xffffffffu;
                        uint flags = tr.Raw.Length > b + 1 ? tr.Raw[b + 1] : 0;
                        if (reference >= sc.References.Count || sc.References[(int)reference].Type != 0x010d) continue;
                        var off = new Vector3(tr.Values[b + 17], tr.Values[b + 18], tr.Values[b + 19]);
                        var local = sc.ToGame(o.SamplePosition(t) + off);
                        Vector3 world = Frame != null ? Frame.TransformPoint(local) : local;
                        var v = PlayRoot(sc.References[(int)reference].Hashcode, Mathf.Max(to - t, 0f), false, world, flags);
                        outVoices?.AddRange(v);
                    }
                }
        }

        public void StopAll()
        {
            foreach (var v in _voices) if (v != null) UnityEngine.Object.Destroy(v.gameObject);
            _voices.Clear();
        }

        public void Dispose()
        {
            StopAll();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            foreach (var c in _clips.Values) if (c != null) UnityEngine.Object.Destroy(c);
            _clips.Clear();
            _waves.Clear();
        }
    }
}
