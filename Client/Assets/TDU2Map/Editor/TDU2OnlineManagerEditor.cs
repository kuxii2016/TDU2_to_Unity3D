using System;
using System.Globalization;
using TDU2.Map.Online;
using UnityEditor;
using UnityEngine;

namespace TDU2.Map.EditorTools
{
    /// <summary>
    /// Online Manager (TDU2GameData): Menü "TDU2/Online Manager in Szene erstellen" und ein Inspector, der im Play-Modus
    /// die Live-Daten zeigt (Anmeldung, Online-Status, eigener Spielstand, Mitspieler, Zuletzt gesehen, Mitspieler-Autos).
    /// </summary>
    [CustomEditor(typeof(TDU2GameData))]
    public sealed class TDU2OnlineManagerEditor : Editor
    {
        [MenuItem("TDU2/Online Manager in Szene erstellen")]
        public static void Create()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = UnityEngine.Object.FindFirstObjectByType<TDU2GameData>();
#else
            var existing = UnityEngine.Object.FindObjectOfType<TDU2GameData>();
#endif
            if (existing != null) { Selection.activeGameObject = existing.gameObject; EditorGUIUtility.PingObject(existing); return; }
            var go = new GameObject("TDU2 Online Manager");
            go.AddComponent<TDU2GameData>();
            Undo.RegisterCreatedObjectUndo(go, "TDU2 Online Manager");
            Selection.activeGameObject = go;
        }

        bool _showPlayers = true, _showRecent, _showOwned, _showRemote, _showChat = true;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Server-Adresse für Tests mit Freunden: IP des Rechners mit TDU2Server (TCP 7777 und UDP 7778 freigeben). " +
                                    "Alle Flash-Filme lesen Online-Status, Spielerzahl, Name, Geld und Auto aus diesem Manager.", MessageType.Info);
            DrawDefaultInspector();
            var gd = (TDU2GameData)target;
            if (!Application.isPlaying) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Angemeldet als", TDU2Session.LoggedIn ? TDU2Session.Nick : "(nicht angemeldet)");
                EditorGUILayout.Toggle("Online", gd.Online);
                EditorGUILayout.IntField("Spieler online", gd.PlayerCount);
                EditorGUILayout.IntField("Spieler-Nr. (UDP)", TDU2Session.PlayerId);
                EditorGUILayout.TextField("Server", TDU2Session.Client != null ? TDU2Session.Client.Host + ":" + TDU2Session.Client.Port + (TDU2Session.Client.Connected ? "  (verbunden)" : "") : "-");
                EditorGUILayout.TextField("Geld", gd.Money.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " $");
                EditorGUILayout.TextField("Aktuelles Auto", gd.CarName);
                var sv = TDU2Session.Save;
                if (sv != null)
                {
                    EditorGUILayout.Toggle("Startauto gewählt", sv.starterChosen);
                    EditorGUILayout.TextField("Position (Spiel)", sv.hasPosition ? $"{sv.px:0} / {sv.py:0} / {sv.pz:0}  ({sv.island})" : "-");
                    EditorGUILayout.TextField("Gespeichert", sv.savedAt);
                }
            }
            if (TDU2Session.LoggedIn && GUILayout.Button("Jetzt speichern")) TDU2Session.SaveAsync();

            var save = TDU2Session.Save;
            if (save != null)
            {
                _showOwned = EditorGUILayout.Foldout(_showOwned, $"Eigene Autos ({save.ownedCars.Count})", true);
                if (_showOwned) foreach (var c in save.ownedCars) EditorGUILayout.LabelField("   " + c);
            }

            _showPlayers = EditorGUILayout.Foldout(_showPlayers, $"Mitspieler online ({gd.Players.Count})", true);
            if (_showPlayers)
                foreach (var p in gd.Players.Values)
                    EditorGUILayout.LabelField($"   #{p.Id}  {p.Nick}", $"{(p.InCar ? p.CarName : "kein Auto")}  {(p.HasPos ? $"@ {p.GamePos.x:0}/{p.GamePos.z:0}" : "")}  {p.Money:N0} $");

            _showRecent = EditorGUILayout.Foldout(_showRecent, $"Zuletzt gesehen ({gd.Recent.Count})", true);
            if (_showRecent)
                foreach (var p in gd.Recent)
                    EditorGUILayout.LabelField("   " + p.Nick, p.LastSeen == default ? "" : p.LastSeen.ToString("dd.MM. HH:mm"));

#if UNITY_2023_1_OR_NEWER
            var mp = UnityEngine.Object.FindFirstObjectByType<TDU2Multiplayer>();
#else
            var mp = UnityEngine.Object.FindObjectOfType<TDU2Multiplayer>();
#endif
            if (mp != null)
            {
                _showRemote = EditorGUILayout.Foldout(_showRemote, "Mitspieler-Autos (UDP)", true);
                if (_showRemote)
                    foreach (var (id, loaded, car, age) in mp.RemoteInfo())
                        EditorGUILayout.LabelField($"   #{id}  {car}", $"{(loaded ? "sichtbar" : "lädt")}  letztes Paket vor {age:0.0}s");
            }
            _showChat = EditorGUILayout.Foldout(_showChat, $"Chat ({gd.ChatLog.Count})", true);
            if (_showChat)
                for (int i = Mathf.Max(0, gd.ChatLog.Count - 12); i < gd.ChatLog.Count; i++)
                {
                    var c = gd.ChatLog[i];
                    EditorGUILayout.LabelField($"   {c.time:HH:mm}  {c.nick}", c.text);
                }
            Repaint();
        }
    }
}
