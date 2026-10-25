using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TDU2.IO;

namespace TDU2.Formats
{
    /// <summary>Knoten eines XMB-Baums. Objekte haben <see cref="Children"/>, Arrays <see cref="Items"/>, Werte <see cref="Value"/>.</summary>
    public sealed class XmbNode
    {
        public string Name;
        public XmbType Type;
        public bool IsArray;
        public object Value;                                   // bool, sbyte, short, int, long, byte, ushort, uint, ulong, float, double, string
        public readonly List<XmbNode> Children = new List<XmbNode>();
        public readonly List<XmbNode> Items = new List<XmbNode>();

        public XmbNode this[string name] => Get(name);

        public XmbNode Get(string name)
        {
            foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
            return null;
        }

        public IEnumerable<XmbNode> GetAll(string name)
        {
            foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.Ordinal)) yield return c;
        }

        public double AsDouble(double def = 0)
        {
            switch (Value)
            {
                case null: return def;
                case bool b: return b ? 1 : 0;
                case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
                default: return Convert.ToDouble(Value, CultureInfo.InvariantCulture);
            }
        }

        public float AsFloat(float def = 0) => Value == null ? def : (float)AsDouble(def);
        public int AsInt(int def = 0) => Value == null ? def : (int)Math.Round(AsDouble(def));
        public ulong AsULong(ulong def = 0) => Value is ulong u ? u : Value is long l ? (ulong)l : Value == null ? def : (ulong)AsDouble(def);
        public bool AsBool(bool def = false) => Value is bool b ? b : Value == null ? def : AsDouble() != 0;
        public string AsString(string def = null) => Value as string ?? (Value != null ? Convert.ToString(Value, CultureInfo.InvariantCulture) : def);

        public float F(string child, float def = 0) { var c = Get(child); return c != null ? c.AsFloat(def) : def; }
        public int I(string child, int def = 0) { var c = Get(child); return c != null ? c.AsInt(def) : def; }
        public bool B(string child, bool def = false) { var c = Get(child); return c != null ? c.AsBool(def) : def; }
        public string S(string child, string def = null) { var c = Get(child); return c != null ? c.AsString(def) : def; }
        public ulong U(string child, ulong def = 0) { var c = Get(child); return c != null ? c.AsULong(def) : def; }

        /// <summary>Alle Zahlen eines Arrays (oder eines Objekts mit Zahlen-Kindern) als float.</summary>
        public float[] Floats()
        {
            var src = IsArray ? Items : Children;
            var r = new float[src.Count];
            for (int i = 0; i < r.Length; i++) r[i] = src[i].AsFloat();
            return r;
        }

        public override string ToString() => IsArray ? $"{Name}[{Items.Count}]" : Type == XmbType.Object ? $"{Name}{{{Children.Count}}}" : $"{Name}={Value}";
    }

    public enum XmbType { Object, Bool, SInt8, SInt16, SInt32, SInt64, UInt8, UInt16, UInt32, UInt64, Float, Double, String, Array, Virtual }

    /// <summary>
    /// "XMBF" – binäres XML-Format von TDU2 (Datenbank, Kameras, Sound-Setups, Entities).
    ///   0x00 "XMBF", 0x08 Offset Deskriptor-Tabelle (Strings), 0x0C Offset Metadaten (Typtabelle, u32-Wörter),
    ///   0x10 Offset Daten, 0x14 Root-Name (relativ zur Deskriptor-Tabelle), 0x18 Root-Typindex.
    ///   Typwort: Bits 0..3 Typ, 4..14 Kindanzahl (Objekte), 15..31 Bytegröße.
    ///   Objekt-Typ t: danach je Kind 2 Wörter {Stringoffset, Typzeiger (Bit31 = Array, Bits 0..29 = Typindex)}.
    ///   Kinder liegen gepackt hintereinander (String = u32 Offset, Array = {u32 Anzahl, u32 Offset}, Virtual = {Typ, Offset}).
    /// Referenz: TDU2Emploder XMBReader (eigene, vereinfachte Implementierung für Little-Endian).
    /// </summary>
    public sealed class XmbFile
    {
        readonly byte[] _b;
        readonly int _desc, _meta, _data;
        public XmbNode Root { get; private set; }

        XmbFile(byte[] b)
        {
            _b = b;
            _desc = Bin.I32(b, 8);
            _meta = Bin.I32(b, 12);
            _data = Bin.I32(b, 16);
        }

        public static bool IsXmb(byte[] b) => b != null && b.Length > 28 && b[0] == 'X' && b[1] == 'M' && b[2] == 'B' && b[3] == 'F';

        public static XmbNode Parse(byte[] b)
        {
            if (!IsXmb(b)) throw new FormatException("no valid XMBF file");
            var x = new XmbFile(b);
            int rootName = Bin.I32(b, 20);
            int rootType = Bin.I32(b, 24);
            x.Root = x.ReadValue(x.CStr(x._desc + rootName), rootType, x._data, 0);
            return x.Root;
        }

        uint W(int i) => Bin.U32(_b, _meta + 4 * i);

        string CStr(int o)
        {
            if (o < 0 || o >= _b.Length) return "";
            int e = o;
            while (e < _b.Length && _b[e] != 0) e++;
            return Encoding.UTF8.GetString(_b, o, e - o);
        }

        static int SizeOf(uint typeWord) => (int)((typeWord >> 15) & 0x1FFFF);

        XmbNode ReadField(string name, uint typePtr, int addr, int depth)
        {
            int t = (int)(typePtr & 0x3FFFFFFF);
            if ((typePtr & 0x80000000u) == 0) return ReadValue(name, t, addr, depth);

            var arr = new XmbNode { Name = name, IsArray = true };
            if (addr + 8 > _b.Length) return arr;
            int n = Bin.I32(_b, addr);
            int baseAddr = _data + Bin.I32(_b, addr + 4);
            uint tw = W(t);
            int ty = (int)(tw & 0xF);
            arr.Type = (XmbType)ty;
            if (n < 0 || n > 1 << 22) return arr;
            for (int i = 0; i < n; i++)
            {
                XmbNode item;
                if (ty == 13 || ty == 14)
                {
                    int p = baseAddr + 8 * i;
                    item = ReadValue("", Bin.I32(_b, p), _data + Bin.I32(_b, p + 4), depth + 1);
                }
                else if (ty == 12)
                {
                    int p = baseAddr + 4 * i;
                    int so = Bin.I32(_b, p);
                    item = new XmbNode { Name = "", Type = XmbType.String, Value = so == -1 ? null : CStr(_data + so) };
                }
                else item = ReadValue("", t, baseAddr + SizeOf(tw) * i, depth + 1);
                arr.Items.Add(item);
            }
            return arr;
        }

        XmbNode ReadValue(string name, int t, int addr, int depth)
        {
            uint tw = W(t);
            int ty = (int)(tw & 0xF);
            var node = new XmbNode { Name = name, Type = (XmbType)ty };
            if (depth > 64 || addr < 0 || addr >= _b.Length) return node;
            switch (ty)
            {
                case 0:
                    {
                        int n = (int)((tw >> 4) & 0x7FF);
                        int off = 0;
                        for (int i = 0; i < n; i++)
                        {
                            int so = (int)W(t + 2 + 2 * i);
                            uint tp = W(t + 3 + 2 * i);
                            string cn = CStr(_desc + so);
                            try { node.Children.Add(ReadField(cn, tp, addr + off, depth + 1)); }
                            catch (Exception) { /* abgeschnittene Daten am Dateiende ignorieren */ }
                            if ((tp & 0x80000000u) != 0) off += 8;
                            else
                            {
                                uint ctw = W((int)(tp & 0x3FFFFFFF));
                                off += (ctw & 0xF) == 12 ? 4 : SizeOf(ctw);
                            }
                        }
                        break;
                    }
                case 1: node.Value = _b[addr] != 0; break;
                case 2: node.Value = (sbyte)_b[addr]; break;
                case 3: node.Value = Bin.I16(_b, addr); break;
                case 4: node.Value = Bin.I32(_b, addr); break;
                case 5: node.Value = BitConverter.ToInt64(_b, addr); break;
                case 6: node.Value = _b[addr]; break;
                case 7: node.Value = Bin.U16(_b, addr); break;
                case 8: node.Value = Bin.U32(_b, addr); break;
                case 9: node.Value = Bin.U64(_b, addr); break;
                case 10: node.Value = Bin.F32(_b, addr); break;
                case 11: node.Value = BitConverter.ToDouble(_b, addr); break;
                case 12:
                    {
                        int so = Bin.I32(_b, addr);
                        node.Value = so == -1 ? null : CStr(_data + so);
                        break;
                    }
                case 13:
                case 14:
                    {
                        // Virtual: {Typindex, Datenoffset}
                        int ti = Bin.I32(_b, addr);
                        var inner = ReadValue(name, ti, _data + Bin.I32(_b, addr + 4), depth + 1);
                        return inner;
                    }
            }
            return node;
        }
    }

    /// <summary>
    /// .cpr = mit XTEA verschlüsselte Datei (db_data.cpr, Physics.cpr ...).
    /// Variante aus TDU2: 8 Byte Seed vorne, danach Blöcke; Klartext[i] = Dec(Cipher[i+1]) XOR Cipher[i].
    /// Schlüssel "Other" (alle .cpr außer Savegames).
    /// </summary>
    public static class CprCrypto
    {
        static readonly uint[] KeyOther = { 0x4FE23C4Au, 0x80BAC211u, 0x6917BD3Au, 0xF0528EBDu };

        public static byte[] Decrypt(byte[] buf)
        {
            if (buf == null || buf.Length < 16) return Array.Empty<byte>();
            int blocks = (buf.Length - 8) >> 3;
            var outp = new byte[blocks * 8];
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 8;
                uint y = Bin.U32(buf, o + 8), z = Bin.U32(buf, o + 12);
                uint sum = 3337565984u;
                for (int i = 0; i < 32; i++)
                {
                    z -= (y + ((y >> 5) ^ (y << 4))) ^ (sum + KeyOther[(sum >> 11) & 3]);
                    sum += 1640531527u;
                    y -= (z + ((z >> 5) ^ (z << 4))) ^ (sum + KeyOther[sum & 3]);
                }
                y ^= Bin.U32(buf, o);
                z ^= Bin.U32(buf, o + 4);
                outp[o] = (byte)y; outp[o + 1] = (byte)(y >> 8); outp[o + 2] = (byte)(y >> 16); outp[o + 3] = (byte)(y >> 24);
                outp[o + 4] = (byte)z; outp[o + 5] = (byte)(z >> 8); outp[o + 6] = (byte)(z >> 16); outp[o + 7] = (byte)(z >> 24);
            }
            return outp;
        }
    }
}