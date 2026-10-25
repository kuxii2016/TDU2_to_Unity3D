using System;
using System.Collections.Generic;
using System.Text;
using TDU2.IO;

namespace TDU2.Formats
{
    /// <summary>
    /// Eden "VMF" – in ein eigenes Containerformat transkodiertes Flash/SWF (Engine "edFlash" in TestDrive2.exe).
    /// Siehe Docs/TDU2_VMF_Flash_Format.md. Dieser Leser zerlegt Kopf, Ressourcentabelle und Tag-Strom
    /// (inkl. verschachtelter Sprites) und extrahiert die für einen Nachbau wichtigen Informationen:
    /// Bitmaps (Tag 126) mit Texturquelle, Sprites (Tag 127), Exporte, Frame-Labels, Textfeld-Variablen, FSCommands.
    /// </summary>
    public sealed class VmfFile
    {
        // ---------------------------------------------------------------- Tag-Codes
        public const int TagEnd = 0, TagShowFrame = 1, TagDefineShape = 2, TagPlaceObject = 4, TagDefineButton = 7,
            TagSetBackgroundColor = 9, TagDefineText = 11, TagDoAction = 12, TagDefineShape2 = 22, TagPlaceObject2 = 26,
            TagRemoveObject2 = 28, TagDefineShape3 = 32, TagDefineText2 = 33, TagDefineButton2 = 34, TagDefineEditText = 37,
            TagFrameLabel = 43, TagDefineMorphShape = 46, TagExportAssets = 56, TagDefineFontName = 88,
            // Eden-spezifisch
            TagEdenFont = 125, TagEdenBitmap = 126, TagEdenSprite = 127;

        public sealed class Tag
        {
            public int Code, Offset, Length;   // Offset = Beginn der Nutzdaten
            public List<Tag> Children;          // nur Sprites (127)
            public override string ToString() => $"Tag {Code} ({TagName(Code)}) @{Offset} len {Length}";
        }

        public sealed class Bitmap
        {
            public int Id, Width, Height;
            public float U0, V0, U1, V1;        // Ausschnitt in der Quelltextur
            public int Page = -1;                // >= 0: Atlas page_{Page+1:000}.2db, sonst eigene Textur fla_{Id:0000}.2db
            public string TextureName => Page >= 0 ? $"page_{Page + 1:000}" : $"fla_{Id:0000}";
        }

        public sealed class Font
        {
            public int Id, TextureId, Flags, GlyphCount, TexturedGlyphs;
            public string Name;
        }

        public sealed class Sprite { public int Id, FrameCount; public List<Tag> Tags; }

        public sealed class EditText { public int Id; public string Variable, InitialText; public int FontId; public float Height; }

        // ---------------------------------------------------------------- Kopf
        public byte[] Data;
        public int Version, HeaderWord, DeclaredLength, StageWidth, StageHeight, FrameRate, FrameCount, TagStart;
        public readonly List<(int page, int id)> ResourceTable = new List<(int, int)>();
        public int ResourceTableExtra;
        public readonly List<Tag> Tags = new List<Tag>();
        public readonly Dictionary<int, Bitmap> Bitmaps = new Dictionary<int, Bitmap>();
        public readonly Dictionary<int, Font> Fonts = new Dictionary<int, Font>();
        public readonly Dictionary<int, Sprite> Sprites = new Dictionary<int, Sprite>();
        public readonly List<(int id, string name)> Exports = new List<(int, string)>();
        public readonly List<string> FrameLabels = new List<string>();
        public readonly List<EditText> EditTexts = new List<EditText>();
        public readonly SortedSet<string> FsCommands = new SortedSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<int, int> TagCounts = new Dictionary<int, int>();

        public static VmfFile Parse(byte[] d)
        {
            if (d == null || d.Length < 19 || d[0] != (byte)'F' || d[1] != (byte)'M' || d[2] != (byte)'V')
                throw new FormatException("no valid VMF file");
            var f = new VmfFile { Data = d };
            f.Version = d[3];
            f.HeaderWord = Bin.U16(d, 4);
            f.DeclaredLength = (int)Bin.U32(d, 6);
            f.StageWidth = (d[10] << 8) | d[11];     // Big-Endian (Bedeutung: Bühnenmaß, Einheit unklar)
            f.StageHeight = (d[12] << 8) | d[13];
            f.FrameRate = (d[14] << 8) | d[15];
            f.FrameCount = Bin.U16(d, 16);
            int o = 19;
            if (d[18] != 0)
            {
                int n = Bin.U16(d, 19); o = 21;
                for (int i = 0; i < n && o + 4 <= d.Length; i++, o += 4) f.ResourceTable.Add((Bin.U16(d, o), Bin.U16(d, o + 2)));
                f.ResourceTableExtra = Bin.U16(d, o); o += 2;
            }
            f.TagStart = o;
            f.ReadTags(o, d.Length, f.Tags, 0);

            var pageOf = new Dictionary<int, int>();
            foreach (var (page, id) in f.ResourceTable) pageOf[id] = page;
            foreach (var b in f.Bitmaps.Values) if (pageOf.TryGetValue(b.Id, out var pg)) b.Page = pg;
            f.ScanFsCommands();
            return f;
        }

        void ReadTags(int o, int end, List<Tag> into, int depth)
        {
            var d = Data;
            while (o + 2 <= end)
            {
                int h = Bin.U16(d, o), code = h >> 6, len = h & 0x3F, p = o + 2;
                if (len == 0x3F) { len = (int)Bin.U32(d, p); p += 4; }
                if (p + len > end) break;
                var t = new Tag { Code = code, Offset = p, Length = len };
                into.Add(t);
                TagCounts[code] = TagCounts.TryGetValue(code, out var c) ? c + 1 : 1;
                try { Interpret(t, depth); } catch (Exception) { /* einzelne unbekannte Varianten überspringen */ }
                o = p + len;
                if (code == TagEnd) break;
            }
        }

        void Interpret(Tag t, int depth)
        {
            var d = Data; int p = t.Offset;
            switch (t.Code)
            {
                case TagEdenSprite:
                    {
                        // u16 id, u16 frameCount, u32 Länge des eingebetteten Tag-Stroms, dann Tags (wie SWF DefineSprite)
                        var s = new Sprite { Id = Bin.U16(d, p), FrameCount = Bin.U16(d, p + 2), Tags = new List<Tag>() };
                        int inner = (int)Bin.U32(d, p + 4);
                        int start = p + 8, end = Math.Min(t.Offset + t.Length, start + Math.Max(0, inner));
                        t.Children = s.Tags;
                        ReadTags(start, end, s.Tags, depth + 1);
                        Sprites[s.Id] = s;
                        break;
                    }
                case TagEdenBitmap:
                    {
                        var b = new Bitmap
                        {
                            Id = Bin.U16(d, p),
                            Width = Bin.U16(d, p + 2),
                            Height = Bin.U16(d, p + 4),
                            U0 = Bin.F32(d, p + 6),
                            V0 = Bin.F32(d, p + 10),
                            U1 = Bin.F32(d, p + 14),
                            V1 = Bin.F32(d, p + 18),
                        };
                        Bitmaps[b.Id] = b;
                        break;
                    }
                case TagEdenFont:
                    {
                        var fo = new Font { Id = Bin.U16(d, p), TextureId = Bin.U16(d, p + 2), Flags = d[p + 4] };
                        int q = p + 5; fo.Name = CString(d, ref q, t.Offset + t.Length);
                        if (q + 4 <= t.Offset + t.Length) { fo.GlyphCount = Bin.U16(d, q); fo.TexturedGlyphs = Bin.U16(d, q + 2); }
                        Fonts[fo.Id] = fo;
                        break;
                    }
                case TagExportAssets:
                    {
                        int n = Bin.U16(d, p), q = p + 2, end = t.Offset + t.Length;
                        for (int i = 0; i < n && q + 2 < end; i++) { int id = Bin.U16(d, q); q += 2; Exports.Add((id, CString(d, ref q, end))); }
                        break;
                    }
                case TagFrameLabel:
                    {
                        int q = p; FrameLabels.Add(CString(d, ref q, t.Offset + t.Length));
                        break;
                    }
                case TagDefineEditText:
                    EditTexts.Add(ReadEditText(t));
                    break;
            }
        }

        /// <summary>SWF DefineEditText: id, RECT, Flags (16 Bit), [Font, Höhe], [Farbe], [MaxLen], [Layout], Variable, [Text].</summary>
        EditText ReadEditText(Tag t)
        {
            var d = Data; int p = t.Offset, end = t.Offset + t.Length;
            var e = new EditText { Id = Bin.U16(d, p) };
            int q = p + 2;
            int nbits = d[q] >> 3;
            int rectBytes = (5 + nbits * 4 + 7) / 8;
            q += rectBytes;
            int f1 = d[q], f2 = d[q + 1]; q += 2;
            bool hasText = (f1 & 0x80) != 0, hasColor = (f1 & 0x04) != 0, hasMaxLen = (f1 & 0x02) != 0, hasFont = (f1 & 0x01) != 0;
            bool hasLayout = (f2 & 0x20) != 0;
            if (hasFont) { e.FontId = Bin.U16(d, q); e.Height = Bin.U16(d, q + 2) / 20f; q += 4; }
            if (hasColor) q += 4;
            if (hasMaxLen) q += 2;
            if (hasLayout) q += 9;
            e.Variable = CString(d, ref q, end);
            if (hasText && q < end) e.InitialText = CString(d, ref q, end);
            return e;
        }

        void ScanFsCommands()
        {
            var d = Data;
            var key = Encoding.ASCII.GetBytes("FSCommand:");
            for (int i = 0; i + key.Length < d.Length; i++)
            {
                int k = 0; while (k < key.Length && d[i + k] == key[k]) k++;
                if (k < key.Length) continue;
                int q = i; FsCommands.Add(CString(d, ref q, d.Length));
                i += key.Length;
            }
        }

        static string CString(byte[] d, ref int q, int end)
        {
            int s = q;
            while (q < end && d[q] != 0) q++;
            var chars = new char[q - s];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)d[s + i];   // Latin-1
            var str = new string(chars);
            if (q < end) q++;
            return str;
        }

        public static string TagName(int code)
        {
            switch (code)
            {
                case 0: return "End";
                case 1: return "ShowFrame";
                case 2: return "DefineShape";
                case 4: return "PlaceObject";
                case 7: return "DefineButton";
                case 9: return "SetBackgroundColor";
                case 11: return "DefineText";
                case 12: return "DoAction";
                case 22: return "DefineShape2";
                case 26: return "PlaceObject2";
                case 28: return "RemoveObject2";
                case 32: return "DefineShape3";
                case 33: return "DefineText2";
                case 34: return "DefineButton2";
                case 37: return "DefineEditText";
                case 43: return "FrameLabel";
                case 46: return "DefineMorphShape";
                case 56: return "ExportAssets";
                case 88: return "DefineFontName";
                case 125: return "Eden:DefineFont(Textur)";
                case 126: return "Eden:DefineBitmap(UV)";
                case 127: return "Eden:DefineSprite";
                default: return "?";
            }
        }

        /// <summary>Lesbarer Bericht (für das Editor-Werkzeug und die Dokumentation).</summary>
        public string Report(string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"== {title}");
            sb.AppendLine($"Version {Version}, Kopfwort {HeaderWord}, Länge {DeclaredLength}/{Data.Length}, Bühne {StageWidth}x{StageHeight}, {FrameRate} fps, {FrameCount} Frames, Tags ab {TagStart}");
            sb.AppendLine($"Ressourcentabelle: {ResourceTable.Count} Einträge (Extra {ResourceTableExtra})");
            sb.Append("Tags: "); foreach (var kv in TagCounts) sb.Append($"{kv.Key}:{TagName(kv.Key)}x{kv.Value}  "); sb.AppendLine();
            sb.AppendLine($"Bitmaps ({Bitmaps.Count}):");
            foreach (var b in Bitmaps.Values) sb.AppendLine($"  #{b.Id} {b.Width}x{b.Height} -> {b.TextureName}.2db uv({b.U0:0.###},{b.V0:0.###})-({b.U1:0.###},{b.V1:0.###})");
            sb.AppendLine($"Fonts ({Fonts.Count}):");
            foreach (var fo in Fonts.Values) sb.AppendLine($"  #{fo.Id} '{fo.Name}' Textur fla_{fo.TextureId:0000} Flags 0x{fo.Flags:x2} Glyphen {fo.GlyphCount}/{fo.TexturedGlyphs}");
            sb.AppendLine($"Sprites: {Sprites.Count}");
            sb.AppendLine($"Exporte ({Exports.Count}):"); foreach (var e in Exports) sb.AppendLine($"  #{e.id} {e.name}");
            sb.AppendLine($"Frame-Labels ({FrameLabels.Count}): {string.Join(", ", FrameLabels)}");
            sb.AppendLine($"Textfelder ({EditTexts.Count}):");
            foreach (var e in EditTexts) if (!string.IsNullOrEmpty(e.Variable)) sb.AppendLine($"  #{e.Id} var '{e.Variable}' font {e.FontId} h {e.Height:0.#} text '{e.InitialText}'");
            sb.AppendLine($"FSCommands ({FsCommands.Count}): {string.Join(", ", FsCommands)}");
            return sb.ToString();
        }
    }
}
