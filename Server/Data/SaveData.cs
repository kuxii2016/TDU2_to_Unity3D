namespace TDU2Server.Data;

// Spielstand (Feldnamen = JSON-Namen, identisch im Unity-Client TDU2Net.cs)

public sealed class SaveData
{
    public int version = 1;
    public string island = "";
    public bool hasPosition;
    public float px, py, pz, heading;
    public string currentCar = "";
    public List<string> ownedCars = new();
    public long money;
    public bool starterChosen;
    public List<OdoEntry> odometers = new();
    public List<LiveryEntry> liveries = new();      // Sticker-Shop: Lackierung/Beklebung je Auto (Base64, Client CarLivery)
    public List<int> stickerBoards = new();         // gekaufte Sticker-Planchen (DBStickersBanks._hashcode)
    public List<int> favoriteColors = new();        // "Meine Farben" (0xRRGGBB, -1 = leer)
    public string avatar = "";                      // Aussehen des Avatars (Client AvatarConfig.Encode)
    public List<int> ownedClothes = new();          // gekaufte Kleidungsstücke (DBClothe._hashcode)
    public List<BookmarkEntry> bookmarks = new();   // Lesezeichen der großen Karte (je Insel, Spielkoordinaten)
    public List<string> favoriteSpots = new();      // als Favorit markierte Spots (Spot-Code)
    public List<HouseLayout> houses = new();        // My House: Einrichtung je Haus (Spot-Code)
    public List<int> ownedFurniture = new();        // gekaufte Möbel (DBFurniture._hashcode)
    public List<int> ownedMaterials = new();        // gekaufte Böden/Tapeten (DBMaterial._hashcode)
    public MyLifeData myLife = new();               // MyTDULife: Stimmung, Profil, Kästen, Avatar-Foto
    public string interior = "";                    // zuletzt betretenes Interieur ("SPOT|bank", leer = draußen)
    public List<CarConfigEntry> carConfigs = new(); // Ausstattung je Auto (Autohändler: Felgen, Lack, Innenraum)
    public string savedAt = "";
}

/// <summary>Ausstattung eines Autos: Lack = Index in DBCarsAndColors, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode.</summary>
public sealed class CarConfigEntry
{
    public string car = "";
    public int color;
    public int rim;
    public int interior;
}

/// <summary>Kilometerstand je Auto.</summary>
public sealed class OdoEntry
{
    public int carId;
    public double meters;
}

/// <summary>Lackierung/Beklebung eines Autos (Sticker-Shop).</summary>
public sealed class LiveryEntry
{
    public string car = "";
    public string data = "";
}

/// <summary>Lesezeichen der großen Karte.</summary>
public sealed class BookmarkEntry
{
    public int id;
    public string island = "";
    public float x, y, z;
}

/// <summary>My House: Einrichtung eines Hauses.</summary>
public sealed class HouseLayout
{
    public string spot = "";
    public List<HouseEntry> furn = new();   // Entity-Name -> Bank
    public List<HouseEntry> mats = new();   // Fläche -> Material
}

/// <summary>Schlüssel/Wert-Paar (Einrichtung, Profilangaben).</summary>
public sealed class HouseEntry
{
    public string k = "";
    public string v = "";
}
