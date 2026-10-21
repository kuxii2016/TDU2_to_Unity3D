using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using TDU2.Map.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map
{
    /// <summary>
    /// Lädt und streamt eine TDU2-Insel (Ibiza / Hawaii) direkt aus den entpackten Spieldateien.
    ///
    /// LOD-Kette wie im Spiel:
    ///   LOD2  Insel-Bank   (&lt;insel&gt;.bnk)         – ganze Insel, 384 m Raster, immer sichtbar wo nichts Feineres geladen ist
    ///   LOD1  Area-Bank    (area-A-B.bnk)          – 6144 m Kachel, 24 m Raster + LOD-Gebäude (area-A-B-o)
    ///   LOD0  Sektor-Bank  (sector-A-B-C-D.bnk)    – 768 m Kachel, 3 m Raster (Full Mesh) + Objekte + Bäume + Gras
    ///
    /// Parsing läuft in Hintergrund-Tasks, Unity-Objekte werden im Main-Thread verteilt über Frames erzeugt.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TDU2MapStreamer : MonoBehaviour
    {
        #region Settings
        [Header("Spieldaten")]
        [Tooltip("Leer = automatisch: <Projektordner>/Euro oder Assets/StreamingAssets/Euro. " + "Relativer Pfad = relativ zum Projektordner. Es wird nichts konvertiert, die .bnk werden direkt gelesen.")]
        public string GameRoot = "";
        [Tooltip("Insel-Ordnername: 'ibiza' oder 'hawai'")]
        public string Island = "ibiza";

        [Header("Betrachter")]
        public Transform Viewer;
        [Tooltip("Verschiebt die Welt so, dass die Inselmitte bei (0,0,0) liegt (bessere Float-Präzision).")]
        public bool CenterWorld = true;
        [Tooltip("TDU2 ist rechtshändig (+X = links), Unity linkshändig. An = Welt wie im Spiel (nicht seitenverkehrt).")]
        public bool MirrorX = true;
        public bool PlaceViewerOnStart = true;
        public Vector3 StartPositionGame = new Vector3(97, 8, 363);
        [Tooltip("Startpunkt wie im Spiel aus GamePC.cpr (DEFAULTSTARTPOSITION bzw. Standard-Haus DefaultHouse) statt StartPositionGame")]
        public bool StartFromGamePc = true;

        [Header("Streaming-Distanzen (m, XZ-Abstand zur Kachel)")]
        public float AreaLoadDistance = 2500f;
        public float AreaUnloadMargin = 1000f;
        public float SectorLoadDistance = 700f;
        public float SectorUnloadMargin = 300f;
        [Range(1, 8)] public int MaxParallelLoads = 3;

        [Header("Inhalte")]
        public bool ShowIslandLod = true;
        public bool LoadAreaObjects = true;
        public bool LoadSectorObjects = true;
        public bool LoadVegetation = true;
        public bool LoadOtherPlacements = true;
        public bool LoadActors = false;
        [Tooltip("World Actors der Sektoren (-A.pmi): Ampeln, Schilder, Bänke, Mülleimer aus scenarics/packs/worldactors.bnk")]
        public bool LoadWorldActors = true;
        public bool LoadGrass = true;
        public bool LoadPois = true;
        [Tooltip("Gebäude der Spots (Händler, Häuser ...) aus exterior/*.bnk an der Spot-Position platzieren")]
        public bool LoadSpotExteriors = true;
        public bool ShowWater = true;
        [Tooltip("MeshCollider für Terrain/Straßen/Gebäude der geladenen Sektoren (zum Fahren).")]
        public bool GenerateColliders = true;
        [Tooltip("Floating Origin: Welt mitverschieben, damit Kamera/Auto nahe (0,0,0) bleiben (gegen Zittern bei großen Koordinaten)")]
        public bool UseFloatingOrigin = true;
        [Tooltip("Fügt beim Start den Fahrzeug-Loader hinzu (F2 = Auto spawnen, F5/F6 = Auto wechseln).")]
        public bool AutoAddVehicleLoader = true;
        public float SeaLevel = 0f;

        [Header("Qualität")]
        [Tooltip("LOD-Distanz = sqrt(Dateiwert) * Faktor")]
        public float LodDistanceScale = 3f;
        public bool CullLastLod = false;
        public float VegetationDistance = 900f;
        public float GrassRadius = 120f;
        [Range(0, 1)] public float GrassDensity = 0.7f;
        public float GrassSpacing = 1.2f;
        public float DetailTiling = 0.098f; //0.25
        [Range(0, 1)] public float DetailStrength = 1f;//0.5f;
        [Range(0, 1)] public float ShadowMapStrength = 0.6f;
        [Tooltip("Helligkeit von Gebäuden/Objekten")]
        [Range(0.5f, 2f)] public float ObjectBrightness = 1.15f;
        [Tooltip("Interieurs der Spots (Autohändler, Häuser) laden")]
        public bool LoadInteriors = true;
        [Tooltip("Terrain an Tunnelportalen öffnen (Zellen knapp über Tunnel-Fahrbahnen ausblenden)")]
        public bool CutTunnelPortals = true;
        [Tooltip("Wrap-Lighting: hellt sonnenabgewandte Seiten auf (0 = reines Lambert)")]
        [Range(0, 1)] public float WrapLighting = 0.35f;
        [Tooltip("Beleuchtung wie im Original (flaches Umgebungslicht + Sonne aus den Wetterwerten, kein Wrap). Siehe TDU2WorldLighting.")]
        public bool OriginalLighting = true;
        [Tooltip("Additive Leuchteffekte (Neon, Halos, Lampenschein) – im Spiel nur nachts sichtbar.")]
        public bool ShowGlowMaterials = true;
        [Tooltip("Tag/Nacht und Wetter wie im Original (fx/game_weathers.xmb, für alle Spieler synchron). Siehe TDU2Weather.")]
        public bool Weather = true;
        [Range(0, 3)] public float WindStrength = 1f;
        [Range(0, 1)] public float VertexColorStrength = 0.5f;
        public bool CastShadows = true;
        public float SectorSkirt = 12f, AreaSkirt = 40f, IslandSkirt = 150f;

        [Header("Shader (Built-in RP). Bei URP wird automatisch 'Universal Render Pipeline/Lit' verwendet.")]
        public TDU2ShaderSet Shaders = new TDU2ShaderSet();

        [Header("Debug")]
        public bool ShowDebugOverlay = true;
        #endregion

        // --- Laufzeit ---
        sealed class AreaPayload { public BankModel Model; public List<TerrainPiece> Pieces; public List<(Spot spot, BankModel model)> Exteriors = new List<(Spot, BankModel)>(); }
        sealed class SectorPayload { public BankModel Model; public MeshData Terrain; public List<(Spot spot, BankModel model)> Exteriors = new List<(Spot, BankModel)>(); }

        sealed class AreaState
        {
            public AreaKey Key;
            public Task<AreaPayload> Task;
            public GameObject Root;
            public BankModel Model;
            public readonly List<BankModel> ExtraModels = new List<BankModel>();
            public readonly BuildResources Res = new BuildResources();
            public readonly List<(SectorKey key, GameObject go)> Registered = new List<(SectorKey, GameObject)>();
            public bool Built, Failed;
        }

        sealed class SectorState
        {
            public SectorKey Key;
            public Task<SectorPayload> Task;
            public GameObject Root;
            public BankModel Model;
            public readonly List<BankModel> ExtraModels = new List<BankModel>();
            public readonly BuildResources Res = new BuildResources();
            public bool Built, Failed, VegetationDone, ActorsDone;
            public int Instances;
        }

        IslandIndex _index;
        Transform _root;
        readonly GlobalTextureCache _textures = new GlobalTextureCache();
        Task<ObjectLibrary> _libraryTask;
        ObjectLibrary _library;
        Task<WorldActorLib> _actorsTask;
        WorldActorLib _actors;
        GrassTypes _grassTypes;
        Material _grassMat, _bushMat;
        int _grassCells = 16, _bushCells = 4;
        SpotsFile _spots;
        readonly Dictionary<AreaKey, GameObject> _islandPieces = new Dictionary<AreaKey, GameObject>();
        readonly BuildResources _islandRes = new BuildResources();
        readonly Dictionary<AreaKey, AreaState> _areas = new Dictionary<AreaKey, AreaState>();
        readonly Dictionary<SectorKey, SectorState> _sectors = new Dictionary<SectorKey, SectorState>();
        readonly Dictionary<SectorKey, List<GameObject>> _lod1BySector = new Dictionary<SectorKey, List<GameObject>>();
        ObjectBuildOptions _opt;
        float _nextScan;
        string _status = "";
        public Vector3 _viewerGame;


        public IslandIndex Index => _index;
        public Transform MapRoot => _root;
        /// <summary>POIs aus level/spots.bin (null, solange nicht geladen).</summary>
        public SpotsFile Spots => _spots;

        static FloatingOrigin FindFloatingOrigin()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<FloatingOrigin>();
#else
            return FindObjectOfType<FloatingOrigin>();
#endif
        }

        /// <summary>Eine frei platzierte Szene (Interieur, Möbel): Unity-Objekte + Ressourcen + Bank.</summary>
        public sealed class SceneHandle
        {
            public GameObject Root;
            public BankModel Model;
            public BuildResources Res;
            public void Destroy()
            {
                if (Root != null) UnityUtil.SafeDestroy(Root);
                Res?.Destroy();
                Model?.Release();
                Root = null; Res = null; Model = null;
            }
        }

        /// <summary>Lädt eine Szenen-Bank (Interieur/Exterior/Möbel) – thread-sicher, für Task.Run.</summary>
        public BankModel LoadSceneModel(string bankPath)
        {
            var em = BankModel.Load(bankPath, BankKind.Exterior);
            _textures.IndexBank(em.Bank);
            return em;
        }

        /// <summary>Baut eine geladene Szene unter parent (lokale Koordinaten = Spielkoordinaten der Szene).</summary>
        public SceneHandle BuildScene(BankModel em, Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var res = new BuildResources();
            if (em != null) ObjectBuilder.BuildObjects(em, go.transform, _textures, res, _opt);
            return new SceneHandle { Root = go, Model = em, Res = res };
        }

        /// <summary>Insel für den nächsten Start (Flug am Flughafen, siehe TDU2IslandTravel); hat Vorrang vor dem DebugManager.</summary>
        public static string IslandOverride;

        void Start()
        {
            Island = !string.IsNullOrEmpty(IslandOverride) ? IslandOverride : (DebugManager.Instance?.StartMap ?? Island);
            DebugManager.Instance.TDU2MapStreamer = this;
            if (UseFloatingOrigin && FindFloatingOrigin() == null) gameObject.AddComponent<FloatingOrigin>();
            try { Initialize(); }
            catch (Exception ex)
            {
                _status = "Fehler: " + ex.Message;
                Debug.LogError("[TDU2] " + ex);
                enabled = false;
            }
        }

        /// <summary>
        /// Startpunkt laut GamePC.cpr auf dieser Insel: aktive DEFAULTSTARTPOSITION, sonst das Standard-Haus (DefaultHouse "EPH_L2E_0345I"),
        /// Kamera etwas darüber (Streaming beginnt dort, TDU2Login setzt danach Auto/Haus).
        /// </summary>
        bool GamePcStartPoint(out Vector3 start)
        {
            start = default;
            try
            {
                var gp = TDU2GamePC.Load(_index?.BnkDir);
                if (gp == null) return false;
                bool sameIsland = string.IsNullOrEmpty(gp.StartIsland) || (_index.IslandName ?? Island ?? "").StartsWith(gp.StartIsland, StringComparison.OrdinalIgnoreCase);
                if (gp.HasStartPosition && sameIsland) { start = gp.StartPosition + Vector3.up * 30f; return true; }
                if (string.IsNullOrEmpty(_index.SpotsFile) || !File.Exists(_index.SpotsFile)) return false;
                var house = gp.FindHouse(SpotsFile.Parse(File.ReadAllBytes(_index.SpotsFile)).Spots);
                if (house == null) return false;
                start = house.Position + Vector3.up * 30f;
                Debug.Log("[TDU2] Startpunkt (GamePC.cpr): Haus " + string.Join("/", gp.Houses) + " bei " + house.Position);
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] GamePC.cpr Startpunkt: " + ex.Message); return false; }
        }

        void Initialize()
        {
            try
            {
                Debug.Log("Initialization started.");
                Debug.Log("Application.dataPath: " + Application.dataPath);
                Debug.Log("StreamingAssets: " + Application.streamingAssetsPath);
                Debug.Log("Configured Game Root: " + GameRoot);
                if (AutoAddVehicleLoader && GetComponent<Vehicles.TDU2VehicleManager>() == null)
                    gameObject.AddComponent<Vehicles.TDU2VehicleManager>().Streamer = this;
                Shaders.Resolve();
                MaterialFactory.Shaders = Shaders;
                MaterialFactory.ShadowMapStrength = ShadowMapStrength;
                MaterialFactory.ObjectBrightness = ObjectBrightness;
                BankModel.IncludeInteriors = LoadInteriors;
                MaterialFactory.WrapLighting = WrapLighting;
                if (OriginalLighting)
                {
                    // Original-Beleuchtung: Ambient + (Sonne − Ambient) x N.L, ohne Wrap/Aufhellung (die überdeckten nur fehlendes Ambient)
                    MaterialFactory.WrapLighting = 0f;
                    MaterialFactory.ObjectBrightness = 1f;
                    if (GetComponent<TDU2WorldLighting>() == null) gameObject.AddComponent<TDU2WorldLighting>();
                }
                if (GetComponent<TDU2TrafficLightDriver>() == null) gameObject.AddComponent<TDU2TrafficLightDriver>();
                MaterialFactory.VertexColorStrength = VertexColorStrength;
                MaterialFactory.ShowGlowMaterials = ShowGlowMaterials;
                MaterialFactory.WindStrength = WindStrength;

                _opt = new ObjectBuildOptions
                {
                    LodDistanceScale = LodDistanceScale,
                    CullLastLod = CullLastLod,
                    CastShadows = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                };
                string root = ResolveGameRoot(GameRoot);

                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    throw new DirectoryNotFoundException("Euro folder not found. GameRoot: " + GameRoot + ", Application.dataPath: " + Application.dataPath);

                Debug.Log("Game Root: " + root);
                Debug.Log("Scan Island: " + Island);
                _index = IslandIndex.Scan(root, Island);

                if (_index == null)
                    throw new InvalidOperationException("IslandIndex.Scan did not return an index. Root: " + root + ", Island: " + Island);

                Debug.Log("Island-Index: " + _index);
                _root = new GameObject("TDU2_" + _index.IslandName).transform;
                if (Weather && OriginalLighting)
                {
                    // Tageszeit + Wetter wie im Original (fx/game_weathers.xmb), aus der Serverzeit -> im Mehrspieler synchron
                    var w = GetComponent<TDU2Weather>();
                    if (w == null) w = gameObject.AddComponent<TDU2Weather>();
                    w.Init(this, Path.Combine(_index.FxDir ?? "", "game_weathers.xmb"), Path.Combine(_index.FxDir ?? "", "streetlamps.xml"));
                }
                _root.SetParent(transform, false);
                var center = new Vector3(_index.AreasX * WorldLayout.AreaSize * 0.5f, 0, _index.AreasZ * WorldLayout.AreaSize * 0.5f);
                if (MirrorX)
                    _root.localScale = new Vector3(-1f, 1f, 1f);
                if (CenterWorld)
                    _root.localPosition = new Vector3(MirrorX ? center.x : -center.x, 0f, -center.z);

                if (Viewer == null && Camera.main != null)
                    Viewer = Camera.main.transform;

                if (Viewer != null)
                {
                    var cam = Viewer.GetComponent<Camera>();
                    if (cam != null && cam.farClipPlane < 20000f)
                        cam.farClipPlane = 60000f;
                    if (cam != null && cam.nearClipPlane < 1f)
                        cam.nearClipPlane = 1f;
                    if (PlaceViewerOnStart)
                    {
                        var start = StartPositionGame;
                        if (StartFromGamePc && GamePcStartPoint(out var gpStart)) start = gpStart;
                        else if (start.x < 0 || start.z < 0)
                            start = new Vector3(center.x, StartPositionGame.y, center.z);
                        Viewer.position = _root.TransformPoint(start);
                        Viewer.rotation = Quaternion.Euler(35f, 0f, 0f);
                    }
                }

                // World Actors (Ampeln, Schilder, Bänke ...) im Hintergrund
                if (LoadWorldActors && _index.WorldActorsBank != null)
                {
                    string wp = _index.WorldActorsBank;
                    var wtex = _textures;
                    _actorsTask = Task.Run(() => WorldActorLib.Load(wp, wtex));
                }

                // Load the “Library” (Trees/Shrubs) in the background
                if (_index.CommonWorldBank != null && (LoadVegetation || LoadOtherPlacements))
                {
                    string p = _index.CommonWorldBank;
                    var tex = _textures;
                    Debug.Log("Load CommonWorldBank: " + p);
                    if (!File.Exists(p))
                        Debug.LogError("“CommonWorldBank” does not exist: " + p);
                    else
                    {
                        _libraryTask = Task.Run(() =>
                        {
                            try
                            {
                                return ObjectLibrary.Load(p, tex);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError("Error loading the ObjectLibrary: " + ex);
                                throw;
                            }
                        });
                    }
                }

                if (!string.IsNullOrEmpty(_index.SpotsFile))
                {
                    try
                    {
                        Debug.Log("Loading Spots: " + _index.SpotsFile);
                        if (!File.Exists(_index.SpotsFile))
                        {
                            Debug.LogWarning("Spot file not found: " + _index.SpotsFile);
                        }
                        else
                        {
                            _spots = SpotsFile.Parse(File.ReadAllBytes(_index.SpotsFile));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("spots.bin error: " + ex);
                    }
                }
                //World Building
                Debug.Log("Build Island LOD...");
                BuildIslandLod();

                if (ShowWater)
                {
                    Debug.Log("Build Water...");
                    BuildWater(center);
                }

                if (LoadGrass)
                {
                    Debug.Log("Initialize Grass...");
                    SetupGrass();
                }

                if (LoadPois && !string.IsNullOrEmpty(_index.SpotsFile))
                {
                    Debug.Log("Buidl POIs...");
                    BuildPois();
                }

                _status = "ready";

                Debug.Log("Initialization completed successfully.");
            }
            catch (Exception ex)
            {
                _status = "error: " + ex.Message;
                Debug.LogError("Initialization failed:\n" + ex);
            }
        }


        /// <summary>        
        /// Searches for the game folder. Order: explicit path (absolute or relative to the project),
        /// /Euro, Assets/StreamingAssets/Euro, Assets/Euro~, folder above the project.
        /// </summary>
        public static string ResolveGameRoot(string configured)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string normalized = configured.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                string configuredPath = Path.IsPathRooted(normalized) ? normalized : Path.Combine(project, normalized);
                candidates.Add(Path.GetFullPath(configuredPath));
            }

            candidates.Add(project);
            candidates.Add(Application.streamingAssetsPath);
            candidates.Add(Application.dataPath);

            var parent = Directory.GetParent(project);

            if (parent != null)
                candidates.Add(parent.FullName);

            foreach (string candidate in candidates.Distinct())
            {
                try
                {
                    Debug.Log("Check Game-Root: " + candidate);
                    if (!Directory.Exists(candidate))
                    {
                        Debug.LogWarning("The directory does not exist: " + candidate);
                        continue;
                    }
                    string islandsDir = IslandIndex.FindIslandsDir(candidate);
                    if (!string.IsNullOrEmpty(islandsDir) && Directory.Exists(islandsDir))
                    {
                        string result = Path.GetFullPath(candidate);
                        Debug.Log("Game-Root found: " + result);
                        Debug.Log("Island directory: " + islandsDir);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("Error while checking  " + candidate + ": " + ex.Message);
                }
            }
            Debug.LogError("Game-Root could not be found. Check the configured path and case sensitivity.");
            return null;
        }

        /// <summary>
        /// Build Island LOD
        /// </summary>
        void BuildIslandLod()
        {
            if (_index.IslandBank == null) { Debug.LogWarning("Island Bank is missing"); return; }
            var model = BankModel.Load(_index.IslandBank, BankKind.Island, loadObjects: false, loadPlacements: false, loadGrass: false);
            if (model.Heights == null) { Debug.LogWarning("Island heightmap not found"); return; }
            int ax = Math.Max(1, _index.AreasX), az = Math.Max(1, _index.AreasZ);
            var pieces = TerrainMeshBuilder.BuildIslandPieces(model.Heights, ax, az, IslandSkirt);
            var color = model.GetTexture(model.ColorMapKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp, null);
            var mat = MaterialFactory.CreateTerrainMaterial(color, null, null, null, DetailTiling, 0f);
            _islandRes.Owned.Add(mat);
            var parent = new GameObject("LOD2_Island").transform;
            parent.SetParent(_root, false);
            foreach (var p in pieces)
            {
                var go = CreateMeshObject(p.Area + "_LOD2", parent, p.WorldOrigin, p.Mesh.ToMesh(), mat, _islandRes, false);
                go.SetActive(ShowIslandLod);
                _islandPieces[p.Area] = go;
            }
            _islandModel = model;
        }
        BankModel _islandModel;

        /// <summary>
        /// Build Island Water
        /// </summary>
        /// <param name="center"></param>
        //void BuildWater(Vector3 center)
        //{
        //    const float half = 150000f;
        //    var m = new Mesh { name = "TDU2_Water" };
        //    m.SetVertices(new List<Vector3> { new Vector3(-half, 0, -half), new Vector3(-half, 0, half), new Vector3(half, 0, -half), new Vector3(half, 0, half) });
        //    m.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
        //    m.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0), new Vector2(1, 1) });
        //    m.SetTriangles(new[] { 0, 1, 2, 2, 1, 3 }, 0);
        //    m.bounds = new Bounds(Vector3.zero, new Vector3(2 * half, 1, 2 * half));
        //    _islandRes.Owned.Add(m);
        //    var go = CreateMeshObject("Water", _root, new Vector3(center.x, SeaLevel, center.z), m, MaterialFactory.Water, null, false);
        //    go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        //}
        void BuildWater(Vector3 center)
        {
            const float half = 150000f;
            const int segments = 128; // 129x129 Vertices, 128x128 Quads
            float step = (half * 2f) / segments;

            int vCount = (segments + 1) * (segments + 1);
            int tCount = segments * segments * 6;

            Vector3[] verts = new Vector3[vCount];
            Vector2[] uvs = new Vector2[vCount];
            Vector3[] norms = new Vector3[vCount];
            int[] tris = new int[tCount];

            int idx = 0;
            for (int z = 0; z <= segments; z++)
            {
                for (int x = 0; x <= segments; x++)
                {
                    float wx = -half + x * step;
                    float wz = -half + z * step;
                    verts[idx] = new Vector3(wx, 0f, wz);
                    // Welt-Space UVs - verhindert sichtbares Tiling bei sehr großer Fläche
                    uvs[idx] = new Vector2(wx * 0.00005f, wz * 0.00005f);
                    norms[idx] = Vector3.up;
                    idx++;
                }
            }

            int t = 0;
            for (int z = 0; z < segments; z++)
            {
                for (int x = 0; x < segments; x++)
                {
                    int i0 = (z * (segments + 1)) + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + (segments + 1);
                    int i3 = i2 + 1;

                    // Unity Triangle Order (Clockwise)
                    tris[t++] = i0;
                    tris[t++] = i2;
                    tris[t++] = i1;
                    tris[t++] = i1;
                    tris[t++] = i2;
                    tris[t++] = i3;
                }
            }

            var m = new Mesh { name = "TDU2_Water" };
            m.vertices = verts;
            m.uv = uvs;
            m.normals = norms;
            m.triangles = tris;
            m.RecalculateBounds();

            _islandRes.Owned.Add(m);
            var go = CreateMeshObject("Water", _root, new Vector3(center.x, SeaLevel - 1.5f, center.z), m, MaterialFactory.Water, null, false);
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            _water = go;
        }
        /// <summary>
        /// Create Grass
        /// </summary>
        void SetupGrass()
        {
            try
            {
                if (_index.GrassTypesXml != null) _grassTypes = GrassTypes.Parse(File.ReadAllText(_index.GrassTypesXml));
                if (_index.TallGrassBank == null || _grassTypes == null) return;
                var bank = BnkArchive.Open(_index.TallGrassBank);
                _textures.IndexBank(bank);
                var g = _textures.Get(_textures.KeyByName("setgrass.2db"), TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                var b = _textures.Get(_textures.KeyByName("setbush.2db"), TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                int iniGrass = 0, iniBush = 0;
                if (_index.FxIni != null)
                {
                    // fx.ini [GRASS] NBGRASSBITMAPS / NBBUSHBITMAPS = Anzahl Sprites im Atlas (Ibiza 9/4, Hawaii 5/1)
                    foreach (var line in File.ReadAllLines(_index.FxIni))
                    {
                        var parts = line.Split('=');
                        if (parts.Length != 2) continue;
                        var key = parts[0].Trim().ToUpperInvariant();
                        int.TryParse(parts[1].Split('/')[0].Trim(), out int val);
                        if (key == "NBGRASSBITMAPS") iniGrass = val;
                        else if (key == "NBBUSHBITMAPS") iniBush = val;
                    }
                }
                if (g != null) { _grassMat = MaterialFactory.CreateGrassMaterial(g, 0.4f); _grassCells = iniGrass > 0 ? iniGrass : Mathf.Max(1, g.width / Mathf.Max(1, g.height)); }
                if (b != null) { _bushMat = MaterialFactory.CreateGrassMaterial(b, 0.4f); _bushCells = iniBush > 0 ? iniBush : Mathf.Max(1, b.width / Mathf.Max(1, b.height)); }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Gras konnte nicht geladen werden: " + ex.Message); }
        }

        /// <summary>
        /// Build World POis
        /// </summary>
        void BuildPois()
        {
            try
            {
                var spots = _spots ?? SpotsFile.Parse(File.ReadAllBytes(_index.SpotsFile));
                var go = new GameObject($"POIs ({spots.Spots.Count})");
                go.transform.SetParent(_root, false);
                go.AddComponent<PoiMarkers>().Build(spots);
            }
            catch (Exception ex) { Debug.LogWarning("spots.bin: " + ex.Message); }
        }

        void Update()
        {
            if (_index == null) return;
            if (Viewer == null && Camera.main != null) Viewer = Camera.main.transform;
            if (Viewer == null) return;
            _viewerGame = _root.InverseTransformPoint(Viewer.position);
            Shader.SetGlobalVector(IdOrigin, _root.position);   // Meer: Wellen in Inselkoordinaten (Floating Origin)

            PollLibrary();
            PollActors();
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                ScanAreas();
                ScanSectors();
            }
            if (Time.unscaledTime >= _nextLodCheck) { _nextLodCheck = Time.unscaledTime + 1f; EnforceLod1(); }
            BuildCompleted();
            // Kartengelände im Hintergrund vorbereiten, sobald die Welt um den Spieler steht (dann ist die Karte beim ersten Öffnen scharf)
            if (PreloadMapTerrain && _mapTask == null && Time.unscaledTime > 15f) RequestMapTerrain();   // ein Hintergrund-Thread
            PollMapTerrain();
        }

        // ------------------------------------------------------------------ Kartengelände (große Karte)
        /// <summary>
        /// Gelände-Kachel der großen Karte: eine ganze Area aus ihrer eigenen Bank (Höhen 24 m, Colormap 1024² = 6 m je Pixel)
        /// statt der groben Insel-LOD (384 m Raster, eine einzige Colormap für die ganze Insel). Kein GameObject – die Karte
        /// zeichnet die Kacheln nur in ihre eigene Kamera (Graphics.DrawMesh), im Spiel sind sie unsichtbar.
        /// </summary>
        public sealed class MapTile { public AreaKey Key; public Mesh Mesh; public Material Material; public Texture2D Color; public Vector3 Origin; }
        [Header("Große Karte")]
        [Tooltip("Kartengelände (alle Areas mit eigener Colormap) nach dem Start im Hintergrund laden")]
        public bool PreloadMapTerrain = true;
        [Tooltip("Raster der Kartenkacheln in Area-Zellen (1 = 24 m, 2 = 48 m, 4 = 96 m)")]
        [Range(1, 8)] public int MapTerrainStep = 2;
        readonly List<MapTile> _mapTiles = new List<MapTile>();
        readonly System.Collections.Concurrent.ConcurrentQueue<(AreaKey key, MeshData mesh, BankModel model)> _mapQueue =
            new System.Collections.Concurrent.ConcurrentQueue<(AreaKey, MeshData, BankModel)>();
        Task _mapTask; volatile bool _mapCancel;
        GameObject _water;
        Shader _mapShader;
        static readonly int IdOrigin = Shader.PropertyToID("_TDU2Origin");

        /// <summary>Fertige Kartenkacheln (Spielkoordinaten: Origin = Area-Ecke, unter <see cref="MapRoot"/>).</summary>
        public IReadOnlyList<MapTile> MapTiles => _mapTiles;
        /// <summary>Alle Kartenkacheln gebaut.</summary>
        public bool MapTerrainReady => _mapTask != null && _mapTask.IsCompleted && _mapQueue.IsEmpty && _mapTiles.Count > 0;
        /// <summary>Meeresfläche (für die Kartenkamera).</summary>
        public GameObject WaterObject => _water;

        /// <summary>Kartengelände laden (einmal je Insel; ein Hintergrund-Thread, höchstens zwei Bänke gleichzeitig im Speicher).</summary>
        public void RequestMapTerrain()
        {
            if (_mapTask != null || _index == null || _index.AreaBanks == null || _index.AreaBanks.Count == 0) return;
            var banks = new List<KeyValuePair<AreaKey, string>>(_index.AreaBanks);
            int step = Mathf.Clamp(MapTerrainStep, 1, 8);
            var q = _mapQueue;
            _mapTask = Task.Run(() =>
            {
                foreach (var kv in banks)
                {
                    if (_mapCancel) return;
                    while (q.Count >= 2 && !_mapCancel) System.Threading.Thread.Sleep(20);
                    try
                    {
                        var model = BankModel.Load(kv.Value, BankKind.Area, false, false, false);
                        if (model.Heights == null || model.ColorMapKey == 0) continue;
                        q.Enqueue((kv.Key, BuildMapGrid(model.Heights, step), model));
                    }
                    catch (Exception) { }
                }
            });
        }

        /// <summary>Höchstens eine Kachel je Bild in Unity-Objekte umwandeln (Textur, Material, Mesh).</summary>
        void PollMapTerrain()
        {
            if (!_mapQueue.TryDequeue(out var it)) return;
            try
            {
                var color = it.model.GetTexture(it.model.ColorMapKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp, null);
                if (color == null) return;
                // unbeleuchteter Karten-Shader (Tageszeit/Licht/Spiegelung der Welt spielen auf der Karte keine Rolle)
                if (_mapShader == null) _mapShader = Shader.Find("TDU2/MapTerrain");
                var mat = _mapShader != null ? new Material(_mapShader) { name = "TDU2 Karte " + it.key, mainTexture = color }
                                             : MaterialFactory.CreateTerrainMaterial(color, null, null, null, DetailTiling, 0f);
                color.anisoLevel = 4;
                _mapTiles.Add(new MapTile { Key = it.key, Mesh = it.mesh.ToMesh(), Material = mat, Color = color, Origin = WorldLayout.AreaOrigin(it.key) });
                if (_mapTask != null && _mapTask.IsCompleted && _mapQueue.IsEmpty) Debug.Log("[TDU2] Kartengelände: " + _mapTiles.Count + " Areas");
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Kartengelände " + it.key + ": " + ex.Message); }
        }

        /// <summary>Ganzes Area-Raster als ein Gitter, jede step-te Zelle (UV wie die LOD1-Stücke: 0..1 über die Area-Colormap).</summary>
        static MeshData BuildMapGrid(HeightGrid g, int step)
        {
            var md = new MeshData { Name = "MapTerrain" };
            int n = Mathf.Max(1, (g.Size - 1) / step), v = n + 1;
            float ext = Mathf.Max(1f, g.Extent);
            for (int z = 0; z < v; z++)
                for (int x = 0; x < v; x++)
                {
                    int gx = Mathf.Min(x * step, g.Size - 1), gz = Mathf.Min(z * step, g.Size - 1);
                    md.Positions.Add(new Vector3(gx * g.Spacing, g.Get(gx, gz), gz * g.Spacing));
                    md.Normals.Add(g.NormalAt(gx, gz));
                    md.UV0.Add(new Vector2(gx * g.Spacing / ext, gz * g.Spacing / ext));
                }
            var tris = new List<int>(n * n * 6);
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int v00 = z * v + x, v10 = v00 + 1, v01 = v00 + v, v11 = v01 + 1;
                    tris.Add(v00); tris.Add(v01); tris.Add(v10);
                    tris.Add(v10); tris.Add(v01); tris.Add(v11);
                }
            md.SubMeshes.Add(tris);
            md.SubMeshMaterialIndex.Add(0);
            return md;
        }

        void DestroyMapTerrain()
        {
            _mapCancel = true;
            foreach (var t in _mapTiles)
            {
                if (t.Mesh != null) UnityUtil.SafeDestroy(t.Mesh);
                if (t.Material != null) UnityUtil.SafeDestroy(t.Material);
                if (t.Color != null) UnityUtil.SafeDestroy(t.Color);
            }
            _mapTiles.Clear();
            while (_mapQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Ladebildschirm: true, sobald alle Areas und Sektoren im Umkreis (Welt-Meter, höchstens die Ladeentfernungen) um
        /// <paramref name="worldPos"/> gebaut (oder fehlgeschlagen) sind und die Objektbibliothek steht.
        /// </summary>
        public bool ReadyAround(Vector3 worldPos, float radius)
        {
            if (_index == null || _root == null) return false;
            if (_libraryTask != null && _library == null) return false;
            var g = _root.InverseTransformPoint(worldPos);
            var p = new Vector2(g.x, g.z);
            float ra = Mathf.Min(radius, AreaLoadDistance), rs = Mathf.Min(radius, SectorLoadDistance);
            foreach (var k in _index.AreaBanks.Keys)
                if (WorldLayout.DistanceToRect(p, WorldLayout.AreaRect(k)) < ra && !(_areas.TryGetValue(k, out var a) && (a.Built || a.Failed))) return false;
            foreach (var k in _index.SectorBanks.Keys)
                if (WorldLayout.DistanceToRect(p, WorldLayout.SectorRect(k)) < rs && !(_sectors.TryGetValue(k, out var st) && (st.Built || st.Failed))) return false;
            return true;
        }

        int RunningJobs()
        {
            int n = 0;
            foreach (var a in _areas.Values) if (a.Task != null && !a.Built && !a.Failed) n++;
            foreach (var s in _sectors.Values) if (s.Task != null && !s.Built && !s.Failed) n++;
            return n;
        }

        void ScanAreas()
        {
            var p = new Vector2(_viewerGame.x, _viewerGame.z);
            var wanted = new List<(float d, AreaKey k)>();
            var drop = new List<AreaKey>();
            foreach (var k in _index.AreaBanks.Keys)
            {
                float d = WorldLayout.DistanceToRect(p, WorldLayout.AreaRect(k));
                if (d < AreaLoadDistance && !_areas.ContainsKey(k)) wanted.Add((d, k));
                else if (d > AreaLoadDistance + AreaUnloadMargin && _areas.ContainsKey(k)) drop.Add(k);
            }
            foreach (var k in drop) UnloadArea(k);
            wanted.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (d, k) in wanted)
            {
                if (RunningJobs() >= MaxParallelLoads) break;
                StartArea(k);
            }
        }

        void ScanSectors()
        {
            var p = new Vector2(_viewerGame.x, _viewerGame.z);
            var wanted = new List<(float d, SectorKey k)>();
            var drop = new List<SectorKey>();
            foreach (var k in _index.SectorBanks.Keys)
            {
                float d = WorldLayout.DistanceToRect(p, WorldLayout.SectorRect(k));
                bool loaded = _sectors.ContainsKey(k);
                if (d < SectorLoadDistance && !loaded && AreasReadyFor(k)) wanted.Add((d, k));
                else if (loaded && d > SectorLoadDistance + SectorUnloadMargin) drop.Add(k);
            }
            foreach (var k in drop) UnloadSector(k);
            wanted.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (d, k) in wanted)
            {
                if (RunningJobs() >= MaxParallelLoads) break;
                StartSector(k);
            }
        }

        /// <summary>Ein Sektor darf erst laden, wenn alle Areas, die er überlappt, gebaut sind (sonst Überlappung mit LOD2).</summary>
        bool AreasReadyFor(SectorKey k)
        {
            var r = WorldLayout.SectorRect(k);
            var a0 = WorldLayout.AreaAt(r.xMin + 1, r.yMin + 1);
            var a1 = WorldLayout.AreaAt(r.xMax - 1, r.yMax - 1);
            for (int ax = a0.X; ax <= a1.X; ax++)
                for (int az = a0.Z; az <= a1.Z; az++)
                {
                    var ak = new AreaKey(ax, az);
                    if (!_index.AreaBanks.ContainsKey(ak)) continue;
                    if (!_areas.TryGetValue(ak, out var st) || !st.Built) return false;
                }
            return true;
        }

        void StartArea(AreaKey k)
        {
            string path = _index.AreaBanks[k];
            bool objs = LoadAreaObjects;
            float skirt = AreaSkirt;
            var st = new AreaState { Key = k };
            var exteriors = LoadSpotExteriors ? SpotsInArea(k) : new List<(Spot, string)>();
            var tex = _textures;
            st.Task = Task.Run(() =>
            {
                var model = BankModel.Load(path, BankKind.Area, objs, false, false);
                tex.IndexBank(model.Bank);
                var pieces = model.Heights != null ? TerrainMeshBuilder.BuildAreaPieces(model.Heights, k, skirt) : new List<TerrainPiece>();
                var payload = new AreaPayload { Model = model, Pieces = pieces };
                foreach (var (spot, file) in exteriors)
                {
                    try { var em = BankModel.Load(file, BankKind.Exterior); tex.IndexBank(em.Bank); TDU2.Map.Vehicles.InteriorFx.PrepareUva(em.Bank); payload.Exteriors.Add((spot, em)); }
                    catch (Exception) { }
                }
                return payload;
            });
            _areas[k] = st;
        }

        void StartSector(SectorKey k)
        {
            string path = _index.SectorBanks[k];
            bool objs = LoadSectorObjects, place = LoadVegetation || LoadOtherPlacements || LoadActors || LoadWorldActors, grass = LoadGrass;
            float skirt = SectorSkirt;
            bool cutTunnels = CutTunnelPortals && objs;
            var st = new SectorState { Key = k };
            var exteriors = LoadSpotExteriors ? SpotsInSector(k) : new List<(Spot, string)>();
            var tex = _textures;
            st.Task = Task.Run(() =>
            {
                var model = BankModel.Load(path, BankKind.Sector, objs, place, grass);
                // Sektortexturen global bekannt machen (Library-Objekte wie Felsen nutzen Texturen aus der Sektorbank)
                tex.IndexBank(model.Bank);
                if (objs) { try { TDU2.Map.Vehicles.InteriorFx.PrepareUva(model.Bank); } catch (Exception) { } }   // Textur-Animationen (.uva)
                bool[] holes = null;
                if (cutTunnels && model.Heights != null)
                {
                    try { holes = TerrainHoles.Compute(model.Heights, model.AllObjectSets()); }
                    catch (Exception ex) { model.Log.Add("TerrainHoles: " + ex.Message); }
                }
                var terrain = model.Heights != null ? TerrainMeshBuilder.BuildSector(model.Heights, skirt, holes) : null;
                var payload = new SectorPayload { Model = model, Terrain = terrain };
                foreach (var (spot, file) in exteriors)
                {
                    try { var em = BankModel.Load(file, BankKind.Exterior); tex.IndexBank(em.Bank); TDU2.Map.Vehicles.InteriorFx.PrepareUva(em.Bank); payload.Exteriors.Add((spot, em)); }
                    catch (Exception) { }
                }
                return payload;
            });
            _sectors[k] = st;
        }

        /// <summary>Pro Frame höchstens eine fertige Kachel in Unity-Objekte umwandeln (verhindert Ruckler).</summary>
        void BuildCompleted()
        {
            foreach (var st in _areas.Values)
            {
                if (st.Built || st.Failed || st.Task == null || !st.Task.IsCompleted) continue;
                if (st.Task.IsFaulted) { st.Failed = true; Debug.LogWarning($"[TDU2] {st.Key}: {st.Task.Exception?.GetBaseException().Message}"); continue; }
                BuildArea(st, st.Task.Result);
                return;
            }
            float best = float.MaxValue; SectorState next = null;
            foreach (var st in _sectors.Values)
            {
                if (st.Built || st.Failed || st.Task == null || !st.Task.IsCompleted) continue;
                if (st.Task.IsFaulted) { st.Failed = true; Debug.LogWarning($"[TDU2] {st.Key}: {st.Task.Exception?.GetBaseException().Message}"); continue; }
                float d = WorldLayout.DistanceToRect(new Vector2(_viewerGame.x, _viewerGame.z), WorldLayout.SectorRect(st.Key));
                if (d < best) { best = d; next = st; }
            }
            if (next != null) BuildSector(next, next.Task.Result);
        }

        void BuildArea(AreaState st, AreaPayload data)
        {
            st.Model = data.Model;
            var origin = WorldLayout.AreaOrigin(st.Key);
            st.Root = new GameObject(st.Key + " (LOD1)");
            st.Root.transform.SetParent(_root, false);
            st.Root.transform.localPosition = origin;

            var color = st.Model.GetTexture(st.Model.ColorMapKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp, _textures);
            var mat = MaterialFactory.CreateTerrainMaterial(color, null, null, null, DetailTiling, 0f);
            st.Res.Owned.Add(mat);
            foreach (var p in data.Pieces)
            {
                var go = CreateMeshObject(p.Sector.ToString(), st.Root.transform, p.WorldOrigin - origin, p.Mesh.ToMesh(), mat, st.Res, true);
                Register(st, p.Sector, go);
            }

            if (LoadAreaObjects && st.Model.Objects != null)
            {
                var objRoot = new GameObject("Objects").transform;
                objRoot.SetParent(st.Root.transform, false);
                ObjectBuilder.BuildObjects(st.Model, objRoot, _textures, st.Res, _opt, (go, centerLocal) =>
                {
                    var w = origin + centerLocal;
                    RegisterSpan(st, WorldLayout.SectorAt(w.x, w.z), go);
                });
            }
            foreach (var (spot, em) in data.Exteriors)
            {
                st.ExtraModels.Add(em);
                var go = BuildExterior(spot, em, st.Root.transform, origin, st.Res, "_low");
                if (go != null) { TDU2.Map.Vehicles.InteriorFx.ApplyUva(em.Bank?.FilePath, go.transform); Register(st, SpotSector(spot), go); }
            }
            st.Built = true;
            if (_islandPieces.TryGetValue(st.Key, out var ip) && ip != null) ip.SetActive(false);
        }

        void Register(AreaState st, SectorKey key, GameObject go)
        {
            st.Registered.Add((key, go));
            if (!_lod1BySector.TryGetValue(key, out var list)) _lod1BySector[key] = list = new List<GameObject>();
            list.Add(go);
            go.SetActive(Lod1Wanted(go, key));
        }

        /// <summary>
        /// LOD-Objekte der Area (area-A-B-o: zusammengefasste Häuserblöcke) reichen oft über mehrere Sektoren. Nur nach dem
        /// Mittelpunkt einsortiert blieb ein Block sichtbar, wenn der Nachbarsektor (mit den echten Gebäuden) schon geladen war,
        /// der eigene aber nicht -> LOD-Gebäude schienen in der Stadt durch die LOD0-Gebäude.
        /// Jetzt bei allen überdeckten Sektoren angemeldet: ausgeblendet, sobald einer davon gebaut ist.
        /// </summary>
        void RegisterSpan(AreaState st, SectorKey center, GameObject go)
        {
            var keys = new List<SectorKey> { center };
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length > 0 && _root != null)
            {
                var wb = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) wb.Encapsulate(rs[i].bounds);
                // Weltbounds -> Spielkoordinaten (Karte ist gespiegelt: min/max neu bilden); 20 % Überstand ignorieren
                var a = _root.InverseTransformPoint(wb.min); var b = _root.InverseTransformPoint(wb.max);
                float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), z0 = Mathf.Min(a.z, b.z), z1 = Mathf.Max(a.z, b.z);
                float mx = (x1 - x0) * 0.2f, mz = (z1 - z0) * 0.2f;
                x0 += mx; x1 -= mx; z0 += mz; z1 -= mz;
                float step = WorldLayout.SectorSize * 0.5f;
                if ((x1 - x0) < WorldLayout.SectorSize * 3f && (z1 - z0) < WorldLayout.SectorSize * 3f)
                    for (float x = x0; x <= x1 + step * 0.5f; x += step)
                        for (float z = z0; z <= z1 + step * 0.5f; z += step)
                        {
                            var k = WorldLayout.SectorAt(Mathf.Min(x, x1), Mathf.Min(z, z1));
                            if (!keys.Contains(k)) keys.Add(k);
                        }
            }
            _lod1Keys[go] = keys;
            foreach (var k in keys) Register(st, k, go);
        }

        readonly Dictionary<GameObject, List<SectorKey>> _lod1Keys = new Dictionary<GameObject, List<SectorKey>>();

        bool SectorBuilt(SectorKey k) => _sectors.TryGetValue(k, out var s) && s.Built;

        /// <summary>LOD1-Objekt sichtbar, solange keiner seiner Sektoren als LOD0 gebaut ist.</summary>
        bool Lod1Wanted(GameObject go, SectorKey key)
        {
            if (go != null && _lod1Keys.TryGetValue(go, out var keys))
            {
                foreach (var k in keys) if (SectorBuilt(k)) return false;
                return true;
            }
            return !SectorBuilt(key);
        }

        float _nextLodCheck;

        /// <summary>
        /// LOD1-Objekte (Area-Gebäude, Spot-Exteriors "_low"), deren Sektor schon als LOD0 gebaut ist, ausgeschaltet halten.
        /// Andere Teile (Interieur: Exterior aus-/einblenden beim Betreten/Verlassen, Teleport in ein Haus) konnten sie wieder
        /// aktivieren -> Low-Poly-Gebäude standen in/über den richtigen. Nur ausschalten, nie einschalten (das macht SetLod1Visible).
        /// </summary>
        public void EnforceLod1()
        {
            foreach (var kv in _lod1BySector)
                foreach (var go in kv.Value)
                    if (go != null && go.activeSelf && !Lod1Wanted(go, kv.Key)) go.SetActive(false);
        }

        void SetLod1Visible(SectorKey key, bool visible)
        {
            if (!_lod1BySector.TryGetValue(key, out var list)) return;
            foreach (var go in list) if (go != null) go.SetActive(Lod1Wanted(go, key));
        }

        void BuildSector(SectorState st, SectorPayload data)
        {
            st.Model = data.Model;
            var origin = WorldLayout.SectorOrigin(st.Key);
            st.Root = new GameObject(st.Key + " (LOD0)");
            st.Root.transform.SetParent(_root, false);
            st.Root.transform.localPosition = origin;

            Texture2D groundColor = null, groundSplat = null;
            if (data.Terrain != null)
            {
                var m = st.Model;
                var color = m.GetTexture(m.ColorMapKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp, _textures);
                var splat = m.GetTexture(m.SplatMapKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp, _textures);
                groundColor = color; groundSplat = splat;
                Texture2D d1 = null, d2 = null;
                if (m.TerrainMaterial != null)
                {
                    if (m.TerrainMaterial.TryGetLayer(MaterialDef.SemDetail1, out var l1)) d1 = m.GetTexture(l1.TextureKey, TextureWrapMode.Repeat, TextureWrapMode.Repeat, _textures);
                    if (m.TerrainMaterial.TryGetLayer(MaterialDef.SemDetail2, out var l2)) d2 = m.GetTexture(l2.TextureKey, TextureWrapMode.Repeat, TextureWrapMode.Repeat, _textures);
                }
                var mat = MaterialFactory.CreateTerrainMaterial(color, splat, d1, d2, DetailTiling, DetailStrength);
                st.Res.Owned.Add(mat);
                CreateMeshObject("Terrain", st.Root.transform, Vector3.zero, data.Terrain.ToMesh(), mat, st.Res, true);
            }

            if (LoadSectorObjects && st.Model.Objects != null)
            {
                var objRoot = new GameObject("Objects").transform;
                objRoot.SetParent(st.Root.transform, false);
                ObjectBuilder.BuildObjects(st.Model, objRoot, _textures, st.Res, _opt);
                TDU2.Map.Vehicles.InteriorFx.ApplyUva(st.Model.Bank?.FilePath, objRoot);   // Textur-Animationen wie im Spiel
            }

            if (LoadGrass && st.Model.Grass != null && _grassTypes != null && _grassMat != null && st.Model.Heights != null)
            {
                var g = st.Root.AddComponent<GrassRenderer>();
                g.Viewer = Viewer;
                g.Heights = st.Model.Heights;
                g.Map = st.Model.Grass;
                g.Types = _grassTypes;
                g.GrassMaterial = _grassMat;
                g.BushMaterial = _bushMat;
                g.GrassAtlasCells = _grassCells;
                g.BushAtlasCells = _bushCells;
                g.Radius = GrassRadius;
                g.Density = GrassDensity;
                g.Spacing = GrassSpacing;
                // wie TD_TallGrass: Gras mit der Bodenfarbe (Colormap) des Sektors einfärben, gebackener Schatten aus der Splatmap
                g.GroundTex = groundColor;
                g.SplatTex = groundSplat;
                g.ShadowStrength = MaterialFactory.ShadowMapStrength;
            }

            foreach (var (spot, em) in data.Exteriors)
            {
                st.ExtraModels.Add(em);
                var ego = BuildExterior(spot, em, st.Root.transform, origin, st.Res, "");
                if (ego != null) TDU2.Map.Vehicles.InteriorFx.ApplyUva(em.Bank?.FilePath, ego.transform);
            }
            if (GenerateColliders) AddColliders(st.Root);
            st.Built = true;
            SetLod1Visible(st.Key, false);
            TryAddVegetation(st);
            TryAddActors(st);
        }

        void PollActors()
        {
            if (_actors != null || _actorsTask == null || !_actorsTask.IsCompleted) return;
            if (_actorsTask.IsFaulted)
            {
                Debug.LogWarning("[TDU2] worldactors.bnk: " + _actorsTask.Exception?.GetBaseException().Message);
                _actorsTask = null;
                return;
            }
            _actors = _actorsTask.Result;
            Debug.Log($"[TDU2] World Actors: {_actors.ByHc.Count} Modelle" + (_actors.Log.Count > 0 ? " – " + string.Join("; ", _actors.Log.Take(8)) : ""));
            foreach (var st in _sectors.Values) if (st.Built) TryAddActors(st);
        }

        /// <summary>World Actors eines Sektors (A-Platzierungen) als Objekte mit statischer Kollision.</summary>
        void TryAddActors(SectorState st)
        {
            if (st.ActorsDone || _actors == null || st.Model == null || st.Root == null || !LoadWorldActors) return;
            st.ActorsDone = true;
            if (st.Model.Actors == null) return;
            var holder = new GameObject("WorldActors");
            holder.transform.SetParent(st.Root.transform, false);
            try
            {
                int n = _actors.Build(st.Model.Actors, holder.transform, _textures, st.Res, _opt, _root);
                if (n == 0) { UnityUtil.SafeDestroy(holder); return; }
                TDU2.Map.Vehicles.InteriorFx.ApplyUva(_actors.Model?.Bank?.FilePath, holder.transform, true);
                // wie im Spiel: Poller, Schilder, Ampeln, Mülleimer fallen beim Anfahren um (Bänke bleiben fest)
                foreach (Transform grp in holder.transform)
                    foreach (Transform inst in grp)
                        WorldActorBody.Physicalize(inst.gameObject, grp.name);
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] World Actors " + st.Key + ": " + ex.Message); }
        }

        /// <summary>MeshCollider für alle opaken Meshes (LOD0) eines Sektors. Laub/Cutout und Transparentes werden ausgelassen.</summary>
        static void AddColliders(GameObject root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var n = mf.gameObject.name;
                if (n.StartsWith("LOD") && n != "LOD0") continue;
                var mr = mf.GetComponent<MeshRenderer>();
                bool solid = false, thin = false;
                if (mr != null)
                    foreach (var m in mr.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        string sn = m.shader.name;
                        if (sn == "TDU2/Foliage" || sn == "TDU2/Sprite" || sn == "TDU2/SpriteGlow" || sn == "TDU2/Grass" || sn == "TDU2/Water") continue;
                        bool cutout = m.HasProperty("_Cutoff") && m.GetFloat("_Cutoff") > 0.01f;
                        bool transparent = m.renderQueue >= 2500;
                        // additive Leuchteffekte (Halos/Lichtkegel) nie
                        if (transparent && m.HasProperty("_DstBlend") && Mathf.Approximately(m.GetFloat("_DstBlend"), (float)BlendMode.One)) continue;
                        if (!cutout && !transparent) { solid = true; break; }
                        thin = true;   // Zäune/Gitter (Alpha-Test), Glasfassaden (transparent)
                    }
                // Zäune/Glas nur, wenn sie aufrecht stehen (keine flachen Decals, Markierungen, Schatten-Quads)
                if (!solid && thin && mf.sharedMesh.bounds.size.y * Mathf.Abs(mf.transform.lossyScale.y) >= 0.5f) solid = true;
                if (!solid) continue;
                if (mf.GetComponent<Collider>() != null) continue;
                if (!UnityUtil.IsCollidable(mf.sharedMesh)) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }
        }

        /// <summary>
        /// Bodenhöhe an einer Weltposition aus den geladenen Höhenrastern (Sektor 3 m, sonst Area 24 m, sonst Insel).
        /// Fallback für Fahrzeuge, falls (noch) kein Collider unter ihnen liegt.
        /// </summary>
        public bool TryGetGroundHeight(Vector3 worldPos, out float worldY)
        {
            worldY = 0f;
            if (_root == null) return false;
            var g = _root.InverseTransformPoint(worldPos);
            float h; bool ok = false; h = 0f;
            var sk = WorldLayout.SectorAt(g.x, g.z);
            if (_sectors.TryGetValue(sk, out var st) && st.Built && st.Model?.Heights != null)
            {
                var o = WorldLayout.SectorOrigin(sk);
                h = st.Model.Heights.Sample(g.x - o.x, g.z - o.z); ok = true;
            }
            if (!ok)
            {
                var ak = WorldLayout.AreaAt(g.x, g.z);
                if (_areas.TryGetValue(ak, out var at) && at.Built && at.Model?.Heights != null)
                {
                    var o = WorldLayout.AreaOrigin(ak);
                    h = at.Model.Heights.Sample(g.x - o.x, g.z - o.z); ok = true;
                }
            }
            if (!ok && _islandModel?.Heights != null) { h = _islandModel.Heights.Sample(g.x, g.z); ok = true; }
            if (!ok) return false;
            worldY = _root.TransformPoint(new Vector3(g.x, h, g.z)).y;
            return true;
        }

        void PollLibrary()
        {
            if (_library != null || _libraryTask == null || !_libraryTask.IsCompleted) return;
            if (_libraryTask.IsFaulted)
            {
                Debug.LogWarning("commonworld.bnk: " + _libraryTask.Exception?.GetBaseException().Message);
                _libraryTask = null;
                return;
            }
            _library = _libraryTask.Result;
            _library.MirrorMeshes = MirrorX;
            // Meer: Normalmap aus commonworld (im Original erzeugt td_ocean_nmap_from_hmap sie zur Laufzeit)
            var wn = _textures.Get(_textures.KeyByName("water_n.2db"), TextureWrapMode.Repeat, TextureWrapMode.Repeat);
            if (wn != null && MaterialFactory.Water.HasProperty("_NormalMap"))
            {
                MaterialFactory.Water.SetTexture("_NormalMap", wn);
                MaterialFactory.Water.SetFloat("_HasNormalMap", 1f);
                Debug.Log("[TDU2] Meer: Normalmap water_n.2db");
            }
            Debug.Log($"Library Loadet: {_library.Scene?.InsHash.Count ?? 0} Objekte, {_textures.Count} Textures");
            foreach (var st in _sectors.Values) if (st.Built) TryAddVegetation(st);
        }

        void TryAddVegetation(SectorState st)
        {
            if (st.VegetationDone || _library == null || st.Model == null || st.Root == null) return;
            st.VegetationDone = true;
            var origin = WorldLayout.SectorOrigin(st.Key);
            var veg = st.Root.AddComponent<VegetationRenderer>();
            veg.Viewer = Viewer;
            veg.MapRoot = _root;
            veg.MirroredMeshes = _library.MirrorMeshes;
            veg.MaxDistance = VegetationDistance;
            veg.CullLastLod = CullLastLod;
            veg.Shadows = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            if (LoadVegetation) AddPlacements(veg, st.Model.Vegetation, origin);
            if (LoadOtherPlacements) AddPlacements(veg, st.Model.Placements, origin);
            if (LoadOtherPlacements) AddPlacements(veg, st.Model.Rails, origin, st.Res);   // Leitplanken (-S.pmi)
            if (LoadActors) AddPlacements(veg, st.Model.Actors, origin);
            st.Instances = veg.InstanceCount;
        }

        void AddPlacements(VegetationRenderer veg, PmiFile pmi, Vector3 origin, BuildResources res = null)
        {
            if (pmi == null) return;
            foreach (var g in pmi.Groups)
            {
                var obj = _library.Get(g.Key, _textures, _opt);
                if (obj == null) continue;
                bool skin = res != null && SkinnedRail(obj);
                var list = new List<Matrix4x4>(g.Instances.Count);
                foreach (var inst in g.Instances)
                {
                    var m = MathUtil.TRS(origin + inst.Position, inst.Rotation, inst.Scale);
                    if (skin && inst.Bones != null && inst.Bones.Length > 0)
                    {
                        // Leitplanke: Mesh mit den Knochenmatrizen dieser Instanz biegen (Pre steckt in Knochen 0)
                        var baked = BakeRail(obj, inst.Bones, _library.MirrorMeshes, res);
                        if (baked != null) { veg.Add(baked, new List<Matrix4x4> { m }); continue; }
                    }
                    list.Add(inst.Pre.HasValue ? m * inst.Pre.Value : m);
                }
                if (list.Count > 0) veg.Add(obj, list);
            }
        }

        static readonly List<Vector4> _skinIdx = new List<Vector4>(), _skinW = new List<Vector4>();

        /// <summary>Hat das Library-Objekt Skinning-Daten (UV-Kanal 4/5, z.B. SF_GLISS)?</summary>
        static bool SkinnedRail(LibraryObject obj)
        {
            foreach (var l in obj.Lods)
            {
                if (l?.Mesh == null) continue;
                l.Mesh.GetUVs(4, _skinIdx);
                if (_skinIdx.Count == l.Mesh.vertexCount && _skinIdx.Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Leitplanken wie im Original (TD_Ground, 4-Bone-Skinning): v' = Summe w_i * (Knochen_i * v). Die 7 Matrizen jeder
        /// -S.pmi-Instanz passen Anfang und Ende jeder 20-m-Planke an die Nachbarn an – ohne sie bleiben an jedem Pfosten
        /// Stufen von einigen Zentimetern. Ergebnis ist ein eigenes Objekt pro Instanz (Platzierung ohne Pre).
        /// </summary>
        static LibraryObject BakeRail(LibraryObject src, Matrix4x4[] bones, bool mirrored, BuildResources res)
        {
            var lods = new LibraryLod[src.Lods.Length];
            for (int l = 0; l < lods.Length; l++)
            {
                var sl = src.Lods[l];
                if (sl?.Mesh == null) return null;
                var mesh = sl.Mesh;
                int n = mesh.vertexCount;
                mesh.GetUVs(4, _skinIdx); mesh.GetUVs(5, _skinW);
                bool has = _skinIdx.Count == n && _skinW.Count == n;
                var v = mesh.vertices; var nr = mesh.normals;
                bool hasN = nr != null && nr.Length == n;
                for (int i = 0; i < n; i++)
                {
                    var p = v[i]; if (mirrored) p.x = -p.x;
                    var q = hasN ? nr[i] : Vector3.up; if (mirrored) q.x = -q.x;
                    Vector3 ap = Vector3.zero, an = Vector3.zero;
                    if (has)
                    {
                        var bi = _skinIdx[i]; var bw = _skinW[i];
                        float ws = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = bw[k];
                            if (w <= 0f) continue;
                            int b = (int)bi[k];
                            var M = b >= 0 && b < bones.Length ? bones[b] : bones[0];
                            ap += w * M.MultiplyPoint3x4(p); an += w * M.MultiplyVector(q); ws += w;
                        }
                        if (ws < 1e-4f) { ap = bones[0].MultiplyPoint3x4(p); an = bones[0].MultiplyVector(q); }
                        else if (Mathf.Abs(ws - 1f) > 1e-3f) { ap /= ws; }
                    }
                    else { ap = bones[0].MultiplyPoint3x4(p); an = bones[0].MultiplyVector(q); }
                    if (mirrored) { ap.x = -ap.x; an.x = -an.x; }
                    v[i] = ap;
                    if (hasN) nr[i] = an.sqrMagnitude > 1e-8f ? an.normalized : Vector3.up;
                }
                var m = UnityEngine.Object.Instantiate(mesh);
                m.name = mesh.name + "_skin";
                m.vertices = v;
                if (hasN) m.normals = nr;
                m.RecalculateBounds();
                res.Owned.Add(m);
                lods[l] = new LibraryLod { Mesh = m, Materials = sl.Materials, MaxDistance = sl.MaxDistance };
            }
            return new LibraryObject { Key = src.Key, Name = src.Name, Lods = lods, Radius = src.Radius + 1f };
        }

        static SectorKey SpotSector(Spot s)
        {
            var k = new SectorKey(s.AreaX, s.AreaZ, s.SectorX, s.SectorZ);
            var r = WorldLayout.SectorRect(k);
            if (r.Contains(new Vector2(s.Position.x, s.Position.z))) return k;
            return WorldLayout.SectorAt(s.Position.x, s.Position.z);
        }

        string ExteriorFile(Spot s, string suffix)
        {
            if (string.IsNullOrEmpty(s.Code) || _index.ExteriorDir == null) return null;
            var f = Path.Combine(_index.ExteriorDir, s.Code.ToLowerInvariant() + suffix + ".bnk");
            return File.Exists(f) ? f : null;
        }

        List<(Spot, string)> SpotsInSector(SectorKey k)
        {
            var list = new List<(Spot, string)>();
            if (_spots == null) return list;
            foreach (var s in _spots.Spots)
                if (SpotSector(s).Equals(k)) { var f = ExteriorFile(s, ""); if (f != null) list.Add((s, f)); }
            return list;
        }

        List<(Spot, string)> SpotsInArea(AreaKey a)
        {
            var list = new List<(Spot, string)>();
            if (_spots == null) return list;
            foreach (var s in _spots.Spots)
            {
                if (!WorldLayout.AreaAt(s.Position.x, s.Position.z).Equals(a)) continue;
                var f = ExteriorFile(s, "_low") ?? ExteriorFile(s, "");
                if (f != null) list.Add((s, f));
            }
            return list;
        }

        string[] specialObjects =
        {
            "PORTAIL",
            "INTERIOR",
            "~a1~8aTAL_GA",
            "PORTAILG",
            "DOOR",
            "~85~96~94~b1~8fxAR",
            "~85~96~94R_GAR",
            "~97~90~a4~95~88~8eL_",
            "~94ORTAIL_",
            "~97ORTAIL_",
            "ECW_A"
        };

        /// <summary>
        /// Placement as in the game: Exterior scenes are modeled locally around (0,0,0) and are placed at
        /// position (+16) in spots.bin with a rotation around the Y-axis equal to angle (+32, radians).
        /// (Verified via the spot's trigger box, which exactly matches the scene's floor plan.)
        /// </summary>
        //GameObject BuildExterior(Spot spot, BankModel em, Transform parent, Vector3 parentOrigin, BuildResources res, string suffix)
        //{
        //    if (em?.Objects == null) return null;
        //    var go = new GameObject($"Spot_{spot.Code}{suffix}");
        //    go.transform.SetParent(parent, false);
        //    go.transform.localPosition = spot.Position - parentOrigin;
        //    go.transform.localRotation = Quaternion.Euler(0f, spot.Value1 * Mathf.Rad2Deg, 0f);
        //    ObjectBuilder.BuildObjects(em, go.transform, _textures, res, _opt);
        //    return go;
        //}

        GameObject BuildExterior(Spot spot, BankModel em, Transform parent,
            Vector3 parentOrigin, BuildResources res, string suffix)
        {
            if (em?.Objects == null)
                return null;

            var go = new GameObject($"Spot_{spot.Code}{suffix}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spot.Position - parentOrigin;
            go.transform.localRotation =
                Quaternion.Euler(0f, spot.Value1 * Mathf.Rad2Deg, 0f);

            ObjectBuilder.BuildObjects(em, go.transform, _textures, res, _opt);

            foreach (Transform obj in go.GetComponentsInChildren<Transform>(true))
            {
                if (specialObjects.Contains(obj.name))
                {
                    if (obj.localPosition != Vector3.zero)
                    {
                        Debug.LogWarning($"[TDU2] Fixe Objektposition: {obj.name} | Alt: {obj.localPosition}");
                        obj.localPosition = Vector3.zero;
                    }
                }
            }

            return go;
        }

        Transform FindChildRecursive(Transform parent, string targetName)
        {
            foreach (Transform child in parent)
            {
                if (child.name.IndexOf(
                        targetName,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }

                Transform found = FindChildRecursive(child, targetName);

                if (found != null)
                    return found;
            }

            return null;
        }

        string GetPath(Transform obj)
        {
            string path = obj.name;

            while (obj.parent != null)
            {
                obj = obj.parent;
                path = obj.name + "/" + path;
            }

            return path;
        }

        GameObject CreateMeshObject(string name, Transform parent, Vector3 localPos, Mesh mesh, Material mat, BuildResources res, bool shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = shadows && CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            res?.Owned.Add(mesh);
            return go;
        }

        void UnloadSector(SectorKey k)
        {
            if (!_sectors.TryGetValue(k, out var st)) return;
            _sectors.Remove(k);
            if (st.Root != null) UnityUtil.SafeDestroy(st.Root);
            st.Res.Destroy();
            st.Model?.Release();
            foreach (var em in st.ExtraModels) em.Release();
            SetLod1Visible(k, true);
        }

        void UnloadArea(AreaKey k)
        {
            if (!_areas.TryGetValue(k, out var st)) return;
            // Sektoren, die diese Area überlappen, zuerst entladen
            var drop = new List<SectorKey>();
            var ar = WorldLayout.AreaRect(k);
            foreach (var s in _sectors.Keys) if (WorldLayout.SectorRect(s).Overlaps(ar)) drop.Add(s);
            foreach (var s in drop) UnloadSector(s);

            _areas.Remove(k);
            foreach (var (key, go) in st.Registered)
            {
                if (_lod1BySector.TryGetValue(key, out var list)) list.Remove(go);
                if (go != null) _lod1Keys.Remove(go);
            }
            if (st.Root != null) UnityUtil.SafeDestroy(st.Root);
            st.Res.Destroy();
            st.Model?.Release();
            foreach (var em in st.ExtraModels) em.Release();
            if (_islandPieces.TryGetValue(k, out var ip) && ip != null) ip.SetActive(ShowIslandLod);
        }

        void OnDestroy()
        {
            DestroyMapTerrain();
            foreach (var k in new List<SectorKey>(_sectors.Keys)) UnloadSector(k);
            foreach (var k in new List<AreaKey>(_areas.Keys)) UnloadArea(k);
            _islandRes.Destroy();
            _islandModel?.Release();
            _library?.Destroy();
            _textures.Clear();
            if (_grassMat != null) UnityUtil.SafeDestroy(_grassMat);
            if (_bushMat != null) UnityUtil.SafeDestroy(_bushMat);
            MaterialFactory.Reset();
        }

        /// <summary>
        /// Setzt den Betrachter an eine Spielposition (Inselkoordinaten).
        /// </summary>
        public void TeleportTo(Vector3 gamePosition)
        {
            if (Viewer != null && _root != null) Viewer.position = _root.TransformPoint(gamePosition);
        }

        public Vector3 ViewerGamePosition => _viewerGame;
        public string lib = "";
        public int Jobs = 0;
        public int builtA = 0, builtS = 0, inst = 0, areaCount = 0, sectorCount = 0;
        /// <summary>Fehlgeschlagene Areas/Sektoren (seit dem Laden der Insel, Stand von <see cref="RefreshStats"/>).</summary>
        public int failedA = 0, failedS = 0;

        /// <summary>Lade-Status der Insel ("ready", "Fehler: …").</summary>
        public string Status => _status;
        /// <summary>World-Actors (Ampeln, Schilder …): geladen / lädt / aus.</summary>
        public string ActorsState => _actors != null ? "geladen" : _actorsTask != null ? "lädt…" : "aus";

        /// <summary>Zähler für Debug-Anzeigen (Entwicklermenü F1, DebugManager F3) aktualisieren.</summary>
        public void RefreshStats()
        {
            Jobs = RunningJobs();
            builtA = 0; builtS = 0; inst = 0; failedA = 0; failedS = 0;
            foreach (var a in _areas.Values) { if (a.Built) builtA++; if (a.Failed) failedA++; }
            foreach (var s in _sectors.Values) { if (s.Built) { builtS++; inst += s.Instances; } if (s.Failed) failedS++; }
            sectorCount = _sectors.Count;
            areaCount = _areas.Count;
            lib = _library != null ? $"Library OK ({_library.MissingCount} unbekannte Keys)" : (_libraryTask != null ? "Library lädt..." : "keine Library");
        }

        /// <summary>Alle Areas und Sektoren entladen; der nächste Scan lädt sie mit den aktuellen Einstellungen neu.</summary>
        public void ReloadWorld()
        {
            foreach (var k in new List<SectorKey>(_sectors.Keys)) UnloadSector(k);
            foreach (var k in new List<AreaKey>(_areas.Keys)) UnloadArea(k);
            foreach (var ip in _islandPieces.Values) if (ip != null) ip.SetActive(ShowIslandLod);
            _nextScan = 0f;
        }

        void OnGUI()
        {
            if (DebugManager.Instance != null && DebugManager.Instance.isSectorDebug == false) return;
            RefreshStats();
            var sk = WorldLayout.SectorAt(_viewerGame.x, _viewerGame.z);
            if (!ShowDebugOverlay || _index == null) return;
            //GUI.Box(new Rect(10, 10, 430, 110), "");
            //GUI.Label(new Rect(20, 15, 420, 100),
            //    $"TDU2 {_index.IslandName}  –  {_status}\n" +
            //    $"Position: {_viewerGame.x:0} / {_viewerGame.y:0} / {_viewerGame.z:0}   {sk}\n" +
            //    $"Areas: {builtA}/{_areas.Count}   Sektoren: {builtS}/{_sectors.Count}   Jobs: {Jobs}\n" +
            //    $"Instanzen (Bäume/Objekte): {inst}   {lib}\n" +
            //    "Steuerung: RMB + WASD/QE, Shift = schnell, Mausrad = Tempo");
        }
    }
}
