using System.IO;
using TDU2.Map.World;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>Menü "TDU2": Streamer in die Szene setzen und Spielordner prüfen. Es wird nichts konvertiert.</summary>
    public static class TDU2MapMenu
    {
        [MenuItem("TDU2/Map Streamer in Szene erstellen")]
        public static void CreateStreamer()
        {
            var go = new GameObject("TDU2 Map");
            var s = go.AddComponent<TDU2MapStreamer>();
            s.Shaders = new TDU2ShaderSet
            {
                Object = Shader.Find("TDU2/Object"),
                ObjectTransparent = Shader.Find("TDU2/ObjectTransparent"),
                Terrain = Shader.Find("TDU2/Terrain"),
                Grass = Shader.Find("TDU2/Grass"),
                Water = Shader.Find("TDU2/Water"),
                Sprite = Shader.Find("TDU2/Sprite"),
                Foliage = Shader.Find("TDU2/Foliage"),
                SpriteGlow = Shader.Find("TDU2/SpriteGlow"),
            };
            var cam = Camera.main;
            if (cam == null)
            {
                var cgo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = cgo.AddComponent<Camera>();
            }
            cam.farClipPlane = 60000f;
            cam.nearClipPlane = 0.5f;
            if (cam.GetComponent<FlyCamera>() == null) cam.gameObject.AddComponent<FlyCamera>();
            s.Viewer = cam.transform;
            if (Object.FindObjectOfType<Light>() == null)
            {
                var l = new GameObject("Sun").AddComponent<Light>();
                l.type = LightType.Directional;
                l.transform.rotation = Quaternion.Euler(50, -30, 0);
                l.shadows = LightShadows.Soft;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 3000;
            RenderSettings.fogEndDistance = 45000;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);
            QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 400f);
            var vm = go.AddComponent<TDU2.Map.Vehicles.TDU2VehicleManager>();
            vm.Streamer = s;
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "TDU2 Map Streamer");
            CheckGameFolder();
        }

        [MenuItem("TDU2/Fahrzeug-Loader hinzufügen")]
        public static void AddVehicleLoader()
        {
            var s = Object.FindObjectOfType<TDU2MapStreamer>();
            if (s == null) { EditorUtility.DisplayDialog("TDU2", "Zuerst 'TDU2/Map Streamer in Szene erstellen' ausführen.", "OK"); return; }
            var vm = s.GetComponent<TDU2.Map.Vehicles.TDU2VehicleManager>();
            if (vm == null) vm = Undo.AddComponent<TDU2.Map.Vehicles.TDU2VehicleManager>(s.gameObject);
            vm.Streamer = s;
            Selection.activeGameObject = s.gameObject;
            EditorUtility.DisplayDialog("TDU2", "Fahrzeug-Loader hinzugefügt.\n\nIm Play-Modus: F2 = Auto spawnen/aussteigen, F5/F6 = Auto wechseln.", "OK");
        }

        [MenuItem("TDU2/Spielordner prüfen")]
        public static void CheckGameFolder()
        {
            var s = Object.FindObjectOfType<TDU2MapStreamer>();
            string root = TDU2MapStreamer.ResolveGameRoot(s != null ? s.GameRoot : "");
            if (root == null)
            {
                EditorUtility.DisplayDialog("TDU2",
                    "Der Ordner 'Euro' wurde nicht gefunden.\n\nKopiere/verlinke den Spielordner 'Euro' in den Projektordner (neben 'Assets'):\n" +
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..")) + "\\Euro\n\n(Nicht in 'Assets', sonst importiert Unity tausende Dateien.)", "OK");
                return;
            }
            var sb = new System.Text.StringBuilder("Gefunden: " + root + "\n\n");
            foreach (var isl in IslandIndex.ListIslands(root))
            {
                try { sb.AppendLine(IslandIndex.Scan(root, isl).ToString()); }
                catch (System.Exception ex) { sb.AppendLine(isl + ": " + ex.Message); }
            }
            Debug.Log("[TDU2] " + sb);
            EditorUtility.DisplayDialog("TDU2", sb.ToString(), "OK");
        }
    }
}
