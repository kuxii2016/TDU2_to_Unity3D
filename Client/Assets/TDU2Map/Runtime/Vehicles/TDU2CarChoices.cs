using System;
using System.Collections.Generic;
using System.Globalization;
using TDU2.Map.Online;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>Ausstattung eines Autos (Autohändler): Lack = Index in DBCarsAndColors des Autos, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode.</summary>
    public struct CarChoice
    {
        public int Color, Rim, Interior;
        public CarChoice(int color, int rim, int interior) { Color = color; Rim = rim; Interior = interior; }
        public override string ToString() => $"Lack {Color}, Felge {Rim}, Innenraum {Interior}";
    }

    /// <summary>
    /// Gekaufte Ausstattung je Auto. Angemeldet im Spielstand (SaveData.carConfigs, Server), sonst in den PlayerPrefs.
    /// Eine vorübergehende Ausstattung (Testfahrt beim Händler) geht vor und wird nicht gespeichert.
    /// Nur im Hauptthread benutzen (die Ladeaufgaben bekommen die Werte als Kopie).
    /// </summary>
    public static class TDU2CarChoices
    {
        const string PrefsPrefix = "TDU2.CarCfg.";
        static readonly Dictionary<string, CarChoice> _temp = new Dictionary<string, CarChoice>(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string car, out CarChoice choice)
        {
            choice = default;
            if (string.IsNullOrEmpty(car)) return false;
            if (_temp.TryGetValue(car, out choice)) return true;
            if (TDU2Session.LoggedIn)
            {
                var e = TDU2Session.Save.carConfigs?.Find(c => string.Equals(c.car, car, StringComparison.OrdinalIgnoreCase));
                if (e == null) return false;
                choice = new CarChoice(e.color, e.rim, e.interior);
                return true;
            }
            string s;
            try { s = PlayerPrefs.GetString(PrefsPrefix + car, ""); } catch (Exception) { return false; }
            var p = s.Split(',');
            if (p.Length != 3) return false;
            int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out choice.Color);
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out choice.Rim);
            int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out choice.Interior);
            return true;
        }

        /// <summary>Ausstattung dauerhaft speichern (Kauf).</summary>
        public static void Set(string car, CarChoice choice)
        {
            if (string.IsNullOrEmpty(car)) return;
            _temp.Remove(car);
            if (TDU2Session.LoggedIn)
            {
                var list = TDU2Session.Save.carConfigs ?? (TDU2Session.Save.carConfigs = new List<CarConfigEntry>());
                var e = list.Find(c => string.Equals(c.car, car, StringComparison.OrdinalIgnoreCase));
                if (e == null) list.Add(e = new CarConfigEntry { car = car });
                e.color = choice.Color; e.rim = choice.Rim; e.interior = choice.Interior;
                return;
            }
            try { PlayerPrefs.SetString(PrefsPrefix + car, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", choice.Color, choice.Rim, choice.Interior)); }
            catch (Exception) { }
        }

        /// <summary>Vorübergehende Ausstattung (Testfahrt), wird nicht gespeichert.</summary>
        public static void SetTemporary(string car, CarChoice choice) { if (!string.IsNullOrEmpty(car)) _temp[car] = choice; }

        public static void ClearTemporary(string car) { if (!string.IsNullOrEmpty(car)) _temp.Remove(car); }
    }
}
