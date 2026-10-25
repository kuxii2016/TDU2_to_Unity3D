using System;
using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// Begehbare Interieurs (Euro/Bnk/interior/*.bnk): Autohändler (icd_*), Wohnbereich (lph_*/ich_*), Garage (gph_*/gch_*) ...
    /// entities.xmb → ExportType_Entity:
    ///   ArrayOf_Entity__FirstPerson   "FirstPerson": StartCamera = __Hc der Startkamera (Ich-Perspektive)
    ///   ArrayOf_Entity__Camera        cam_entree, cam_entree_garage, Cam_Hostess ... (orient_pos_scale, Fov)
    ///   ArrayOf_Entity__Volume__FpSelectable  Hotspots: Exit/ExitDoor (Type 0), Hostess_area (1), Garage (5), Living (6),
    ///                                 Diary (9), Dressing (10), Billboard (12/14), Bar (50), Office (51), Club (54), Media (57, "td_media")
    ///                                 volume_mat = Spielraum → Einheitswürfel [-1, 1]³ (Spalten _0.._2, Translation _3)
    ///   ArrayOf_Entity__PosCam        D_CAR_01.. Stellplätze für Autos
    ///   ArrayOf_Entity__MyHouseObject Möbel (interior/myhouse/&lt;name ohne _NN&gt;.bnk)
    /// Ausrichtung wie BankModel.EntityMatrix (Quaternion konjugiert); "vorne" = lokale +Z-Achse.
    /// </summary>
    public sealed class InteriorScene
    {
        public class Ent
        {
            public string Name;
            public ulong Hc;
            public Matrix4x4 M = Matrix4x4.identity;    // Spielraum der Szene
            public XmbNode Node;
            public Vector3 Position => M.GetColumn(3);
            public Vector3 Forward => M.MultiplyVector(Vector3.forward).normalized;
        }

        public sealed class Selectable : Ent
        {
            public int Type;
            public string Str;
            public Matrix4x4 Volume = Matrix4x4.identity;   // Spielraum -> [-1,1]^3
            public ulong Camera, Cinematic;

            /// <summary>Strahl (Spielraum der Szene) gegen das Volumen; Abstand bis zum Eintritt.</summary>
            public bool Raycast(Vector3 o, Vector3 d, out float t)
            {
                var lo = Volume.MultiplyPoint3x4(o);
                var ld = Volume.MultiplyVector(d);
                float t0 = 0f, t1 = float.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    if (Mathf.Abs(ld[i]) < 1e-8f)
                    {
                        if (lo[i] < -1f || lo[i] > 1f) { t = 0; return false; }
                        continue;
                    }
                    float a = (-1f - lo[i]) / ld[i], b = (1f - lo[i]) / ld[i];
                    if (a > b) { var tmp = a; a = b; b = tmp; }
                    t0 = Mathf.Max(t0, a); t1 = Mathf.Min(t1, b);
                    if (t0 > t1) { t = 0; return false; }
                }
                t = t0;
                return true;
            }

            public bool Contains(Vector3 p)
            {
                var l = Volume.MultiplyPoint3x4(p);
                return Mathf.Abs(l.x) <= 1f && Mathf.Abs(l.y) <= 1f && Mathf.Abs(l.z) <= 1f;
            }
        }

        /// <summary>Ausstellungsauto auf Drehteller (Entity__Actor__ActorVehiclePosition, z.B. Casino-Lobby): VehicleId, Umdrehungen/min.</summary>
        public sealed class ShowCar : Ent
        {
            public int VehicleId;
            public float TurnsPerMinute;
        }

        public string BankPath;
        public readonly List<ShowCar> ShowCars = new List<ShowCar>();
        public ulong StartCamera;
        public readonly List<Ent> Cameras = new List<Ent>();
        public readonly List<Ent> CarSlots = new List<Ent>();
        public readonly List<Ent> HouseObjects = new List<Ent>();
        public readonly List<Ent> Npcs = new List<Ent>();
        public readonly List<Selectable> Selectables = new List<Selectable>();

        public Ent Camera(ulong hc)
        {
            foreach (var c in Cameras) if (c.Hc == hc) return c;
            return null;
        }

        public Ent Camera(string name)
        {
            foreach (var c in Cameras) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>Startpunkt der Ich-Perspektive: FirstPerson.StartCamera, sonst cam_entree, sonst erste Kamera.</summary>
        public Ent StartCam => Camera(StartCamera) ?? Camera("cam_entree") ?? (Cameras.Count > 0 ? Cameras[0] : null);

        public static InteriorScene Parse(BnkArchive bank)
        {
            var sc = new InteriorScene { BankPath = bank.FilePath };
            var e = bank.Find("entities.xmb");
            if (e == null) return sc;
            var root = XmbFile.Parse(bank.Read(e));
            var et = root.Get("ExportType_Entity");
            if (et == null) return sc;
            foreach (var arr in et.Children)
            {
                if (!arr.IsArray) continue;
                foreach (var it in arr.Items)
                {
                    switch (arr.Name)
                    {
                        case "ArrayOf_Entity__FirstPerson": sc.StartCamera = it.U("StartCamera"); break;
                        case "ArrayOf_Entity__Camera": sc.Cameras.Add(Fill(new Ent(), it)); break;
                        case "ArrayOf_Entity__PosCam": sc.CarSlots.Add(Fill(new Ent(), it)); break;
                        case "ArrayOf_Entity__MyHouseObject": sc.HouseObjects.Add(Fill(new Ent(), it)); break;
                        case "ArrayOf_Entity__PNJ": sc.Npcs.Add(Fill(new Ent(), it)); break;
                        case "ArrayOf_Entity__Actor__ActorVehiclePosition":
                            {
                                var v = Fill(new ShowCar(), it);
                                v.VehicleId = it.I("VehicleId"); v.TurnsPerMinute = it.F("NbTurnByMinute");
                                if (v.VehicleId > 0) sc.ShowCars.Add(v);
                                break;
                            }
                        case "ArrayOf_Entity__Volume__FpSelectable":
                            {
                                var s = Fill(new Selectable(), it);
                                s.Type = it.I("Type"); s.Str = it.S("SelectableString", "");
                                s.Camera = it.U("Camera"); s.Cinematic = it.U("Cinematic");
                                var vm = it.Get("volume_mat");
                                if (vm != null)
                                {
                                    var m = new Matrix4x4();
                                    for (int c = 0; c < 4; c++)
                                    {
                                        var col = vm.Get("_" + c);
                                        m.SetColumn(c, col != null ? new Vector4(col.F("X"), col.F("Y"), col.F("Z"), col.F("W", c == 3 ? 1f : 0f)) : (c == 3 ? new Vector4(0, 0, 0, 1) : Vector4.zero));
                                    }
                                    if (Mathf.Abs(m.determinant) > 1e-9f) s.Volume = m;
                                    else s.Volume = s.M.inverse;
                                }
                                else s.Volume = s.M.inverse;
                                sc.Selectables.Add(s);
                                break;
                            }
                    }
                }
            }
            sc.CarSlots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return sc;
        }

        static T Fill<T>(T e, XmbNode it) where T : Ent
        {
            e.Name = it.S("__Name", "");
            e.Hc = it.U("__Hc");
            e.Node = it;
            var ops = it.Get("orient_pos_scale");
            if (ops != null) e.M = BankModel.EntityMatrix(ops);
            return e;
        }

        /// <summary>Hotspot-Beschriftung (wie HST_LIVING_* / HST_CAR_DEALER* im Spiel).</summary>
        public static string Label(Selectable s)
        {
            string n = s.Name ?? "";
            if (n.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)) return "Verlassen";
            switch (s.Type)
            {
                case 1: return "Hostess – Auto kaufen";
                case 5: return "Garage";
                case 6: return "Wohnbereich";
                case 9: return "MyTULive";
                case 10: return "Garderobe";
                case 12:
                case 14: return "Fahrzeugtafel – Auto wählen";
                case 50: return "Bar";
                case 51: return "Büro";
                case 54: return "Club";
                case 57: return "TDU Media";
            }
            return n;
        }
    }
}