using System;
using System.Collections;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Online;
using TDU2.Vehicles;
using UnityEngine;
using static TDU2.Cinematics.CinData;
using static TDU2.Vehicles.TDU2InteriorNpcs;
using static UnityEngine.UIElements.UxmlAttributeDescription;

namespace TDU2
{
    /// <summary>
    /// Aktueller Spielmodus (Enum <see cref="Gamemode"/> wie im Original, GAME_MODE_*).
    /// Abfrage überall: <c>TDU2GameMode.Current</c> (z.B. für Discord RPC), Änderungen über <see cref="Changed"/>.
    /// Der Modus wird jedes Bild aus dem Spielzustand bestimmt (Login, Karte, Interieur, Testfahrt …);
    /// Missionen/Zwischensequenzen setzen ihn ausdrücklich mit <see cref="Push"/> und geben ihn mit <see cref="Pop"/> wieder frei.
    /// </summary>
    public sealed class TDU2GameMode : MonoBehaviour
    {
        static Gamemode _current = Gamemode.GAME_MODE_BOOTMENU;
        static Gamemode? _forced;
        static float _since;
        static TDU2Interiors _interiors { get; set; }

        /// <summary>Aktueller Spielmodus.</summary>
        public static Gamemode Current { get { EnsureInstance(); return _current; } }

        /// <summary>Name ohne Präfix, z.B. "FREERIDE".</summary>
        public static string CurrentName => Current.ToString().Replace("GAME_MODE_", "");

        /// <summary>Sekunden (unskaliert) seit dem letzten Moduswechsel (z.B. Discord-Zeitstempel).</summary>
        public static float SecondsInMode => Time.unscaledTime - _since;

        /// <summary>Laufende Mission/Story-Schritt ("Driving Tess" …), null = keine (z.B. für Discord RPC).</summary>
        public static string MissionName => TDU2.Story.TDU2Story.CurrentMission;
        /// <summary>Nummer des Story-Schritts (0 Avatar choice … 12), -1 = keine Geschichte.</summary>
        public static int MissionStep => TDU2.Story.TDU2Story.CurrentStep;
        /// <summary>Aktuelles Ziel der Mission (lokalisiert, wie in pop_goal/pop_tutorial), null = keins.</summary>
        public static string MissionObjective => TDU2.Story.TDU2Story.CurrentObjective;

        /// <summary>UTC-Zeitpunkt des letzten Moduswechsels.</summary>
        public static DateTime ModeStartedUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>(alt, neu) bei jedem Wechsel.</summary>
        public static event Action<Gamemode, Gamemode> Changed;

        /// <summary>Modus ausdrücklich setzen (Mission, Kino, Avatar-Wahl …); gilt bis <see cref="Pop"/>.</summary>
        public static void Push(Gamemode mode) { EnsureInstance(); _forced = mode; Apply(mode); }

        /// <summary>Ausdrücklich gesetzten Modus freigeben (danach wieder automatisch).</summary>
        public static void Pop() { _forced = null; Apply(Detect()); }

        /// <summary>Gilt gerade ein ausdrücklich gesetzter Modus?</summary>
        public static bool IsForced => _forced.HasValue;


        public string CurrentMode;
        public string Mission;
        public string Goal;
        public string Interiour;
        public bool isFreeroam = false;
        public string OnlineInfo;

        static void Apply(Gamemode m)
        {
            if (m == _current) return;
            var old = _current;
            _current = m; _since = Time.unscaledTime; ModeStartedUtc = DateTime.UtcNow;
            DebugManager.switchGamemode(m);   // Banner "GAME MODE : …" wie im Original-Log
            try { Changed?.Invoke(old, m); }
            catch (Exception ex) { DebugManager.Error("[TDU2] GameMode.Changed: " + ex.Message); }

            _inst.StartCoroutine("UpdatePresence");
        }

        IEnumerator UpdatePresence()
        {
            yield return new WaitForSeconds(1.5f);
            var rpc = BuildRPC();
            DiscordController.UpdatePresence(rpc.Line1, rpc.Line2, rpc.Line3);
        }

        static void EnsureInstance()
        {
            if (_inst != null || !Application.isPlaying) return;
            var go = new GameObject("TDU2GameMode");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<TDU2GameMode>();
        }

        static TDU2GameMode _inst;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() { _forced = null; _current = Gamemode.GAME_MODE_BOOTMENU; EnsureInstance(); }

        void Update()
        {
            //CurrentMode = CurrentName;
            //Mission = MissionName;
            //Goal = MissionObjective;

            //if (Current == Gamemode.GAME_MODE_FREERIDE)
            //    isFreeroam = true;
            //else
            //    isFreeroam = false;

            //if (isFreeroam)
            //{
            //    var OnlineService = TDU2GameData.Instance;
            //    if (OnlineService != null && OnlineService.Online)
            //    {
            //        OnlineInfo = OnlineService.PlayerCount + " Online \nDriving: " + OnlineService.CarName;
            //    }
            //}

            //try
            //{
            //    Interiour = GetInteriourInfos();
            //}
            //catch (Exception) { }
            if (_forced.HasValue) return;
            Apply(Detect());
        }

        public static string GetIslandName()
        {
            var island = _interiors.Manager?.Streamer?.Index?.IslandName ?? "";
            if (string.IsNullOrEmpty(island)) 
                return "Unknown";

            if (island.Contains("hawa")) 
                return "Hawaii";

            if (island.Contains("ibiz")) 
                return "Ibiza";

            if (island.Contains("casi")) 
                return "Casino Island";

            return island;
        }

        public static (string Line1, string Line2, string Line3) BuildRPC()
        {
            try
            {
                DebugManager.Info("Update RPC Prepare: " + Current + " Gamemode");

                var OnlineService = TDU2GameData.Instance;
                var house = GetInteriourInfos();
                (string Line1out, string Line2out, string Line3out) info = ("", "", "");
                switch (Current)
                {
                    case Gamemode.GAME_MODE_BOOTMENU:
                        info.Line1out = "Starting Test Drive Unlimited 2";
                        info.Line2out = "Initializing game...";
                        info.Line3out = "";
                        break;
                    case Gamemode.GAME_MODE_FREERIDE:
                        info.Line1out = "Players Online: " + OnlineService.PlayerCount;
                        info.Line2out = "Cruising in " + GetIslandName();
                        info.Line3out = "Driving: " + OnlineService.CarName;
                        break;
                    case Gamemode.GAME_MODE_MAP:
                        info.Line1out = "Players Online: " + OnlineService.PlayerCount;
                        info.Line2out = "Looking at the map";
                        info.Line3out = "Driving: " + OnlineService.CarName;
                        break;
                    case Gamemode.GAME_MODE_SHOP:
                        break;
                    case Gamemode.GAME_MODE_MISSION:
                        break;
                    case Gamemode.GAME_MODE_EDITOR:
                        break;
                    case Gamemode.GAME_MODE_CARSHOWCASE:
                        break;
                    case Gamemode.GAME_MODE_TESTCAR:
                        break;
                    case Gamemode.GAME_MODE_GARAGE:
                        info.Line1out = "Players Online: " + OnlineService.PlayerCount;
                        info.Line2out = $"In the Garage at {house}";
                        info.Line3out = "Location: " + GetIslandName();
                        break;
                    case Gamemode.GAME_MODE_LIVING:
                        info.Line1out = "Players Online: " + OnlineService.PlayerCount;
                        info.Line2out = $"Relaxing at Home — {house}";
                        info.Line3out = "Location: " + GetIslandName();
                        break;
                    case Gamemode.GAME_MODE_GOODIES:
                        break;
                    case Gamemode.GAME_MODE_GAMBLING_HALL:
                        break;
                    case Gamemode.GAME_MODE_DRESSING:
                        break;
                    case Gamemode.GAME_MODE_ISLAND_CHANGE:
                        break;
                    case Gamemode.GAME_MODE_VIDEOBUMPER:
                        break;
                    case Gamemode.GAME_MODE_AITRAINER:
                        break;
                    case Gamemode.GAME_MODE_CINVIEWER:
                        break;
                    case Gamemode.GAME_MODE_ESTATES:
                        break;
                    case Gamemode.GAME_MODE_CLUB:
                        break;
                    case Gamemode.GAME_MODE_LOBBY:
                        break;
                    case Gamemode.GAME_MODE_LOGIN:
                        info.Line1out = "Connecting to TDU2";
                        info.Line2out = "Logging in...";
                        info.Line3out = "Preparing to explore";
                        break;
                    case Gamemode.GAME_MODE_JAIL:
                        break;
                    case Gamemode.GAME_MODE_SCHOOL:
                        break;
                    case Gamemode.GAME_MODE_SCREEN_SHOTS:
                        break;
                    case Gamemode.GAME_MODE_AITEST:
                        break;
                    case Gamemode.GAME_MODE_HAIRDRESSER:
                        break;
                    case Gamemode.GAME_MODE_PHOTOGRAPH:
                        break;
                    case Gamemode.GAME_MODE_SPOT_VIEWER:
                        break;
                    case Gamemode.GAME_MODE_REPLAY:
                        break;
                    case Gamemode.GAME_MODE_CASINO:
                        break;
                    case Gamemode.GAME_MODE_STICKERS:
                        break;
                    case Gamemode.GAME_MODE_CINE:
                        break;
                    case Gamemode.GAME_MODE_AVATAR_CHOICE:
                        break;
                    case Gamemode.GAME_MODE_SURGERY:
                        break;
                    case Gamemode.GAME_MODE_AIRPORT:
                        break;
                    case Gamemode.GAME_MODE_FIRST_WM:
                        break;
                    case Gamemode.GAME_MODE_CAR_WASH:
                        break;
                    case Gamemode.GAME_MODE_EXIT:
                        break;
                    case Gamemode.GAME_MODE_CASINOAVATARCHOICE:
                        break;
                }
                DebugManager.Debug("Set RPC: " + info.Line1out + "\n" + info.Line2out + "\n" + info.Line3out);
            }
            catch (Exception e)
            {
                DebugManager.Error("Failed on RPC: " + e
                    .ToString());
            }
            return ("", "", "");
        }

        static string GetInteriourInfos()
        {
            try
            {
                var bank = _interiors.CurrentBankStem;
                var code = _interiors.CurrentSpot.Code;
                var house = _interiors.Manager.Database.HouseForSpot(_interiors.CurrentSpot, _interiors.Manager?.Streamer?.Index?.IslandName ?? "").NameHash;
                return FlashLocalization.Text(bank, TDU2PauseMenu.SavedLanguage(), house);
            }
            catch (Exception)
            {
                return "";
            }
        }


        /// <summary>Modus aus dem Spielzustand bestimmen.</summary>
        static Gamemode Detect()
        {
            if (TDU2Login.Active) return Gamemode.GAME_MODE_LOGIN;
            if (TDU2BigMap.IsOpen) return Gamemode.GAME_MODE_MAP;
            if (TDU2Airport.IsOpen) return Gamemode.GAME_MODE_AIRPORT;
            if (TDU2Interiors.TestDriveActive) return Gamemode.GAME_MODE_TESTCAR;
            if (TDU2Interiors.CasinoMenuOpen) return Gamemode.GAME_MODE_CASINO;
            if (_interiors == null) _interiors = FindFirstObjectByType<TDU2Interiors>();
            var ti = _interiors;
            if (ti != null && ti.Inside)
            {
                if (ti.InGarageRoom) return Gamemode.GAME_MODE_GARAGE;
                if (ti.InStickerShop) return Gamemode.GAME_MODE_STICKERS;
                var code = ti.CurrentSpot?.Code ?? "";
                return FromSpotCode(code);
            }
            return Gamemode.GAME_MODE_FREERIDE;
        }
        
        /// <summary>Spielmodus eines Spot-Interieurs (Code wie "ECD_AUDI", "EPH_L2E").</summary>
        public static Gamemode FromSpotCode(string code)
        {
            string c = (code ?? "").ToUpperInvariant();
            if (c.StartsWith("ECD") || c.StartsWith("EBD")) return Gamemode.GAME_MODE_SHOP;
            if (c.StartsWith("EPH")) return Gamemode.GAME_MODE_LIVING;
            if (c.StartsWith("ECH")) return Gamemode.GAME_MODE_CLUB;
            if (c.StartsWith("ECW")) return Gamemode.GAME_MODE_CAR_WASH;
            if (c.StartsWith("ESTICK") || c.StartsWith("ETUN")) return Gamemode.GAME_MODE_STICKERS;
            if (c.StartsWith("ERS")) return Gamemode.GAME_MODE_SCHOOL;
            if (c.StartsWith("ERE")) return Gamemode.GAME_MODE_ESTATES;
            if (c.StartsWith("EAS")) return Gamemode.GAME_MODE_GOODIES;
            if (c.StartsWith("EHD")) return Gamemode.GAME_MODE_HAIRDRESSER;
            if (c.StartsWith("ESUR")) return Gamemode.GAME_MODE_SURGERY;
            if (c.StartsWith("EGAM")) return Gamemode.GAME_MODE_GAMBLING_HALL;
            if (c.StartsWith("ELAND") || c.StartsWith("EAIR")) return Gamemode.GAME_MODE_AIRPORT;
            if (c.StartsWith("ECA")) return Gamemode.GAME_MODE_CASINO;
            return Gamemode.GAME_MODE_FREERIDE;
        }
    }
}