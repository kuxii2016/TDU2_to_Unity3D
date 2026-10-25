using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Avatars
{
    /// <summary>
    /// Ein Avatar (Spielfigur) wie im Spiel: ein Skelett (.xsb) häutet Körper, Kopf und alle Kleidungsstücke.
    /// Der GameObject-Ursprung ist die Avatar-Wurzel im Spielraum (rechtshändig, +Z vorn) – das Objekt gehört unter einen
    /// gespiegelten Elternteil (z.B. CarModel.Visual), so wie alle Spielmodelle hier.
    /// Ablauf: <see cref="Create"/> lädt im Hintergrund, baut dann die Renderer; Posen pro Frame mit
    /// <see cref="BeginPose"/> / <see cref="ApplyClip"/> / <see cref="SolveTwoBone"/> / <see cref="CommitPose"/>.
    /// </summary>
    public sealed class TDU2Avatar : MonoBehaviour
    {
        public AvatarConfig Config;
        public XsbSkeleton Skeleton { get; private set; }
        public Transform[] Bones { get; private set; }
        public bool Ready { get; private set; }
        public event Action<TDU2Avatar> Built;
        /// <summary>Fußraum-Schatten der Avatar-Shader (im Auto).</summary>
        public bool InCar { get => _inCar; set { _inCar = value; ApplyInCar(); } }
        bool _inCar;

        readonly List<SkinnedMeshRenderer> _renderers = new List<SkinnedMeshRenderer>();
        readonly List<SkinnedMeshRenderer> _headRenderers = new List<SkinnedMeshRenderer>();
        readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        readonly List<AvatarPartData> _parts = new List<AvatarPartData>();
        Task<(XsbSkeleton skel, List<AvatarPartData> body, List<(ClotheInfo info, AvatarPartData data)> clothes)> _load;

        // Arbeits-Pose (lokal) und Modellraum-Matrizen
        Quaternion[] _lr; Vector3[] _lp; Vector3[] _ls;
        Matrix4x4[] _g;
        bool _headHidden;

        /// <summary>Avatar unter <paramref name="parent"/> anlegen und im Hintergrund laden.</summary>
        public static TDU2Avatar Create(Transform parent, AvatarConfig cfg, string name = "TDU2 Avatar")
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<TDU2Avatar>();
            a.Config = cfg ?? AvatarConfig.Default();
            a.StartLoad();
            return a;
        }

        void StartLoad()
        {
            var cfg = Config.Clone() ?? Config;
            _load = Task.Run(() =>
            {
                var skel = AvatarLibrary.Skeleton(cfg.Sex);
                var body = AvatarLibrary.LoadBody(cfg.Sex, cfg.Ethnic);
                var clothes = new List<(ClotheInfo, AvatarPartData)>();
                bool hat = false;
                foreach (int h in cfg.Items) { var hc = AvatarLibrary.Clothe(h); if (hc != null && hc.Part == ClothePart.Helmet && !hc.IsNothing) hat = true; }
                for (int i = 0; i < cfg.Items.Count; i++)
                {
                    var ci = AvatarLibrary.Clothe(cfg.Items[i]);
                    if (ci == null) continue;
                    if (ci.Sex == 1 && cfg.Sex == 0 || ci.Sex == 2 && cfg.Sex == 1) continue;   // falsches Geschlecht
                    AvatarPartData data = null;
                    try { data = AvatarLibrary.LoadClothe(ci, i < cfg.Colors.Count ? cfg.Colors[i] : 0, hat); }
                    catch (Exception ex) 
                    { 
                        DebugManager.Error("Clothes " + ci.File + ": " + ex.Message);
                    }
                    clothes.Add((ci, data));
                }
                return (skel, body, clothes);
            });
        }

        void Update()
        {
            if (_load == null || !_load.IsCompleted) return;
            var t = _load; _load = null;
            if (t.IsFaulted) 
            { 
                DebugManager.Error("Avatar loding: " + t.Exception?.GetBaseException().Message);
                return; 
            }
            try { Build(t.Result.skel, t.Result.body, t.Result.clothes); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void Build(XsbSkeleton skel, List<AvatarPartData> body, List<(ClotheInfo info, AvatarPartData data)> clothes)
        {
            if (skel == null) 
            { 
                DebugManager.Error("Avatar: missing Skelett (avatar/common_bodies.bnk?)");
                return; 
            }
            Skeleton = skel;
            int n = skel.Count;
            Bones = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject(Bin.KeyToString(skel.Key[i]));
                Bones[i] = go.transform;
            }
            for (int i = 0; i < n; i++)
            {
                int p = skel.Parent[i];
                Bones[i].SetParent(p >= 0 && p < n ? Bones[p] : transform, false);
                Bones[i].localPosition = skel.Pos[i];
                Bones[i].localRotation = skel.Rot[i];
                Bones[i].localScale = skel.Scale[i];
            }
            _lr = new Quaternion[n]; _lp = new Vector3[n]; _ls = new Vector3[n]; _g = new Matrix4x4[n];
            BeginPose(); CommitPose();

            // Sichtbare Körperteile: ein Teil bleibt sichtbar, solange kein getragenes Stück es verdeckt
            var visible = new Dictionary<ulong, bool>();
            foreach (var f in AvatarLibrary.BodyFlags)
            {
                bool vis = true;
                foreach (var c in clothes) if (c.info != null && c.info.Visible.TryGetValue(f, out bool v) && !v) { vis = false; break; }
                string up = f.ToUpperInvariant();
                foreach (var full in new[] { "TOP_" + up, "BOTTOM_" + up, "TOP_ARMS_" + up, up })
                    visible[MaterialDef.KeyFromName(full)] = vis;
            }

            foreach (var part in body) AddPart(part, visible, part.Name.Contains("_head_"));
            foreach (var c in clothes)
                if (c.data != null)
                {
                    bool head = c.info.Part == ClothePart.Hair || c.info.Part == ClothePart.Helmet || c.info.Part == ClothePart.Glasses || c.info.Part == ClothePart.Beard;
                    AddPart(c.data, null, head);
                }
            ApplyBrowColor();
            Ready = true;
            ApplyInCar();
            if (_headHidden) { _headHidden = false; HideHead(true); }
            try { Built?.Invoke(this); } catch (Exception ex) { Debug.LogException(ex); }
        }

        void AddPart(AvatarPartData part, Dictionary<ulong, bool> visible, bool head)
        {
            if (part == null || part.Prims.Count == 0) return;
            _parts.Add(part);
            var keep = new List<int>();
            for (int i = 0; i < part.Prims.Count; i++)
            {
                var def = part.PrimMaterials[i];
                if (def != null && visible != null && visible.TryGetValue(def.Key, out bool vis) && !vis) continue;
                if (def != null && AvatarMaterials.Skip(def)) continue;
                keep.Add(i);
            }
            if (keep.Count == 0) return;

            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tan = new List<Vector4>();
            var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>(); var uv2 = new List<Vector2>();
            var bw = new List<BoneWeight>();
            var subs = new List<int[]>();
            var mats = new Material[keep.Count];
            bool anyTan = false;
            foreach (var i in keep) if (part.Prims[i].Tan != null) anyTan = true;
            for (int k = 0; k < keep.Count; k++)
            {
                var pr = part.Prims[keep[k]];
                int b = pos.Count;
                pos.AddRange(pr.Pos); nrm.AddRange(pr.Nrm); bw.AddRange(pr.Weights);
                if (anyTan) { if (pr.Tan != null) tan.AddRange(pr.Tan); else for (int v = 0; v < pr.Pos.Length; v++) tan.Add(new Vector4(1, 0, 0, 1)); }
                uv0.AddRange(pr.Uv[0]);
                uv1.AddRange(pr.Uv.Length > 1 ? pr.Uv[1] : pr.Uv[0]);
                uv2.AddRange(pr.Uv.Length > 2 ? pr.Uv[2] : pr.Uv[0]);
                var tris = new int[pr.Tris.Length];
                for (int t = 0; t < tris.Length; t++) tris[t] = pr.Tris[t] + b;
                subs.Add(tris);
                var def = part.PrimMaterials[keep[k]];
                mats[k] = AvatarMaterials.Create(def, (key, u, v) => GetTexture(part, key, u, v), pr.Uv.Length);
                if (part.HasHairColor && mats[k].GetFloat("_Mode") > 2.5f && mats[k].GetFloat("_Mode") < 3.5f)
                { mats[k].SetColor("_PrimClr", part.HairPrim); mats[k].SetColor("_ScdClr", part.HairScd); }
                _owned.Add(mats[k]);
            }
            var mesh = new Mesh { name = "TDU2 Avatar " + part.Name };
            if (pos.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(pos); mesh.SetNormals(nrm);
            if (anyTan) mesh.SetTangents(tan);
            mesh.SetUVs(0, uv0); mesh.SetUVs(1, uv1); mesh.SetUVs(2, uv2);
            mesh.boneWeights = bw.ToArray();
            mesh.bindposes = Skeleton.InvBind;
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            _owned.Add(mesh);

            var go = new GameObject(part.Name);
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = Bones;
            smr.rootBone = Bones[0];
            smr.sharedMaterials = mats;
            smr.updateWhenOffscreen = false;
            smr.localBounds = new Bounds(new Vector3(0f, 0.2f, 0f), new Vector3(2.4f, 3.2f, 2.4f));
            smr.quality = SkinQuality.Bone4;
            _renderers.Add(smr);
            if (head) _headRenderers.Add(smr);
        }

        /// <summary>Augenbrauen/Wimpern (Masken-Materialien) in der Haarfarbe der getragenen Frisur.</summary>
        void ApplyBrowColor()
        {
            var col = AvatarMaterials.DefaultBrow;
            foreach (var p in _parts) if (p != null && p.HasHairColor) { col = Color.Lerp(p.HairPrim, p.HairScd, 0.25f) * 0.85f; col.a = 1f; break; }
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.HasProperty("_AlphaLum") && m.GetFloat("_AlphaLum") > 0.5f) m.SetColor("_Color", col);
            }
        }

        Texture2D GetTexture(AvatarPartData part, ulong key, TextureWrapMode u, TextureWrapMode v)
        {
            if (key == 0) return null;
            if (part.Textures.TryGet(key, u, v, out var t)) return t;
            return AvatarLibrary.CommonTextures.Get(key, u, v);
        }

        void ApplyInCar()
        {
            if (!Ready) return;
            var mpb = new MaterialPropertyBlock();
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(mpb);
                mpb.SetFloat("_InCar", _inCar ? 1f : 0f);
                r.SetPropertyBlock(mpb);
            }
        }

        /// <summary>Kopf, Haare, Brille, Bart aus-/einblenden (Cockpit-Kamera).</summary>
        public void HideHead(bool hide)
        {
            if (!Ready) { _headHidden = hide; return; }
            if (_headHidden == hide) return;
            _headHidden = hide;
            foreach (var r in _headRenderers)
                if (r != null) r.shadowCastingMode = hide ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        }

        public bool Visible { get; private set; } = true;
        public void SetVisible(bool on) { if (on == Visible) return; Visible = on; foreach (var r in _renderers) if (r != null) r.enabled = on; }

        // ------------------------------------------------------------------ Posen
        /// <summary>Arbeits-Pose auf die Ruhepose des Skeletts setzen.</summary>
        public void BeginPose()
        {
            if (Skeleton == null) return;
            for (int i = 0; i < Skeleton.Count; i++) { _lr[i] = Skeleton.Rot[i]; _lp[i] = Skeleton.Pos[i]; _ls[i] = Skeleton.Scale[i]; }
        }

        /// <summary>Clip zum Zeitpunkt t überlagern (weight 1 = ersetzen).</summary>
        public void ApplyClip(AnmClip clip, float t, float weight = 1f, bool positions = true)
        {
            if (clip == null || Skeleton == null || weight <= 0f) return;
            foreach (var tr in clip.Tracks)
            {
                if (!Skeleton.ByHash.TryGetValue(tr.Hash, out int i)) continue;
                if (tr.Rot != null)
                {
                    var q = tr.Rot.SampleRot(t);
                    _lr[i] = weight >= 1f ? q : Quaternion.Slerp(_lr[i], q, weight);
                }
                if (positions && tr.Pos != null)
                {
                    var p = tr.Pos.SampleVec(t);
                    _lp[i] = weight >= 1f ? p : Vector3.Lerp(_lp[i], p, weight);
                }
            }
        }

        void ComputeGlobals()
        {
            int n = Skeleton.Count;
            for (int i = 0; i < n; i++)
            {
                var m = Matrix4x4.TRS(_lp[i], _lr[i], _ls[i]);
                int p = Skeleton.Parent[i];
                _g[i] = p >= 0 ? _g[p] * m : m;
            }
        }

        Vector3 GPos(int i) => _g[i].GetColumn(3);
        Quaternion GRot(int i) => _g[i].rotation;

        /// <summary>Weltrotation (Modellraum) eines Knochens setzen; Kinder folgen.</summary>
        void SetGlobalRotation(int i, Quaternion g)
        {
            int p = Skeleton.Parent[i];
            var pr = p >= 0 ? GRot(p) : Quaternion.identity;
            _lr[i] = Quaternion.Inverse(pr) * g;
            ComputeGlobals();
        }

        /// <summary>
        /// Zwei-Knochen-IK im Avatar-Raum: Kette <paramref name="upper"/> (Schulter/Hüfte) – <paramref name="mid"/> (Ellbogen/Knie) –
        /// <paramref name="effector"/> (IK-Knochen an Hand/Fuß), Ziel in Modellraum-Koordinaten. Die Beugeebene bleibt die der Animation.
        /// </summary>
        public void SolveTwoBone(int upper, int mid, int effector, Vector3 target, float weight = 1f)
        {
            if (Skeleton == null || upper < 0 || mid < 0 || effector < 0 || weight <= 0f) return;
            ComputeGlobals();
            Vector3 a = GPos(upper), b = GPos(mid), c = GPos(effector);
            float la = (b - a).magnitude, lb = (c - b).magnitude;
            if (la < 1e-4f || lb < 1e-4f) return;
            target = Vector3.Lerp(c, target, weight);
            Vector3 at = target - a;
            float d = Mathf.Clamp(at.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            // 1) Gelenk beugen: Winkel bei mid so, dass |c - a| = d
            Vector3 ba = a - b, bc = c - b;
            float cur = Vector3.Angle(ba, bc);
            float want = Mathf.Acos(Mathf.Clamp((la * la + lb * lb - d * d) / (2f * la * lb), -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(ba, bc);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(ba, Vector3.up);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;
            axis.Normalize();
            SetGlobalRotation(mid, Quaternion.AngleAxis(want - cur, axis) * GRot(mid));
            // 2) Kette ausrichten
            c = GPos(effector);
            var rot = Quaternion.FromToRotation(c - a, target - a);
            SetGlobalRotation(upper, rot * GRot(upper));
        }

        /// <summary>Position eines Knochens im Avatar-Raum (aktuelle Arbeits-Pose).</summary>
        public Vector3 BonePosition(int i) { ComputeGlobals(); return GPos(i); }

        /// <summary>Weltpunkt in den Avatar-Raum umrechnen (berücksichtigt die Spiegelung der Eltern).</summary>
        public Vector3 ToAvatarSpace(Vector3 world) => transform.worldToLocalMatrix.MultiplyPoint3x4(world);

        /// <summary>Arbeits-Pose auf die Knochen-Transforms schreiben.</summary>
        public void CommitPose()
        {
            if (Bones == null) return;
            for (int i = 0; i < Bones.Length; i++)
            {
                var b = Bones[i];
                if (b == null) continue;
                b.localPosition = _lp[i];
                b.localRotation = _lr[i];
            }
        }

        public int Bone(string name) => Skeleton != null ? Skeleton.Find(name) : -1;

        void OnDestroy()
        {
            foreach (var o in _owned) if (o != null) Destroy(o);
            _owned.Clear();
            foreach (var p in _parts) p.Textures.Destroy();
            _parts.Clear();
        }
    }

    /// <summary>Materialien der Avatar-Shader-Sets für "TDU2/Avatar".</summary>
    public static class AvatarMaterials
    {
        static Shader _shader, _blend;
        static readonly ulong SemSkin = MaterialDef.KeyFromName("SKIN"), SemNormal = MaterialDef.KeyFromName("NORMAL"),
            SemTex = MaterialDef.KeyFromName("TEX"), SemHair = MaterialDef.KeyFromName("HAIR"), SemOcc = MaterialDef.KeyFromName("OCCLUSION"),
            SemL1 = MaterialDef.KeyFromName("LAYER1"), SemL2 = MaterialDef.KeyFromName("LAYER2"), SemL3 = MaterialDef.KeyFromName("LAYER3"),
            SemGrain = MaterialDef.KeyFromName("DETAIL_GRAIN");

        /// <summary>Overlays, die das Original mit eigenen Durchgängen mischt (Augen-Schatten): ausgelassen.</summary>
        public static bool Skip(MaterialDef def)
        {
            var n = def.Name ?? "";
            return n.IndexOf("EYES_S", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool Layer(MaterialDef def, ulong sem, out MaterialLayer l)
        {
            foreach (var x in def.Layers) if (x.Semantic == sem) { l = x; return true; }
            l = default; return false;
        }

        static Texture2D Tex(MaterialLayer l, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> get) =>
            l.TextureKey == 0 ? null : get(l.TextureKey, TextureDef.WrapFromEden(l.AddressU), TextureDef.WrapFromEden(l.AddressV));

        public static readonly Color DefaultBrow = new Color(0.09f, 0.065f, 0.045f, 1f);

        static Color Clr(MaterialDef def, string p, Color fallback) =>
            def.TryGetParam(p, out var v) ? new Color(v.x, v.y, v.z, v.w) : fallback;

        public static Material Create(MaterialDef def, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> get, int uvSets)
        {
            if (_shader == null) _shader = Shader.Find("TDU2/Avatar") ?? Shader.Find("Standard");
            if (_blend == null) _blend = Shader.Find("TDU2/AvatarBlend") ?? _shader;
            // Zustände des Originals aus den Material-Flags (.2dm +0x40): [1] 2 = beidseitig, [3] 3 = Alpha-Test mit Referenz [6]/255,
            // [8]/[9] Blendfaktoren (5/6 = SrcAlpha/InvSrcAlpha: Augenbrauen, Wimpern, Haar-Strähnen, Ansätze)
            var fl = def != null ? def.Flags : null;
            bool blend = fl != null && fl[8] == 5 && fl[9] == 6;
            bool test = fl != null && fl[3] == 3;
            var m = new Material(blend ? _blend : _shader) { name = def != null ? "AVT " + def.Name : "AVT ?" };
            if (def == null) { m.SetFloat("_Mode", 2f); m.SetColor("_Color", new Color(0.6f, 0.6f, 0.6f, 1f)); return m; }
            m.SetFloat("_Cull", fl[1] == 2 ? 0f : 2f);
            if (test) m.SetFloat("_Cutoff", Mathf.Clamp(fl[6] / 255f, 0.02f, 0.95f));
            if (blend) { m.renderQueue = 3000 + (test ? 1 : 0); if (!test) m.SetFloat("_Cutoff", 0.004f); }
            if (def.TryGetParam("SPECPARM", out var sp)) m.SetVector("_Spec", new Vector4(sp.x, Mathf.Max(1f, sp.y), 0f, 0f));
            if (def.TryGetParam("CARINTEG", out var ci)) m.SetVector("_CarInteg", ci);
            if (def.TryGetParam("ATTEFACT", out var af)) m.SetVector("_AtteFact", af);
            Texture2D t;
            if (Layer(def, SemSkin, out var skin))
            {
                m.SetFloat("_Mode", 0f);
                if ((t = Tex(skin, get)) != null) m.SetTexture("_MainTex", t);
                // SKINCLR ist die Farbe des durchscheinenden Lichts (TD_Skinface), nicht die Hautfarbe; GLOSSDETAILCUT = 5. Layer
                m.SetColor("_Color", Color.white);
                m.SetColor("_SkinClr", Clr(def, "SKINCLR", Color.white));
                m.SetColor("_DclClr", Clr(def, "DCLCLR", Color.black));
                if (def.TryGetParam("COEFFS", out var co)) m.SetVector("_Coeffs", co);
                if (def.Layers.Count >= 5 && (t = Tex(def.Layers[4], get)) != null) { m.SetTexture("_GlossTex", t); m.SetFloat("_GlossOn", 1f); }
                // SPECTRALSCATTERING = 4. Layer: Hautton-Rampe der Herkunft (color_white / color_asia / color_black)
                if (def.Layers.Count >= 4 && (t = Tex(def.Layers[3], get)) != null) { t.wrapMode = TextureWrapMode.Clamp; m.SetTexture("_RampTex", t); m.SetFloat("_RampOn", 1f); }
                if (Layer(def, SemNormal, out var nl) && (t = Tex(nl, get)) != null) { m.SetTexture("_BumpMap", t); m.SetFloat("_BumpOn", 1f); }
            }
            else if (Layer(def, SemOcc, out var occ))
            {
                m.SetFloat("_Mode", 1f);
                m.SetColor("_BaseClr", Clr(def, "BASECLR", new Color(0.5f, 0.5f, 0.5f, 1f)));
                if ((t = Tex(occ, get)) != null) { m.SetTexture("_BumpMap", t); m.SetFloat("_BumpOn", 1f); }
                if (Layer(def, SemGrain, out var gr) && (t = Tex(gr, get)) != null) { m.SetTexture("_DetailTex", t); m.SetFloat("_DetailOn", 1f); }
                // UVSCALE: xy = Kachelung LAYER1 (auf UV1), zw = Stoffkorn (auf UV0)
                if (def.TryGetParam("UVSCALE", out var uvs))
                {
                    if (Mathf.Abs(uvs.x) < 1e-4f || Mathf.Abs(uvs.y) < 1e-4f) { uvs.x = 1f; uvs.y = 1f; }
                    m.SetVector("_UVScale", uvs);
                }
                var on = Vector4.zero;
                // TD_Clothe2: LAYER1 auf UV1, LAYER2/LAYER3 auf UV2 (fehlende Sätze -> letzter vorhandener)
                var lu = new Vector4(Mathf.Min(1, uvSets - 1), Mathf.Min(2, uvSets - 1), Mathf.Min(2, uvSets - 1), 0f);
                if (Layer(def, SemL1, out var l1) && (t = Tex(l1, get)) != null) { m.SetTexture("_Layer1", t); on.x = 1f; m.SetColor("_ClrL1", Clr(def, "CLRL1", Color.white)); }
                if (Layer(def, SemL2, out var l2) && (t = Tex(l2, get)) != null) { m.SetTexture("_Layer2", t); on.y = 1f; m.SetColor("_ClrL2", Clr(def, "CLRL2", Color.white)); }
                if (Layer(def, SemL3, out var l3) && (t = Tex(l3, get)) != null) { m.SetTexture("_Layer3", t); on.z = 1f; m.SetColor("_ClrL3", Clr(def, "CLRL3", Color.white)); }
                m.SetVector("_LayerOn", on);
                m.SetVector("_LayerUV", new Vector4(Mathf.Max(0, lu.x), Mathf.Max(0, lu.y), Mathf.Max(0, lu.z), 0f));
            }
            else if (Layer(def, SemHair, out var hair))
            {
                m.SetFloat("_Mode", 3f);
                if ((t = Tex(hair, get)) != null) m.SetTexture("_MainTex", t);
                m.SetColor("_PrimClr", Clr(def, "PRIMCLR", Color.black));
                m.SetColor("_ScdClr", Clr(def, "SCDCLR", new Color(0.5f, 0.35f, 0.2f, 1f)));
                if (!test && !blend) m.SetFloat("_Cutoff", 0.35f);
            }
            else
            {
                Layer(def, SemTex, out var tex);
                // "EYEBROW"/"EYELASHES" sind keine Augen (sonst deckend als Auge gezeichnet -> schwarzer Block)
                string dn = def.Name ?? "";
                bool eye = dn.IndexOf("EYE", StringComparison.OrdinalIgnoreCase) >= 0
                           && dn.IndexOf("LASH", StringComparison.OrdinalIgnoreCase) < 0
                           && dn.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) < 0;
                m.SetFloat("_Mode", eye ? 4f : 2f);
                if ((t = Tex(tex, get)) != null)
                {
                    m.SetTexture("_MainTex", t);
                    // ohne Flags-Angabe: Texturen mit Alpha (Gurte, Accessoires) -> Alpha-Test
                    if (!test && !blend && (t.format == TextureFormat.DXT5 || t.format == TextureFormat.RGBA32)) m.SetFloat("_Cutoff", 0.3f);
                    // Geblendet, aber Textur ohne Alpha (DXT1: Augenbrauen, Wimpern = weiße Härchen auf Schwarz) -> Maske aus der Helligkeit,
                    // Farbe = Haarfarbe (TDU2Avatar setzt sie nach dem Aufbau, sonst dunkelbraun)
                    if (blend && !eye && (t.format == TextureFormat.DXT1 || t.format == TextureFormat.RGB24))
                    {
                        m.SetFloat("_AlphaLum", 1f);
                        m.SetColor("_Color", DefaultBrow);
                        m.SetFloat("_Cutoff", 0.02f);
                    }
                }
                // Brillengläser (TEX = OMBRE2-Verlauf): Farbe/Deckkraft zwischen BCOLOR (unten) und TCOLOR (oben)
                if (def.TryGetParam("BCOLOR", out var bc))
                {
                    m.SetFloat("_Ombre", 1f);
                    m.SetColor("_BColor", new Color(bc.x, bc.y, bc.z, bc.w));
                    m.SetColor("_TColor", Clr(def, "TCOLOR", new Color(0f, 0f, 0f, 1f)));
                }
            }
            return m;
        }
    }
}