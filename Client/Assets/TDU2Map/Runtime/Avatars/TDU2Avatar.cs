using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDU2.Formats;
using TDU2.IO;
using UnityEngine;

namespace TDU2.Avatars
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

        /// <summary>
        /// Gesicht wie das Original (Material-Überschreibungen je Avatar, td2.exe 0x67CB20 / 0x67CD60): "EYES" UVOFF = Zelle EyeID%25 des
        /// 5x5-Iris-Atlas (u = (n/5)/5, v = (n%5)/5), "EYEBROW" UVOFF = Zelle EyebrowsID des 2x4-Brauen-Atlas (u = (k/4)/2, v = (k%4)/4) und
        /// COLOR = EyebrowsColor. Ohne .avt-Angaben (eigener Spieler-Avatar): Brauen in der Haarfarbe der Frisur; Wimpern behalten COLOR.
        /// </summary>
        void ApplyBrowColor()
        {
            var col = AvatarMaterials.DefaultBrow;
            if (Config != null && Config.HasBrowColor) col = Config.BrowColor;
            else foreach (var p in _parts) if (p != null && p.HasHairColor) { col = Color.Lerp(p.HairPrim, p.HairScd, 0.25f) * 0.85f; col.a = 1f; break; }
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string n = m.name ?? "";
                    bool brow = n.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (brow && m.GetFloat("_Mode") > 1.5f && m.GetFloat("_Mode") < 2.5f) m.SetColor("_Color", col);
                    if (!m.HasProperty("_UVOff") || Config == null) continue;
                    if (brow && Config.BrowId >= 0)
                    {
                        int k = Config.BrowId % 8;
                        m.SetVector("_UVOff", new Vector4((k / 4) * 0.5f, (k % 4) * 0.25f, 0f, 0f));
                    }
                    else if (n == "AVT EYES" && Config.EyeId >= 0)
                    {
                        int e = Config.EyeId % 25;
                        m.SetVector("_UVOff", new Vector4((e / 5) * 0.2f, (e % 5) * 0.2f, 0f, 0f));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Diagnose (Sitzungs-Log)
        /// <summary>Alle Materialien der Teile mit Shader, Modus und Texturen ins Sitzungs-Log (einmal je Avatar).</summary>
        public void DiagMaterials(string who)
        {
            if (!Ready) return;
            var sb = new System.Text.StringBuilder();
            sb.Append(who).Append(" sex=").Append(Config?.Sex).Append(" ethnic=").Append(Config?.Ethnic).Append(" eye=").Append(Config?.EyeId)
              .Append(" brow=").Append(Config?.BrowId).Append(" browColor=").Append(Config != null && Config.HasBrowColor ? Config.BrowColor.ToString() : "-");
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                sb.Append("\n    [").Append(r.name).Append("] layer=").Append(r.gameObject.layer).Append(" enabled=").Append(r.enabled);
                var mats = r.sharedMaterials;
                for (int k = 0; k < mats.Length; k++)
                {
                    var m = mats[k];
                    if (m == null) { sb.Append("\n      #").Append(k).Append(" null"); continue; }
                    var t = m.mainTexture as Texture2D;
                    sb.Append("\n      #").Append(k).Append(' ').Append(m.name).Append(" shader=").Append(m.shader != null ? m.shader.name : "?")
                      .Append(" q=").Append(m.renderQueue);
                    foreach (var p in new[] { "_Mode", "_AlphaLum", "_Cutoff", "_Cull" }) if (m.HasProperty(p)) sb.Append(' ').Append(p).Append('=').Append(m.GetFloat(p).ToString("0.###"));
                    if (m.HasProperty("_Color")) sb.Append(" _Color=").Append(m.GetColor("_Color"));
                    if (m.HasProperty("_UVOff")) sb.Append(" _UVOff=").Append(m.GetVector("_UVOff"));
                    sb.Append(" tex=").Append(t != null ? $"{t.name} {t.format} {t.width}x{t.height} mips={t.mipmapCount}" : "-");
                }
            }
            TDU2Diag.Log("AVATAR", sb.ToString());
        }

        /// <summary>
        /// Gesichtsknochen relativ zum Kopf, aktuell gegen Ruhe (aus den inversen Bind-Matrizen): große Abweichungen = Lider/Augen/Kiefer
        /// stehen nicht dort, wo die Haut gebunden ist (Haut und innere Teile verschieben sich gegeneinander).
        /// </summary>
        public void DiagFace(string who)
        {
            if (!Ready || Skeleton == null || Bones == null) return;
            int head = Bone("HEAD");
            if (head < 0 || Bones[head] == null) return;
            var headRestInv = Skeleton.InvBind[head];
            var headNowInv = Bones[head].worldToLocalMatrix;
            var sb = new System.Text.StringBuilder(who);
            foreach (var n in new[] { "Head_Facial", "L_Eye_All", "L_EyeBall", "L_UpperEyeLid", "L_LowerEyeLid", "L_InnerBrow", "L_OuterBrow", "R_EyeBall", "R_UpperEyeLid", "M_Jaw", "L_LowerLip" })
            {
                int i = Bone(n);
                if (i < 0 || Bones[i] == null) { sb.Append("\n    ").Append(n).Append(" fehlt"); continue; }
                Vector3 rest = headRestInv.MultiplyPoint3x4(Skeleton.InvBind[i].inverse.MultiplyPoint3x4(Vector3.zero));
                Vector3 now = headNowInv.MultiplyPoint3x4(Bones[i].position);
                float scale = Bones[head].lossyScale.magnitude / Mathf.Sqrt(3f);
                sb.Append("\n    ").Append(n).Append(" rest=").Append(TDU2Diag.F(rest)).Append(" jetzt=").Append(TDU2Diag.F(now))
                  .Append(" Δ=").Append(((now - rest).magnitude * 100f).ToString("0.00")).Append(" cm (Kopfskala ").Append(scale.ToString("0.###")).Append(")")
                  .Append(" lokal=").Append(TDU2Diag.F(Bones[i].localPosition)).Append(" ").Append(TDU2Diag.F(Bones[i].localRotation)).Append(" s=").Append(TDU2Diag.F(Bones[i].localScale));
            }
            TDU2Diag.Log("FACE", sb.ToString());
        }

        static readonly HashSet<string> _snapDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gesichtsbild für die Fehlersuche: unsichtbare Hilfskamera rendert den Kopf (frontal, schräg, nah an den Augen) in eine
        /// Textur, gespeichert als Docs/Logs/face_&lt;key&gt;.png (je Figur einmal, höchstens 16 je Sitzung). Zeigt, wie Augenbrauen,
        /// Wimpern und Augen tatsächlich aussehen, ohne Screenshot.
        /// </summary>
        public void DiagSnapshot(string key, string who)
        {
            if (!TDU2Diag.Enabled || !Ready || Bones == null || string.IsNullOrEmpty(key) || _snapDone.Count >= 16 || _snapDone.Contains(key)) return;
            int head = Bone("HEAD"), le = Bone("L_EyeBall"), re = Bone("R_EyeBall");
            if (head < 0 || le < 0 || re < 0 || Bones[head] == null || Bones[le] == null || Bones[re] == null) return;
            _snapDone.Add(key);
            Vector3 eyes = (Bones[le].position + Bones[re].position) * 0.5f;
            Vector3 fwd = eyes - Bones[head].position; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f) fwd = transform.forward;
            fwd.Normalize();
            const int S = 512;
            var go = new GameObject("TDU2 DiagFaceCam");
            RenderTexture rt = null; Texture2D tex = null;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 30f; cam.fieldOfView = 25f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.3f, 0.3f, 0.35f, 1f);
                rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                var dirs = new[] { fwd, Quaternion.AngleAxis(40f, Vector3.up) * fwd, fwd };
                var dists = new[] { 0.5f, 0.5f, 0.17f };
                tex = new Texture2D(S * dirs.Length, S, TextureFormat.RGB24, false);
                for (int i = 0; i < dirs.Length; i++)
                {
                    cam.transform.position = eyes + dirs[i] * dists[i] + Vector3.up * 0.01f;
                    cam.transform.rotation = Quaternion.LookRotation(eyes - cam.transform.position, Vector3.up);
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, S, S), S * i, 0);
                    RenderTexture.active = prev;
                }
                tex.Apply();
                var safe = new System.Text.StringBuilder();
                foreach (var ch in key) safe.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
                string path = TDU2Diag.FilePath("face_" + safe + ".png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                TDU2Diag.Log("AVATAR", $"Gesichtsbild {who}: {path}");
                cam.targetTexture = null;
            }
            catch (Exception ex) { TDU2Diag.Log("AVATAR", "Gesichtsbild " + who + " fehlgeschlagen: " + ex.Message); }
            finally
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                if (tex != null) Destroy(tex);
                Destroy(go);
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
                b.localScale = _ls[i];
            }
        }

        /// <summary>Lokale Lage eines Knochens in der Arbeits-Pose setzen bzw. mit Gewicht <paramref name="w"/> überblenden (FaceFX).</summary>
        public void BlendLocal(int i, Vector3 pos, Quaternion rot, Vector3 scale, float w)
        {
            if (Skeleton == null || _lp == null || i < 0 || i >= Skeleton.Count || w <= 0f) return;
            if (w >= 1f) { _lp[i] = pos; _lr[i] = rot; _ls[i] = scale; return; }
            _lp[i] = Vector3.Lerp(_lp[i], pos, w);
            _lr[i] = Quaternion.Slerp(_lr[i], rot, w);
            _ls[i] = Vector3.Lerp(_ls[i], scale, w);
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
            // UVOFF: Atlas-Zelle (Iris, Augenbrauen); TDU2Avatar überschreibt sie je Figur
            if (def.TryGetParam("UVOFF", out var uo)) m.SetVector("_UVOff", new Vector4(uo.x, uo.y, 0f, 0f));
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
                // Augäpfel = Shader-Set TD_Eye: deckend (nicht geblendet) und OHNE COLOR-Parameter (EYES, OCC_EYES_B). Alles mit COLOR
                // ist TD_Avatar (Farbe = TEX x COLOR, Deckkraft = TEX.a) – auch "Eye_01" (Lidschatten-Make-up an w_pn_a_jewels_tess,
                // geblendet, DXT5): als Auge gezeichnet wurde daraus ein deckender Fleck um die Augen.
                bool eye = dn.IndexOf("EYE", StringComparison.OrdinalIgnoreCase) >= 0
                           && dn.IndexOf("LASH", StringComparison.OrdinalIgnoreCase) < 0
                           && dn.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) < 0
                           && !blend && !def.TryGetParam("COLOR", out _);
                m.SetFloat("_Mode", eye ? 4f : 2f);
                // TD_Avatar des Kopfes: Farbe = TEX x COLOR (Zähne 0.83, Wimpern/Brauen schwarz); Kleidung/Accessoires unverändert
                bool face = dn.IndexOf("LASH", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("BROW", StringComparison.OrdinalIgnoreCase) >= 0
                            || dn.IndexOf("TEETH", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("TONGUE", StringComparison.OrdinalIgnoreCase) >= 0;
                if (face && def.TryGetParam("COLOR", out var tc)) m.SetColor("_Color", new Color(tc.x, tc.y, tc.z, 1f));
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
                        // Original TD_Avatar: Farbe = COLOR (Wimpern, Brauen: schwarz; Brauen bekommen EyebrowsColor)
                        m.SetColor("_Color", def.TryGetParam("COLOR", out var cc) ? new Color(cc.x, cc.y, cc.z, 1f) : DefaultBrow);
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