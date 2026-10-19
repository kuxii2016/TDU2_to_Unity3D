using System;
using System.Collections.Generic;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Formats
{
    /// <summary>Ein Einstellungs-Tag im MDSC-Block einer .ig2-Datei (z.B. "gmth" = Spielmodus, "nblp" = Runden).</summary>
    public sealed class Ige2Tag
    {
        public string Name;
        public byte Flags;
        public byte Group;
        public byte[] Data;

        public bool HasValue => Data != null && Data.Length >= 4;
        public uint AsUInt => HasValue ? BigEndian.U32(Data, 0) : 0;
        public int AsInt => HasValue ? BigEndian.I32(Data, 0) : 0;
        public float AsFloat => HasValue ? BigEndian.F32(Data, 0) : 0f;
        /// <summary>8-Byte-Werte (Marke/Modell-Paare wie VBrM, CvBr); bei 4 Byte der Int-Wert.</summary>
        public long AsLong => Data != null && Data.Length >= 8 ? ((long)BigEndian.U32(Data, 0) << 32) | BigEndian.U32(Data, 4) : AsInt;
        /// <summary>64-Byte-Texte (mnme, DesC).</summary>
        public string AsText
        {
            get
            {
                if (Data == null) return "";
                int n = 0; while (n < Data.Length && Data[n] != 0) n++;
                return System.Text.Encoding.ASCII.GetString(Data, 0, n);
            }
        }
    }

    /// <summary>Art eines Checkpoints (CHK2-Wort 0). Reihenfolge wie die Typtabelle des Challenge-Editors in der EXE
    /// ("Checkpoint Normal", "Checkpoint Cone", "Checkpoint Gate", "Braking Zone").</summary>
    public enum Ige2GateKind : uint { Normal = 0, Cones = 1, Gate = 2, BrakingZone = 3 }

    /// <summary>Ein Checkpoint-Tor (CHK2) einer Challenge: Position, Ausrichtung und Durchfahrtsbreite.</summary>
    public sealed class Ige2Checkpoint
    {
        public int Placement;               // 0 = auf einer Straße (road/piece), 1 = frei gesetzt
        public int Road, Piece, Segment, Side;
        public float Along;                 // Lage auf dem Straßenstück
        public Vector3 Position;            // Weltposition (Inselkoordinaten)
        public Vector3 Right, Up, Forward;  // Achsen aus der Transformationsmatrix (+Z zeigt die Fahrtrichtung)
        public uint GateKind;               // 0 Linie, 1 Kegel-Tor, 2 Tor (Bogen), 3 Bremszone
        public uint GateFlag;
        public float Width;                 // Durchfahrtsbreite in m
        public readonly int[] Links = new int[4];
        public Ige2GateKind Kind => (Ige2GateKind)GateKind;
    }

    /// <summary>
    /// Deko-Objekt (DEC2) einer Rennschul-Prüfung. Wort 0 = Art:
    ///   0 einzelner Kegel, 1/2 Anfang/Ende einer Kegel-Linie, 3 einzelne Absperrung, 4/5 Anfang/Ende einer Absperrungs-Linie.
    /// (gesichert über alle 529 .ig2: jede 1 wird von einer 2 gefolgt, jede 4 von einer 5; Typnamen "Cone", "ConeLine",
    /// "Separ", "SeparLine" aus der Typtabelle des Challenge-Editors in der EXE.)
    /// </summary>
    public sealed class Ige2Decor
    {
        public int Placement;
        public uint Kind;
        public Vector3 Position;
        public Vector3 Right, Up, Forward;
    }

    /// <summary>Fahrzeug (VEHI) einer Prüfung, z.B. die Autos der Überhol-Tests. Einstellungen als MDSC-Tags:
    /// VSph = Auto-ID (DBCarPhysicsData._id_car), Spmx = Höchsttempo (mm/s), Stch = Checkpoint, ab dem es losfährt (vermutet).</summary>
    public sealed class Ige2Vehicle
    {
        public int Placement;
        public int Road, Piece;
        public Vector3 Position;
        public Vector3 Right, Up, Forward;
        public readonly Dictionary<string, Ige2Tag> Settings = new Dictionary<string, Ige2Tag>();
        public Ige2Tag Setting(string n) => Settings.TryGetValue(n, out var t) ? t : null;
        public int CarId => Setting("VSph")?.AsInt ?? 0;
        public float MaxSpeed => (Setting("Spmx")?.AsInt ?? 0) / 1000f;     // m/s
        public int StartCheckpoint => Setting("Stch")?.AsInt ?? 0;
    }

    /// <summary>
    /// .ig2 - Challenge-/Rennstreckendefinition (Big-Endian).
    ///   "IGE2" u32 Version, @0x1C "MDSC" { u32 Größe, u32 Anzahl, Tags }, danach "ENTI" { u32 Größe, u32 Anzahl, Records }.
    ///   Tag:    char[4] Name, u8 Flags, u8 Group, u16 Länge, Daten.
    ///   Record: char[4] Art, u32 Größe, u32 Placement; bei Placement=0 u32 road/piece/segment/side + f32 along;
    ///           danach f32[16] Transformationsmatrix (Zeilen: Achsen | Translation), dann artabhängige Felder.
    ///   CHK2:   u32 GateKind, u32 Flag, f32 Breite, i32[4] Nachfolger.
    ///   DEC2:   u32 Art, u32 0.
    ///   VEHI:   u32 50, u32 ?, u32 n, n MDSC-Tags, i32 -1 x 17.
    /// Der Tag mit dem Namen "\0\0\0#" (hier "#") ist 1 bei Rundkursen (gesichert an allen Rennschul-Prüfungen,
    /// deren Beschreibung "Runde"/"Rundkurs" nennt).
    /// </summary>
    public sealed class Ige2File
    {
        public int Version;
        public readonly Dictionary<string, Ige2Tag> Settings = new Dictionary<string, Ige2Tag>();
        public readonly List<Ige2Checkpoint> Checkpoints = new List<Ige2Checkpoint>();
        public readonly List<Ige2Decor> Decors = new List<Ige2Decor>();
        public readonly List<Ige2Vehicle> Vehicles = new List<Ige2Vehicle>();
        public readonly List<string> RecordKinds = new List<string>();
        public int ConeCount => Decors.Count;
        public int VehicleCount => Vehicles.Count;

        public Ige2Tag Setting(string name) => Settings.TryGetValue(name, out var t) ? t : null;
        public int Int(string name, int def = 0) { var t = Setting(name); return t != null && t.HasValue ? t.AsInt : def; }

        public uint Mode => Setting("gmth")?.AsUInt ?? 0;
        public int Island => Setting("isla")?.AsInt ?? -1;
        public int Laps => Setting("nblp")?.AsInt ?? 0;
        public uint Length => Setting("leme")?.AsUInt ?? 0;
        /// <summary>Rundkurs (Tag "#" = 1): Ziel ist wieder die Startlinie, <see cref="Laps"/> Runden.</summary>
        public bool IsCircuit => Int("#") == 1;

        public static bool LooksLikeIge2(byte[] b) => b != null && b.Length >= 0x20 && BigEndian.TagIs(b, 0, "IGE2");

        public static Ige2File Parse(byte[] b)
        {
            var f = new Ige2File();
            if (!LooksLikeIge2(b)) return f;
            f.Version = BigEndian.I32(b, 8);

            int mdsc = BigEndian.I32(b, 4);
            if (mdsc < 0 || mdsc + 12 > b.Length || !BigEndian.TagIs(b, mdsc, "MDSC")) return f;
            int msize = BigEndian.I32(b, mdsc + 4);
            int mcount = BigEndian.I32(b, mdsc + 8);
            int mEnd = Math.Min(mdsc + msize, b.Length);
            ReadTags(b, mdsc + 12, mEnd, mcount, f.Settings);

            int enti = mEnd;
            if (enti + 12 > b.Length || !BigEndian.TagIs(b, enti, "ENTI")) return f;
            int esize = BigEndian.I32(b, enti + 4);
            int ecount = BigEndian.I32(b, enti + 8);
            int q = enti + 12, eEnd = Math.Min(enti + esize, b.Length);
            for (int i = 0; i < ecount && q + 12 <= eEnd; i++)
            {
                string kind = BigEndian.Tag(b, q);
                int rsize = BigEndian.I32(b, q + 4);
                int place = BigEndian.I32(b, q + 8);
                f.RecordKinds.Add(kind);
                int baseP = q + 12 + (place == 0 ? 20 : 0);
                if (rsize <= 0 || q + rsize > b.Length) break;
                if (baseP + 64 <= q + rsize)
                {
                    if (kind == "CHK2") f.ReadCheckpoint(b, q, baseP, place, q + rsize);
                    else if (kind == "DEC2") f.ReadDecor(b, baseP, place, q + rsize);
                    else if (kind == "VEHI") f.ReadVehicle(b, q, baseP, place, q + rsize);
                }
                q += rsize;
            }
            return f;
        }

        /// <summary>MDSC-Tags lesen. Namen werden von führenden Nullbytes befreit ("\0\0\0#" -> "#"); der namenlose Kopf-Tag entfällt.</summary>
        static void ReadTags(byte[] b, int p, int end, int count, Dictionary<string, Ige2Tag> into)
        {
            for (int i = 0; i < count && p + 8 <= end; i++)
            {
                int len = BigEndian.U16(b, p + 6);
                string raw = BigEndian.Tag(b, p);
                var tag = new Ige2Tag { Name = raw.Trim('\0'), Flags = b[p + 4], Group = b[p + 5] };
                if (len > 0 && p + 8 + len <= b.Length)
                {
                    tag.Data = new byte[len];
                    Array.Copy(b, p + 8, tag.Data, 0, len);
                }
                if (tag.Name.Length > 0 && !into.ContainsKey(tag.Name)) into[tag.Name] = tag;
                p += 8 + len;
            }
        }

        static void ReadAxes(byte[] b, int baseP, out Vector3 pos, out Vector3 right, out Vector3 up, out Vector3 fwd)
        {
            pos = new Vector3(BigEndian.F32(b, baseP + 12), BigEndian.F32(b, baseP + 28), BigEndian.F32(b, baseP + 44));
            right = new Vector3(BigEndian.F32(b, baseP + 0), BigEndian.F32(b, baseP + 16), BigEndian.F32(b, baseP + 32));
            up = new Vector3(BigEndian.F32(b, baseP + 4), BigEndian.F32(b, baseP + 20), BigEndian.F32(b, baseP + 36));
            fwd = new Vector3(BigEndian.F32(b, baseP + 8), BigEndian.F32(b, baseP + 24), BigEndian.F32(b, baseP + 40));
        }

        void ReadCheckpoint(byte[] b, int q, int baseP, int place, int recEnd)
        {
            var cp = new Ige2Checkpoint { Placement = place };
            if (place == 0)
            {
                cp.Road = BigEndian.I32(b, q + 12);
                cp.Piece = BigEndian.I32(b, q + 16);
                cp.Segment = BigEndian.I32(b, q + 20);
                cp.Side = BigEndian.I32(b, q + 24);
                cp.Along = BigEndian.F32(b, q + 28);
            }
            ReadAxes(b, baseP, out cp.Position, out cp.Right, out cp.Up, out cp.Forward);
            if (baseP + 92 <= recEnd)
            {
                cp.GateKind = BigEndian.U32(b, baseP + 64);
                cp.GateFlag = BigEndian.U32(b, baseP + 68);
                cp.Width = BigEndian.F32(b, baseP + 72);
                for (int i = 0; i < 4; i++) cp.Links[i] = BigEndian.I32(b, baseP + 76 + i * 4);
            }
            Checkpoints.Add(cp);
        }

        void ReadDecor(byte[] b, int baseP, int place, int recEnd)
        {
            var d = new Ige2Decor { Placement = place };
            ReadAxes(b, baseP, out d.Position, out d.Right, out d.Up, out d.Forward);
            if (baseP + 68 <= recEnd) d.Kind = BigEndian.U32(b, baseP + 64);
            Decors.Add(d);
        }

        void ReadVehicle(byte[] b, int q, int baseP, int place, int recEnd)
        {
            var v = new Ige2Vehicle { Placement = place };
            if (place == 0) { v.Road = BigEndian.I32(b, q + 12); v.Piece = BigEndian.I32(b, q + 16); }
            ReadAxes(b, baseP, out v.Position, out v.Right, out v.Up, out v.Forward);
            int p = baseP + 64;
            if (p + 12 <= recEnd)
            {
                int n = BigEndian.I32(b, p + 8);
                ReadTags(b, p + 12, recEnd, n, v.Settings);
            }
            Vehicles.Add(v);
        }
    }
}
