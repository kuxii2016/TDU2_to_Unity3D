using System.Collections.Generic;
using TDU2.Formats;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>Ein POI (Spot) als GameObject – sichtbar als Marker, mit Infos im Inspector.</summary>
    public sealed class PoiMarker : MonoBehaviour
    {
        public string Code;
        public string Category;
        public int SpotIndex;
        public Vector3 GamePosition;
        public Vector3 SecondPosition;
        public string AreaSector;
        public int TypeA, TypeB;
        public string KeyHex;

        void OnDrawGizmos()
        {
            if (DebugManager.Instance == null || !DebugManager.Instance.isPOIMarker) return;
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
            Gizmos.DrawSphere(transform.position, 4f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 40f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * 45f, $"{Code}\n{Category}");
#endif
        }
    }

    /// <summary>Erzeugt POI-Marker aus spots.bin und zeigt zur Laufzeit Labels per OnGUI.</summary>
    public sealed class PoiMarkers : MonoBehaviour
    {
        public float LabelDistance = 3000f;
        public bool ShowRuntimeLabels = false;
        public bool ShowMarkerMeshes = false;
        public Color MarkerColor = new Color(1f, 0.55f, 0.1f);
        readonly List<PoiMarker> _markers = new List<PoiMarker>();
        Material _mat;
        GUIStyle _style;

        public void Start()
        {
            if (DebugManager.Instance == null || !DebugManager.Instance.isPOIMarker) return;
            ShowRuntimeLabels = DebugManager.Instance.isPOIMarker;
            ShowMarkerMeshes = DebugManager.Instance.isPOIMarker;
        }
        public void Build(SpotsFile spots)
        {
            foreach (var s in spots.Spots)
            {
                var go = new GameObject($"POI_{s.Index:00}_{s.Code}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = s.Position;
                var m = go.AddComponent<PoiMarker>();
                m.Code = s.Code;
                m.Category = s.Category;
                m.SpotIndex = s.Index;
                m.GamePosition = s.Position;
                m.SecondPosition = s.Position2;
                m.AreaSector = $"area {s.AreaX}-{s.AreaZ}, sector {s.SectorX}-{s.SectorZ}";
                m.TypeA = s.TypeA; m.TypeB = s.TypeB;
                m.KeyHex = TDU2.IO.Bin.KeyHex(s.Key);
                if (ShowMarkerMeshes) AddPin(go);
                _markers.Add(m);
            }
        }

        void AddPin(GameObject parent)
        {
            if (_mat == null)
            {
                var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
                _mat = new Material(sh) { color = MarkerColor, name = "POI" };
                if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", MarkerColor);
            }
            var pin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pin.name = "Pin";
            var col = pin.GetComponent<Collider>();
            if (col != null) UnityUtil.SafeDestroy(col);
            pin.transform.SetParent(parent.transform, false);
            pin.transform.localPosition = new Vector3(0, 15f, 0);
            pin.transform.localScale = new Vector3(2f, 15f, 2f);
            pin.GetComponent<Renderer>().sharedMaterial = _mat;
        }

        void OnGUI()
        {
            if (DebugManager.Instance == null || !DebugManager.Instance.isPOIMarker) return;
            if (!ShowRuntimeLabels) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };
            foreach (var m in _markers)
            {
                if (m == null) continue;
                var wp = m.transform.position + Vector3.up * 35f;
                float d = Vector3.Distance(cam.transform.position, wp);
                if (d > LabelDistance) continue;
                var sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0) continue;
                var r = new Rect(sp.x - 80, Screen.height - sp.y - 20, 160, 40);
                GUI.Label(r, $"{m.Code}\n{m.Category} ({d:0} m)", _style);
            }
        }

        void OnDestroy()
        {
            if (_mat != null) UnityUtil.SafeDestroy(_mat);
        }
    }
}
