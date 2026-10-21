using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TDU2.Map.World
{
    /// <summary>
    /// Findet alle Map-Dateien einer Insel:
    ///   Euro/Bnk/islands/&lt;insel&gt;/level/&lt;insel&gt;.bnk           Insel-LOD (Terrain + Objekte)
    ///   .../level/commonworld.bnk                              Objekt-Library (Bäume, Büsche, Props)
    ///   .../level/area-A/area-A-B/area-A-B.bnk                 Area-LOD
    ///   .../level/area-A/area-A-B/sector-A-B-C-D.bnk           Sektor (Full Mesh + Objekte + Vegetation)
    ///   .../level/spots.bin                                    POIs
    ///   .../fx/grasstypes.xml, .../fx/tallgrass.bnk            Gras
    /// </summary>
    public sealed class IslandIndex
    {
        public string IslandName;
        public string IslandDir, LevelDir, FxDir;
        /// <summary>Euro/Bnk (enthält database, vehicules, sound ...).</summary>
        public string BnkDir;
        public string IslandBank, CommonWorldBank, SpotsFile, GrassTypesXml, TallGrassBank, WorldActorsBank, FxIni, ExteriorDir;
        public readonly Dictionary<AreaKey, string> AreaBanks = new Dictionary<AreaKey, string>();
        public readonly Dictionary<SectorKey, string> SectorBanks = new Dictionary<SectorKey, string>();
        public int AreasX, AreasZ;

        static readonly Regex AreaRx = new Regex(@"^area-(\d+)-(\d+)\.bnk$", RegexOptions.IgnoreCase);
        static readonly Regex SectorRx = new Regex(@"^sector-(\d+)-(\d+)-(\d+)-(\d+)\.bnk$", RegexOptions.IgnoreCase);

        /// <summary>Sucht den islands-Ordner ausgehend vom angegebenen Pfad (Spielordner, Euro, Bnk oder islands).</summary>
        public static string FindIslandsDir(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string[] candidates =
            {
                root,
                Path.Combine(root, "islands"),
                Path.Combine(root, "Bnk", "islands"),
                Path.Combine(root, "Euro", "Bnk", "islands"),
                Path.Combine(root, "Euro~", "Bnk", "islands"),   // in Assets: "~" = von Unity nicht importiert
                Path.Combine(root, "euro", "bnk", "islands"),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c) && (Directory.Exists(Path.Combine(c, "ibiza")) || Directory.Exists(Path.Combine(c, "hawai"))))
                    return c;
            return null;
        }

        public static IEnumerable<string> ListIslands(string root)
        {
            var dir = FindIslandsDir(root);
            if (dir == null) yield break;
            foreach (var d in Directory.GetDirectories(dir))
                if (Directory.Exists(Path.Combine(d, "level"))) yield return Path.GetFileName(d);
        }

        public static IslandIndex Scan(string root, string island)
        {
            var islands = FindIslandsDir(root) ?? throw new DirectoryNotFoundException("islands-Ordner nicht gefunden unter: " + root);
            var idx = new IslandIndex { IslandName = island, IslandDir = Path.Combine(islands, island) };
            idx.BnkDir = Directory.GetParent(islands)?.FullName;
            idx.LevelDir = Path.Combine(idx.IslandDir, "level");
            idx.FxDir = Path.Combine(idx.IslandDir, "fx");
            if (!Directory.Exists(idx.LevelDir))
                throw new DirectoryNotFoundException(idx.LevelDir);

            idx.IslandBank = FileOrNull(Path.Combine(idx.LevelDir, island + ".bnk"));
            idx.CommonWorldBank = FileOrNull(Path.Combine(idx.LevelDir, "commonworld.bnk"));
            idx.SpotsFile = FileOrNull(Path.Combine(idx.LevelDir, "spots.bin"));
            idx.GrassTypesXml = FileOrNull(Path.Combine(idx.FxDir, "grasstypes.xml"));
            idx.TallGrassBank = FileOrNull(Path.Combine(idx.FxDir, "tallgrass.bnk"));
            idx.FxIni = FileOrNull(Path.Combine(idx.FxDir, "fx.ini"));
            var ext = Path.Combine(idx.IslandDir, "exterior");
            idx.ExteriorDir = Directory.Exists(ext) ? ext : null;
            idx.WorldActorsBank = FileOrNull(Path.Combine(idx.IslandDir, "scenarics", "packs", "worldactors.bnk"));

            int maxA = -1, maxB = -1;
            foreach (var f in Directory.GetFiles(idx.LevelDir, "*.bnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                var m = SectorRx.Match(name);
                if (m.Success)
                {
                    var k = new SectorKey(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
                    idx.SectorBanks[k] = f;
                    continue;
                }
                m = AreaRx.Match(name);
                if (m.Success)
                {
                    var a = new AreaKey(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                    idx.AreaBanks[a] = f;
                    maxA = Math.Max(maxA, a.X);
                    maxB = Math.Max(maxB, a.Z);
                }
            }
            idx.AreasX = maxA + 1;
            idx.AreasZ = maxB + 1;
            return idx;
        }

        static string FileOrNull(string p) => File.Exists(p) ? p : null;

        public override string ToString() => $"{IslandName}: {AreaBanks.Count} Areas ({AreasX}x{AreasZ}), {SectorBanks.Count} Sektoren";
    }
}
