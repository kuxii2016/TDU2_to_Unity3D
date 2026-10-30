using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>
    /// Umfragen vom Server (im Web-Panel angelegt) für den Umfrage-Kanal von TDU Media: <see cref="Refresh"/> holt die Liste
    /// (op "surveys"), <see cref="Vote"/> stimmt ab (op "vote", Antwort = neue Liste mit Summen). Abfragen nacheinander,
    /// Ergebnis im Hauptthread über <see cref="Tick"/> (ruft TDU2TDUMedia jedes Bild).
    /// </summary>
    public static class TDU2Surveys
    {
        public static readonly List<SurveyEntry> Items = new List<SurveyEntry>();
        /// <summary>Liste geändert (Hauptthread).</summary>
        public static event Action Changed;
        /// <summary>Letzter Fehler (Code vom Server, z.B. "closed"), leer = keiner.</summary>
        public static string LastError { get; private set; } = "";
        /// <summary>Gerade eine Abfrage unterwegs.</summary>
        public static bool Busy => _task != null;
        public static bool Loaded { get; private set; }

        static Task<NetResponse> _task;
        static string _taskOp;
        static NetRequest _queued;

        public static void Refresh() => Queue(new NetRequest { op = "surveys" });

        public static void Vote(int surveyId, int choice) => Queue(new NetRequest { op = "vote", target = surveyId, choice = choice });

        static void Queue(NetRequest r)
        {
            // eine Stimme hat Vorrang vor einem bloßen Neuladen
            if (_queued != null && _queued.op == "vote" && r.op == "surveys") return;
            _queued = r;
        }

        public static void Tick()
        {
            if (_task == null)
            {
                if (_queued == null || !TDU2Session.LoggedIn || TDU2Session.Client == null) return;
                var r = _queued; _queued = null;
                r.session = TDU2Session.SessionId;
                _taskOp = r.op;
                _task = TDU2Session.Client.Send(r);
                return;
            }
            if (!_task.IsCompleted) return;
            var resp = _task.Status == TaskStatus.RanToCompletion ? _task.Result : null;
            string op = _taskOp;
            _task = null;
            if (resp == null || !resp.ok)
            {
                LastError = resp?.error ?? "no_answer";
                if (LastError != "unknown_op") Debug.LogWarning($"[TDU2] Umfragen ({op}): {LastError}");
                if (op == "vote") Refresh();
                try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[TDU2] Umfragen: " + ex.Message); }
                return;
            }
            LastError = "";
            Items.Clear();
            if (resp.surveys != null) foreach (var s in resp.surveys) if (s != null) Items.Add(s);
            Loaded = true;
            TDU2.TDU2Diag.Log("HAUS", $"Umfragen ({op}): {Items.Count} vom Server");
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[TDU2] Umfragen: " + ex.Message); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Items.Clear(); _task = null; _queued = null; LastError = ""; Loaded = false; }
    }
}
