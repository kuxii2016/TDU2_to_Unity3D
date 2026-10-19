using System;
using System.IO;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Original-Instrumente aus GAUGES (Euro/Bnk/frontend/hires/gauges.bnk, VMF-Flash) wie im Spiel:
    ///  - Stil des Autos: Frame-Label "Style" aus hud.ini (z.B. "458_Italia") in /HUD/compteur_ext und /HUD/compteur_int
    ///  - Ziffernblatt/Nadel aus der Instrumentenbank des Autos (frontend/hires/gauges/&lt;_hud_file_name&gt;.bnk):
    ///    c_ext = EXT_RPM, c_int_sp = INT_KMH/INT_MPH, c_int_rpm = INT_RPM, aiguille_speed = Needle
    ///  - Nadelwinkel: hud.ini "exterior_rpm"/"interior_*" (val_min..val_max -> ori_min..ori_max Grad)
    ///  - Variablen: /:Speed_c /:Speed_d /:Speed_u (Ziffern), /:Gear, /:SpeedUnit, /:Km + /:Centaine (Kilometerzähler)
    ///  - Außenkamera: compteur_ext, Stoßstange/Motorhaube: compteur_int, Cockpit: speed_cokpit
    /// </summary>
    public sealed class TDU2VehicleHud : MonoBehaviour
    {
        public TDU2Vehicle Vehicle;
        public TDU2VehicleCamera VehicleCamera;
        public bool ShowHud = true;
        public string Error { get; private set; }

        VmfPlayer _player;
        GaugeConfig _cfg;
        bool _mph;
        readonly System.Collections.Generic.List<Texture2D> _owned = new System.Collections.Generic.List<Texture2D>();
        string _style;
        int _lastLayout = -1;
        // Kilometerzähler (/:Km ganze Kilometer bzw. Meilen, /:Centaine Zehntel), pro Auto in PlayerPrefs gespeichert
        double _odoMeters;
        string _odoKey;
        Vector3 _lastPos;
        bool _hasLastPos;
        float _odoSaveTimer;

        public void Bind(string bnkDir, TDU2Vehicle vehicle, CarAssets assets, TDU2VehicleCamera cam)
        {
            SaveOdometer();
            Vehicle = vehicle; VehicleCamera = cam; Error = null;
            _odoKey = vehicle?.Car != null ? "TDU2.Odo." + vehicle.Car.Id : null;
            _odoMeters = 0;
            if (_odoKey != null) { try { _odoMeters = double.Parse(PlayerPrefs.GetString(_odoKey, "0"), System.Globalization.CultureInfo.InvariantCulture); } catch { _odoMeters = 0; } }
            _hasLastPos = false;
            _cfg = assets?.Gauges; _mph = vehicle?.Car != null && !vehicle.Car.Kmh;
            try
            {
                if (_player == null)
                {
                    string bank = null;
                    foreach (var res in new[] { "hires", "medres", "lowres" })
                    {
                        bank = CarDatabase.FindFile(Path.Combine(bnkDir, "frontend", res), "gauges.bnk");
                        if (bank != null) break;
                    }
                    if (bank == null) { Error = "frontend/*/gauges.bnk fehlt"; return; }
                    _player = gameObject.AddComponent<VmfPlayer>();
                    _player.Load(bank);
                    _player.GotoLabel("/", "on", true);
                }
                // Instrumententexturen des Autos
                foreach (var t in _owned) if (t != null) Destroy(t);
                _owned.Clear();
                _style = null;
                var gb = vehicle?.Car != null ? CarDatabase.GaugeBank(bnkDir, vehicle.Car) : null;
                if (gb != null)
                {
                    var bank = BnkArchive.Open(gb);
                    Texture2D Tex(string name)
                    {
                        var e = bank.Find(name + ".2db");
                        if (e == null) return null;
                        var t = TextureDef.Parse(bank.Read(e), name)?.CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                        if (t != null) _owned.Add(t);
                        return t;
                    }
                    _player.SetDynamicTexture("c_ext", Tex("ext_rpm"));
                    _player.SetDynamicTexture("c_int_sp", Tex(_mph ? "int_mph" : "int_kmh"));
                    _player.SetDynamicTexture("c_int_rpm", Tex("int_rpm"));
                    _player.SetDynamicTexture("aiguille_speed", Tex("needle"));
                    var ini = bank.Find("hud.ini");
                    if (ini != null)
                    {
                        string text = System.Text.Encoding.ASCII.GetString(bank.Read(ini));
                        int i = text.IndexOf("Style", StringComparison.OrdinalIgnoreCase);
                        if (i >= 0)
                        {
                            int q0 = text.IndexOf('"', i), q1 = q0 >= 0 ? text.IndexOf('"', q0 + 1) : -1;
                            if (q1 > q0) _style = text.Substring(q0 + 1, q1 - q0 - 1);
                        }
                        if (_cfg == null) _cfg = GaugeConfig.Parse(text);
                    }
                }
                _lastLayout = -1;
                ApplyStyle();
            }
            catch (Exception ex) { Error = ex.Message; Debug.LogWarning("[TDU2] HUD: " + ex); }
        }

        void ApplyStyle()
        {
            if (_player == null) return;
            int layout = (float)Screen.width / Mathf.Max(1, Screen.height) > 1.5f ? 0 : 1;
            if (layout != _lastLayout)
            {
                _player.GotoLabel("/HUD", layout == 0 ? "16_9" : "4_3");
                _lastLayout = layout;
            }
            if (!string.IsNullOrEmpty(_style))
            {
                _player.GotoLabel("/HUD/compteur_ext", _style);
                _player.GotoLabel("/HUD/compteur_int", _style);
            }
        }

        float Angle(string type, float value, float defMax)
        {
            float v0 = 0, v1 = defMax, o0 = 0, o1 = 264.5f;
            if (_cfg != null && _cfg.TryGet(type, out var g)) { v0 = g.valMin; v1 = g.valMax; o0 = g.oriMin; o1 = g.oriMax; }
            return Mathf.Lerp(o0, o1, Mathf.Clamp01((value - v0) / Mathf.Max(1e-3f, v1 - v0)));
        }

        void LateUpdate()
        {
            if (_player == null) return;
            bool active = ShowHud && Vehicle != null && Vehicle.isActiveAndEnabled;
            _player.Visible = active;
            if (!active) return;
            if (_lastLayout < 0 || (_lastLayout == 0) != ((float)Screen.width / Mathf.Max(1, Screen.height) > 1.5f)) ApplyStyle();

            var v = Vehicle;
            UpdateOdometer(v);
            float speed = _mph ? v.SpeedKmh / 1.609f : v.SpeedKmh;
            int sp = Mathf.Clamp(Mathf.RoundToInt(speed), 0, 999);
            _player.SetVariable("/:Speed_c", sp >= 100 ? (sp / 100).ToString() : "");
            _player.SetVariable("/:Speed_d", sp >= 10 ? (sp / 10 % 10).ToString() : "");
            _player.SetVariable("/:Speed_u", (sp % 10).ToString());
            _player.SetVariable("/:SpeedInteger", sp.ToString());
            _player.SetVariable("/:SpeedUnit", _mph ? "MPH" : "KMH");
            _player.SetVariable("/:Gear", v.Gear < 0 ? "R" : v.Gear == 0 ? "N" : v.Gear.ToString());

            float rpmMax = v.Car != null && v.Car.RpmMax > 0 ? v.Car.RpmMax : 8000f;
            _player.SetRotation("/HUD/compteur_ext/aiguille", Angle("exterior_rpm", v.Rpm, rpmMax));
            _player.SetRotation("/HUD/compteur_int/aiguille_rpm", Angle("interior_rpm", v.Rpm, rpmMax));
            _player.SetRotation("/HUD/compteur_int/aiguille_speed", Angle(_mph ? "interior_mph" : "interior_kmh", speed, 360f));

            // Ansicht: außen = großer Drehzahlmesser, Stoßstange/Haube = Innen-Instrumente, Cockpit = kleine Digitalanzeige
            int mode = VehicleCamera != null && VehicleCamera.CurrentDef != null ? VehicleCamera.CurrentDef.Mode % 10 : 0;
            bool cockpit = VehicleCamera != null && VehicleCamera.IsInterior;
            bool fixedCam = VehicleCamera != null && VehicleCamera.CurrentDef != null && VehicleCamera.CurrentDef.IsFixed;
            _player.SetVisible("/HUD/compteur_ext", !fixedCam);
            _player.SetVisible("/HUD/compteur_int", fixedCam && !cockpit);
            _player.SetVisible("/HUD/speed_cokpit", cockpit);
            _player.SetVisible("/HUD/speed_cokpit_sh", cockpit);
            _ = mode;
        }

        void UpdateOdometer(TDU2Vehicle v)
        {
            var p = v.transform.position;
            if (_hasLastPos)
            {
                float d = Vector3.Distance(p, _lastPos);
                if (d < 50f && v.SpeedKmh > 0.5f) _odoMeters += d;   // Sprünge (Reset/Teleport) nicht zählen
            }
            _lastPos = p; _hasLastPos = true;
            double units = _odoMeters / (_mph ? 1609.344 : 1000.0);
            long whole = (long)Math.Floor(units);
            int tenth = (int)Math.Floor((units - whole) * 10.0);
            string km = (whole % 100000000).ToString("D8");   // wie MngHud.cpp: "%08d" für /:Km, "%01d" für /:Centaine
            _player.SetVariable("/:Km", km);
            _player.SetVariable("/:Centaine", tenth.ToString());
            _player.SetVariable("/:Distance", km);
            _odoSaveTimer += Time.unscaledDeltaTime;
            if (_odoSaveTimer > 10f) SaveOdometer();
        }

        void SaveOdometer()
        {
            _odoSaveTimer = 0f;
            if (_odoKey == null) return;
            PlayerPrefs.SetString(_odoKey, _odoMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        void OnEnable() => FloatingOrigin.Shifted += OnShift;
        void OnShift(Vector3 d) { _lastPos -= d; }
        void OnDisable() { FloatingOrigin.Shifted -= OnShift; SaveOdometer(); }

        void OnDestroy()
        {
            foreach (var t in _owned) if (t != null) Destroy(t);
            _owned.Clear();
        }
    }
}
