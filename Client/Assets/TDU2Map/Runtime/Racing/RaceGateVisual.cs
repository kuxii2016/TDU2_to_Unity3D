using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map.Racing
{
    public enum RaceGateState { Pending, Next, Passed }

    /// <summary>
    /// Visuelles Tor einer Prüfung: Mesh + Material "TDU2/Checkpoint", Zustandsfarbe
    /// (nächstes/passiert/offen) über MaterialPropertyBlock (__Instance_Color) und UV-Puls (__UVA_0).
    /// </summary>
    public sealed class RaceGateVisual : MonoBehaviour
    {
        public int Index;
        public RaceCheckpoint Checkpoint;

        MeshRenderer _renderer;
        Renderer[] _renderers;
        MaterialPropertyBlock _mpb;
        /// <summary>true = Original-Modell (CheckPoint-Actor aus worldactors.bnk) statt des Ersatz-Quads.</summary>
        public bool IsModel { get; private set; }

        static Material _shared;
        static Mesh _quad;
        static Texture2D _tex;

        public static RaceGateVisual Create(Transform parent, Material material, RaceCheckpoint cp, Vector3 worldPos, Quaternion worldRot)
        {
            var go = new GameObject($"Gate_{cp.Index}");
            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;
            go.transform.rotation = worldRot;
            go.transform.localScale = new Vector3(Mathf.Max(2f, cp.Width), 6f, 1f);

            var v = go.AddComponent<RaceGateVisual>();
            v.Index = cp.Index;
            v.Checkpoint = cp;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Quad();
            v._renderer = go.AddComponent<MeshRenderer>();
            v._renderer.sharedMaterial = material;
            v._renderer.shadowCastingMode = ShadowCastingMode.Off;
            v._renderer.receiveShadows = false;
            v._mpb = new MaterialPropertyBlock();
            v.SetState(cp.IsStart || cp.IsFinish ? RaceGateState.Next : RaceGateState.Pending);
            return v;
        }

        /// <summary>
        /// Ersatz-Tor ohne Originalmodell: zwei leuchtende Pfosten an den Torenden und eine Linie am Boden
        /// (keine Wand mehr – die Sicht bleibt frei). Lokaler Raum: +Z = Fahrtrichtung, Ursprung = Tormitte am Boden.
        /// </summary>
        public static RaceGateVisual CreatePosts(Transform parent, Material material, RaceCheckpoint cp, float width)
        {
            var go = new GameObject($"Gate_{cp.Index}");
            go.transform.SetParent(parent, false);
            float hw = Mathf.Max(1f, width * 0.5f);
            foreach (var side in new[] { -1f, 1f })
                for (int k = 0; k < 2; k++)   // je Pfosten zwei gekreuzte Flächen
                {
                    var post = new GameObject("Post");
                    post.transform.SetParent(go.transform, false);
                    post.transform.localPosition = new Vector3(side * hw, 0f, 0f);
                    post.transform.localRotation = Quaternion.Euler(0f, k * 90f, 0f);
                    post.transform.localScale = new Vector3(0.7f, PostHeight, 1f);
                    post.AddComponent<MeshFilter>().sharedMesh = Quad();
                    post.AddComponent<MeshRenderer>().sharedMaterial = material;
                }
            var line = new GameObject("Line");
            line.transform.SetParent(go.transform, false);
            line.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            line.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            line.transform.localScale = new Vector3(hw * 2f, 0.6f, 1f);
            line.AddComponent<MeshFilter>().sharedMesh = Quad();
            line.AddComponent<MeshRenderer>().sharedMaterial = material;
            var v = Attach(go, cp);
            v.IsModel = false;
            v.SetState(cp.IsStart || cp.IsFinish ? RaceGateState.Next : RaceGateState.Pending);
            return v;
        }

        public static float PostHeight = 3.5f;

        GameObject _on, _off;
        RaceGateState _state;
        float _phase;

        /// <summary>Original-Tor mit An-/Aus-Variante (Start_line/_Off, Finish_line/_Off). off darf null sein.</summary>
        public static RaceGateVisual AttachOnOff(GameObject go, RaceCheckpoint cp, GameObject on, GameObject off)
        {
            var v = Attach(go, cp);
            v._on = on; v._off = off;
            v._phase = cp.Index * 0.37f;
            v.SetState(cp.IsStart || cp.IsFinish ? RaceGateState.Next : RaceGateState.Pending);
            return v;
        }

        /// <summary>Puls wie die UV-Animation des Originals (__UVA_0) – nur Helligkeit, solange die .UVA nicht ausgewertet wird.</summary>
        void Update()
        {
            if (!IsModel || _mpb == null || _renderers == null || _state == RaceGateState.Passed) return;
            float a = _state == RaceGateState.Next ? 0.75f + 0.25f * Mathf.Sin((Time.time + _phase) * 5f) : 0.45f;
            _mpb.SetColor("_Color", new Color(1f, 1f, 1f, a));
            foreach (var r in _renderers) if (r != null) r.SetPropertyBlock(_mpb);
        }

        /// <summary>Original-Tormodell (bereits gebaut unter go) als Checkpoint-Optik verwenden.</summary>
        public static RaceGateVisual Attach(GameObject go, RaceCheckpoint cp)
        {
            var v = go.AddComponent<RaceGateVisual>();
            v.Index = cp.Index;
            v.Checkpoint = cp;
            v.IsModel = true;
            v._renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var r in v._renderers) { r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false; }
            v._mpb = new MaterialPropertyBlock();
            v.SetState(cp.IsStart || cp.IsFinish ? RaceGateState.Next : RaceGateState.Pending);
            return v;
        }

        public void SetState(RaceGateState state)
        {
            _state = state;
            if (_on != null || _off != null)
            {
                bool showOn = state == RaceGateState.Next || _off == null;
                if (_on != null) _on.SetActive(state != RaceGateState.Passed && showOn);
                if (_off != null) _off.SetActive(state != RaceGateState.Passed && !showOn);
            }
            Color c;
            switch (state)
            {
                case RaceGateState.Passed: c = new Color(0.25f, 1f, 0.35f, 0f); break;      // passiert: ausgeblendet
                case RaceGateState.Next: c = new Color(0.35f, 1f, 0.55f, 0.85f); break;     // nächstes Tor: grün
                default: c = new Color(0.45f, 0.75f, 1f, 0.3f); break;                       // später: blass blau
            }
            if (IsModel)
            {
                // Original-Textur behalten, nur abtönen (__Instance_Color); passierte Tore fast ausblenden
                c = state == RaceGateState.Passed ? new Color(0.3f, 1f, 0.4f, 0.15f) : state == RaceGateState.Next ? new Color(1f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 0.45f);
            }
            _mpb.SetColor("_Color", c);
            if (_renderer != null) _renderer.SetPropertyBlock(_mpb);
            if (_renderers != null) foreach (var r in _renderers) if (r != null) r.SetPropertyBlock(_mpb);
        }

        /// <summary>Gemeinsames Material (Cartoon-Ersatz für die echten pack_cp-Texturen).</summary>
        public static Material SharedMaterial()
        {
            if (_shared != null) return _shared;
            var sh = Shader.Find("TDU2/Checkpoint");
            _shared = new Material(sh);
            _shared.mainTexture = Texture();
            _shared.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _shared.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            _shared.SetFloat("_ZWrite", 0f);
            _shared.SetFloat("_Cutoff", 0f);
            _shared.renderQueue = 3000;
            return _shared;
        }

        /// <summary>UV-Puls (__UVA_0): Textur läuft vertikal, die Breite pulsiert leicht.</summary>
        public static void Pulse(Material mat, float time)
        {
            if (mat == null) return;
            // leichtes Atmen der Helligkeit statt Textur-Scroll (Textur ist geklemmt)
            float puls = 0.85f + 0.15f * Mathf.Sin(time * 4f);
            mat.SetVector("_MainTex_ST", new Vector4(1f, 1f, 0f, 0f));
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(puls, puls, puls, 1f));
        }

        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            var m = new Mesh { name = "RaceGate" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f),
                new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };   // beidseitig
            m.RecalculateNormals();
            _quad = m;
            return m;
        }

        static Texture2D Texture()
        {
            if (_tex != null) return _tex;
            const int w = 32, h = 64;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                float up = Mathf.Pow(1f - v, 1.3f);                                  // unten hell, nach oben auslaufend
                float band = 0.75f + 0.25f * Mathf.Sin(v * Mathf.PI * 8f);            // leichte Pulsbänder
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)(w - 1);
                    float side = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(u * 2f - 1f)), 1.5f);   // weiche Kanten
                    px[y * w + x] = new Color(1f, 1f, 1f, side * up * band);
                }
            }
            t.SetPixels(px);
            t.Apply();
            _tex = t;
            return _tex;
        }
    }
}
