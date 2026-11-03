using TDU2.Traffic;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>Traffic: menu "TDU2/Create Traffic Manager In Scene" and live view in the Inspector (play mode).</summary>
    [CustomEditor(typeof(TDU2Traffic))]
    public sealed class TDU2TrafficEditor : Editor
    {
        [MenuItem("TDU2/Create Traffic Manager In Scene")]
        public static void Create()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = Object.FindFirstObjectByType<TDU2Traffic>();
#else
            var existing = Object.FindObjectOfType<TDU2Traffic>();
#endif
            if (existing != null) { Selection.activeGameObject = existing.gameObject; EditorGUIUtility.PingObject(existing); return; }
            var go = new GameObject("TDU2 Traffic Manager");
            go.AddComponent<TDU2Traffic>();
            Undo.RegisterCreatedObjectUndo(go, "TDU2 Traffic Manager");
            Selection.activeGameObject = go;
        }

        bool _showCars = true;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Traffic from <Island>_ROAD_MAP/_ROAD_DETAIL.IA, cars from islands/<island>/vehicules/traffic, mix from aiconfig.cpr. " +
                                    "In multiplayer every player simulates the cars near them; the others receive them synchronised (TDU2Server relays).", MessageType.Info);
            DrawDefaultInspector();
            var t = (TDU2Traffic)target;
            if (!Application.isPlaying) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Status", t.Status);
                EditorGUILayout.IntField("Cars", t.CarCount);
                EditorGUILayout.IntField("Owned by this player", t.OwnedCount);
            }
            _showCars = EditorGUILayout.Foldout(_showCars, "Cars", true);
            if (_showCars)
                foreach (var c in t.Info())
                    EditorGUILayout.LabelField($"   {(c.mine ? "●" : "○")} {c.id & 0xFFFF:X4} {c.model}", $"{c.mode}  {c.kmh:0} km/h  {c.dist:0} m  owner #{c.owner}");
            Repaint();
        }
    }
}
