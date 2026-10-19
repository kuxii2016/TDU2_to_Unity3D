using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.World
{
    public sealed class LibraryLod
    {
        public Mesh Mesh;
        public Material[] Materials;   // pro Submesh
        public float MaxDistance;      // Weltmeter (bereits skaliert)
    }

    public sealed class LibraryObject
    {
        public ulong Key;
        public string Name;
        public LibraryLod[] Lods;
        public float Radius;
    }

    /// <summary>
    /// commonworld.bnk: gemeinsame Objekt-Library der Insel (Bäume, Büsche, Felsen, Props).
    /// Die .pmi-Dateien der Sektoren referenzieren INS-Keys dieser Library.
    ///   library.3dd: INSA (Key -> INS mit LOD-Liste + MBNK), OBJA, HIEA
    ///   library.3dg: Geometrien, library.2dm: Materialien, *.2db: Texturen
    /// </summary>
    public sealed class ObjectLibrary
    {
        public BnkArchive Bank;
        public Scene3DD Scene;
        public Geometry3DG Geo;
        public Material2DM Materials;
        public readonly BuildResources Resources = new BuildResources();
        /// <summary>
        /// Map-Root ist an X gespiegelt (TDU2 ist rechtshändig, Unity linkshändig). Für GPU-Instancing werden die Meshes
        /// vorgespiegelt und die Instanzmatrizen mit <see cref="UnityUtil.MirrorXMatrix"/> multipliziert, damit jede
        /// Instanzmatrix eine positive Determinante hat (Culling/Winding bleibt korrekt).
        /// </summary>
        public bool MirrorMeshes;
        readonly Dictionary<Mesh, Mesh> _mirrored = new Dictionary<Mesh, Mesh>();
        readonly Dictionary<ulong, LibraryObject> _objects = new Dictionary<ulong, LibraryObject>();
        readonly HashSet<ulong> _missing = new HashSet<ulong>();

        public int MissingCount => _missing.Count;

        /// <summary>Thread-sicher (Hintergrund).</summary>
        public static ObjectLibrary Load(string path, GlobalTextureCache textures)
        {
            var lib = new ObjectLibrary { Bank = BnkArchive.Open(path) };
            var e3dd = lib.Bank.FindByExt(".3dd");
            var e3dg = lib.Bank.FindByExt(".3dg");
            var e2dm = lib.Bank.FindByExt(".2dm");
            if (e3dd != null) lib.Scene = Scene3DD.Parse(lib.Bank.Read(e3dd));
            if (e3dg != null) lib.Geo = Geometry3DG.Parse(lib.Bank.Read(e3dg));
            if (e2dm != null) lib.Materials = Material2DM.Parse(lib.Bank.Read(e2dm));
            textures?.IndexBank(lib.Bank);
            return lib;
        }

        public bool Contains(ulong insKey) => Scene != null && Scene.InsHash.ContainsKey(insKey);

        /// <summary>Main-Thread: Meshes/Materialien eines Library-Objekts (gecacht).</summary>
        public LibraryObject Get(ulong insKey, GlobalTextureCache textures, ObjectBuildOptions opt)
        {
            if (_objects.TryGetValue(insKey, out var obj)) return obj;
            if (Scene == null || Geo == null || !Scene.InsHash.TryGetValue(insKey, out int insOff))
            {
                _missing.Add(insKey);
                _objects[insKey] = null;
                return null;
            }
            var ins = Scene.GetIns(insOff);
            var bankKeys = Scene.ReadMaterialBank(ins.MaterialBankOffset);
            var lods = new List<LibraryLod>();
            float radius = 1f;
            System.Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex = (k, u, v) => textures.Get(k, u, v);
            foreach (var l in ins.Lods)
            {
                var md = Geo.BuildMesh(l.ObjKey);
                var r = ObjectBuilder.GetRenderable(md, l.ObjKey, bankKeys, Materials, getTex, Resources, opt, ins.MaterialBankOffset);
                if (r.mesh == null) continue;
                var mesh = r.mesh;
                if (MirrorMeshes)
                {
                    if (!_mirrored.TryGetValue(mesh, out var mm)) { mm = UnityUtil.MirrorX(mesh); _mirrored[mesh] = mm; Resources.Owned.Add(mm); }
                    mesh = mm;
                }
                lods.Add(new LibraryLod { Mesh = mesh, Materials = r.mats, MaxDistance = ObjectBuilder.LodDistance(l.RawDistance, opt.LodDistanceScale) });
                radius = Mathf.Max(radius, l.Sphere.w);
            }
            obj = lods.Count == 0 ? null : new LibraryObject { Key = insKey, Name = Bin.KeyToString(insKey), Lods = lods.ToArray(), Radius = radius };
            _objects[insKey] = obj;
            return obj;
        }

        public void Destroy()
        {
            Resources.Destroy();
            _objects.Clear();
            _mirrored.Clear();
        }
    }
}
