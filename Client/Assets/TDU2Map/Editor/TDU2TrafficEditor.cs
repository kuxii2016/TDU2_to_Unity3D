using TDU2.Map.Traffic;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>Verkehr: Menü "TDU2/Traffic Manager in Szene erstellen" und Live-Anzeige im Inspector (Play-Modus).</summary>
    [CustomEditor(typeof(TDU2Traffic))]
    public sealed class TDU2TrafficEditor : Editor
    {
        [MenuItem("TDU2/Traffic Manager in Szene erstellen")]
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
            EditorGUILayout.HelpBox("Verkehr aus <Insel>_ROAD_MAP/_ROAD_DETAIL.IA, Autos aus islands/<insel>/vehicules/traffic, Mischung aus aiconfig.cpr. " +
                                    "Im Mehrspieler simuliert jeder Spieler die Autos in seiner Nähe; die anderen sehen sie synchron (TDU2Server leitet weiter).", MessageType.Info);
            DrawDefaultInspector();
            var t = (TDU2Traffic)target;
            if (!Application.isPlaying) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Status", t.Status);
                EditorGUILayout.IntField("Autos", t.CarCount);
                EditorGUILayout.IntField("davon eigene", t.OwnedCount);
            }
            _showCars = EditorGUILayout.Foldout(_showCars, "Autos", true);
            if (_showCars)
                foreach (var c in t.Info())
                    EditorGUILayout.LabelField($"   {(c.mine ? "●" : "○")} {c.id & 0xFFFF:X4} {c.model}", $"{c.mode}  {c.kmh:0} km/h  {c.dist:0} m  Besitzer #{c.owner}");
            Repaint();
        }
    }
}
