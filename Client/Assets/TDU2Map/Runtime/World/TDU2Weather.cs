using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.World
{
    /// <summary>Wetterarten wie im Spiel (Tabelle der exe: Sunny Cloudy Rainny Before_rain After_rain Tempest).</summary>
    public enum WeatherKind { Sunny = 0, Cloudy = 1, Rainy = 2, BeforeRain = 3, AfterRain = 4, Tempest = 5 }

    /// <summary>
    /// islands/&lt;insel&gt;/fx/game_weathers.xmb (big-endian XMBF): der Spieltag als 24 Schlüsselbilder (00, 01.30 ... 23.20),
    /// je 7 Blöcke: Block 0 nur zeitabhängig (sundirection, Tunnel), Block 1-6 die Wetterarten mit je ~151 Parametern
    /// (sun_diffuse/ambient, fog_*, rain_*, thunder_*, outside_street_lightmap, spot_*_lightmap_day_night ...).
    /// </summary>
    public sealed class TDU2WeatherData
    {
        public sealed class Key
        {
            public string Name;
            public float Hour;
            public Dictionary<string, Vector4>[] Blocks;
            public Dictionary<string, string>[] Strings;
        }

        public readonly List<Key> Keys = new List<Key>();

        public static TDU2WeatherData Load(string path)
        {
            var b = File.ReadAllBytes(path);
            var root = new BeXmb(b).Root() as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("weather", out var wo) || !(wo is List<object> weathers)) throw new InvalidDataException("kein WEATHER_EXPORT: " + path);
            var data = new TDU2WeatherData();
            foreach (var o in weathers)
            {
                if (!(o is Dictionary<string, object> w)) continue;
                var k = new Key
                {
                    Name = w.TryGetValue("name", out var n) ? n as string : "",
                    Hour = ToInt(w, "hour") + ToInt(w, "minutes") / 60f,
                };
                var blocks = w.TryGetValue("intemperys", out var io) ? io as List<object> : null;
                int nb = blocks != null ? blocks.Count : 0;
                k.Blocks = new Dictionary<string, Vector4>[Math.Max(nb, 7)];
                k.Strings = new Dictionary<string, string>[k.Blocks.Length];
                for (int i = 0; i < k.Blocks.Length; i++) { k.Blocks[i] = new Dictionary<string, Vector4>(); k.Strings[i] = new Dictionary<string, string>(); }
                for (int i = 0; i < nb; i++)
                {
                    if (!(blocks[i] is Dictionary<string, object> blk) || !blk.TryGetValue("weather_params", out var po) || !(po is List<object> ps)) continue;
                    foreach (var pp in ps)
                    {
                        if (!(pp is Dictionary<string, object> p) || !p.TryGetValue("name", out var pn) || !(pn is string name)) continue;
                        if (!p.TryGetValue("param", out var pv) || !(pv is Dictionary<string, object> v)) continue;
                        if (v.TryGetValue("value", out var sv) && sv is string s) { k.Strings[i][name] = s; continue; }
                        k.Blocks[i][name] = new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
                    }
                }
                data.Keys.Add(k);
            }
            data.Keys.Sort((a, c) => a.Hour.CompareTo(c.Hour));
            return data;
        }

        static int ToInt(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
        static float F(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 0f;

        /// <summary>Zwei Schlüsselbilder um die Uhrzeit (0..24) und der Mischanteil.</summary>
        public void Bracket(float hour, out Key a, out Key b, out float f)
        {
            int n = Keys.Count;
            a = b = Keys[0]; f = 0f;
            if (n == 1) return;
            int i = n - 1;
            for (int k = 0; k < n; k++) if (Keys[k].Hour <= hour) i = k;
            a = Keys[i]; b = Keys[(i + 1) % n];
            float h0 = a.Hour, h1 = b.Hour, h = hour;
            if (h1 <= h0) { h1 += 24f; if (h < h0) h += 24f; }
            f = h1 > h0 ? Mathf.Clamp01((h - h0) / (h1 - h0)) : 0f;
        }

        public Vector4 Value(Key k, int block, string name, Vector4 def)
        {
            if (block < 0 || block >= k.Blocks.Length) return def;
            return k.Blocks[block].TryGetValue(name, out var v) ? v : def;
        }

        /// <summary>Parameter zur Uhrzeit, zwischen zwei Wetterarten gemischt (Block = Art + 1; Block 0 = nur Zeit).</summary>
        public Vector4 Sample(string name, float hour, int blockA, int blockB, float t, Vector4 def = default)
        {
            Bracket(hour, out var a, out var b, out float f);
            var va = Vector4.Lerp(Value(a, blockA, name, def), Value(b, blockA, name, def), f);
            if (blockB == blockA || t <= 0f) return va;
            var vb = Vector4.Lerp(Value(a, blockB, name, def), Value(b, blockB, name, def), f);
            return Vector4.Lerp(va, vb, t);
        }

        /// <summary>Minimaler Leser für big-endian XMBF (Wetter-Exporte). Kinder eines Objekttyps: {Typ, Name}-Paare.</summary>
        sealed class BeXmb
        {
            readonly byte[] _b; readonly int _desc, _meta, _data;
            public BeXmb(byte[] b)
            {
                if (b.Length < 28 || b[0] != 'X' || b[1] != 'M' || b[2] != 'B' || b[3] != 'F') throw new InvalidDataException("keine XMBF-Datei");
                _b = b; _desc = I(8); _meta = I(12); _data = I(16);
            }
            int I(int o) => (_b[o] << 24) | (_b[o + 1] << 16) | (_b[o + 2] << 8) | _b[o + 3];
            uint W(int i) => (uint)I(_meta + 4 * i);
            string CStr(int o)
            {
                int e = o; while (e < _b.Length && _b[e] != 0) e++;
                return Encoding.UTF8.GetString(_b, o, e - o);
            }
            public object Root() => Val(I(24), _data, 0);
            int SizeOf(int t)
            {
                uint tw = W(t); int ty = (int)(tw & 15);
                if (ty == 12) return 4;
                if (ty == 13 || ty == 14) return 8;
                return (int)((tw >> 15) & 0x1FFFF);
            }
            object Field(uint tp, int addr, int depth)
            {
                int t = (int)(tp & 0x3FFFFFFF);
                if ((tp & 0x80000000u) == 0) return Val(t, addr, depth);
                int n = I(addr), bas = _data + I(addr + 4), sz = SizeOf(t);
                var list = new List<object>(Math.Max(0, n));
                for (int i = 0; i < n; i++) list.Add(Val(t, bas + sz * i, depth + 1));
                return list;
            }
            object Val(int t, int addr, int depth)
            {
                if (depth > 32) return null;
                uint tw = W(t); int ty = (int)(tw & 15);
                switch (ty)
                {
                    case 0:
                        {
                            int n = (int)((tw >> 4) & 0x7FF), off = 0;
                            var obj = new Dictionary<string, object>(n);
                            for (int i = 0; i < n; i++)
                            {
                                uint tp = W(t + 2 + 2 * i); string name = CStr(_desc + (int)W(t + 3 + 2 * i));
                                obj[name] = Field(tp, addr + off, depth + 1);
                                off += (tp & 0x80000000u) != 0 ? 8 : SizeOf((int)(tp & 0x3FFFFFFF));
                            }
                            return obj;
                        }
                    case 1: return _b[addr] != 0;
                    case 2: return (int)(sbyte)_b[addr];
                    case 3: return (int)(short)((_b[addr] << 8) | _b[addr + 1]);
                    case 4: return I(addr);
                    case 6: return (int)_b[addr];
                    case 7: return (_b[addr] << 8) | _b[addr + 1];
                    case 8: return (long)(uint)I(addr);
                    case 10: return BitConverter.ToSingle(BitConverter.GetBytes(I(addr)), 0);
                    case 12: { int so = I(addr); return so == -1 ? null : CStr(_data + so); }
                    case 13:
                    case 14: return Val(I(addr), _data + I(addr + 4), depth + 1);
                    default: return null;
                }
            }
        }
    }

    /// <summary>
    /// Tageszeit und Wetter wie im Original (game_weathers.xmb), für alle Spieler gleich: Uhrzeit und Wetterfolge werden aus
    /// der Serverzeit (TDU2Session.ServerSeconds) berechnet – keine eigenen Netzwerkpakete nötig, jeder Client kommt zum
    /// selben Ergebnis. Steuert: Sonne (Richtung, Farbe, Schatten), Umgebungslicht, Nebel, Himmel-Helligkeit, Nachtleuchten
    /// (Straßenlampen, Neon, Lampenlicht der Masten), nasse Straßen, Regen, Gewitter, Scheinwerfer, Verschmutzung/Abwaschen,
    /// Reifengrip/-geräusch bei Nässe, Radio-Wettermeldungen und die Uhr im Pausemenü.
    /// </summary>
    public sealed class TDU2Weather : MonoBehaviour
    {
        public static TDU2Weather Instance { get; private set; }
        /// <summary>Spielzeit in Stunden (0..24).</summary>
        public static float Hour { get; private set; } = 12f;
        public static WeatherKind Kind { get; private set; } = WeatherKind.Sunny;
        /// <summary>0 = Tag, 1 = Nacht (spot_exterior_lightmap_day_night.y).</summary>
        public static float Night { get; private set; }
        /// <summary>Regenstärke 0..1 (rain_alpha / 120).</summary>
        public static float Rain { get; private set; }
        /// <summary>Nässe der Fahrbahn 0..1 (rain_ground_wet), trocknet nach dem Regen langsam ab.</summary>
        public static float GroundWet { get; private set; }
        /// <summary>Wetterwechsel: das Radio bringt als nächstes eine Wettermeldung.</summary>
        public static bool NewsPending;
        /// <summary>Scheinwerfer an (car_headlights_intensity &gt; 0: nachts, Dämmerung, Regen) – auch für Verkehr und Mitspieler.</summary>
        public static bool HeadlightsWanted { get; private set; }

        [Header("Zeit (aus der Serverzeit -> im Mehrspieler synchron)")]
        [Tooltip("Echtzeit-Minuten für einen Spieltag")] public float DayMinutes = 120f;
        [Tooltip("Verschiebung der Uhr (Stunden)")] public float HourOffset = 0f;
        [Tooltip(">= 0: feste Uhrzeit (Test)")] public float ForceHour = -1f;
        [Header("Wetter")]
        [Tooltip("Echtzeit-Minuten je Wetterabschnitt")] public float WeatherMinutes = 14f;
        [Tooltip("Übergang zwischen zwei Wetterarten (Sekunden)")] public float TransitionSeconds = 120f;
        [Tooltip(">= 0: feste Wetterart (0 Sonne, 1 Wolken, 2 Regen, 3 vor Regen, 4 nach Regen, 5 Gewitter)")] public int ForceKind = -1;
        [Header("Umsetzung")]
        [Tooltip("Sonne/Ambient der Wetterdatei x Faktor (die Werte sind für die Original-Shader, ~0.65 / 0.75 passt zu unseren)")]
        public float DiffuseScale = 0.65f, AmbientScale = 0.75f;
        public bool ApplyFog = true;
        [Range(0f, 1f)] public float FogStrength = 0.05f;
        public bool AutoHeadlights = true;
        [Tooltip("Straßenlampen: echte Lichter an den nächsten Masten (nachts)")] public int LampLights = 64;
        public float LampRadius = 140f;
        [Range(0f, 2f)] public float LampIntensity = 1f;
        [Tooltip("Straßenlicht im Gelände (Splatmap G) x outside_street_lightmap")][Range(0f, 1f)] public float StreetLightStrength = 0.5f;
        public int RainDrops = 1400;

        TDU2MapStreamer _streamer;
        TDU2WeatherData _data;
        string _error;
        Light _sun;
        TDU2WorldLighting _lighting;
        Material _sky; float _skyExposure = -1f;
        WeatherKind _lastKind = (WeatherKind)(-1);
        float _wet;
        bool _headOn, _headInit;
        float _nextSlow;
        readonly Dictionary<long, WeatherKind> _kindCache = new Dictionary<long, WeatherKind>();

        // Straßenlampen (streetlamps.xml)
        struct LampDef { public Color Color; public float Intensity, Near, Far, Open, Falloff; }
        readonly Dictionary<string, LampDef> _lampDefs = new Dictionary<string, LampDef>(StringComparer.OrdinalIgnoreCase);
        readonly List<Light> _lampPool = new List<Light>();
        readonly List<float> _lampBase = new List<float>();
        readonly List<(Matrix4x4 m, Bounds b, string name)> _lampHits = new List<(Matrix4x4, Bounds, string)>();
        float _nextLampScan;

        // Regen / Gewitter
        RainField _rain;
        bool _wasRaining;
        float _nextThunder, _flash;

        static readonly int IdNightK = Shader.PropertyToID("_TDU2NightK");
        static readonly int IdOceanFresnel = Shader.PropertyToID("_TDU2OceanFresnel"), IdOceanSpec = Shader.PropertyToID("_TDU2OceanSpec"),
            IdOceanRefl = Shader.PropertyToID("_TDU2OceanRefl"), IdOceanSpecPow = Shader.PropertyToID("_TDU2OceanSpecPower"),
            IdOceanBlur = Shader.PropertyToID("_TDU2OceanBlur"), IdOceanNMap = Shader.PropertyToID("_TDU2OceanNMap"), IdOceanSet = Shader.PropertyToID("_TDU2OceanSet");
        static readonly int IdWet = Shader.PropertyToID("_TDU2Wet"), IdDayDim = Shader.PropertyToID("_TDU2DayDim"),
            IdStreet = Shader.PropertyToID("_TDU2StreetLight"), IdNightWin = Shader.PropertyToID("_TDU2NightWindows");

        public void Init(TDU2MapStreamer streamer, string weatherFile, string lampsFile)
        {
            _streamer = streamer;
            try
            {
                if (File.Exists(weatherFile)) { _data = TDU2WeatherData.Load(weatherFile); DebugManager.Info($"Wetter: {_data.Keys.Count} Zeitpunkte aus {Path.GetFileName(weatherFile)}"); }
                else _error = "game_weathers.xmb fehlt: " + weatherFile;
            }
            catch (Exception ex) { _error = ex.Message; }
            if (_error != null) Debug.LogWarning("[TDU2] Wetter: " + _error);
            try { if (File.Exists(lampsFile)) LoadLamps(File.ReadAllText(lampsFile)); } catch (Exception) { }
        }

        void Awake() { Instance = this; }

        // ------------------------------------------------------------------ Kameras: Nebel/Licht nur für die Spielkamera
        // Die große Karte und die Minimap filmen die Insel von weit oben -> ohne Nebel und immer mit Tageslicht.
        bool _fogWanted, _mapState; Color _saveAmb; AmbientMode _saveMode; Quaternion _saveSunRot; Color _saveSunCol; float _saveSunInt, _saveShadow;

        void OnEnable() { Camera.onPreCull += PreCull; Camera.onPostRender += PostRender; }
        void OnDisable() { Camera.onPreCull -= PreCull; Camera.onPostRender -= PostRender; }

        static bool IsMapCamera(Camera c) => c != null && c.name != null && (c.name.StartsWith("TDU2 Map Camera") || c.name.StartsWith("TDU2 Minimap Camera"));

        void PreCull(Camera c)
        {
            if (c == null) return;
            bool map = IsMapCamera(c);
            bool game = c == Camera.main || c.cameraType == CameraType.SceneView;
            RenderSettings.fog = _fogWanted && game && !map;
            if (map && _data != null && !_mapState)
            {
                // Tageslicht (12:00, Sonne) für die Karte
                _mapState = true;
                _saveAmb = RenderSettings.ambientLight; _saveMode = RenderSettings.ambientMode;
                RenderSettings.ambientMode = AmbientMode.Flat;
                var a = _data.Sample("sun_ambient", 12f, 1, 1, 0f, new Vector4(0.58f, 0.67f, 0.82f, 0));
                RenderSettings.ambientLight = new Color(a.x, a.y, a.z) * AmbientScale;
                if (_sun != null)
                {
                    _saveSunRot = _sun.transform.rotation; _saveSunCol = _sun.color; _saveSunInt = _sun.intensity; _saveShadow = _sun.shadowStrength;
                    var d = (Vector3)_data.Sample("sundirection", 12f, 0, 0, 0f, new Vector4(0, -0.856f, -0.423f, 0));
                    var root = _streamer != null ? _streamer.MapRoot : null;
                    var wd = root != null ? root.TransformDirection(d) : d;
                    if (wd.sqrMagnitude > 1e-6f) _sun.transform.rotation = Quaternion.LookRotation(wd.normalized);
                    var df = _data.Sample("sun_diffuse", 12f, 1, 1, 0f, new Vector4(2.01f, 1.74f, 1.36f, 0)) * DiffuseScale - a * AmbientScale;
                    float k = Mathf.Max(0.001f, Mathf.Max(df.x, Mathf.Max(df.y, df.z)));
                    _sun.color = new Color(df.x / k, df.y / k, df.z / k); _sun.intensity = k;
                }
                Shader.SetGlobalFloat(IdNightK, 0f); Shader.SetGlobalFloat(IdNightWin, 0f); Shader.SetGlobalFloat(IdWet, 0f);
            }
        }

        void PostRender(Camera c)
        {
            if (!_mapState || !IsMapCamera(c)) return;
            _mapState = false;
            RenderSettings.ambientMode = _saveMode; RenderSettings.ambientLight = _saveAmb;
            if (_sun != null) { _sun.transform.rotation = _saveSunRot; _sun.color = _saveSunCol; _sun.intensity = _saveSunInt; _sun.shadowStrength = _saveShadow; }
            Shader.SetGlobalFloat(IdNightK, _nightK); Shader.SetGlobalFloat(IdNightWin, _nightWin); Shader.SetGlobalFloat(IdWet, _wetShown);
        }

        float _nightK, _nightWin, _wetShown;

        // Zustand der Szene vor dem Wetter (Sonne) – in Interieurs wiederhergestellt, wie es vorher war
        bool _origSaved; Quaternion _origSunRot; Color _origSunCol; float _origSunInt, _origShadow;
        void SaveOriginal()
        {
            if (_origSaved || _sun == null) return;
            _origSaved = true;
            _origSunRot = _sun.transform.rotation; _origSunCol = _sun.color; _origSunInt = _sun.intensity; _origShadow = _sun.shadowStrength;
        }
        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var l in _lampPool) if (l != null) Destroy(l.gameObject);
            _rain?.Destroy();
            Shader.SetGlobalFloat(IdWet, 0f); Shader.SetGlobalFloat(IdDayDim, 0f); Shader.SetGlobalFloat(IdStreet, 0f);
        }

        void LoadLamps(string xml)
        {
            foreach (Match m in Regex.Matches(xml, "<Lamp\\s+([^>]*)/>"))
            {
                string a = m.Groups[1].Value;
                string Attr(string n) { var mm = Regex.Match(a, n + "\\s*=\\s*\"([^\"]*)\""); return mm.Success ? mm.Groups[1].Value : null; }
                string name = Attr("name"); string col = Attr("color");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(col)) continue;
                col = col.TrimStart('#');
                if (col.Length < 8) continue;
                byte r = Convert.ToByte(col.Substring(0, 2), 16), g = Convert.ToByte(col.Substring(2, 2), 16), b = Convert.ToByte(col.Substring(4, 2), 16), al = Convert.ToByte(col.Substring(6, 2), 16);
                float P(string n, float d) => float.TryParse(Attr(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
                _lampDefs[name] = new LampDef
                {
                    Color = new Color32(r, g, b, 255),
                    Intensity = al / 255f,
                    Near = P("near", 8f),
                    Far = P("far", 10f),
                    Open = P("open_angle", 30f),
                    Falloff = P("falloff_angle", 50f),
                };
            }
        }

        // ------------------------------------------------------------------ Zeit und Wetterfolge (deterministisch)
        double Now => TDU2.Online.TDU2Session.ServerSeconds;

        static uint Hash(long x)
        {
            ulong h = (ulong)x * 0x9E3779B97F4A7C15UL;
            h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27;
            return (uint)(h ^ (h >> 32));
        }

        /// <summary>Wetterart im Abschnitt seg: Markow-Kette, von einem festen Startpunkt 48 Abschnitte vorher gerechnet.</summary>
        WeatherKind KindAt(long seg)
        {
            if (_kindCache.TryGetValue(seg, out var k)) return k;
            var s = WeatherKind.Sunny;
            for (long i = seg - 48; i <= seg; i++) s = Next(s, Hash(i) % 1000 / 1000f);
            if (_kindCache.Count > 64) _kindCache.Clear();
            _kindCache[seg] = s;
            return s;
        }

        static WeatherKind Next(WeatherKind s, float r)
        {
            switch (s)
            {
                case WeatherKind.Sunny: return r < 0.62f ? WeatherKind.Sunny : r < 0.9f ? WeatherKind.Cloudy : WeatherKind.BeforeRain;
                case WeatherKind.Cloudy: return r < 0.35f ? WeatherKind.Sunny : r < 0.65f ? WeatherKind.Cloudy : WeatherKind.BeforeRain;
                case WeatherKind.BeforeRain: return r < 0.7f ? WeatherKind.Rainy : r < 0.88f ? WeatherKind.Tempest : WeatherKind.Cloudy;
                case WeatherKind.Rainy: return r < 0.4f ? WeatherKind.Rainy : r < 0.5f ? WeatherKind.Tempest : WeatherKind.AfterRain;
                case WeatherKind.Tempest: return r < 0.3f ? WeatherKind.Tempest : r < 0.6f ? WeatherKind.Rainy : WeatherKind.AfterRain;
                default: return r < 0.7f ? WeatherKind.Sunny : WeatherKind.Cloudy;   // AfterRain
            }
        }

        /// <summary>Radio: passende Wettermeldung (Regen / Gewitter / Sonne) oder null bei Wolken/vor dem Regen.</summary>
        public static TDU2.Vehicles.RadioMessageType? RadioWeather()
        {
            switch (Kind)
            {
                case WeatherKind.Rainy: return TDU2.Vehicles.RadioMessageType.WeatherRain;
                case WeatherKind.Tempest: return TDU2.Vehicles.RadioMessageType.WeatherStorm;
                case WeatherKind.Sunny:
                case WeatherKind.AfterRain: return Night < 0.5f ? TDU2.Vehicles.RadioMessageType.WeatherSun : (TDU2.Vehicles.RadioMessageType?)null;
                default: return null;
            }
        }

        /// <summary>Radio: Tageszeit-Moderation (Sonnenaufgang 5-9, Mittag 11-14:30, Abend 18:30-22) oder null.</summary>
        public static TDU2.Vehicles.RadioMessageType? RadioTime()
        {
            float h = Hour;
            if (h >= 5f && h < 9f) return TDU2.Vehicles.RadioMessageType.TimeSunrise;
            if (h >= 11f && h < 14.5f) return TDU2.Vehicles.RadioMessageType.TimeMidday;
            if (h >= 18.5f && h < 22f) return TDU2.Vehicles.RadioMessageType.TimeNightfall;
            return null;
        }

        // ------------------------------------------------------------------ jedes Bild
        void Update()
        {
            if (_data == null || _data.Keys.Count == 0) return;
            double now = Now;
            float dayLen = Mathf.Max(1f, DayMinutes) * 60f;
            float hour = ForceHour >= 0f ? ForceHour : (float)(((now / dayLen) * 24.0 + HourOffset) % 24.0);
            if (hour < 0f) hour += 24f;
            Hour = hour;

            // Wetter: Abschnitt und Übergang vom vorigen
            double segLen = Mathf.Max(1f, WeatherMinutes) * 60.0;
            long seg = (long)Math.Floor(now / segLen);
            WeatherKind cur = ForceKind >= 0 ? (WeatherKind)Mathf.Clamp(ForceKind, 0, 5) : KindAt(seg);
            WeatherKind prev = ForceKind >= 0 ? cur : KindAt(seg - 1);
            float since = (float)(now - seg * segLen);
            float t = TransitionSeconds > 0f ? Mathf.Clamp01(since / TransitionSeconds) : 1f;
            t = t * t * (3f - 2f * t);
            Kind = t >= 0.5f ? cur : prev;
            if (Kind != _lastKind) { if ((int)_lastKind >= 0) NewsPending = true; _lastKind = Kind; }
            int ba = (int)prev + 1, bb = (int)cur + 1;

            Vector4 S(string n, Vector4 d = default) => _data.Sample(n, hour, ba, bb, t, d);

            // ---------------- Sonne / Licht
            var dir = (Vector3)_data.Sample("sundirection", hour, 0, 0, 0f, new Vector4(0, -0.856f, -0.423f, 0));
            if (_sun == null) FindSun();
            var diff = S("sun_diffuse", new Vector4(2.01f, 1.74f, 1.36f, 0));
            var amb = S("sun_ambient", new Vector4(0.58f, 0.67f, 0.82f, 0));
            float shadowOp = S("sun_shadow_opacity", Vector4.one).x;
            // Gewitter: Blitze hellen kurz alles auf
            float flash = UpdateThunder(S("thunder_off_on").x);
            bool inside = InsideInterior();
            if (inside) _fogWanted = false;
            SaveOriginal();
            if (_sun != null && inside)
            {
                // Interieur: Sonne wie vor dem Wetter (die Interieurs sind darauf abgestimmt), kein Nebel
                _sun.transform.rotation = _origSunRot; _sun.color = _origSunCol; _sun.intensity = _origSunInt; _sun.shadowStrength = _origShadow;
            }
            else if (_sun != null)
            {
                var root = _streamer != null ? _streamer.MapRoot : null;
                var wd = root != null ? root.TransformDirection(dir) : dir;
                if (wd.sqrMagnitude > 1e-6f) _sun.transform.rotation = Quaternion.LookRotation(wd.normalized);
                _sun.shadowStrength = Mathf.Clamp01(shadowOp);
            }
            if (_lighting == null) _lighting = FindAnyObjectByType<TDU2WorldLighting>();
            if (!inside)
            {
                var d = new Color(diff.x, diff.y, diff.z, 1f) * DiffuseScale;
                var a = new Color(amb.x, amb.y, amb.z, 1f) * AmbientScale * (1f + flash * 2.5f);
                if (_lighting != null) { _lighting.SunDiffuse = d; _lighting.Ambient = a; _lighting.Exposure = 1f; _lighting.Apply(); }
                else
                {
                    RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = a;
                    if (_sun != null) { _sun.color = d; _sun.intensity = 1f; }
                }
                // ---------------- Nebel
                _fogWanted = ApplyFog;
                if (ApplyFog)
                {
                    var fc = S("fog_color", new Vector4(0.7f, 0.62f, 0.55f, 0));
                    float fs = S("fog_start").x, fe = Mathf.Max(fs + 10f, S("fog_end", new Vector4(1500, 0, 0, 0)).x), fm = Mathf.Clamp(S("fog_max", new Vector4(0.33f, 0, 0, 0)).x * FogStrength, 0.05f, 1f);
                    RenderSettings.fogMode = FogMode.Linear;
                    RenderSettings.fogColor = new Color(fc.x, fc.y, fc.z, 1f);
                    RenderSettings.fogStartDistance = fs;
                    RenderSettings.fogEndDistance = fs + (fe - fs) / fm;   // bei fog_end ist der Nebel bei fog_max
                }
            }

            // ---------------- Meer (TD_Ocean): Fresnel, Reflexion, Glanz, Normalmap-Kacheln – teils nur im ersten Wetterblock angegeben
            Vector4 O(string n, Vector4 d) => _data.Sample(n, hour, ba, bb, t, _data.Sample(n, hour, 1, 1, 0f, d));
            Shader.SetGlobalVector(IdOceanFresnel, O("ocean_fresnel", new Vector4(0.3f, 30f, 0.5f, 0f)));
            Shader.SetGlobalVector(IdOceanSpec, O("ocean_specularColorAndCutOff", new Vector4(0.502f, 0.596f, 0.674f, 0f)));
            Shader.SetGlobalVector(IdOceanRefl, O("ocean_reflectionColor", new Vector4(0.498f, 0.498f, 0.498f, 0f)));
            Shader.SetGlobalFloat(IdOceanSpecPow, O("ocean_specularPower", new Vector4(20f, 0, 0, 0)).x);
            Shader.SetGlobalFloat(IdOceanBlur, O("ocean_reflectionBluriness", new Vector4(0.05f, 0, 0, 0)).x);
            Shader.SetGlobalVector(IdOceanNMap, new Vector4(O("ocean_NMapHR", new Vector4(0.15f, 1f, 0, 0)).x, O("ocean_NMapLR", new Vector4(0.015f, 1f, 0, 0)).x, 0f, 0f));
            Shader.SetGlobalFloat(IdOceanSet, 1f);

            // ---------------- Tag/Nacht-Leuchten
            Night = Mathf.Clamp01(S("spot_exterior_lightmap_day_night", new Vector4(1, 0, 0, 0)).y);
            float street = Mathf.Clamp01(S("outside_street_lightmap").x / 1.5f);
            Shader.SetGlobalFloat(IdDayDim, 1f - Mathf.Max(street, Night));
            _nightK = Mathf.Max(street, Night);
            Shader.SetGlobalFloat(IdNightK, _nightK);
            Shader.SetGlobalFloat(IdStreet, street * 1.5f * StreetLightStrength);
            _nightWin = Mathf.Max(S("building_nightwindow_frequency").x, Night);
            Shader.SetGlobalFloat(IdNightWin, _nightWin);
            UpdateSky(Night, Kind);

            // ---------------- Regen, Nässe
            Rain = Mathf.Clamp01(S("rain_alpha").x / 120f);
            float wetTarget = Mathf.Clamp01(S("rain_ground_wet").x / 0.7f);
            // nass werden in ~1 min, abtrocknen in ~6 min
            _wet = Mathf.MoveTowards(_wet, wetTarget, Time.deltaTime * (wetTarget > _wet ? 1f / 60f : 1f / 360f));
            GroundWet = _wet;
            // Es regnet -> Straße ist mindestens so nass wie der Regen (auch wenn ein Block rain_ground_wet nicht setzt)
            _wetShown = inside ? 0f : Mathf.Max(_wet, Rain);
            Shader.SetGlobalFloat(IdWet, _wetShown);
            if (RainDrops > 0 && Rain > 0.01f) { if (_rain == null) _rain = new RainField(RainDrops); }
            _rain?.Update(Rain * (inside ? 0f : 1f), Time.deltaTime, Mathf.Lerp(1f, 0.35f, Night));
            // Diagnose: nur beim Umschalten loggen (hilft, wenn Regen "einfach aufhört")
            bool rainingNow = Rain > 0.01f && !inside;
            if (rainingNow != _wasRaining)
            {
                _wasRaining = rainingNow;
                DebugManager.Debug($"Regen {(rainingNow ? "an" : "aus")}: Rain={Rain:0.00} Nässe={_wetShown:0.00} inside={inside} Kind={Kind} ForceKind={ForceKind}");
            }

            // ---------------- seltener: Lampen, Scheinwerfer, Schmutz
            if (Time.unscaledTime >= _nextSlow)
            {
                _nextSlow = Time.unscaledTime + 0.5f;
                UpdateHeadlights(S("car_headlights_intensity").x > 0.05f);
                UpdateCarDirt();
            }
            UpdateLamps(street, inside);
        }

        TDU2.Vehicles.TDU2Interiors[] _interiors;
        float _nextInteriorScan;

        bool InsideInterior()
        {
            if (_interiors == null || Time.unscaledTime >= _nextInteriorScan)
            {
                _interiors = FindObjectsByType<TDU2.Vehicles.TDU2Interiors>(FindObjectsSortMode.None);
                _nextInteriorScan = Time.unscaledTime + 2f;
            }
            foreach (var i in _interiors) if (i != null && i.Inside) return true;
            return false;
        }

        void FindSun()
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { _sun = l; break; }
        }

        void UpdateSky(float night, WeatherKind kind)
        {
            var sky = RenderSettings.skybox;
            if (sky == null || !sky.HasProperty("_Exposure")) return;
            if (sky != _sky)
            {
                // Kopie verwenden: das Skybox-Material ist ein Asset (Änderungen würden im Editor gespeichert)
                _skyExposure = sky.GetFloat("_Exposure");
                _sky = new Material(sky) { name = sky.name + " (Wetter)" };
                RenderSettings.skybox = _sky;
                sky = _sky;
            }
            float cloud = kind == WeatherKind.Rainy || kind == WeatherKind.Tempest ? 0.55f : kind == WeatherKind.Cloudy || kind == WeatherKind.BeforeRain ? 0.8f : 1f;
            sky.SetFloat("_Exposure", _skyExposure * Mathf.Lerp(1f, 0.06f, night) * cloud * (1f + _flash * 3f));
        }

        float UpdateThunder(float on)
        {
            if (on < 0.5f) { _flash = 0f; return 0f; }
            if (Time.time >= _nextThunder)
            {
                _nextThunder = Time.time + UnityEngine.Random.Range(4f, 14f);
                _flash = UnityEngine.Random.Range(0.6f, 1f);
            }
            _flash = Mathf.MoveTowards(_flash, 0f, Time.deltaTime * 4f);
            // Flackern
            return _flash > 0f ? _flash * (0.6f + 0.4f * Mathf.PerlinNoise(Time.time * 25f, 0.3f)) : 0f;
        }

        readonly HashSet<int> _autoLit = new HashSet<int>();

        void UpdateHeadlights(bool want)
        {
            HeadlightsWanted = want;
            bool changed = !_headInit || want != _headOn;
            _headInit = true; _headOn = want;
            if (changed) _autoLit.Clear();
            foreach (var l in FindObjectsByType<TDU2.Vehicles.TDU2VehicleLights>(FindObjectsSortMode.None))
            {
                if (l == null) continue;
                // Leuchtstärke der Gläser/Flares: tagsüber gedämpft, nachts voll
                l.Brightness = Mathf.Lerp(0.3f, 1.1f, Night);
                if (!AutoHeadlights || l.External) continue;
                // eigenes Auto: beim Wechsel Tag/Nacht und bei neu gespawnten Autos einmal setzen, danach darf man selbst schalten
                if (_autoLit.Add(l.GetInstanceID()) && (want ? l.Headlights == 0 : l.Headlights == 1)) l.Headlights = want ? 1 : 0;
            }
        }

        void UpdateCarDirt()
        {
            bool raining = Rain > 0.2f;
            foreach (var d in FindObjectsByType<TDU2.Vehicles.TDU2CarDirt>(FindObjectsSortMode.None))
                if (d != null) d.Raining = raining;   // DBCarDirtData: im Regen wäscht sich der Schmutz ab
        }

        // ------------------------------------------------------------------ Straßenlampen
        static readonly string[] LampPrefixes = { "SF_LIGH" };

        void UpdateLamps(float on, bool inside)
        {
            if (LampLights <= 0) return;
            float k = inside ? 0f : on;
            if (k <= 0.01f)
            {
                foreach (var l in _lampPool) if (l != null && l.enabled) l.enabled = false;
                return;
            }
            if (Time.unscaledTime < _nextLampScan)
            {
                for (int i = 0; i < _lampPool.Count; i++)
                    if (_lampPool[i] != null && _lampPool[i].enabled) _lampPool[i].intensity = _lampBase[i] * k * LampIntensity;
                return;
            }
            _nextLampScan = Time.unscaledTime + 0.7f;
            var cam = Camera.main;
            if (cam == null) return;
            var cp = cam.transform.position;
            _lampHits.Clear();
            Vegetation_FindInstances(cp);
            // Lampenköpfe sammeln, nächste zuerst
            var heads = new List<(float d, Vector3 p, LampDef def)>();
            foreach (var (m, b, name) in _lampHits)
            {
                var def = DefFor(name);
                var c = b.center; var e = b.extents;
                float top = b.max.y - 0.35f;
                if (e.x > 1.2f || e.z > 1.2f)
                {
                    // Doppelausleger: zwei Köpfe an den Enden der langen Achse
                    var ax = e.x >= e.z ? new Vector3(e.x * 0.85f, 0, 0) : new Vector3(0, 0, e.z * 0.85f);
                    AddHead(heads, m.MultiplyPoint3x4(new Vector3(c.x, top, c.z) + ax), cp, def);
                    AddHead(heads, m.MultiplyPoint3x4(new Vector3(c.x, top, c.z) - ax), cp, def);
                }
                else AddHead(heads, m.MultiplyPoint3x4(new Vector3(c.x, top, c.z)), cp, def);
            }
            heads.Sort((x, y) => x.d.CompareTo(y.d));
            int n = Mathf.Min(LampLights, heads.Count);
            while (_lampPool.Count < n)
            {
                var go = new GameObject("TDU2 StreetLamp");
                go.transform.SetParent(transform, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot; l.shadows = LightShadows.None; l.renderMode = LightRenderMode.ForcePixel;
                _lampPool.Add(l); _lampBase.Add(3f);
            }
            for (int i = 0; i < _lampPool.Count; i++)
            {
                var l = _lampPool[i];
                if (i >= n) { l.enabled = false; continue; }
                var h = heads[i];
                l.transform.SetPositionAndRotation(h.p, Quaternion.LookRotation(Vector3.down, Vector3.forward));
                l.color = h.def.Color;
                l.range = Mathf.Max(6f, h.def.Far * 1.6f);
                l.spotAngle = Mathf.Clamp(h.def.Falloff * 2f, 30f, 150f);
                l.innerSpotAngle = Mathf.Clamp(h.def.Open * 2f, 1f, l.spotAngle - 1f);
                _lampBase[i] = 2.2f + h.def.Intensity * 4f;
                l.intensity = _lampBase[i] * k * LampIntensity;
                l.enabled = true;
            }
        }

        static void AddHead(List<(float d, Vector3 p, LampDef def)> heads, Vector3 p, Vector3 cam, LampDef def) => heads.Add(((p - cam).sqrMagnitude, p, def));

        void Vegetation_FindInstances(Vector3 cp) => VegetationRenderer.FindInstances(LampPrefixes, cp, LampRadius, _lampHits);

        LampDef DefFor(string objName)
        {
            // Library-Name z.B. "SF_LIGHF" -> streetlamps.xml "SF_LighF"
            if (objName != null && _lampDefs.TryGetValue(objName, out var d)) return d;
            if (objName != null && objName.Length >= 8 && _lampDefs.TryGetValue(objName.Substring(0, 8), out d)) return d;
            return new LampDef { Color = new Color(0.97f, 0.84f, 0.65f), Intensity = 0.3f, Near = 8f, Far = 10f, Open = 28f, Falloff = 48f };
        }


        // ------------------------------------------------------------------ Regen
        /// <summary>Regenstreifen um die Kamera (eigenes Mesh, kein Partikelmodul nötig).</summary>
        sealed class RainField
        {
            readonly Vector3[] _p; readonly float[] _speed;
            readonly Mesh _mesh; readonly GameObject _go; readonly Material _mat;
            readonly Vector3[] _v; readonly Color32[] _c; readonly Vector2[] _uv;
            const float Box = 28f, Height = 18f;

            public RainField(int n)
            {
                _p = new Vector3[n]; _speed = new float[n];
                var rnd = new System.Random(1234);
                for (int i = 0; i < n; i++)
                {
                    _p[i] = new Vector3((float)rnd.NextDouble() * 2f * Box - Box, (float)rnd.NextDouble() * Height, (float)rnd.NextDouble() * 2f * Box - Box);
                    _speed[i] = 14f + (float)rnd.NextDouble() * 6f;
                }
                _v = new Vector3[n * 4]; _c = new Color32[n * 4]; _uv = new Vector2[n * 4];
                var t = new int[n * 6];
                for (int i = 0; i < n; i++)
                {
                    int b = i * 4;
                    _uv[b] = new Vector2(0, 0); _uv[b + 1] = new Vector2(0, 1); _uv[b + 2] = new Vector2(1, 1); _uv[b + 3] = new Vector2(1, 0);
                    t[i * 6] = b; t[i * 6 + 1] = b + 1; t[i * 6 + 2] = b + 2; t[i * 6 + 3] = b; t[i * 6 + 4] = b + 2; t[i * 6 + 5] = b + 3;
                }
                _mesh = new Mesh { name = "Rain", indexFormat = IndexFormat.UInt32 };
                _mesh.MarkDynamic();
                _mesh.vertices = _v; _mesh.colors32 = _c; _mesh.uv = _uv; _mesh.triangles = t;
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
                var tex = new Texture2D(4, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "RainStreak" };
                var px = new Color32[4 * 32];
                for (int y = 0; y < 32; y++) for (int x = 0; x < 4; x++)
                    {
                        float a = Mathf.Sin(y / 31f * Mathf.PI) * (x == 1 || x == 2 ? 1f : 0.35f);
                        px[y * 4 + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                tex.SetPixels32(px); tex.Apply();
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                         ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Unlit/Transparent");
                if (sh == null) { Debug.LogWarning("TDU2Weather: kein Regen-Shader gefunden (Sprites/Default fehlt im Build?)"); sh = Shader.Find("Standard"); }
                _mat = new Material(sh) { name = "Rain", mainTexture = tex, renderQueue = 3100 };
                _go = new GameObject("TDU2 Rain");
                _go.layer = 2;   // "Ignore Raycast": von der Auto-Reflexionssonde ausgeschlossen (sonst leuchten Tropfen weiß auf dem Lack)
                _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = _go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
            }

            public void Update(float amount, float dt, float brightness)
            {
                var cam = Camera.main;
                bool show = amount > 0.005f && cam != null;
                if (_go.activeSelf != show) _go.SetActive(show);
                if (!show) return;
                var cp = cam.transform.position;
                var right = cam.transform.right;
                int n = _p.Length, active = Mathf.RoundToInt(n * Mathf.Clamp01(amount));
                byte alpha = (byte)(Mathf.Lerp(25f, 85f, amount) * brightness);
                var fall = new Vector3(0.6f, -1f, 0.25f).normalized;
                for (int i = 0; i < n; i++)
                {
                    int b = i * 4;
                    if (i >= active) { _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = cp; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = default; continue; }
                    var p = _p[i] + fall * (_speed[i] * dt);
                    if (p.y < 0f) p.y += Height;
                    _p[i] = p;
                    // Box um die Kamera wiederholen
                    float x = Mathf.Repeat(p.x - cp.x + Box, 2f * Box) - Box, z = Mathf.Repeat(p.z - cp.z + Box, 2f * Box) - Box;
                    var wp = new Vector3(cp.x + x, cp.y - Height * 0.35f + p.y, cp.z + z);
                    var len = fall * (0.9f + _speed[i] * 0.04f);
                    var w = right * 0.012f;
                    _v[b] = wp - w; _v[b + 1] = wp - w - len; _v[b + 2] = wp + w - len; _v[b + 3] = wp + w;
                    var c = new Color32((byte)(150f * brightness), (byte)(162f * brightness), (byte)(180f * brightness), alpha);
                    _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
                }
                _mesh.vertices = _v; _mesh.colors32 = _c;
                // Bounds fest um die Kamera: verhindert Frustum-Culling, wenn alle Tropfen gerade auf einem Punkt liegen
                _mesh.bounds = new Bounds(cp, Vector3.one * (Box * 2f + Height) * 2f);
            }

            public void Destroy()
            {
                if (_go != null) UnityEngine.Object.Destroy(_go);
                if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
                if (_mat != null) UnityEngine.Object.Destroy(_mat);
            }
        }
    }
}