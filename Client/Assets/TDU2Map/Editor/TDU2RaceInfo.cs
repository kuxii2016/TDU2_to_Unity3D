using TDU2.Formats;
using TDU2.Racing;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.EditorTools
{
    /// <summary>Auswahlobjekt des Rennen-Fensters: zeigt ein geladenes Rennen im Inspector (siehe TDU2RaceInfoEditor).</summary>
    public sealed class TDU2RaceInfo : ScriptableObject
    {
        public string File;
        public int SelectedCheckpoint = -1;
        [System.NonSerialized] public RaceDefinition Race;
        [System.NonSerialized] public Ige2File Ig2;
        [System.NonSerialized] public LicenseInfo License;
        [System.NonSerialized] public CarDatabase Cars;
        public string CarName = "";
        public string CarSticker;

        public void Set(string file, RaceDefinition race, Ige2File ig2, LicenseInfo license, CarDatabase cars)
        {
            File = file; Race = race; Ig2 = ig2; License = license; Cars = cars;
            name = "Rennen " + (file ?? "").ToUpperInvariant();
            CarName = race != null ? CarLabel(race.CarId) : "";
            CarSticker = null;
            if (race != null && cars != null && cars.ById.TryGetValue(race.CarId, out var c) && c.Raw != null)
            {
                var st = c.Raw.S("_sticker", "");
                if (!string.IsNullOrEmpty(st) && st != "##") CarSticker = st;
            }
        }

        public string CarLabel(int id)
        {
            if (Cars == null || id == 0 || !Cars.ById.TryGetValue(id, out var c)) return "";
            string ver = c.Raw != null ? c.Raw.S("_version_name", "") : "";
            return (c.Brand + " " + c.ModelName + " " + ver).Trim() + "  [" + c.FileName + "]";
        }
    }
}
