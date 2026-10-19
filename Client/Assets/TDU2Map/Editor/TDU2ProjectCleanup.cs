using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>
    /// Ordnerstruktur aufräumen (einmalig): verschiebt die Skripte mit AssetDatabase.MoveAsset – die .meta-Dateien (GUIDs)
    /// wandern mit, Verweise in Szenen/Prefabs bleiben erhalten. Namespaces bleiben unverändert (kein Code muss angepasst werden).
    ///   Runtime/Vehicles   Fahrzeug: Modell, Daten, Physik, Kamera, Sound, Lichter, Effekte, Instrumente, Manager
    ///   Runtime/UI         Flash-Oberflächen: Login, Pausemenü, Optionen, große Karte, Minimap, HUD
    ///   Runtime/Input      Tastenbelegung
    ///   Runtime/Interiors  Händler/Häuser betreten, Laufen in der Ich-Perspektive
    ///   Runtime/World      Karten-Streaming und Welt
    ///   Runtime/Traffic    Verkehr
    /// Mehrfach ausführbar: schon verschobene Dateien werden übersprungen.
    /// </summary>
    public static class TDU2ProjectCleanup
    {
        const string Base = "Assets/TDU2Map/Runtime/";

        static readonly (string from, string to)[] Moves =
        {
            ("Vehicles/TDU2Login.cs", "UI/TDU2Login.cs"),
            ("Vehicles/TDU2PauseMenu.cs", "UI/TDU2PauseMenu.cs"),
            ("Vehicles/TDU2Options.cs", "UI/TDU2Options.cs"),
            ("Vehicles/TDU2BigMap.cs", "UI/TDU2BigMap.cs"),
            ("Vehicles/TDU2Minimap.cs", "UI/TDU2Minimap.cs"),
            ("Vehicles/TDU2VehicleHud.cs", "UI/TDU2VehicleHud.cs"),
            ("Vehicles/TDU2Controls.cs", "Input/TDU2Controls.cs"),
            ("Vehicles/TDU2Interiors.cs", "Interiors/TDU2Interiors.cs"),
            ("World/InteriorScene.cs", "Interiors/InteriorScene.cs"),
            ("World/TDU2FirstPerson.cs", "Interiors/TDU2FirstPerson.cs"),
            ("TDU2MapStreamer.cs", "World/TDU2MapStreamer.cs"),
        };

        [MenuItem("TDU2/Projekt aufräumen (Ordner UI, Input, Interiors, World)")]
        public static void Run()
        {
            var todo = new List<(string, string)>();
            foreach (var (f, t) in Moves)
            {
                string src = Base + f, dst = Base + t;
                if (File.Exists(src) && !File.Exists(dst)) todo.Add((src, dst));
            }
            if (todo.Count == 0) { EditorUtility.DisplayDialog("TDU2", "Alles schon aufgeräumt.", "OK"); return; }
            var msg = "Diese Dateien werden verschoben (GUIDs bleiben, Szenen-Verweise bleiben gültig):\n\n";
            foreach (var (s, d) in todo) msg += s.Substring(Base.Length) + "  →  " + d.Substring(Base.Length) + "\n";
            if (!EditorUtility.DisplayDialog("TDU2 – Projekt aufräumen", msg, "Verschieben", "Abbrechen")) return;

            foreach (var (_, d) in todo) EnsureFolder(Path.GetDirectoryName(d).Replace('\\', '/'));
            var errors = new List<string>();
            foreach (var (s, d) in todo)
            {
                var err = AssetDatabase.MoveAsset(s, d);
                if (!string.IsNullOrEmpty(err)) errors.Add(s + ": " + err);
            }
            AssetDatabase.Refresh();
            if (errors.Count > 0) Debug.LogError("[TDU2] Aufräumen:\n" + string.Join("\n", errors));
            else Debug.Log($"[TDU2] Aufräumen: {todo.Count} Dateien verschoben.");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
