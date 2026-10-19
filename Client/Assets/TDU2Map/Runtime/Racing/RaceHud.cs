using System.Globalization;
using TDU2.Map.Flash;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>
    /// Renn-/Prüfungsanzeige über das Original-HUD (frontend/&lt;res&gt;/hud.bnk → hud.vmf), Pfade und Variablen wie in der EXE
    /// (Zeitfahr-Komponente bei 0xb4cc80 / 0xb4ee00 / 0xb4f0c0):
    ///   /_3_2_1_go          Labels 3_go / 2_go / 1_go / go (Start-Countdown)
    ///   /HUD/time           laufende Zeit  /:Min /:Sec /:Cent              (ohne Zeitlimit)
    ///   /HUD/ref_score      Referenzzeit   /:MinRef /:SecRef /:CentRef, Unterclip cart Label "time"
    ///   /HUD/time_cd        Restzeit       /:MinCd /:SecCd /:CentCd       (Zeitlimit "ChCD"; time und ref_score aus)
    ///   /HUD/time_out       letzte 5 s des Limits: Label "on", /:time_out = Sekunden
    ///   /HUD/checkpoints    /:NbCheckpoints /:TotalCheckpoints /:CheckTime /:Gap /:IsCheckGreen /:IsLapOver, Label "chekpoint"
    ///   /HUD/laps           /:NbTurns /:TotalTurns (/:LastLap)  – nur Rundkurse
    /// Rennlogik bleibt in <see cref="TDU2RaceManager"/>.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        [Tooltip("Rennlogik; muss gesetzt sein.")]
        public TDU2RaceManager Race;
        [Tooltip("Original-HUD (VmfPlayer aus TDU2Minimap.HudPlayer).")]
        public VmfPlayer Hud;
        [Tooltip("Optional: Minimap, auf der die Rennstrecke eingezeichnet wird.")]
        public TDU2Minimap Minimap;

        const string CountdownClip = "/_3_2_1_go", TimeClip = "/HUD/time", TimeCdClip = "/HUD/time_cd", RefClip = "/HUD/ref_score",
                     TimeOutClip = "/HUD/time_out", CheckClip = "/HUD/checkpoints", LapsClip = "/HUD/laps";
        bool _shown;
        int _lastTimeOut = -1;
        TDU2RaceManager _bound;

        void OnEnable() => Bind();
        void OnDisable() { Unbind(); HideAll(); if (Minimap != null) Minimap.SetRaceRoute(null); }

        void Bind()
        {
            if (Race == null || _bound == Race) return;
            Unbind();
            _bound = Race;
            Race.Prepared += OnPrepared;
            Race.Started += OnStarted;
            Race.CheckpointPassed += OnCheckpoint;
            Race.Penalty += OnPenalty;
            Race.Completed += OnEnd;
            Race.Failed += OnEnd;
            Race.Aborted += OnEnd;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.Prepared -= OnPrepared;
            _bound.Started -= OnStarted;
            _bound.CheckpointPassed -= OnCheckpoint;
            _bound.Penalty -= OnPenalty;
            _bound.Completed -= OnEnd;
            _bound.Failed -= OnEnd;
            _bound.Aborted -= OnEnd;
            _bound = null;
        }

        VmfPlayer H
        {
            get
            {
                if (Hud == null && Minimap != null) Hud = Minimap.HudPlayer;
                return Hud;
            }
        }

        void OnPrepared(TDU2RaceManager r)
        {
            if (Minimap != null) { Minimap.SetRaceRoute(r.Race); Minimap.SetRaceProgress(r.NextCheckpointIndex); }
            _penaltyAt = -10f;
            TDU2FrontendSounds.Preload("three", "two", "one", "go", "chptpass", "chptrmv", "time", "time2", "wrong");
            var h = H; if (h == null) return;
            TDU2FrontendSounds.Attach(h);   // HUD meldet sndp:three/two/one/go, sndp:chptpass, sndp:time ...
            _shown = true; _lastTimeOut = -1;
            h.SetVisible(CountdownClip, true);
            h.GotoLabel(CountdownClip, "3_go", true);
            // Anzeigen vorbereiten (noch geschlossen)
            SetTimeVars(h, "", 0f);
            var race = r.Race;
            h.SetVariable("/:NbCheckpoints", "0");
            h.SetVariable("/:TotalCheckpoints", r.TargetCount.ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:CheckTime", "");
            h.SetVariable("/:Gap", "");
            h.SetVariable("/:NbTurns", "1");
            h.SetVariable("/:TotalTurns", race.Laps.ToString(CultureInfo.InvariantCulture));
            h.SetValue("/:LastLap", race.IsCircuit && race.Laps <= 1);
            h.SetValue("/:IsPenalty", false);
            if (race.TargetTimeMs > 0)
            {
                Split(race.TargetTimeMs / 1000f, out var m, out var s, out var c);
                h.SetVariable("/:MinRef", m); h.SetVariable("/:SecRef", s); h.SetVariable("/:CentRef", c);
            }
        }

        void OnStarted(TDU2RaceManager r)
        {
            var h = H; if (h == null) return;
            var race = r.Race;
            if (race.HasTimeLimit)
            {
                Show(h, TimeCdClip, true); Show(h, TimeClip, false); Show(h, RefClip, false);
            }
            else
            {
                Show(h, TimeClip, true); Show(h, TimeCdClip, false);
                if (race.TargetTimeMs > 0) { Show(h, RefClip, true); h.GotoLabel(RefClip + "/cart", "time", false); }
            }
            Show(h, CheckClip, true);
            if (race.IsCircuit && race.Laps > 1) Show(h, LapsClip, true);
        }

        void OnCheckpoint(TDU2RaceManager r, int seqIndex)
        {
            if (Minimap != null) Minimap.SetRaceProgress(r.NextCheckpointIndex);
            var h = H; if (h == null) return;
            var race = r.Race;
            h.SetVariable("/:NbCheckpoints", r.PassedCount.ToString(CultureInfo.InvariantCulture));
            h.SetVariable("/:CheckTime", FormatRaceTime(r.ElapsedSeconds + r.PenaltyMs / 1000f));
            // Abstand zur gleichmäßig verteilten Referenzzeit (Original: Abstand zur Referenz-Splitzeit "CpTs")
            if (race.TargetTimeMs > 0 && r.TargetCount > 0)
            {
                float refT = race.TargetTimeMs / 1000f * r.PassedCount / r.TargetCount;
                float gap = r.ElapsedSeconds + r.PenaltyMs / 1000f - refT;
                h.SetVariable("/:Gap", (gap <= 0f ? "-" : "+") + FormatRaceTime(Mathf.Abs(gap)));
                h.SetVariable("/:IsCheckGreen", gap <= 0f ? "1" : "0");
            }
            bool lapOver = race.IsCircuit && race.TargetsPerLap > 0 && r.PassedCount % race.TargetsPerLap == 0;
            h.SetVariable("/:IsLapOver", lapOver ? "1" : "0");
            h.GotoLabel(CheckClip, "chekpoint", true);
            if (race.IsCircuit)
            {
                h.SetVariable("/:NbTurns", r.Lap.ToString(CultureInfo.InvariantCulture));
                h.SetValue("/:LastLap", r.Lap >= race.Laps);
            }
        }

        // ------------------------------------------------------------------ Zeitstrafe (Treffer an Hütchen/Begrenzung)
        [Tooltip("Einblendung der Zeitstrafe (Sekunden)")]
        public float PenaltyShowSeconds = 2.2f;
        float _penaltyAt = -10f;
        int _penaltyMs;
        GUIStyle _penStyle, _penSmall;

        void OnPenalty(TDU2RaceManager r, int ms)
        {
            _penaltyAt = Time.unscaledTime;
            _penaltyMs = ms;
            TDU2FrontendSounds.Play("wrong");
            var h = H;
            if (h != null) h.SetValue("/:IsPenalty", true);
            TDU2DebugLog.Event("Zeitstrafe +" + FormatRaceTimeMs(ms) + " (gesamt " + FormatRaceTimeMs(r.PenaltyMs) + ")");
        }

        void OnGUI()
        {
            if (Race == null || !Race.Running || Race.PenaltyMs <= 0) return;
            if (_penStyle == null)
            {
                _penStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _penSmall = new GUIStyle(_penStyle);
            }
            GUI.depth = -50;
            float sw = Screen.width, sh = Screen.height;
            float age = Time.unscaledTime - _penaltyAt;
            if (age >= 0f && age < PenaltyShowSeconds)
            {
                float a = age < 0.15f ? age / 0.15f : Mathf.Clamp01((PenaltyShowSeconds - age) / 0.6f);
                float pop = age < 0.15f ? 1.35f - 0.35f * (age / 0.15f) : 1f;
                _penStyle.fontSize = Mathf.RoundToInt(sh * 0.055f * pop);
                var rc = new Rect(0f, sh * 0.26f - age * sh * 0.015f, sw, sh * 0.09f);
                string txt = "+" + FormatRaceTimeMs(_penaltyMs);
                DrawShadowed(rc, txt, _penStyle, new Color(1f, 0.18f, 0.12f, a));
                _penSmall.fontSize = Mathf.RoundToInt(sh * 0.022f);
                DrawShadowed(new Rect(0f, rc.yMax - sh * 0.01f, sw, sh * 0.04f), "ZEITSTRAFE", _penSmall, new Color(1f, 1f, 1f, a * 0.9f));
            }
            // Summe der Strafen klein unter der Zeit (oben rechts wie die Zeitanzeige des HUDs)
            _penSmall.fontSize = Mathf.RoundToInt(sh * 0.022f);
            var cur = _penSmall.alignment; _penSmall.alignment = TextAnchor.MiddleRight;
            DrawShadowed(new Rect(0f, sh * 0.205f, sw - sw * 0.035f, sh * 0.035f), "Strafe +" + FormatRaceTimeMs(Race.PenaltyMs), _penSmall, new Color(1f, 0.3f, 0.2f, 0.95f));
            _penSmall.alignment = cur;
        }

        static void DrawShadowed(Rect r, string t, GUIStyle st, Color c)
        {
            var old = st.normal.textColor;
            st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.75f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), t, st);
            st.normal.textColor = c;
            GUI.Label(r, t, st);
            st.normal.textColor = old;
        }

        void OnEnd(TDU2RaceManager r)
        {
            if (Minimap != null) Minimap.SetRaceRoute(null);
            HideAll();
        }

        void HideAll()
        {
            var h = H;
            if (h == null || !_shown) return;
            _shown = false;
            foreach (var c in new[] { TimeClip, TimeCdClip, RefClip, CheckClip, LapsClip, TimeOutClip })
            {
                if (!h.GotoLabel(c, "default", false)) h.GotoLabel(c, "off", false);
            }
            h.GotoLabel(TimeOutClip, "off", false);
            h.GotoLabel(CountdownClip, "default", false);
            h.SetVisible(CountdownClip, false);
        }

        static void Show(VmfPlayer h, string clip, bool on)
        {
            if (on) { h.SetVisible(clip, true); h.GotoLabel(clip, "open", true); }
            else h.GotoLabel(clip, "default", false);
        }

        void Update()
        {
            if (Race != null && _bound != Race) Bind();
            if (Race == null || !_shown) return;
            var h = H; if (h == null) return;
            if (Race.State == RaceState.Countdown) return;
            if (!Race.Running) return;
            var race = Race.Race;
            if (race.HasTimeLimit)
            {
                float left = Race.TimeLeft;
                SetTimeVars(h, "Cd", left);
                int sec = Mathf.CeilToInt(left);
                if (sec <= 5 && sec >= 1 && sec != _lastTimeOut)
                {
                    if (_lastTimeOut < 0) h.GotoLabel(TimeOutClip, "on", true);
                    _lastTimeOut = sec;
                    h.SetVariable("/:time_out", sec.ToString(CultureInfo.InvariantCulture));
                }
            }
            else SetTimeVars(h, "", Race.ElapsedSeconds + Race.PenaltyMs / 1000f);
        }

        static void SetTimeVars(VmfPlayer h, string suffix, float seconds)
        {
            Split(seconds, out var m, out var s, out var c);
            h.SetVariable("/:Min" + suffix, m);
            h.SetVariable("/:Sec" + suffix, s);
            h.SetVariable("/:Cent" + suffix, c);
        }

        static void Split(float seconds, out string min, out string sec, out string cent)
        {
            if (seconds < 0f) seconds = 0f;
            int totalMs = (int)(seconds * 1000f + 0.5f);
            min = (totalMs / 60000).ToString(CultureInfo.InvariantCulture);
            sec = ((totalMs / 1000) % 60).ToString("00", CultureInfo.InvariantCulture);
            cent = ((totalMs / 10) % 100).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Original-Zeitformat der Rennen: Minuten'sekunden"Hundertstel, z.B. 1'23"45.
        /// (Aus den Formatstrings "%d'%02d"%02d" bzw. "%3d'%02d"%02d" der ausführbaren Datei abgeleitet.)
        /// </summary>
        public static string FormatRaceTime(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int totalMs = (int)(seconds * 1000f + 0.5f);
            int cc = (totalMs / 10) % 100;
            int ss = (totalMs / 1000) % 60;
            int mm = totalMs / 60000;
            return string.Format(CultureInfo.InvariantCulture, "{0}'{1:00}\"{2:00}", mm, ss, cc);
        }

        public static string FormatRaceTimeMs(int ms) => FormatRaceTime(ms / 1000f);
    }
}
