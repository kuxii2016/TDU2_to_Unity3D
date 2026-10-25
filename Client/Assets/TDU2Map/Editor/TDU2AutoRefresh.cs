using System.IO;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>
    /// Lädt geänderte Skripte/Shader unter Assets/TDU2Map automatisch neu (auch wenn "Auto Refresh" in den
    /// Preferences aus ist). Dateien, die von außen geschrieben werden, lösen so sofort ein Neukompilieren aus.
    /// Im Play-Modus wird bis zum Beenden gewartet.
    /// </summary>
    [InitializeOnLoad]
    static class TDU2AutoRefresh
    {
        static FileSystemWatcher _watcher;
        static volatile bool _dirty;
        static double _dirtySince;

        static TDU2AutoRefresh()
        {
            string dir = Path.Combine(Application.dataPath, "TDU2Map");
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (s, e) =>
            {
                string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                if (ext == ".cs" || ext == ".shader" || ext == ".cginc" || ext == ".hlsl") _dirty = true;
            };
            _watcher.Changed += h; _watcher.Created += h; _watcher.Renamed += (s, e) => h(s, e);
            _watcher.EnableRaisingEvents = true;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!_dirty) { _dirtySince = 0; return; }
            if (_dirtySince == 0) { _dirtySince = EditorApplication.timeSinceStartup; return; }
            // kurz warten, bis alle Dateien eines Pakets geschrieben sind; im Play-Modus bis zum Ende warten
            if (EditorApplication.timeSinceStartup - _dirtySince < 1.5 || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            _dirty = false; _dirtySince = 0;
            DebugManager.Debug("Script Changes dedected, Compile new...");
            AssetDatabase.Refresh();
        }
    }
}
