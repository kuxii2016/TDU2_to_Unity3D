namespace TDU2Server.Data;

/// <summary>MyTDULife (Tagebuch "My Life"): wird von anderen Spielern über op "profile" bzw. die Spielerliste gelesen.</summary>
public sealed class MyLifeData
{
    public string mood = "big_smile";          // Smiley (setMood des Films: neutral, big_smile, tongue, wink, sad, angry, nervous, shy, show_off, angel, demon)
    public string moodText = "TDU2 to Unity3D";             // Stimmungstext (max. 50 Zeichen)
    public List<string> boxes = new();       // Kästen ed1..ed7 (racing, driver_profile …)
    public List<HouseEntry> fields = new();  // Profilangaben: Liste -> Index ("attitude" -> "2")
    public int birthDay, birthMonth, birthYear;
    public string gender = "";
    public string photo = "";                // Avatar-Foto (JPG, Base64)
    public int photoVer;                     // erhöht sich mit jedem neuen Foto
}
