using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// Texturen einer einzelnen geladenen Bank (Sektor/Area/Insel). Die TextureDefs werden im Hintergrund
    /// gelesen, die Unity-Texturen erst bei Bedarf im Main-Thread erzeugt und beim Entladen zerstört.
    /// </summary>
    public sealed class LocalTextureSet
    {
        public readonly Dictionary<ulong, TextureDef> Defs = new Dictionary<ulong, TextureDef>();
        readonly Dictionary<ulong, Texture2D> _created = new Dictionary<ulong, Texture2D>();

        public void Add(TextureDef def)
        {
            if (def != null && !Defs.ContainsKey(def.Key)) Defs[def.Key] = def;
        }

        public bool TryGet(ulong key, TextureWrapMode u, TextureWrapMode v, out Texture2D tex)
        {
            if (_created.TryGetValue(key, out tex)) return tex != null;
            if (!Defs.TryGetValue(key, out var def)) { tex = null; return false; }
            tex = def.CreateTexture(u, v);
            _created[key] = tex;
            def.Data = null; // Rohdaten freigeben
            return tex != null;
        }

        public void Destroy()
        {
            foreach (var t in _created.Values) if (t != null) UnityUtil.SafeDestroy(t);
            _created.Clear();
            Defs.Clear();
        }
    }

    /// <summary>
    /// Globaler Texturindex (commonworld.bnk, tallgrass.bnk ...): Key -> Bankeintrag, lazy geladen und behalten.
    /// </summary>
    public sealed class GlobalTextureCache
    {
        readonly Dictionary<ulong, (BnkArchive bank, BnkEntry entry)> _index = new Dictionary<ulong, (BnkArchive, BnkEntry)>();
        readonly Dictionary<ulong, Texture2D> _loaded = new Dictionary<ulong, Texture2D>();
        readonly Dictionary<string, ulong> _byName = new Dictionary<string, ulong>(System.StringComparer.OrdinalIgnoreCase);

        public int Count => _index.Count;

        /// <summary>Liest die Keys aller .2db einer Bank (nur 40 Byte pro Textur). Thread-sicher aufrufbar.</summary>
        public void IndexBank(BnkArchive bank)
        {
            var add = new List<(ulong, BnkEntry, string)>();
            foreach (var e in bank.All(".2db"))
            {
                var head = bank.ReadPartial(e, 0, 0x28);
                ulong key = TextureDef.PeekKey(head);
                if (key != 0) add.Add((key, e, e.Name));
            }
            lock (_index)
            {
                foreach (var (key, e, name) in add)
                {
                    if (!_index.ContainsKey(key)) _index[key] = (bank, e);
                    _byName[name] = key;
                }
            }
        }

        public bool Contains(ulong key) { lock (_index) return _index.ContainsKey(key); }

        public ulong KeyByName(string fileName)
        {
            lock (_index) return _byName.TryGetValue(fileName, out var k) ? k : 0;
        }

        /// <summary>Main-Thread.</summary>
        public Texture2D Get(ulong key, TextureWrapMode u = TextureWrapMode.Repeat, TextureWrapMode v = TextureWrapMode.Repeat, bool keepReadable = false)
        {
            if (key == 0) return null;
            if (_loaded.TryGetValue(key, out var t)) return t;
            (BnkArchive bank, BnkEntry entry) src;
            lock (_index) { if (!_index.TryGetValue(key, out src)) return null; }
            var def = TextureDef.Parse(src.bank.Read(src.entry), src.entry.Name);
            t = def?.CreateTexture(u, v, false, keepReadable);
            _loaded[key] = t;
            return t;
        }

        public void Clear()
        {
            foreach (var t in _loaded.Values) if (t != null) UnityUtil.SafeDestroy(t);
            _loaded.Clear();
        }
    }
}
