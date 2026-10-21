using System;
using System.Collections.Generic;
using TDU2.Map.Avatars;
using TDU2.Map.IO;
using TDU2.Map.World;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Figuren eines Interieurs (Entity__PNJ: Verkäuferin, Friseur …) mit ihrem DBPNJ-Aussehen (ConfigName = DBPNJ._configid).
    /// Platz und Pose aus der Cinematic-Szene, deren Darsteller die Figur bindet (__Hc) – bevorzugt die "LIFE"-Szene (Laufmodus),
    /// sonst die erste; ohne Szene an der Entität mit co_stand. Läden können eine Figur umstellen (<see cref="SetPose"/>, z.B.
    /// Verkäuferin hinter der Theke = M_LOOP). Dazu die .sce der Bank (<see cref="Cines"/>) und ihre Clips (<see cref="Clip"/>).
    /// </summary>
    public sealed class TDU2InteriorNpcs : MonoBehaviour
    {
        public sealed class Npc
        {
            public string ConfigName, Name;
            public ulong Hc;
            public Transform Root;
            public TDU2Avatar Avatar;
            public AnmClip Clip;
            public float T;
        }

        public readonly List<Npc> Npcs = new List<Npc>();
        /// <summary>Figuren anzeigen (Entwicklermenü).</summary>
        public static bool Show = true;
        public Dictionary<string, CineScene> Cines { get; private set; } = new Dictionary<string, CineScene>(StringComparer.OrdinalIgnoreCase);
        public BnkArchive Bank { get; private set; }
        public Transform Scene { get; private set; }
        readonly Dictionary<string, AnmClip> _clips = new Dictionary<string, AnmClip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clip der Interieur-Bank (sonst avatarsdriver.bnk).</summary>
        public AnmClip Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_clips.TryGetValue(name, out var c)) return c;
            c = CineScene.LoadClip(Bank, name) ?? AvatarLibrary.Clip(name);
            _clips[name] = c;
            return c;
        }

        /// <summary>Darsteller, der die Figur spielt, in der ersten Szene, deren Name <paramref name="sceneContains"/> enthält.</summary>
        public CineScene.Obj FindActor(ulong hc, string sceneContains)
        {
            foreach (var kv in Cines)
            {
                if (sceneContains != null && kv.Key.IndexOf(sceneContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var o in kv.Value.Objects) if (!o.IsCamera && o.Bind == hc && o.HasPosition) return o;
            }
            return null;
        }

        /// <summary>Kamera-/Darsteller-Objekt mit diesem Namen in irgendeiner Szene der Bank.</summary>
        public CineScene.Obj FindObject(string name, string sceneContains = null)
        {
            foreach (var kv in Cines)
            {
                if (sceneContains != null && kv.Key.IndexOf(sceneContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var o = kv.Value.Find(name);
                if (o != null) return o;
            }
            return null;
        }

        public void Build(Transform scene, InteriorScene data, string bankPath)
        {
            Clear();
            Scene = scene;
            try { Bank = !string.IsNullOrEmpty(bankPath) ? BnkArchive.Open(bankPath) : null; } catch (Exception) { Bank = null; }
            Cines = CineScene.LoadAll(Bank);
            if (data == null || scene == null || !AvatarLibrary.Ready) return;
            foreach (var e in data.Npcs)
            {
                string cfgName = e.Node != null ? e.Node.S("ConfigName", "") : "";
                var cfg = AvatarLibrary.PnjConfig(cfgName);
                if (cfg == null) { Debug.Log("[TDU2] Interieur-Figur ohne DBPNJ: " + e.Name + " (" + cfgName + ")"); continue; }
                // Nur Figuren mit einer Laufmodus-Szene (M_LIFE) stehen ständig im Raum; die übrigen treten nur in Cinematics
                // auf (z.B. die Maklerin im Haus: M_REALTOR beim Kauf)
                var life = FindActor(e.Hc, "LIFE");
                if (life == null) { Debug.Log("[TDU2] Interieur-Figur " + e.Name + " (" + cfgName + ") nur in Cinematics – nicht aufgestellt"); continue; }
                var npc = new Npc { ConfigName = cfgName, Name = e.Name, Hc = e.Hc };
                var go = new GameObject("TDU2 Figur " + e.Name);
                go.transform.SetParent(scene, false);
                npc.Root = go.transform;
                var act = life;
                if (act != null) Place(npc, act, act.Clip);
                else
                {
                    go.transform.localPosition = e.Position;
                    var f = e.Forward; f.y = 0f;
                    go.transform.localRotation = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
                    npc.Clip = Clip(cfg.Sex == 1 ? "co_stand-w" : "co_stand-m");
                }
                npc.Avatar = TDU2Avatar.Create(go.transform, cfg, "TDU2 " + (AvatarLibrary.PnjName(cfgName) ?? e.Name));
                Npcs.Add(npc);
                Debug.Log("[TDU2] Interieur-Figur " + e.Name + ": " + cfgName + " (" + AvatarLibrary.PnjName(cfgName) + "), Pose " + (npc.Clip != null ? npc.Clip.Name : "-"));
            }
        }

        void Place(Npc npc, CineScene.Obj act, string clip)
        {
            npc.Root.localPosition = act.Position;
            npc.Root.localRotation = act.Rotation;
            var c = Clip(clip);
            if (c != null && c != npc.Clip) { npc.Clip = c; npc.T = 0f; }
        }

        /// <summary>Figur (ConfigName enthält <paramref name="configContains"/>, null = erste) an einen Darsteller-Platz stellen.</summary>
        public Npc SetPose(string configContains, CineScene.Obj act, string clip = null)
        {
            foreach (var n in Npcs)
            {
                if (configContains != null && (n.ConfigName ?? "").IndexOf(configContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (act != null) Place(n, act, clip ?? act.Clip);
                return n;
            }
            return null;
        }

        /// <summary>Figur zurück in ihre Laufmodus-Pose (LIFE-Szene).</summary>
        public void ResetPose(Npc n)
        {
            if (n == null) return;
            var act = FindActor(n.Hc, "LIFE") ?? FindActor(n.Hc, null);
            if (act != null) Place(n, act, act.Clip);
        }

        public Npc First(string configContains)
        {
            foreach (var n in Npcs) if (configContains == null || (n.ConfigName ?? "").IndexOf(configContains, StringComparison.OrdinalIgnoreCase) >= 0) return n;
            return null;
        }

        public void Clear()
        {
            foreach (var n in Npcs) if (n.Root != null) Destroy(n.Root.gameObject);
            Npcs.Clear();
            _clips.Clear();
            Cines = new Dictionary<string, CineScene>(StringComparer.OrdinalIgnoreCase);
            Bank = null; Scene = null;
        }

        void LateUpdate()
        {
            foreach (var n in Npcs)
            {
                var a = n.Avatar;
                if (a == null || !a.Ready) continue;
                a.SetVisible(Show);
                n.T += Time.deltaTime;
                a.BeginPose();
                if (n.Clip != null) a.ApplyClip(n.Clip, Mathf.Repeat(n.T, Mathf.Max(0.05f, n.Clip.Length)), 1f, true);
                a.CommitPose();
            }
        }

        void OnDestroy() => Clear();
    }
}
