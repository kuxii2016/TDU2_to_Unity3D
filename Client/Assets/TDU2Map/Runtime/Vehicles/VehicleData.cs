using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>Stückweise lineare Kurve (x aufsteigend), außerhalb geklemmt.</summary>
    public sealed class Curve
    {
        public float[] X = Array.Empty<float>(), Y = Array.Empty<float>();

        public static Curve FromPairs(IList<float> flat)
        {
            var c = new Curve();
            int n = flat.Count / 2;
            c.X = new float[n]; c.Y = new float[n];
            for (int i = 0; i < n; i++) { c.X[i] = flat[2 * i]; c.Y[i] = flat[2 * i + 1]; }
            // nach x sortieren (sicherheitshalber)
            Array.Sort(c.X, c.Y);
            return c;
        }

        public static Curve Constant(float v) => new Curve { X = new[] { 0f }, Y = new[] { v } };

        public float Evaluate(float x)
        {
            int n = X.Length;
            if (n == 0) return 0f;
            if (n == 1 || x <= X[0]) return Y[0];
            if (x >= X[n - 1]) return Y[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (x <= X[i])
                {
                    float dx = X[i] - X[i - 1];
                    float t = dx > 1e-6f ? (x - X[i - 1]) / dx : 1f;
                    return Mathf.Lerp(Y[i - 1], Y[i], t);
                }
            }
            return Y[n - 1];
        }

        public float MaxY { get { float m = float.MinValue; foreach (var y in Y) m = Mathf.Max(m, y); return m; } }
        public float MinY { get { float m = float.MaxValue; foreach (var y in Y) m = Mathf.Min(m, y); return m; } }
    }

    // ===================================================================================== Datenbank

    /// <summary>Eintrag aus DBCarPhysicsData (db_data.cpr). Einheiten wie in der Datenbank (mm, kg, km/h, %).</summary>
    public sealed class CarPhysics
    {
        public XmbNode Raw;
        public int Id;
        public string FileName, ModelName, Brand, HudFile;
        /// <summary>DBBrands._logo_global_index: Kachel in frontend/AllRes/logotexturepage.bnk (brands_map, 16 x 4 je 64 px); -1 = keins.</summary>
        public int LogoIndex = -1;
        public bool Kmh = true;
        public float Mass, Length, Width, Height, Wheelbase, TrackFront, TrackRear, OverhangFront, OverhangRear;
        public float GravityCenterLength, GravityCenterHeight;   // % Last vorne, cm über Radmitte
        public int Drive, Gearbox, NbGears;                      // Drive: 1 = Allrad, 3 = Front, 4 = Heck
        public float[] GearRatios = new float[11];
        public float FinalDrive, ShiftTimeUp, ShiftTimeDown, MaxPowerShiftUpPercent;
        public float PowerBhp, PowerRpm, TorqueNm, TorqueRpm, RpmMin, RpmMax, RedLine, RupteurTime, TimeIgnition;
        public float EngineInertia, EngineDamping, EngineBrakeNm, GearboxInertia;
        public float BrakeBalance, Braking100To0, BrakesPowerFront, BrakesPowerRear;
        public float RideHeightFront, RideHeightRear, SuspensionLengthFront, SuspensionLengthRear;
        public float SuspensionRateFront, SuspensionRateRear, DamperRateFront, DamperRateRear;
        public float AntiRollFront, AntiRollRear, AntiRollDampingFront, AntiRollDampingRear;
        public float CamberFront, CamberRear, ToeFront, ToeRear, CasterFront;
        public float Cx, LiftLoadFront, LiftLoadRear, LiftVelocity, GroundEffectLoad, GroundEffectVelocity;
        public float MaxLatFrictionFront, MaxLatFrictionRear, MaxLongFrictionFront, MaxLongFrictionRear, MaxNormalFrictionFront, MaxNormalFrictionRear;
        public float WetFrictionPercent;
        public float DiffValueFront, DiffValueRear, DiffValueCentral, TransmissionPrimacy;
        public int DiffTypeFront, DiffTypeRear, DiffTypeCentral;
        public bool Abs, TractionControl;
        public float AbsSlipSecure, AbsSlipSport, AbsSlipHyper, AbsSlipOff;
        public float TcSlipSecure, TcSlipSport, TcSlipHyper, TcSlipOff;
        public float HandBrakeSlipRatio;
        public float WheelMassFront, WheelMassRear, TireRadius;
        public float MaxTheoricalSpeed, Accel0To100;
        public float ThrottleRatioNormal, ThrottleRatioSport;
        public int CameraId, DefaultRims, TiresPhysics;
        public float StabilityManagement;
        public bool IsBike;


        public int CalculateDifficulty()
        {
            float score = 0f;

            // 1. Leistung pro Tonne: viel Leistung macht das Auto anspruchsvoller.
            float powerPerTon = PowerBhp / Math.Max(Mass / 1000f, 0.1f);

            if (powerPerTon > 500f) score += 25f;
            else if (powerPerTon > 400f) score += 20f;
            else if (powerPerTon > 300f) score += 15f;
            else if (powerPerTon > 200f) score += 10f;
            else if (powerPerTon > 120f) score += 5f;

            // 2. Antrieb: Die Werte müssen zu deinem TDU2-Enum passen.
            // Beispielannahme: 1 = Front, 2 = Heck, 4 = Allrad.
            switch (Drive)
            {
                case 1: // Frontantrieb
                    score += 0f;
                    break;

                case 2: // Heckantrieb
                    score += 10f;
                    break;

                case 4: // Allradantrieb
                    score -= 5f;
                    break;
            }

            // 3. Fahrhilfen
            if (!Abs)
                score += 7f;

            if (!TractionControl)
                score += 12f;

            // 4. Reifenhaftung: Weniger Grip bedeutet schwierigere Kontrolle.
            float averageGrip =
                (MaxLatFrictionFront + MaxLatFrictionRear +
                 MaxLongFrictionFront + MaxLongFrictionRear) / 4f;

            if (averageGrip < 0.8f) score += 12f;
            else if (averageGrip < 1.1f) score += 7f;
            else if (averageGrip >= 1.8f) score -= 5f;

            // 5. Radstand: kurze Fahrzeuge reagieren oft agiler.
            if (Wheelbase < 2300f) score += 8f;
            else if (Wheelbase < 2500f) score += 4f;
            else if (Wheelbase > 3000f) score -= 3f;

            // 6. Bremsverhalten: längerer Bremsweg erschwert die Kontrolle.
            if (Braking100To0 > 150f) score += 8f;
            else if (Braking100To0 > 130f) score += 4f;
            else if (Braking100To0 < 95f) score -= 3f;

            // 7. Nasshaftung
            if (WetFrictionPercent < 50f) score += 8f;
            else if (WetFrictionPercent < 70f) score += 4f;

            // 8. Endgültige Einstufung von 1 bis 5.
            if (score < 15f) return 1;
            if (score < 30f) return 2;
            if (score < 45f) return 3;
            if (score < 60f) return 4;

            return 5;
        }


        public static CarPhysics From(XmbNode n)
        {
            float F(string k, float d = 0) => n.F(k, d);
            var c = new CarPhysics
            {
                Raw = n,
                Id = n.I("_id_car"),
                FileName = n.S("_car_file_name", ""),
                HudFile = n.S("_hud_file_name", ""),
                Kmh = n.B("_kmh", true),
                ModelName = n.S("_model_name", n.S("_car_model", "")),
                Mass = F("_mass", 1400),
                Length = F("_length", 4500),
                Width = F("_width", 1900),
                Height = F("_height", 1300),
                Wheelbase = F("_wheelbase", 2700),
                TrackFront = F("_track_front", 1600),
                TrackRear = F("_track_back", 1600),
                OverhangFront = F("_overhang_front"),
                OverhangRear = F("_overhang_rear"),
                GravityCenterLength = F("_gravity_center_length", 50),
                GravityCenterHeight = F("_gravity_center_height", 20),
                Drive = n.I("_drive", 4),
                Gearbox = n.I("_gearbox", 2),
                NbGears = n.I("_nb_gears", 6),
                FinalDrive = F("_final_drive_ratio", 3.5f),
                ShiftTimeUp = F("_shifttime_up", 0.2f),
                ShiftTimeDown = F("_shifttime_down", 0.2f),
                MaxPowerShiftUpPercent = F("_max_power_shift_up_purcent", 90),
                PowerBhp = F("_power_bhp", 300),
                PowerRpm = F("_power_rpm", 6500),
                TorqueNm = F("_torque_nm", 400),
                TorqueRpm = F("_torque_rpm", 4500),
                RpmMin = F("_rpm_min", 900),
                RpmMax = F("_rpm_max", 7500),
                RedLine = F("_red_line", 7000),
                RupteurTime = F("_rupteur_time", 0.1f),
                TimeIgnition = F("_time_ignition", 0.5f),
                EngineInertia = F("_engine_inertia", 20),
                EngineDamping = F("_engine_damping", 0.1f),
                EngineBrakeNm = F("_engine_brake_nm"),
                GearboxInertia = F("_gearbox_inertia"),
                BrakeBalance = F("_brake_balance", 60),
                Braking100To0 = F("_braking_100_0", 120),
                BrakesPowerFront = F("_brakes_power_front"),
                BrakesPowerRear = F("_brakes_power_rear"),
                RideHeightFront = F("_ride_height_front", 130),
                RideHeightRear = F("_ride_height_rear", 130),
                SuspensionLengthFront = F("_suspension_length_front", 150),
                SuspensionLengthRear = F("_suspension_lenght_rear", 150),
                SuspensionRateFront = F("_suspension_rate_front", 50),
                SuspensionRateRear = F("_suspension_rate_rear", 50),
                DamperRateFront = F("_damper_rate_front", 5),
                DamperRateRear = F("_damper_rate_rear", 5),
                AntiRollFront = F("_antirollbar_front", 10),
                AntiRollRear = F("_antirollbar_rear", 10),
                AntiRollDampingFront = F("_antirollbar_damping_front"),
                AntiRollDampingRear = F("_antirollbar_damping_rear"),
                CamberFront = F("_camber_front"),
                CamberRear = F("_camber_rear"),
                ToeFront = F("_toe_front"),
                ToeRear = F("_toe_rear"),
                CasterFront = F("_caster_front"),
                Cx = F("_cx", 0.35f),
                LiftLoadFront = F("_lift_load_front"),
                LiftLoadRear = F("_lift_load_rear"),
                LiftVelocity = F("_lift_velocity", 300),
                GroundEffectLoad = F("_ground_effect_load"),
                GroundEffectVelocity = F("_ground_effect_velocity", 300),
                MaxLatFrictionFront = F("_max_lateral_friction_front", 1.5f),
                MaxLatFrictionRear = F("_max_lateral_friction_rear", 1.5f),
                MaxLongFrictionFront = F("_max_longitudinal_friction_front", 1.5f),
                MaxLongFrictionRear = F("_max_longitudinal_friction_rear", 1.5f),
                MaxNormalFrictionFront = F("_max_normal_friction_front", 1.5f),
                MaxNormalFrictionRear = F("_max_normal_friction_rear", 1.5f),
                WetFrictionPercent = F("_wet_friction_percent", 80),
                DiffTypeFront = n.I("_differential_type_front", 1),
                DiffTypeRear = n.I("_differential_type_rear", 1),
                DiffTypeCentral = n.I("_differential_type_central", 1),
                DiffValueFront = F("_differential_value_front", 0),
                DiffValueRear = F("_differential_value_rear", 0),
                DiffValueCentral = F("_differential_value_central", 50),
                TransmissionPrimacy = F("_transmission_primacy"),
                Abs = n.B("_abs", true),
                TractionControl = n.B("_traction_control", true),
                AbsSlipSecure = F("_abs_slip_ratio_secure", -0.08f),
                AbsSlipSport = F("_abs_slip_ratio_sport", -0.1f),
                AbsSlipHyper = F("_abs_slip_ratio_hypersport", -0.15f),
                AbsSlipOff = F("_abs_slip_ratio_off", -0.2f),
                TcSlipSecure = F("_traction_control_slip_ratio_secure", 0.1f),
                TcSlipSport = F("_traction_control_slip_ratio_sport", 0.8f),
                TcSlipHyper = F("_traction_control_slip_ratio_hypersport", 10f),
                TcSlipOff = F("_traction_control_slip_ratio_off", -1f),
                HandBrakeSlipRatio = F("_hand_brake_slip_ratio", -0.2f),
                WheelMassFront = F("_wheel_mass_front", 20),
                WheelMassRear = F("_wheel_mass_rear", 20),
                TireRadius = F("_tire_radius", 64),
                MaxTheoricalSpeed = F("_max_theorical_speed", 250),
                Accel0To100 = F("_acceleration_0_100_kmh", 6),
                ThrottleRatioNormal = F("_throttle_ratio_normal", 100),
                ThrottleRatioSport = F("_throttle_ratio_sport", 100),
                CameraId = n.I("_camera"),
                DefaultRims = n.I("_default_rims"),
                TiresPhysics = n.I("_tires_physics"),
                StabilityManagement = F("_stability_management"),
            };
            for (int i = 0; i < 11; i++) c.GearRatios[i] = F("_gear_ratio" + (i + 1));
            c.IsBike = n.F("_max_lean_angle") > 0f;
            if (c.WheelMassFront <= 0) c.WheelMassFront = 20; if (c.WheelMassRear <= 0) c.WheelMassRear = 20;
            return c;
        }

        public override string ToString() => $"{ModelName} ({FileName}, #{Id})";
    }

    public sealed class RimInfo
    {
        public int Id, Price;
        public string Path, FileFront, FileRear, DisplayName;
        public float DiameterFront, DiameterRear, WidthFront, WidthRear, ProfileFront, ProfileRear;   // Zoll, mm, %

        /// <summary>Reifen-Außenradius in Metern (Felge + Flanke).</summary>
        public float TireRadius(bool front) => front
            ? DiameterFront * 0.0254f * 0.5f + WidthFront * ProfileFront * 0.00001f
            : DiameterRear * 0.0254f * 0.5f + WidthRear * ProfileRear * 0.00001f;
    }

    public sealed class CarColor
    {
        public string ColorId, ColorId2, Name, Callipers;
        public int Price;
        public int[] Interiors = new int[15];
    }

    public sealed class InteriorInfo
    {
        public int Id, Price;
        public string Name, Material;
        public string[] Colors = new string[3];
    }

    /// <summary>
    /// Fahrzeugdatenbank aus Euro/Bnk/database/db_data.cpr (XTEA-verschlüsseltes XMBF).
    /// Tabellen: DBCarPhysicsData (212 Felder pro Auto), DBRims, DBCarsAndRims, DBCarsAndColors, DBInterior, DBTiresPhysics.
    /// </summary>
    /// <summary>DBCarShop: Händler-/Shop-Bestand je Spot ("ECD_ITAL_3210", Interieur-Variante mit Endung "I").</summary>
    public sealed class CarShopInfo
    {
        public string SpotName;                 // z.B. "ECD_FERR_7841"
        public readonly List<int> CarIds = new List<int>();   // _slot_1.._slot_9 (_id_car), 0 = leer
        public int ClothesBrand;
        public string Code => SpotName != null && SpotName.LastIndexOf('_') > 0 ? SpotName.Substring(0, SpotName.LastIndexOf('_')) : SpotName;
        public bool IsInteriorVariant => SpotName != null && SpotName.EndsWith("I");
    }

    /// <summary>DBHouse: Immobilien (Spotname, Stellplätze, Preis).</summary>
    public sealed class HouseInfo
    {
        public string SpotName; public int CarSlots, Level, BuyPrice;
        public string Code => SpotName != null && SpotName.LastIndexOf('_') > 0 ? SpotName.Substring(0, SpotName.LastIndexOf('_')) : SpotName;
    }

    public sealed class CarDatabase
    {
        public readonly List<CarShopInfo> Shops = new List<CarShopInfo>();
        public readonly List<HouseInfo> Houses = new List<HouseInfo>();
        public readonly List<CarPhysics> Cars = new List<CarPhysics>();
        public readonly Dictionary<int, CarPhysics> ById = new Dictionary<int, CarPhysics>();
        /// <summary>Verkehrsautos (DBCarPhysicsData, Marke 40): 2001-2034 Hawaii, 3001-3014 Ibiza.</summary>
        public readonly List<CarPhysics> Traffic = new List<CarPhysics>();
        public readonly Dictionary<int, CarPhysics> TrafficById = new Dictionary<int, CarPhysics>();
        public readonly Dictionary<int, RimInfo> Rims = new Dictionary<int, RimInfo>();
        public readonly Dictionary<int, List<int>> CarRims = new Dictionary<int, List<int>>();
        public readonly Dictionary<int, List<CarColor>> Colors = new Dictionary<int, List<CarColor>>();
        public readonly Dictionary<int, InteriorInfo> Interiors = new Dictionary<int, InteriorInfo>();
        public readonly Dictionary<int, string> TirePhysics = new Dictionary<int, string>();
        /// <summary>Wurzel von db_data.cpr (für weitere Tabellen, z.B. DBClothe der Avatare).</summary>
        public XmbNode Root;

        public static string FindDatabase(string bnkDir)
        {
            var p = Path.Combine(bnkDir, "database", "db_data.cpr");
            return File.Exists(p) ? p : null;
        }

        public static CarDatabase Load(string dbDataCpr)
        {
            var root = XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(dbDataCpr)));
            var db = new CarDatabase { Root = root };

            var phys = root.Get("DBCarPhysicsData");
            if (phys != null)
                foreach (var it in phys.Items)
                {
                    var c = CarPhysics.From(it);
                    // Verkehrsautos (Marke 40 TRAFFIC, Dateiname "##"): Modell = _car_model + _car_version
                    if (it.I("_car_brand") == 40) { db.Traffic.Add(c); if (!db.TrafficById.ContainsKey(c.Id)) db.TrafficById[c.Id] = c; continue; }
                    if (string.IsNullOrEmpty(c.FileName) || c.FileName == "##") continue;
                    db.Cars.Add(c);
                    if (!db.ById.ContainsKey(c.Id)) db.ById[c.Id] = c;
                }

            // Markennamen (DBBrands: _hashcode -> _manufacturer_name), CarPhysicsData._car_brand
            var brands = root.Get("DBBrands");
            if (brands != null)
            {
                var byId = new Dictionary<int, string>();
                var logoById = new Dictionary<int, int>();
                foreach (var r in brands.Items) { byId[r.I("_hashcode")] = r.S("_manufacturer_name", ""); logoById[r.I("_hashcode")] = r.I("_logo_global_index", -1); }
                foreach (var c in db.Cars)
                {
                    if (c.Raw == null) continue;
                    int bid = c.Raw.I("_car_brand");
                    if (byId.TryGetValue(bid, out var bn) && !string.IsNullOrEmpty(bn)) c.Brand = bn;
                    // 62/63 = Platzhalter (Generic/Traffic/Default) -> kein Logo
                    if (logoById.TryGetValue(bid, out var li) && li >= 0 && li < 62) c.LogoIndex = li;
                }
            }

            var rims = root.Get("DBRims");
            if (rims != null)
                foreach (var r in rims.Items)
                {
                    var ri = new RimInfo
                    {
                        Id = r.I("_hashcode"),
                        Path = r.S("_rsc_path", ""),
                        FileFront = r.S("_rsc_file_name_front", ""),
                        FileRear = r.S("_rsc_file_name_rear", ""),
                        DisplayName = r.S("_displayname", ""),
                        Price = r.I("_price"),
                        DiameterFront = r.F("_diameter_front", 18),
                        DiameterRear = r.F("_diameter_rear", 18),
                        WidthFront = r.F("_largeur_front", 225),
                        WidthRear = r.F("_largeur_rear", 245),
                        ProfileFront = r.F("_hauteur_front", 40),
                        ProfileRear = r.F("_hauteur_rear", 40),
                    };
                    db.Rims[ri.Id] = ri;
                }

            var cr = root.Get("DBCarsAndRims");
            if (cr != null)
                foreach (var r in cr.Items)
                {
                    int car = r.I("_car"), rim = r.I("_rim");
                    if (!db.CarRims.TryGetValue(car, out var l)) db.CarRims[car] = l = new List<int>();
                    l.Add(rim);
                }

            var cc = root.Get("DBCarsAndColors");
            if (cc != null)
                foreach (var r in cc.Items)
                {
                    var col = new CarColor
                    {
                        ColorId = r.S("_color_id_1", ""),
                        ColorId2 = r.S("_color_id_2", ""),
                        Name = r.S("_color_name", ""),
                        Callipers = r.S("_callipers", ""),
                        Price = r.I("_price"),
                    };
                    for (int i = 0; i < 15; i++) col.Interiors[i] = r.I("_interior_" + (i + 1), 1);
                    int car = r.I("_car");
                    if (!db.Colors.TryGetValue(car, out var l)) db.Colors[car] = l = new List<CarColor>();
                    l.Add(col);
                }

            var ints = root.Get("DBInterior");
            if (ints != null)
                foreach (var r in ints.Items)
                {
                    var ii = new InteriorInfo { Id = r.I("_hashcode"), Name = r.S("_interior_name", ""), Material = r.S("_material", ""), Price = r.I("_price") };
                    for (int i = 0; i < 3; i++) ii.Colors[i] = r.S("_interior_color_" + (i + 1), "");
                    db.Interiors[ii.Id] = ii;
                }

            var shops = root.Get("DBCarShop");
            if (shops != null)
                foreach (var r in shops.Items)
                {
                    var sh = new CarShopInfo { SpotName = r.S("_spot_name", ""), ClothesBrand = r.I("_clothesshopbrand") };
                    for (int i = 1; i <= 9; i++) sh.CarIds.Add(r.I("_slot_" + i));
                    if (r.B("_isvalid", true)) db.Shops.Add(sh);
                }
            var houses = root.Get("DBHouse");
            if (houses != null)
                foreach (var r in houses.Items)
                    db.Houses.Add(new HouseInfo { SpotName = r.S("_spotname", ""), CarSlots = r.I("_nbcarslots", 2), Level = r.I("_level"), BuyPrice = r.I("_buyprice") });

            var tp = root.Get("DBTiresPhysics");
            if (tp != null) foreach (var r in tp.Items) db.TirePhysics[r.I("_hashcode")] = r.S("_tires_physics", "");
            return db;
        }

        /// <summary>Markenlogo eines Händlers (häufigste Marke seines Bestands, wie das Spiel es auf der Karte zeigt); -1 = keins.</summary>
        public int DealerLogo(string spotCode)
        {
            var shop = ShopFor(spotCode);
            if (shop == null) return -1;
            var count = new Dictionary<int, int>();
            int best = -1, bestN = 0;
            foreach (int id in shop.CarIds)
            {
                if (id == 0 || !ById.TryGetValue(id, out var c) || c.LogoIndex < 0) continue;
                count.TryGetValue(c.LogoIndex, out int n); n++; count[c.LogoIndex] = n;
                if (n > bestN) { bestN = n; best = c.LogoIndex; }
            }
            return best;
        }

        /// <summary>Bestand eines Händlers für einen Spot-Code (z.B. "ECD_ITAL"); bevorzugt die Interieur-Variante.</summary>
        public CarShopInfo ShopFor(string spotCode)
        {
            CarShopInfo first = null;
            foreach (var s in Shops)
            {
                if (!string.Equals(s.Code, spotCode, StringComparison.OrdinalIgnoreCase) && !string.Equals(s.Code, spotCode + "I", StringComparison.OrdinalIgnoreCase))
                {
                    // "ECD_ITAL_1207I": Code = "ECD_ITAL", Name endet auf I
                    var c = s.SpotName ?? "";
                    if (!c.StartsWith(spotCode + "_", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (s.IsInteriorVariant) return s;
                if (first == null) first = s;
            }
            return first;
        }

        public HouseInfo HouseFor(string spotCode)
        {
            foreach (var h in Houses) if (string.Equals(h.Code, spotCode, StringComparison.OrdinalIgnoreCase)) return h;
            return null;
        }

        /// <summary>Pfade der Fahrzeugbänke unter Euro/Bnk.</summary>
        public static string CarBank(string bnkDir, CarPhysics c) => FindFile(Path.Combine(bnkDir, "vehicules", "high"), c.FileName + ".bnk");
        public static string InteriorBank(string bnkDir, CarPhysics c) => FindFile(Path.Combine(bnkDir, "vehicules", "interiors"), c.FileName + "_i.bnk");
        /// <summary>Instrumenten-Bank (frontend/&lt;res&gt;/gauges/&lt;_hud_file_name&gt;.bnk) mit hud.ini.</summary>
        public static string GaugeBank(string bnkDir, CarPhysics c)
        {
            string name = !string.IsNullOrEmpty(c.HudFile) ? c.HudFile : c.FileName;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                var f = FindFile(Path.Combine(bnkDir, "frontend", res, "gauges"), name + ".bnk");
                if (f != null) return f;
            }
            return null;
        }

        public static string SoundBank(string bnkDir, CarPhysics c) => FindFile(Path.Combine(bnkDir, "sound", "vehicles"), c.FileName + "_high.bnk");
        public static string RimBank(string bnkDir, RimInfo r, bool front) =>
            r == null ? null : FindFile(Path.Combine(bnkDir, "vehicules", "rim", r.Path), (front ? r.FileFront : r.FileRear) + ".bnk");
        public static string TireBank(string bnkDir, float diameterInch)
        {
            int d = Mathf.RoundToInt(diameterInch);
            for (int k = 0; k < 6; k++)
            {
                foreach (int dd in new[] { d + k, d - k })
                {
                    var f = FindFile(Path.Combine(bnkDir, "vehicules", "rim", "tire"), $"t_{dd}p_oh.bnk");
                    if (f != null) return f;
                }
            }
            return null;
        }
        public static string CommonCarBank(string bnkDir) => FindFile(Path.Combine(bnkDir, "vehicules"), "common_car.bnk");

        /// <summary>Datei im Ordner suchen (Groß-/Kleinschreibung egal, Windows-Pfade aus der DB).</summary>
        public static string FindFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            var direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            // Ordner case-insensitiv auflösen
            string resolvedDir = ResolveDir(dir);
            if (resolvedDir == null) return null;
            foreach (var f in Directory.GetFiles(resolvedDir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        static string ResolveDir(string dir)
        {
            if (Directory.Exists(dir)) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent)) return null;
            var rp = ResolveDir(parent);
            if (rp == null) return null;
            string leaf = Path.GetFileName(dir);
            foreach (var d in Directory.GetDirectories(rp))
                if (string.Equals(Path.GetFileName(d), leaf, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }
    }

    // ===================================================================================== Physics.cpr

    /// <summary>
    /// Globale Fahrdynamik-Parameter aus Physics.cpr (Spielordner, XTEA-verschlüsselter Text, Abschnitt [Car]).
    /// Fehlt die Datei (nur "Euro" im Projekt), werden die Originalwerte als Standard verwendet.
    /// </summary>
    public sealed class PhysicsConfig
    {
        public readonly Dictionary<string, float[]> Values = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        public string Source = "intern";

        public const string DefaultText = @"[Car]
        NbLoopForMiddlePrecision = 3
        ReverseTime = 0.6
        ExtraGravityMin = 0.1
        ExtraGravityMax = 1.0
        ExtraGravityAccelDuration = 0.1
        ExtraGravityAccelDelay = 0.01
        CollisionOffset = 0.1
        OffRoadDirtLinearDamping = 0.0 0.0 100.0 0.0 200.0 0.0 300.0 0.01 400.0 0.03
        OffRoadGrassLinearDamping = 0.0 0.0 100.0 0.0 200.0 0.07 300.0 0.15 400.0 0.5
        NotOffRoadDirtLinearDamping = 0.0 0.1 100.0 0.1 200.0 0.2 300.0 0.2 400.0 0.2
        NotOffRoadGrassLinearDamping = 0.0 0.15 100.0 0.15 200.0 0.5 300.0 0.5 400.0 1.0
        OffRoadAngularDamping = 0.0 0.0 100.0 0.0 200.0 0.0 300.0 0.1 400.0 1.0
        SecureAngularDamping = 0.0 0.4 100.0 0.4 200.0 0.6 300.0 1.0 400.0 2.0
        SportAngularDamping = 0.0 0.15 100.0 0.3 200.0 0.6 300.0 1.0 400.0 1.0
        HyperSportAngularDamping = 0.0 0.0 100.0 0.2 200.0 0.4 300.0 0.6 400.0 0.8
        AbsControlActivation = 0
        AbsControlVelocityMin = 1
        AbsControlVelocityMax = 50
        AbsControlSlipRatio = -0.2 -0.2 -0.2
        TractionControlActivation = 10
        TractionControlVelocityMin = 10
        TractionControlVelocityMax = 70
        TractionControlSlipRatio = 0.5 2.0 20.0
        HandBrakeActivation = 0
        HandBrakeVelocityMin = 1
        HandBrakeVelocityMax = 50
        HandBrakeSlipRatio = -0.4 -0.5 -0.6
        SteeringVelocity = 0.0 380.0 100.0 370.0 200.0 360.0 300.0 350.0 400.0 340.0
        SteeringMaxAngle = 0.0 30.0 100.0 13.0 200.0 10.0 300.0 8.0 400.0 6.5
        SteeringSensitivity = 0.0 1.00 100.0 1.00 200.0 1.00 300.0 1.00 400.0 1.00
        ";

        public static PhysicsConfig Load(string bnkDir)
        {
            // Physics.cpr liegt im Spielordner (neben Euro) – optional
            try
            {
                var euro = bnkDir != null ? Directory.GetParent(bnkDir) : null;
                var game = euro?.Parent;
                foreach (var dir in new[] { game?.FullName, euro?.FullName, bnkDir })
                {
                    if (dir == null) continue;
                    var f = CarDatabase.FindFile(dir, "Physics.cpr");
                    if (f == null) continue;
                    var bytes = CprCrypto.Decrypt(File.ReadAllBytes(f));
                    var text = System.Text.Encoding.ASCII.GetString(bytes);
                    var cfg = Parse(text, "Car");
                    if (cfg.Values.Count > 5) { cfg.Source = f; return cfg; }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Physics.cpr: " + ex.Message); }
            return Parse(DefaultText, "Car");
        }

        public static PhysicsConfig Parse(string text, string section)
        {
            var cfg = new PhysicsConfig();
            string cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[")) { cur = line.Trim('[', ']'); continue; }
                if (!string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq).Trim();
                var vals = new List<float>();
                foreach (var tok in line.Substring(eq + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) vals.Add(f);
                if (vals.Count > 0 && !key.Contains(" ") && !key.Contains("\t")) cfg.Values[key] = vals.ToArray();
            }
            return cfg;
        }

        public float Get(string key, float def, int index = 0) => Values.TryGetValue(key, out var v) && v.Length > index ? v[index] : def;
        readonly Dictionary<string, Curve> _curves = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
        public Curve GetCurve(string key, float def)
        {
            if (_curves.TryGetValue(key, out var c)) return c;
            c = Values.TryGetValue(key, out var v) && v.Length >= 2 ? Curve.FromPairs(v) : Curve.Constant(def);
            _curves[key] = c;
            return c;
        }
    }

    // ===================================================================================== Drehmoment

    /// <summary>Drehmomentkurven aus der Fahrzeugbank: &lt;car&gt;_max.xmb (Volllast) und &lt;car&gt;_min.xmb (Schubmoment), x = U/min, y = Nm.</summary>
    public static class TorqueCurves
    {
        public static Curve Parse(byte[] xmb)
        {
            var root = XmbFile.Parse(xmb);
            var curve = root.Get("Curve") ?? root;
            var pa = curve.Get("PointArray");
            var flat = new List<float>();
            if (pa != null)
                foreach (var v in pa.Children)            // mehrere "Vector2"-Kinder gleichen Namens
                    foreach (var f in v.Items.Count > 0 ? v.Items : v.Children) flat.Add(f.AsFloat());
            return flat.Count >= 4 ? Curve.FromPairs(flat) : null;
        }

        /// <summary>Ersatzkurve aus Spitzenwerten der Datenbank (falls keine XMB vorhanden).</summary>
        public static Curve Synthetic(CarPhysics c)
        {
            float tq = c.TorqueNm, trpm = c.TorqueRpm, prpm = c.PowerRpm, red = Mathf.Max(c.RedLine, prpm + 200);
            float tqAtPower = c.PowerBhp * 745.7f / (prpm * Mathf.PI / 30f);
            return Curve.FromPairs(new float[] { 0, tq * 0.45f, c.RpmMin, tq * 0.6f, trpm, tq, prpm, Mathf.Min(tq, tqAtPower), red, tqAtPower * 0.9f, red + 500, 0 });
        }

        public static Curve SyntheticBrake(CarPhysics c) => Curve.FromPairs(new float[] { 0, 0, 1000, -c.TorqueNm * 0.05f, c.RedLine, -c.TorqueNm * 0.15f });
    }

    // ===================================================================================== Kameras

    /// <summary>Kamera aus camerasingame.xmb. Modi: 20 Verfolger nah, 21 Verfolger fern, 22 Stoßstange, 23 Cockpit, 24 Motorhaube; 4x = Rückblick.</summary>
    public sealed class CarCamera
    {
        public int CarId, Mode, BackMode;
        public float FovMin = 55, FovMax = 65, FovSpeedMin, FovSpeedMax = 150, Near = 0.3f;
        public Vector3 FixedPos, FixedTarget;          // Fahrzeugkoordinaten (Spiel: +X = links)
        public float FollowPos, FollowPosH, FollowTarg, FollowTargH, FollowSpeed;
        public float FollowPosMax, FollowPosMaxH, FollowTargMax, FollowTargMaxH, FollowSpeedMax;
        public float FollowDelay, FollowDelayMax, LeanFactor, LeanFactorMax;
        public float RangeLeftRight = 130, MaxUp = 30, MaxDown = 15;
        public float ShakeY;
        public bool IsFixed => Mode % 10 >= 2;     // 22/23/24 bzw. 42/43/44

        public static CarCamera From(XmbNode n)
        {
            return new CarCamera
            {
                CarId = n.I("id_car"),
                Mode = n.I("mode"),
                BackMode = n.I("backviewmode"),
                FovMin = n.F("fov_min", 55),
                FovMax = n.F("fov_max", 65),
                FovSpeedMin = n.F("fov_speed_min"),
                FovSpeedMax = n.F("fov_speed_max", 150),
                Near = Mathf.Max(0.05f, n.F("nearProjPlane", 0.3f)),
                FixedPos = new Vector3(n.F("fixed_pos_x"), n.F("fixed_pos_y"), n.F("fixed_pos_z")),
                FixedTarget = new Vector3(n.F("fixed_targ_x"), n.F("fixed_targ_y"), n.F("fixed_targ_z", 1)),
                FollowPos = n.F("follow_pos_offset", -6.5f),
                FollowPosH = n.F("follow_pos_offset_H", 1.5f),
                FollowTarg = n.F("follow_targ_offset", 1.5f),
                FollowTargH = n.F("follow_targ_offset_H", 0.9f),
                FollowSpeed = n.F("follow_speed_offset", 50),
                FollowPosMax = n.F("follow_pos_max_offset", -6.5f),
                FollowPosMaxH = n.F("follow_pos_max_offset_h", 1.6f),
                FollowTargMax = n.F("follow_targ_max_offset", 1.6f),
                FollowTargMaxH = n.F("follow_targ_max_offset_h", 1.3f),
                FollowSpeedMax = n.F("follow_speed_max_offset", 250),
                FollowDelay = n.F("follow_delay_offset"),
                FollowDelayMax = n.F("follow_delay_max_offset"),
                LeanFactor = n.F("follow_lean_factor_offset"),
                LeanFactorMax = n.F("follow_lean_factor_max_offset"),
                RangeLeftRight = n.F("range_angle_left_right", 130),
                MaxUp = n.F("max_angle_up", 30),
                MaxDown = n.F("max_angle_down", 15),
                ShakeY = n.F("shake_y_magn_slow"),
            };
        }
    }

    public sealed class CameraDatabase
    {
        public readonly Dictionary<int, Dictionary<int, CarCamera>> ByCar = new Dictionary<int, Dictionary<int, CarCamera>>();
        public int[] SwitchOrder = { 21, 20, 23, 24, 22 };

        public static CameraDatabase Load(string bnkDir)
        {
            var db = new CameraDatabase();
            var f = CarDatabase.FindFile(Path.Combine(bnkDir, "database"), "camerasingame.xmb");
            if (f == null) return db;
            var root = XmbFile.Parse(File.ReadAllBytes(f));
            var cig = root.Get("CameraInGame") ?? root;
            int nSwitch = cig.I("nb_camera_switch", 0);
            if (nSwitch > 0)
            {
                var order = new List<int>();
                for (int i = 0; i < nSwitch; i++) { var c = cig.Get($"p_array_camera_switch[{i}]"); if (c != null) order.Add(c.AsInt()); }
                if (order.Count > 0) db.SwitchOrder = order.ToArray();
            }
            foreach (var car in cig.Children)
            {
                if (car.Type != XmbType.Object || car.Get("id_car") == null) continue;
                int id = car.I("id_car");
                if (!db.ByCar.TryGetValue(id, out var modes)) db.ByCar[id] = modes = new Dictionary<int, CarCamera>();
                foreach (var cam in car.Children)
                {
                    if (cam.Type != XmbType.Object || cam.Get("mode") == null) continue;
                    var cc = CarCamera.From(cam);
                    modes[cc.Mode] = cc;
                }
            }
            return db;
        }

        /// <summary>Kamera eines Autos; fehlende Modi kommen aus den Standard-Sätzen (id 9998/9999/10000).</summary>
        public CarCamera Get(int carId, int mode)
        {
            if (ByCar.TryGetValue(carId, out var m) && m.TryGetValue(mode, out var c)) return c;
            foreach (int fb in new[] { 9998, 9999, 9997, 10000 })
                if (ByCar.TryGetValue(fb, out var fm) && fm.TryGetValue(mode, out var fc)) return fc;
            return null;
        }
    }

    // ===================================================================================== Sound

    public sealed class SoundLink
    {
        public int[] Physic;
        public int Audio;          // 0 = Tonhöhe, 1 = Lautstärke
        public Curve Curve;
    }

    public sealed class SoundSample
    {
        public string Name, Wave;
        public float BaseVolume = .8f;
        public bool Trigger, Loop;
        public int Panel;
        public float[] StateVolume = { 0.5f, .75f, 1, 1, 1, 1 };   // [Innen, Außen, ?, Asphalt, Gras, Erde]
        public Vector3 Position;
        public readonly List<SoundLink> Links = new List<SoundLink>();
    }

    /// <summary>
    /// CarVSTConfig.xmb aus sound/vehicles/&lt;car&gt;_high.bnk: Samples mit Kurven Physikwert -> Tonhöhe/Lautstärke.
    /// Physikwerte: 0 = Drehzahl (U/min), 1 = Geschwindigkeit (km/h), 3 = Motormoment (Nm, negativ = Schub),
    /// 26..29 = Querschlupf je Rad (m/s), 34..37 = Längsschlupf je Rad (m/s), 43 = Nässe (0..1).
    /// </summary>
    public sealed class CarSoundConfig
    {
        public readonly List<SoundSample> Samples = new List<SoundSample>();

        public static CarSoundConfig Parse(byte[] xmb)
        {
            var root = XmbFile.Parse(xmb);
            var files = new List<string>();
            var sf = root.Get("SampleFiles");
            if (sf != null) foreach (var it in sf.Items) files.Add(it.AsString(""));
            var cfg = new CarSoundConfig();
            var sc = root.Get("SamplesConfig");
            if (sc == null) return cfg;
            foreach (var s in sc.Items)
            {
                int wi = s.I("nWaveIndex", -1);
                var smp = new SoundSample
                {
                    Name = s.S("sSampleName", ""),
                    Wave = wi >= 0 && wi < files.Count ? files[wi] : null,
                    BaseVolume = s.F("fBaseVolume", 1),
                    Trigger = s.B("bIsTrigger"),
                    Loop = s.B("nMustBeLooped"),
                    Panel = s.I("PanelID"),
                };
                var sv = s.Get("PlaybackStateVolume");
                if (sv != null && sv.Items.Count > 0) smp.StateVolume = sv.Floats();
                var ip = s.Get("InstancePosition");
                if (ip != null && ip.Items.Count > 0) smp.Position = new Vector3(ip.Items[0].F("x"), ip.Items[0].F("y"), ip.Items[0].F("z"));
                var links = s.Get("Links");
                if (links != null)
                    foreach (var l in links.Items)
                    {
                        var ph = l.Get("Physic");
                        var cs = l.Get("CurveSet");
                        if (ph == null || cs == null) continue;
                        var phys = new int[ph.Items.Count];
                        for (int i = 0; i < phys.Length; i++) phys[i] = ph.Items[i].AsInt();
                        smp.Links.Add(new SoundLink { Physic = phys, Audio = l.I("Audio"), Curve = Curve.FromPairs(cs.Floats()) });
                    }
                cfg.Samples.Add(smp);
            }
            return cfg;
        }
    }

    /// <summary>WAV-Dekoder für die Fahrzeug-Sounds: PCM 16 Bit und Microsoft ADPCM (Format 2, so liegen die TDU2-Samples vor).</summary>
    public static class WavDecoder
    {
        static readonly int[] AdaptTable = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

        public static bool TryDecode(byte[] b, out float[] samples, out int channels, out int rate)
        {
            samples = null; channels = 0; rate = 0;
            if (b == null || b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
            int o = 12, fmtOff = -1, fmtLen = 0, dataOff = -1, dataLen = 0;
            while (o + 8 <= b.Length)
            {
                int sz = BitConverter.ToInt32(b, o + 4);
                if (sz < 0) break;
                if (b[o] == 'f' && b[o + 1] == 'm' && b[o + 2] == 't') { fmtOff = o + 8; fmtLen = sz; }
                else if (b[o] == 'd' && b[o + 1] == 'a' && b[o + 2] == 't' && b[o + 3] == 'a') { dataOff = o + 8; dataLen = Math.Min(sz, b.Length - dataOff); }
                o += 8 + sz + (sz & 1);
            }
            if (fmtOff < 0 || dataOff < 0) return false;
            int tag = BitConverter.ToUInt16(b, fmtOff);
            channels = BitConverter.ToUInt16(b, fmtOff + 2);
            rate = BitConverter.ToInt32(b, fmtOff + 4);
            int align = BitConverter.ToUInt16(b, fmtOff + 12);
            int bits = BitConverter.ToUInt16(b, fmtOff + 14);
            if (channels < 1 || channels > 2) return false;

            if (tag == 1 && bits == 16)
            {
                int n = dataLen / 2;
                samples = new float[n];
                for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(b, dataOff + 2 * i) / 32768f;
                return true;
            }
            if (tag != 2 || fmtLen < 22) return false;

            int spb = BitConverter.ToUInt16(b, fmtOff + 18);
            int nCoef = BitConverter.ToUInt16(b, fmtOff + 20);
            var c1 = new int[nCoef]; var c2 = new int[nCoef];
            for (int i = 0; i < nCoef; i++) { c1[i] = BitConverter.ToInt16(b, fmtOff + 22 + 4 * i); c2[i] = BitConverter.ToInt16(b, fmtOff + 24 + 4 * i); }

            int blocks = dataLen / align;
            var outp = new List<float>(blocks * spb * channels);
            var pred = new int[2]; var delta = new int[2]; var s1 = new int[2]; var s2 = new int[2];
            for (int bk = 0; bk < blocks; bk++)
            {
                int p = dataOff + bk * align, end = p + align;
                for (int c = 0; c < channels; c++) pred[c] = Math.Min(b[p++], nCoef - 1);
                for (int c = 0; c < channels; c++) { delta[c] = BitConverter.ToInt16(b, p); p += 2; }
                for (int c = 0; c < channels; c++) { s1[c] = BitConverter.ToInt16(b, p); p += 2; }
                for (int c = 0; c < channels; c++) { s2[c] = BitConverter.ToInt16(b, p); p += 2; }
                for (int c = 0; c < channels; c++) outp.Add(s2[c] / 32768f);
                for (int c = 0; c < channels; c++) outp.Add(s1[c] / 32768f);
                int ch = 0;
                int produced = 0, needed = (spb - 2) * channels;
                while (p < end && produced < needed)
                {
                    int by = b[p++];
                    for (int h = 0; h < 2 && produced < needed; h++)
                    {
                        int nib = h == 0 ? by >> 4 : by & 15;
                        int sn = (nib & 8) != 0 ? nib - 16 : nib;
                        int pv = ((s1[ch] * c1[pred[ch]]) + (s2[ch] * c2[pred[ch]])) >> 8;
                        int v = pv + sn * delta[ch];
                        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                        s2[ch] = s1[ch]; s1[ch] = v;
                        delta[ch] = Math.Max(16, (AdaptTable[nib] * delta[ch]) >> 8);
                        outp.Add(v / 32768f);
                        produced++;
                        ch = (ch + 1) % channels;
                    }
                }
            }
            samples = outp.ToArray();
            return true;
        }
    }

    /// <summary>
    /// hud.ini aus der Instrumenten-Bank: "AddHud &lt;Typ&gt; val_min val_max ori_min ori_max"
    /// (Typen interior_kmh, interior_mph, interior_rpm, exterior_rpm; Winkel in Grad).
    /// </summary>
    public sealed class GaugeConfig
    {
        public readonly Dictionary<string, (float valMin, float valMax, float oriMin, float oriMax)> Gauges =
            new Dictionary<string, (float, float, float, float)>(StringComparer.OrdinalIgnoreCase);

        public static GaugeConfig Parse(string text)
        {
            var g = new GaugeConfig();
            if (string.IsNullOrEmpty(text)) return g;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("AddHud", StringComparison.OrdinalIgnoreCase)) continue;
                int q0 = line.IndexOf('"'), q1 = q0 >= 0 ? line.IndexOf('"', q0 + 1) : -1;
                if (q1 < 0) continue;
                string type = line.Substring(q0 + 1, q1 - q0 - 1);
                var parts = line.Substring(q1 + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out var v0) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out var v1) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out var o0) &&
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out var o1))
                    g.Gauges[type] = (v0, v1, o0, o1);
            }
            return g;
        }

        public bool TryGet(string type, out (float valMin, float valMax, float oriMin, float oriMax) v) => Gauges.TryGetValue(type, out v);
    }
}
