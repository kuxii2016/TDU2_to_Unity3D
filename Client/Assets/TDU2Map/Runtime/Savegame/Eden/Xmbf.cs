// Portiert aus EdenSavegame (TDU2 Savegame editor) für Unity (C# 9 / .NET Standard 2.1, ohne unsafe).
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EdenSavegame
{
    public sealed class XmbfDocument
    {
        public byte[] Template { get; set; } = Array.Empty<byte>();
        public string RootName { get; set; } = "";
        public XObject Root { get; set; } = new XObject();
        public int RootTypeId { get; set; }
        public int RootNameOff { get; set; }
    }

    public sealed class GrowBuffer
    {
        public byte[] _data;
        public int _len;

        public GrowBuffer(byte[] initial)
        {
            _data = (byte[])initial.Clone();
            _len = initial.Length;
        }

        public int Length => _len;

        public void Ensure(int cap)
        {
            if (cap <= _data.Length) return;
            int n = _data.Length < 16 ? 16 : _data.Length;
            while (n < cap) n *= 2;
            Array.Resize(ref _data, n);
        }

        public byte ReadByte(int off) => _data[off];
        public uint ReadU32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));

        public void WriteByte(int off, byte v) => _data[off] = v;
        public void WriteU16(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(off), v);
        public void WriteU32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(off), v);
        public void WriteU64(int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(off), v);
        public void WriteF32(int off, float v) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(off), NetCompat.SingleToUInt32Bits(v));
        public void WriteF64(int off, double v) => BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(off), NetCompat.DoubleToUInt64Bits(v));

        public int Append(byte[] b)
        {
            Ensure(_len + b.Length);
            int at = _len;
            Array.Copy(b, 0, _data, at, b.Length);
            _len += b.Length;
            return at;
        }

        public int ZeroFill(int count)
        {
            Ensure(_len + count);
            int at = _len;
            Array.Clear(_data, at, count);
            _len += count;
            return at;
        }

        public byte[] ToArray()
        {
            var r = new byte[_len];
            Array.Copy(_data, 0, r, 0, _len);
            return r;
        }

        public bool TryReadString(int off, out string s)
        {
            if (off < 0 || off >= _len)
            {
                s = "";
                return false;
            }
            int e = off;
            while (e < _len && _data[e] != 0) e++;
            s = Encoding.UTF8.GetString(_data, off, e - off);
            return true;
        }
    }

    public static class Xmbf
    {
        public const uint NullStringOffset = 0xFFFFFFFF;

        // ------------------------------------------------------------------
        // Decoding
        // ------------------------------------------------------------------

        public static XmbfDocument Decode(byte[] xmbf)
        {
            if (xmbf.Length < 28 || xmbf[0] != (byte)'X' || xmbf[1] != (byte)'M' || xmbf[2] != (byte)'B' || xmbf[3] != (byte)'F')
                throw new InvalidDataException("Invalid XMBF magic header.");

            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(4, 4));
            int sec0Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(8, 4));
            int sec1Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(12, 4));
            int sec2Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(16, 4));
            int rootNameOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(20, 4));
            int rootTypeId = (int)BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(24, 4));

            byte[] sec0 = NetCompat.Sub(xmbf, sec0Off, sec1Off);
            int n1 = (sec2Off - sec1Off) / 4;
            var sec1 = new uint[n1];
            for (int i = 0; i < n1; i++)
                sec1[i] = BinaryPrimitives.ReadUInt32LittleEndian(xmbf.AsSpan(sec1Off + i * 4, 4));
            byte[] sec2 = NetCompat.Sub(xmbf, sec2Off, xmbf.Length);

            string GetSym(int off)
            {
                if (off < 0 || off >= sec0.Length) return "";
                int e = off;
                while (e < sec0.Length && sec0[e] != 0) e++;
                return NetCompat.Latin1String(sec0, off, e - off);
            }

            string? GetStr(uint off)
            {
                if (off == NullStringOffset) return null;
                int o = (int)off;
                if (o < 0 || o >= sec2.Length) return "";
                int e = o;
                while (e < sec2.Length && sec2[e] != 0) e++;
                return Encoding.UTF8.GetString(sec2, o, e - o);
            }

            XPrim ParsePrimitive(XType t, int off)
            {
                switch (t)
                {
                    case XType.Bool: return new XPrim(t, sec2[off] != 0);
                    case XType.SInt8: return new XPrim(t, (long)(sbyte)sec2[off]);
                    case XType.SInt16: return new XPrim(t, (long)BinaryPrimitives.ReadInt16LittleEndian(sec2.AsSpan(off)));
                    case XType.SInt32: return new XPrim(t, (long)BinaryPrimitives.ReadInt32LittleEndian(sec2.AsSpan(off)));
                    case XType.SInt64: return new XPrim(t, BinaryPrimitives.ReadInt64LittleEndian(sec2.AsSpan(off)));
                    case XType.UInt8: return new XPrim(t, (ulong)sec2[off]);
                    case XType.UInt16: return new XPrim(t, (ulong)BinaryPrimitives.ReadUInt16LittleEndian(sec2.AsSpan(off)));
                    case XType.UInt32: return new XPrim(t, (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(off)));
                    case XType.UInt64: return new XPrim(t, BinaryPrimitives.ReadUInt64LittleEndian(sec2.AsSpan(off)));
                    case XType.Float: return new XPrim(t, NetCompat.ReadSingleLittleEndian(sec2.AsSpan(off)));
                    case XType.Double: return new XPrim(t, NetCompat.ReadDoubleLittleEndian(sec2.AsSpan(off)));
                    case XType.String:
                        uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(off));
                        return new XPrim(t, GetStr(ptr));
                    default:
                        return new XPrim(t, null);
                }
            }

            XObject ParseStruct(int typeId, int offset)
            {
                uint hdr = sec1[typeId];
                int ntype = (int)(hdr & 0xF);
                if (ntype != 0)
                    throw new InvalidDataException($"Expected struct type (0), got {ntype}");
                int numFields = (int)((hdr >> 4) & 0x7FF);

                var res = new XObject();
                int cur = offset;
                for (int f = 0; f < numFields; f++)
                {
                    uint dw0 = sec1[typeId + 2 + f * 2];
                    uint dw1 = sec1[typeId + 2 + f * 2 + 1];
                    string fname = GetSym((int)(dw0 & 0x7FFFFFFF));
                    bool isArr = ((dw1 >> 31) & 1) != 0;
                    int tId = (int)(dw1 & 0x3FFFFFFF);

                    if (isArr)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(cur));
                        uint arrOff = BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(cur + 4));
                        cur += 8;

                        uint elemHdr = sec1[tId];
                        var elemType = (XType)(elemHdr & 0xF);
                        int elemSize = (int)((elemHdr >> 15) & 0x1FFFF);

                        var arr = new XArray { ElementType = elemType };
                        if (elemType == XType.Object)
                        {
                            for (int k = 0; k < cnt; k++)
                                arr.Items.Add(ParseStruct(tId, (int)arrOff + k * elemSize));
                        }
                        else if (elemType == XType.String)
                        {
                            for (int k = 0; k < cnt; k++)
                            {
                                uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan((int)arrOff + k * 4));
                                arr.Items.Add(new XPrim(XType.String, GetStr(sOff)));
                            }
                        }
                        else
                        {
                            for (int k = 0; k < cnt; k++)
                                arr.Items.Add(ParsePrimitive(elemType, (int)arrOff + k * elemSize));
                        }
                        res[fname] = arr;
                    }
                    else
                    {
                        uint targetHdr = sec1[tId];
                        var targetNtype = (XType)(targetHdr & 0xF);
                        int tSize = (int)((targetHdr >> 15) & 0x1FFFF);

                        if (targetNtype == XType.Object)
                        {
                            res[fname] = ParseStruct(tId, cur);
                            cur += tSize;
                        }
                        else if (targetNtype == XType.String)
                        {
                            uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(cur));
                            res[fname] = new XPrim(XType.String, GetStr(sOff));
                            cur += 4;
                        }
                        else if (targetNtype == XType.Virtual)
                        {
                            int vTid = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(cur));
                            int vOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec2.AsSpan(cur + 4));
                            res[fname] = ParseStruct(vTid, vOff);
                            cur += 8;
                        }
                        else
                        {
                            res[fname] = ParsePrimitive(targetNtype, cur);
                            cur += tSize;
                        }
                    }
                }
                return res;
            }

            var doc = new XmbfDocument
            {
                Template = xmbf,
                RootName = GetSym(rootNameOff),
                Root = ParseStruct(rootTypeId, 0),
                RootTypeId = rootTypeId,
                RootNameOff = rootNameOff
            };
            _ = ver;
            return doc;
        }

        // ------------------------------------------------------------------
        // Surgical (offset-preserving) encoding
        // ------------------------------------------------------------------

        public static byte[] Encode(XmbfDocument doc)
        {
            byte[] template = doc.Template;
            int sec0Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(8, 4));
            int sec1Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(12, 4));
            int sec2Off = (int)BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(16, 4));

            byte[] sec0 = NetCompat.Sub(template, sec0Off, sec1Off);
            int n1 = (sec2Off - sec1Off) / 4;
            var sec1 = new uint[n1];
            for (int i = 0; i < n1; i++)
                sec1[i] = BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(sec1Off + i * 4, 4));

            string GetSym(int off)
            {
                if (off < 0 || off >= sec0.Length) return "";
                int e = off;
                while (e < sec0.Length && sec0[e] != 0) e++;
                return NetCompat.Latin1String(sec0, off, e - off);
            }

            var buf = new GrowBuffer(NetCompat.Sub(template, sec2Off, template.Length));
            var stringCache = new Dictionary<string, uint>(StringComparer.Ordinal);

            uint AddString(string s)
            {
                if (stringCache.TryGetValue(s, out var cached)) return cached;
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                var full = new byte[bytes.Length + 1];
                Array.Copy(bytes, full, bytes.Length);
                int off = buf.Append(full);
                uint uoff = (uint)off;
                stringCache[s] = uoff;
                return uoff;
            }

            void WritePrimitive(int off, XType type, XPrim? prim)
            {
                if (prim is null) return;
                switch (type)
                {
                    case XType.Bool:
                        bool bv = prim.Value is bool b ? b : prim.AsLong() != 0;
                        if ((buf.ReadByte(off) != 0) != bv)
                            buf.WriteByte(off, (byte)(bv ? 1 : 0));
                        break;
                    case XType.SInt8: buf.WriteByte(off, (byte)(sbyte)prim.AsLong()); break;
                    case XType.SInt16: buf.WriteU16(off, (ushort)(short)prim.AsLong()); break;
                    case XType.SInt32: buf.WriteU32(off, (uint)(int)prim.AsLong()); break;
                    case XType.SInt64: buf.WriteU64(off, (ulong)prim.AsLong()); break;
                    case XType.UInt8: buf.WriteByte(off, (byte)prim.AsULong()); break;
                    case XType.UInt16: buf.WriteU16(off, (ushort)prim.AsULong()); break;
                    case XType.UInt32: buf.WriteU32(off, (uint)prim.AsULong()); break;
                    case XType.UInt64: buf.WriteU64(off, prim.AsULong()); break;
                    case XType.Float: buf.WriteF32(off, (float)prim.AsDouble()); break;
                    case XType.Double: buf.WriteF64(off, prim.AsDouble()); break;
                }
            }

            void WriteStringSlot(int slot, XPrim? prim)
            {
                string? newVal = prim?.Value as string;
                uint oldPtr = buf.ReadU32(slot);
                string? oldVal = oldPtr == NullStringOffset ? null : (buf.TryReadString((int)oldPtr, out var os) ? os : "");
                if (!string.Equals(newVal, oldVal, StringComparison.Ordinal))
                {
                    uint np = newVal is null ? NullStringOffset : AddString(newVal);
                    buf.WriteU32(slot, np);
                }
            }

            void EncodeArrayElements(XType elemType, int elemSize, int typeId, XArray arr, int arrBase)
            {
                if (elemType == XType.Object)
                {
                    for (int k = 0; k < arr.Count; k++)
                        EncodeStruct(typeId, arr.Items[k] as XObject ?? new XObject(), arrBase + k * elemSize);
                }
                else if (elemType == XType.String)
                {
                    for (int k = 0; k < arr.Count; k++)
                        WriteStringSlot(arrBase + k * 4, arr.Items[k] as XPrim);
                }
                else
                {
                    for (int k = 0; k < arr.Count; k++)
                        WritePrimitive(arrBase + k * elemSize, elemType, arr.Items[k] as XPrim);
                }
            }

            void EncodeStruct(int typeId, XObject obj, int baseOff)
            {
                uint hdr = sec1[typeId];
                int numFields = (int)((hdr >> 4) & 0x7FF);
                int cur = baseOff;
                for (int f = 0; f < numFields; f++)
                {
                    uint dw0 = sec1[typeId + 2 + f * 2];
                    uint dw1 = sec1[typeId + 2 + f * 2 + 1];
                    string fname = GetSym((int)(dw0 & 0x7FFFFFFF));
                    bool isArr = ((dw1 >> 31) & 1) != 0;
                    int tId = (int)(dw1 & 0x3FFFFFFF);
                    XValue? val = obj[fname];

                    if (isArr)
                    {
                        var arr = val as XArray ?? new XArray();
                        uint elemHdr = sec1[tId];
                        var elemType = (XType)(elemHdr & 0xF);
                        int elemSize = (int)((elemHdr >> 15) & 0x1FFFF);

                        uint oldCnt = buf.ReadU32(cur);
                        uint oldOff = buf.ReadU32(cur + 4);

                        if (arr.Count == 0)
                        {
                            if (oldCnt != 0)
                            {
                                buf.WriteU32(cur, 0);
                                buf.WriteU32(cur + 4, 0);
                            }
                        }
                        else if (oldCnt == (uint)arr.Count)
                        {
                            EncodeArrayElements(elemType, elemSize, tId, arr, (int)oldOff);
                        }
                        else
                        {
                            int need = elemType == XType.String ? arr.Count * 4 : arr.Count * elemSize;
                            int newOff = buf.ZeroFill(need);
                            EncodeArrayElements(elemType, elemSize, tId, arr, newOff);
                            buf.WriteU32(cur, (uint)arr.Count);
                            buf.WriteU32(cur + 4, (uint)newOff);
                        }
                        cur += 8;
                    }
                    else
                    {
                        uint targetHdr = sec1[tId];
                        var targetNtype = (XType)(targetHdr & 0xF);
                        int tSize = (int)((targetHdr >> 15) & 0x1FFFF);

                        if (targetNtype == XType.Object)
                        {
                            EncodeStruct(tId, val as XObject ?? new XObject(), cur);
                            cur += tSize;
                        }
                        else if (targetNtype == XType.String)
                        {
                            WriteStringSlot(cur, val as XPrim);
                            cur += 4;
                        }
                        else if (targetNtype == XType.Virtual)
                        {
                            cur += 8;
                        }
                        else
                        {
                            WritePrimitive(cur, targetNtype, val as XPrim);
                            cur += tSize;
                        }
                    }
                }
            }

            EncodeStruct(doc.RootTypeId, doc.Root, 0);

            byte[] newSec2 = buf.ToArray();
            var output = new byte[sec2Off + newSec2.Length];
            Array.Copy(template, 0, output, 0, sec2Off);
            Array.Copy(newSec2, 0, output, sec2Off, newSec2.Length);
            return output;
        }
    }
}
