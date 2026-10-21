namespace TDU2Server.Net.Protocol;

/// <summary>Ein Spieler in der Liste von "status".</summary>
public sealed class PlayerEntry
{
    public int id;
    public string nick = "";
    public int carId;
    public int color;
    public long money;
    public int nbCars;
    public bool inCar;
    public float px, pz;
    public int nationality;
    public int island;          // 1 Ibiza, 2 Hawaii, 0 unbekannt
    public string avatar = "";  // Aussehen (AvatarConfig.Encode) – Mitspieler sehen die Kleidung im Auto
    public string mood = "";    // MyTDULife: Smiley, Stimmungstext, Foto-Version (das Foto selbst über op "profile")
    public string moodTxt = "";
    public int photoVer;
    public int rideWith;        // fährt als Beifahrer bei Spieler … mit (0 = nein)
    public int passenger;       // hat Spieler … als Beifahrer (0 = nein)
}
