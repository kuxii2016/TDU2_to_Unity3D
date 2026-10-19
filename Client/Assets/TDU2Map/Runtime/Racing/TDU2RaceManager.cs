using System;
using System.Collections.Generic;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Racing
{
    public enum RaceState { Idle, Preparing, Countdown, Running, Finished }

    /// <summary>Ergebnis einer Prüfung.</summary>
    public sealed class RaceResult
    {
        public RaceDefinition Race;
        public bool Passed;
        public bool TimedOut;
        public bool Aborted;
        public int RawTimeMs;        // gefahrene Zeit
        public int PenaltyMs;        // Zeitstrafen
        public int FinalTimeMs => RawTimeMs + PenaltyMs;
        public int TargetMs;         // Referenzzeit (timG bzw. Zeitlimit)
        public readonly List<int> Splits = new List<int>();
    }

    /// <summary>
    /// Führt ein Rennen/eine Fahrprüfung aus – Logik nach GMCompDrivingSchool/Zeitfahren der EXE:
    ///  - Vorbereitung: wartet, bis das Prüfungsauto geladen ist und die Strecke gestreamt wurde, baut Tore/Props.
    ///  - Countdown (3-2-1-GO, Auto gehalten), danach läuft die Zeit.
    ///  - Checkpoints in Reihenfolge (<see cref="RaceDefinition.TargetSequence"/>), Rundkurse über die Startlinie,
    ///    Bremszone (CHK2-Art 3): im Feld anhalten.
    ///  - Zeitlimit (ChCD/CtDw): Ablauf = durchgefallen ("/:IsTimeOut"). Ohne Limit: bestanden, wenn Zeit + Strafen &lt;= timG.
    ///  - Zeitstrafen: DBEnvironnement.PenaltyTimeLevel01..03 (10 s) je Treffer, wenn die Prüfung PeLv &gt; 0 hat (vermutet).
    /// Alle Berechnungen in Inselkoordinaten (MapRoot), damit der Floating Origin nichts verschiebt.
    /// </summary>
    public sealed class TDU2RaceManager : MonoBehaviour
    {
        public TDU2MapStreamer Streamer;
        public TDU2VehicleManager Vehicles;
        public Material GateMaterial;             // optional: Material aus dem Checkpoint-Shader (td_checkpoint)
        public bool ShowGates = true;
        public bool SpawnProps = true;
        [Tooltip("Dauer des 3-2-1-GO-Countdowns (s, Echtzeit) – im HUD-Film bis 'go' ca. 3,7 s bei 30 fps.")]
        public float CountdownSeconds = 3.7f;
        [Tooltip("Zeitstrafe je Treffer (s) – Original: DBEnvironnement.PenaltyTimeLevel01..03 = 10 s.")]
        public float PenaltySeconds = 10f;
        [Tooltip("Strafen auch bei Prüfungen ohne Strafstufe (PeLv = 0) geben.")]
        public bool PenaltiesAlways = false;
        [Tooltip("Mindest-Aufprallgeschwindigkeit (m/s) für eine Strafe.")]
        public float PenaltyMinImpact = 2.5f;
        [Tooltip("Taste zum Abbrechen der Prüfung.")]
        public KeyCode AbortKey = KeyCode.Backspace;
        [Tooltip("Max. Wartezeit (s) auf das Streaming der Strecke vor dem Start.")]
        public float MaxPrepareSeconds = 20f;

        public RaceDefinition Race { get; private set; }
        public RaceState State { get; private set; }
        public bool Running => State == RaceState.Running;
        public bool Finished => State == RaceState.Finished;
        public float ElapsedSeconds { get; private set; }
        public float CountdownLeft { get; private set; }
        public int PassedCount { get; private set; }          // erledigte Ziele (ohne Startlinie)
        public int PenaltyMs { get; private set; }
        public int Lap => Race == null ? 1 : Mathf.Min(Race.Laps, 1 + (Race.TargetsPerLap > 0 ? PassedCount / Race.TargetsPerLap : 0));
        public bool InBrakingZone { get; private set; }
        public RaceResult Result { get; private set; }
        public RaceProps Props { get; private set; }
        /// <summary>Gegner/Fahrzeuge der laufenden Prüfung (null ohne).</summary>
        public RaceOpponents Opponents => _opponents;

        public event Action<TDU2RaceManager> Prepared;          // Strecke steht, Countdown beginnt
        public event Action<TDU2RaceManager> Started;           // GO
        public event Action<TDU2RaceManager, int> CheckpointPassed;   // Index in der Zielfolge
        public event Action<TDU2RaceManager> LapCompleted;
        public event Action<TDU2RaceManager, int> Penalty;      // ms
        public event Action<TDU2RaceManager> Completed;         // Ziel erreicht (Result gesetzt)
        public event Action<TDU2RaceManager> Failed;            // Zeit abgelaufen (Result gesetzt)
        public event Action<TDU2RaceManager> Aborted;

        /// <summary>Aktuell anzuvisierendes Tor (null, wenn nicht aktiv).</summary>
        public RaceCheckpoint Next => Race != null && _seq != null && _target < _seq.Count ? Race.Checkpoints[_seq[_target]] : null;
        public int TargetCount => _seq != null ? _seq.Count : 0;
        /// <summary>Index des nächsten Tors in <see cref="RaceDefinition.Checkpoints"/> (für die Minimap).</summary>
        public int NextCheckpointIndex => Next != null ? Next.Index : -1;

        /// <summary>Bestanden (für den laufenden/fertigen Lauf) – Zielzeit eingehalten.</summary>
        public bool WithinTarget => Race != null && (Race.TargetTimeMs <= 0 || (int)(ElapsedSeconds * 1000f) + PenaltyMs <= Race.TargetTimeMs);

        /// <summary>Restzeit des Zeitlimits (s), nur bei <see cref="RaceDefinition.HasTimeLimit"/>.</summary>
        public float TimeLeft => Race != null && Race.HasTimeLimit ? Mathf.Max(0f, Race.TimeLimitMs / 1000f - ElapsedSeconds - PenaltyMs / 1000f) : 0f;

        List<int> _seq;
        int _target;
        float _prevSigned;
        float _prepareT, _stopT;
        bool _haveGround;
        readonly List<RaceGateVisual> _gateVisuals = new List<RaceGateVisual>();
        Material _activeMaterial;
        RaceOpponents _opponents;
        float _lastPenaltyT = -10f;

        Transform Root => Streamer != null ? Streamer.MapRoot : null;

        // ------------------------------------------------------------------ Steuerung

        /// <summary>Prüfung vorbereiten. Das Prüfungsauto muss bereits gespawnt sein bzw. gerade geladen werden.</summary>
        public bool StartRace(RaceDefinition race)
        {
            Abort(false);
            if (race == null || race.Checkpoints.Count < 2) return false;
            if (Streamer == null) { Debug.LogWarning("TDU2RaceManager: kein Streamer gesetzt"); return false; }
            Race = race;
            _seq = race.TargetSequence();
            _target = 0;
            PassedCount = 0; PenaltyMs = 0; ElapsedSeconds = 0f; CountdownLeft = CountdownSeconds;
            Result = null; InBrakingZone = false; _stopT = 0f; _prepareT = 0f; _haveGround = false;
            State = RaceState.Preparing;
            return true;
        }

        public void Abort() => Abort(true);

        void Abort(bool notify)
        {
            bool was = State == RaceState.Running || State == RaceState.Countdown || State == RaceState.Preparing;
            if (notify && was)
            {
                Result = new RaceResult { Race = Race, Aborted = true, RawTimeMs = (int)(ElapsedSeconds * 1000f), PenaltyMs = PenaltyMs };
            }
            State = RaceState.Idle;
            Cleanup();
            if (notify && was) Aborted?.Invoke(this);
        }

        /// <summary>Tore, Props und Gegner entfernen (nach dem Ergebnis).</summary>
        public void Cleanup()
        {
            ClearGates();
            if (Props != null) { Props.Hit -= OnPropHit; Props.Clear(); Props = null; }
            if (_opponents != null) { _opponents.Clear(); }
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if ((State == RaceState.Running || State == RaceState.Countdown) && Time.timeScale > 0f && Input.GetKeyDown(AbortKey)) { Abort(); return; }
#endif
            switch (State)
            {
                case RaceState.Preparing: TickPrepare(Time.unscaledDeltaTime); break;
                case RaceState.Countdown:
                    if (Time.timeScale <= 0f) break;
                    CountdownLeft -= Time.unscaledDeltaTime;
                    if (CountdownLeft <= 0f) Go();
                    break;
                case RaceState.Running: Tick(Time.deltaTime); break;
            }
            if (ShowGates && _gateVisuals.Count > 0 && _activeMaterial != null) RaceGateVisual.Pulse(_activeMaterial, Time.time);
        }

        /// <summary>Während Vorbereitung/Countdown das Auto festhalten (nach dem Eingabe-Update des Fahrzeug-Managers).</summary>
        void LateUpdate()
        {
            if (State != RaceState.Preparing && State != RaceState.Countdown) return;
            var v = Vehicles != null ? Vehicles.Current : null;
            if (v == null) return;
            if (State == RaceState.Preparing) v.AccelPedal = 0f;   // im Countdown darf Gas gegeben werden (Drehzahl), die Bremse hält
            v.BrakePedal = 1f;
            v.HandBrake = 1f;
        }

        void TickPrepare(float dt)
        {
            _prepareT += dt;
            var v = Vehicles != null ? Vehicles.Current : null;
            bool carReady = v != null && Vehicles != null && !Vehicles.IsLoadingCar;
            if (!carReady && _prepareT < MaxPrepareSeconds + 15f) return;
            // Boden unter dem Start geladen?
            if (!_haveGround && Root != null)
            {
                var w = Root.TransformPoint(Race.StartPosition);
                _haveGround = Physics.Raycast(w + Vector3.up * 50f, Vector3.down, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore);
                if (!_haveGround && _prepareT < MaxPrepareSeconds) return;
            }
            // Props-Modelle (worldactors.bnk) fertig geladen?
            string bank = RaceProps.WorldActorsBank(Vehicles != null ? Vehicles.BnkDir : null, Streamer != null && Streamer.Index != null ? Streamer.Index.IslandName : null);
            if (SpawnProps && !RaceProps.Preload(bank) && _prepareT < MaxPrepareSeconds) return;

            BuildTrack();
            State = RaceState.Countdown;
            CountdownLeft = CountdownSeconds;
            Prepared?.Invoke(this);
        }

        void Go()
        {
            State = RaceState.Running;
            ElapsedSeconds = 0f;
            var pos = VehicleIsland();
            _prevSigned = pos.HasValue && Next != null ? Signed(Next, pos.Value) : 0f;
            if (_opponents != null) _opponents.OnRaceStarted();
            Started?.Invoke(this);
        }

        /// <summary>Ein Simulationsschritt (von Update aufgerufen; öffentlich für Tests).</summary>
        public void Tick(float deltaSeconds)
        {
            if (State != RaceState.Running || Race == null) return;
            ElapsedSeconds += deltaSeconds;
            int elapsedMs = (int)(ElapsedSeconds * 1000f);

            // Zeitlimit
            if (Race.HasTimeLimit && Race.TimeLimitMs > 0 && elapsedMs + PenaltyMs >= Race.TimeLimitMs) { Fail(true); return; }
            if (!Race.HasTimeLimit && Race.TimeLimitMs > 0 && elapsedMs >= Race.TimeLimitMs) { Fail(true); return; }

            var p = VehicleIsland();
            if (p == null || Next == null) return;
            var pos = p.Value;
            var cp = Next;

            if (cp.IsBrakingZone && _target == _seq.Count - 1)
            {
                // Bremszone: im Feld zum Stehen kommen
                Vector3 d = pos - cp.Position; d.y = 0f;
                float lat = Vector3.Dot(d, cp.Right), lon = Vector3.Dot(d, cp.Forward);
                InBrakingZone = Mathf.Abs(lat) <= cp.Width * 0.5f + 0.5f && Mathf.Abs(lon) <= cp.ZoneDepth * 0.5f + 0.5f;
                float kmh = Vehicles != null && Vehicles.Current != null ? Mathf.Abs(Vehicles.Current.SpeedKmh) : 99f;
                if (InBrakingZone && kmh < 1.5f) { _stopT += deltaSeconds; if (_stopT > 0.3f) Advance(pos); }
                else _stopT = 0f;
                return;
            }

            float signed = Signed(cp, pos);
            Vector3 dd = pos - cp.Position; dd.y = 0f;
            float lateral = Mathf.Abs(Vector3.Dot(dd, cp.Right));
            float half = cp.HalfAcceptWidth(Race.School == 'B');
            // Durchfahrt in beliebiger Richtung (Kegel-Tore sind in den Daten teils umgekehrt ausgerichtet)
            bool crossed = (_prevSigned < 0f) != (signed < 0f) && Mathf.Abs(signed - _prevSigned) < 30f && lateral <= half;
            bool close = Mathf.Abs(signed) < 1.0f && lateral <= half;
            if (crossed || close) Advance(pos);
            else _prevSigned = signed;
        }

        void Advance(Vector3 pos)
        {
            int ms = (int)(ElapsedSeconds * 1000f);
            PassedCount++;
            int idx = _target;
            _target++;
            InBrakingZone = false; _stopT = 0f;
            HighlightNext();
            if (_opponents != null) _opponents.OnCheckpoint(_seq[idx]);
            CheckpointPassed?.Invoke(this, idx);
            if (Race.IsCircuit && Race.TargetsPerLap > 0 && PassedCount % Race.TargetsPerLap == 0) LapCompleted?.Invoke(this);
            _splits.Add(ms);
            if (_target >= _seq.Count) { Finish(); return; }
            _prevSigned = Signed(Next, pos);
        }

        readonly List<int> _splits = new List<int>();

        void Finish()
        {
            State = RaceState.Finished;
            int ms = (int)(ElapsedSeconds * 1000f);
            Result = new RaceResult { Race = Race, RawTimeMs = ms, PenaltyMs = PenaltyMs, TargetMs = Race.HasTimeLimit ? Race.TimeLimitMs : Race.TargetTimeMs };
            Result.Splits.AddRange(_splits);
            Result.Passed = Race.HasTimeLimit ? Result.FinalTimeMs < Race.TimeLimitMs : (Race.TargetTimeMs <= 0 || Result.FinalTimeMs <= Race.TargetTimeMs);
            _splits.Clear();
            Completed?.Invoke(this);
        }

        void Fail(bool timeout)
        {
            State = RaceState.Finished;
            Result = new RaceResult { Race = Race, TimedOut = timeout, Passed = false, RawTimeMs = (int)(ElapsedSeconds * 1000f), PenaltyMs = PenaltyMs, TargetMs = Race.HasTimeLimit ? Race.TimeLimitMs : Race.TargetTimeMs };
            _splits.Clear();
            Failed?.Invoke(this);
        }

        // ------------------------------------------------------------------ Strafen

        void OnPropHit(GameObject prop, float speed) => AddPenalty(speed, prop != null ? prop.name : "Prop");

        void OnCollisionReport(float impact) => AddPenalty(impact, "Wand/Objekt");

        void AddPenalty(float impact, string what = "")
        {
            if (State != RaceState.Running || Race == null) return;
            string why = null;
            if (!PenaltiesAlways && Race.PenaltyLevel <= 0) why = "Prüfung ohne Strafstufe (PeLv 0)";
            else if (impact < PenaltyMinImpact) why = "zu leicht (< " + PenaltyMinImpact.ToString("0.0") + " m/s)";
            else if (Time.time - _lastPenaltyT < 1.5f) why = "gleicher Treffer (< 1,5 s)";
            TDU2DebugLog.Event("Treffer " + what + " mit " + impact.ToString("0.0") + " m/s – " + (why ?? "Strafe +" + PenaltySeconds.ToString("0") + " s"));
            if (why != null) return;
            _lastPenaltyT = Time.time;
            int ms = Mathf.RoundToInt(PenaltySeconds * 1000f);
            PenaltyMs += ms;
            Penalty?.Invoke(this, ms);
        }

        /// <summary>Kollisionen des Spielerautos mit Wänden/Verkehr melden (von <see cref="RaceCarCollisionProbe"/>).</summary>
        internal void ReportCarCollision(float impact) => OnCollisionReport(impact);

        // ------------------------------------------------------------------ Hilfen

        float Signed(RaceCheckpoint cp, Vector3 islandPos)
        {
            Vector3 d = islandPos - cp.Position; d.y = 0f;
            return Vector3.Dot(d, cp.Forward);
        }

        Vector3? VehicleIsland()
        {
            var v = Vehicles != null ? Vehicles.Current : null;
            if (v == null || Root == null) return null;
            return Root.InverseTransformPoint(v.transform.position);
        }

        // ------------------------------------------------------------------ Tore, Props, Gegner

        void BuildTrack()
        {
            ClearGates();
            _activeMaterial = GateMaterial != null ? GateMaterial : RaceGateVisual.SharedMaterial();
            if (SpawnProps || ShowGates)
            {
                Props = new RaceProps();
                Props.Hit += OnPropHit;
                var race = Race;
                if (!SpawnProps) { race = ShallowWithoutDecors(Race); }
                Props.Build(Streamer, race, _activeMaterial, ShowGates ? _gateVisuals : null);
                Debug.Log("[TDU2] Prüfung " + Race.Name + ": " + Props.Count + " Props (" + Props.Source + "), " + _gateVisuals.Count + " Tore, "
                          + (Race.IsCircuit ? Race.Laps + " Runden" : "Punkt-zu-Punkt") + (Race.EndsInBrakingZone ? ", Bremszone im Ziel" : ""));
            }
            HighlightNext();

            if (Race.Opponents.Count > 0 && Vehicles != null)
            {
                if (_opponents == null) _opponents = gameObject.AddComponent<RaceOpponents>();
                _opponents.Setup(this, Streamer, Vehicles, Race);
            }
            var v = Vehicles != null ? Vehicles.Current : null;
            if (v != null && v.GetComponent<RaceCarCollisionProbe>() == null) v.gameObject.AddComponent<RaceCarCollisionProbe>().Race = this;
            else if (v != null) v.GetComponent<RaceCarCollisionProbe>().Race = this;
        }

        static RaceDefinition ShallowWithoutDecors(RaceDefinition r)
        {
            var c = new RaceDefinition { Name = r.Name, IsCircuit = r.IsCircuit, Laps = r.Laps };
            c.Checkpoints.AddRange(r.Checkpoints);
            return c;
        }

        void ClearGates()
        {
            for (int i = 0; i < _gateVisuals.Count; i++) if (_gateVisuals[i] != null) UnityUtil.SafeDestroy(_gateVisuals[i].gameObject);
            _gateVisuals.Clear();
        }

        void HighlightNext()
        {
            int next = NextCheckpointIndex;
            for (int i = 0; i < _gateVisuals.Count; i++)
            {
                if (_gateVisuals[i] == null) continue;
                int cpIndex = _gateVisuals[i].Index;
                if (cpIndex == next) _gateVisuals[i].SetState(RaceGateState.Next);
                else if (IsPassed(cpIndex)) _gateVisuals[i].SetState(RaceGateState.Passed);
                else _gateVisuals[i].SetState(RaceGateState.Pending);
            }
        }

        bool IsPassed(int cpIndex)
        {
            if (_seq == null) return false;
            // im aktuellen Durchgang schon passiert?
            int lapStart = Race.TargetsPerLap > 0 ? (_target / Race.TargetsPerLap) * Race.TargetsPerLap : 0;
            for (int i = lapStart; i < _target && i < _seq.Count; i++) if (_seq[i] == cpIndex) return true;
            return cpIndex == 0 && !Race.IsCircuit;
        }

        void OnDisable() { if (State != RaceState.Idle) Abort(false); }
    }

    /// <summary>Hängt am Spielerauto und meldet harte Kollisionen (Wände, Verkehr) an die laufende Prüfung.</summary>
    public sealed class RaceCarCollisionProbe : MonoBehaviour
    {
        public TDU2RaceManager Race;

        void OnCollisionEnter(Collision c)
        {
            if (Race == null || !Race.Running) return;
            if (c.collider != null && c.collider.GetComponentInParent<RacePropHit>() != null) return;   // Props melden selbst
            // Boden ignorieren: nur Stöße mit nennenswerter horizontaler Komponente
            var n = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.up;
            if (Mathf.Abs(n.y) > 0.7f) return;
            Race.ReportCarCollision(c.relativeVelocity.magnitude);
        }
    }
}
