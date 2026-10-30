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
    public enum XType
    {
        Object = 0,
        Bool = 1,
        SInt8 = 2,
        SInt16 = 3,
        SInt32 = 4,
        SInt64 = 5,
        UInt8 = 6,
        UInt16 = 7,
        UInt32 = 8,
        UInt64 = 9,
        Float = 10,
        Double = 11,
        String = 12,
        Virtual = 13,
        Array = 100
    }

    public static class XTypeInfo
    {
        public static string Name(XType t) => t switch
        {
            XType.Object => "OBJECT",
            XType.Bool => "BOOL",
            XType.SInt8 => "SINT8",
            XType.SInt16 => "SINT16",
            XType.SInt32 => "SINT32",
            XType.SInt64 => "SINT64",
            XType.UInt8 => "UINT8",
            XType.UInt16 => "UINT16",
            XType.UInt32 => "UINT32",
            XType.UInt64 => "UINT64",
            XType.Float => "FLOAT",
            XType.Double => "DOUBLE",
            XType.String => "STRING",
            XType.Virtual => "VIRTUAL",
            XType.Array => "ARRAY",
            _ => "?"
        };

        public static bool IsPrimitive(XType t) => t >= XType.Bool && t <= XType.String;
    }

    public abstract class XValue
    {
        public abstract XType Type { get; }
        public virtual string TypeName => XTypeInfo.Name(Type);

        public abstract string Display { get; }

        public bool IsEditable => XTypeInfo.IsPrimitive(Type) || Type == XType.String;
    }

    public sealed class XObject : XValue
    {
        public readonly List<string> _order = new List<string>();
        public readonly Dictionary<string, XValue> _map = new Dictionary<string, XValue>(StringComparer.Ordinal);

        public override XType Type => XType.Object;
        public override string Display => $"{{object, {_order.Count} fields}}";

        public IReadOnlyList<string> Keys => _order;
        public int Count => _order.Count;

        public XValue? this[string key]
        {
            get => _map.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (value is null)
                {
                    Remove(key);
                    return;
                }
                if (!_map.ContainsKey(key))
                    _order.Add(key);
                _map[key] = value;
            }
        }

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public bool Remove(string key)
        {
            if (_map.Remove(key))
            {
                _order.Remove(key);
                return true;
            }
            return false;
        }

        public IEnumerable<KeyValuePair<string, XValue>> Items
        {
            get
            {
                foreach (var k in _order)
                    yield return new KeyValuePair<string, XValue>(k, _map[k]);
            }
        }
    }

    public sealed class XArray : XValue
    {
        public XType ElementType { get; set; } = XType.Object;
        public List<XValue> Items { get; } = new List<XValue>();

        public override XType Type => XType.Array;
        public override string TypeName => $"ARRAY<{XTypeInfo.Name(ElementType)}>";
        public override string Display => $"[{Items.Count} x {XTypeInfo.Name(ElementType)}]";

        public int Count => Items.Count;
    }

    public sealed class XPrim : XValue
    {
        public XPrim(XType type, object? value)
        {
            Type = type;
            Value = value;
        }

        public override XType Type { get; }
        public object? Value { get; set; }

        public override string Display => FormatValue();

        public long AsLong() => Value switch
        {
            bool b => b ? 1 : 0,
            sbyte s => s,
            byte by => by,
            short sh => sh,
            ushort us => us,
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => unchecked((long)ul),
            float f => (long)f,
            double d => (long)d,
            string s => long.TryParse(s, out var r) ? r : 0,
            _ => 0
        };

        public ulong AsULong() => Value switch
        {
            bool b => b ? 1UL : 0UL,
            sbyte s => unchecked((ulong)s),
            byte by => by,
            short sh => unchecked((ulong)sh),
            ushort us => us,
            int i => unchecked((ulong)i),
            uint u => u,
            long l => unchecked((ulong)l),
            ulong ul => ul,
            float f => (ulong)f,
            double d => (ulong)d,
            string s => ulong.TryParse(s, out var r) ? r : 0,
            _ => 0
        };

        public double AsDouble() => Value switch
        {
            bool b => b ? 1 : 0,
            float f => f,
            double d => d,
            sbyte s => s,
            byte by => by,
            short sh => sh,
            ushort us => us,
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => ul,
            string s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
            _ => 0
        };

        public string? AsString() => Value as string;

        public string FormatValue()
        {
            if (Value is null) return "<null>";
            if (Value is bool b) return b ? "True" : "False";
            if (Value is float f) return f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (Value is double d) return d.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
            if (Value is string s) return s;
            return Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
