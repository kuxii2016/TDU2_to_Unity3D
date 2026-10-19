using System;
using TDU2.Map;
using TDU2.Map.Online;
using TDU2.Map.Traffic;
using TDU2.Map.World;
using UnityEngine;
using UnityEngine.Profiling;

public enum Islands
{
    ibiza,
    hawai,
    casino
}

public class DebugManager : MonoBehaviour
{
    public TDU2MapStreamer TDU2MapStreamer;
    public TDU2Traffic TDU2Traffic;
    public static DebugManager Instance { get; private set; }
    public bool isSectorDebug = false;
    public bool isVehicleDebug = false;
    public bool isPOIMarker = false;
    public Islands StartIsland = Islands.ibiza;
    public string StartMap => StartIsland.ToString();
    public string StartVehicle = "delta_integrale_evo_2";

    public KeyCode ToggleSectorDebugKey = KeyCode.F3;
    private GUIStyle guiStyle = new GUIStyle();
    private bool isDebugActive = false;
    private string Direction = "";
    private string formatedString = "{value} FPS";
    private float updateRateSeconds = 4.0F;
    int frameCount = 0;
    float dt = 0.0F;
    private float fps = 0.0F;
    string CurrentFrames;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleSectorDebugKey))
        {
            isDebugActive = !isDebugActive;
        }
        FrameCount();
    }
    private void FrameCount()
    {
        frameCount++;
        dt += Time.unscaledDeltaTime;
        if (dt > 1.0 / updateRateSeconds)
        {
            fps = frameCount / dt;
            frameCount = 0;
            dt -= 1.0F / updateRateSeconds;
        }

        CurrentFrames = formatedString.Replace("{value}", System.Math.Round((fps * 1), 1).ToString("0.0"));
    }


    private void OnGUI()
    {
        try
        {
            if (isDebugActive)
            {
                string Traffic = "";
                var t = (TDU2Traffic)TDU2Traffic;
                foreach (var c in t.Info())
                    Traffic += $"{(c.mine ? "●" : "○")} {c.id & 0xFFFF:X4} {c.model} {c.mode}  {c.kmh:0} km/h  {c.dist:0} m  Owner #{c.owner}\n";
                guiStyle.fontSize = 30;
                guiStyle.normal.textColor = Color.black;
                guiStyle.alignment = TextAnchor.UpperLeft;
                var sk = WorldLayout.SectorAt(TDU2MapStreamer._viewerGame.x, TDU2MapStreamer._viewerGame.z);
                GUI.Label(new Rect(13, 70, 800, 600),
                    $"TDU2 2 Unity\n" +
                    $"{CurrentFrames} fps ({dt} / {frameCount}) T:{updateRateSeconds}\n" +
                    $"Server: {TDU2GameData.Instance.ServerHost}:{TDU2GameData.Instance.TcpPort} Loggedin: {TDU2GameData.Instance.Online} Players: {TDU2GameData.Instance.PlayerCount}\n" +
                    $"Position: {TDU2MapStreamer._viewerGame.x:0} / {TDU2MapStreamer._viewerGame.y:0} / {TDU2MapStreamer._viewerGame.z:0}   {sk}\n" +
                    $"World: {TDU2MapStreamer.Index.IslandName} SectorBanks: {TDU2MapStreamer.Index.SectorBanks.Count} Areabanks: {TDU2MapStreamer.Index.AreaBanks.Count}\n" +
                    $"Spots: {TDU2MapStreamer.Spots.Spots.Count} " +
                    $"Areas: {TDU2MapStreamer.builtA}/{TDU2MapStreamer.areaCount}   Sektoren: {TDU2MapStreamer.builtS}/{TDU2MapStreamer.sectorCount}   Jobs: {TDU2MapStreamer.Jobs}\n" +
                    $"Instanzen Tree: {TDU2MapStreamer.inst} Lib: {TDU2MapStreamer.lib}\n" +
                    $"\n" +
                    $"Car: {TDU2GameData.Instance.CurrentCar?.Id} {TDU2GameData.Instance.CurrentCar?.Brand} {TDU2GameData.Instance.CurrentCar?.ModelName}" + "\n\n" +
                    $"Traffic {TDU2Traffic.EnableTraffic} maxCars: {TDU2Traffic.MaxCars} CarList:\n" +
                    $"Cars Spawned: {TDU2Traffic.CarCount} ({TDU2Traffic.OwnedCount} Own) \n {TDU2Traffic.Status}\n" +
                    $"{Traffic}\n", guiStyle);


                guiStyle.alignment = TextAnchor.UpperRight;
                GUI.Label(new Rect(Screen.width - 28, 17, 15, 15),
                    $"\n" +
                    $"Allocated: {(Profiler.GetMonoHeapSizeLong() - Profiler.GetMonoUsedSizeLong()) / 100000} % {String.Format("{0:0.00}", FormatBytes(Profiler.GetMonoUsedSizeLong()))}/{String.Format("{0:0.00}", FormatBytes(Profiler.GetMonoHeapSizeLong()))}MB \n" +
                    $"MEM: {(int)(Profiler.GetTempAllocatorSize() / 1024) / 10000} % {String.Format("{0:0.00}", FormatBytes(Profiler.GetTempAllocatorSize()))}/{String.Format("{0:0.00}", FormatBytes(Profiler.GetTotalAllocatedMemoryLong()))}GB \n\n \n" +
                    $"CPU: {(SystemInfo.processorType + "@ " + (SystemInfo.processorFrequency / 1000) + " Ghz")}\n\n" +
                    $"Display: {Screen.width}x{Screen.height}\n" +
                    $"Vendor: {SystemInfo.graphicsDeviceName} {SystemInfo.graphicsDeviceType}\n" +
                    $"Device Ram Free: {Profiler.GetAllocatedMemoryForGraphicsDriver() - (SystemInfo.graphicsMemorySize + 1024)}/{(SystemInfo.graphicsMemorySize + 1024) / 1024}GB\n" +
                    $"Treiber: {SystemInfo.graphicsDeviceVersion}", guiStyle);


                guiStyle.normal.textColor = Color.green;
                guiStyle.alignment = TextAnchor.UpperLeft;
                GUI.Label(new Rect(15, 72, 800, 600),
                    $"TDU2 2 Unity\n" +
                    $"{CurrentFrames} fps ({dt} / {frameCount}) T:{updateRateSeconds}\n" +
                    $"Server: {TDU2GameData.Instance.ServerHost}:{TDU2GameData.Instance.TcpPort} Loggedin: {TDU2GameData.Instance.Online} Players: {TDU2GameData.Instance.PlayerCount}\n" +
                    $"Position: {TDU2MapStreamer._viewerGame.x:0} / {TDU2MapStreamer._viewerGame.y:0} / {TDU2MapStreamer._viewerGame.z:0}   {sk}\n" +
                    $"World: {TDU2MapStreamer.Index.IslandName} SectorBanks: {TDU2MapStreamer.Index.SectorBanks.Count} Areabanks: {TDU2MapStreamer.Index.AreaBanks.Count}\n" +
                    $"Spots: {TDU2MapStreamer.Spots.Spots.Count} " +
                    $"Areas: {TDU2MapStreamer.builtA}/{TDU2MapStreamer.areaCount}   Sektoren: {TDU2MapStreamer.builtS}/{TDU2MapStreamer.sectorCount}   Jobs: {TDU2MapStreamer.Jobs}\n" +
                    $"Instanzen Tree: {TDU2MapStreamer.inst} Lib: {TDU2MapStreamer.lib}\n" +
                    $"\n" +
                    $"Car: {TDU2GameData.Instance.CurrentCar?.Id} {TDU2GameData.Instance.CurrentCar?.Brand} {TDU2GameData.Instance.CurrentCar?.ModelName}" + "\n\n" +
                    $"Traffic {TDU2Traffic.EnableTraffic} maxCars: {TDU2Traffic.MaxCars} CarList:\n" +
                    $"Cars Spawned: {TDU2Traffic.CarCount} ({TDU2Traffic.OwnedCount} Own) \n {TDU2Traffic.Status}\n" +
                    $"{Traffic}\n", guiStyle);


                guiStyle.alignment = TextAnchor.UpperRight;
                GUI.Label(new Rect(Screen.width - 30, 15, 15, 15),
                    $"\n" +
                    $"Allocated: {(Profiler.GetMonoHeapSizeLong() - Profiler.GetMonoUsedSizeLong()) / 100000} % {String.Format("{0:0.00}", FormatBytes(Profiler.GetMonoUsedSizeLong()))}/{String.Format("{0:0.00}", FormatBytes(Profiler.GetMonoHeapSizeLong()))}MB \n" +
                    $"MEM: {(int)(Profiler.GetTempAllocatorSize() / 1024) / 10000} % {String.Format("{0:0.00}", FormatBytes(Profiler.GetTempAllocatorSize()))}/{String.Format("{0:0.00}", FormatBytes(Profiler.GetTotalAllocatedMemoryLong()))}GB \n\n \n" +
                    $"CPU: {(SystemInfo.processorType + "@ " + (SystemInfo.processorFrequency / 1000) + " Ghz")}\n\n" +
                    $"Display: {Screen.width}x{Screen.height}\n" +
                    $"Vendor: {SystemInfo.graphicsDeviceName} {SystemInfo.graphicsDeviceType}\n" +
                    $"Device Ram Free: {Profiler.GetAllocatedMemoryForGraphicsDriver() - (SystemInfo.graphicsMemorySize + 1024)}/{(SystemInfo.graphicsMemorySize + 1024) / 1024}GB\n" +
                    $"Treiber: {SystemInfo.graphicsDeviceVersion}", guiStyle);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] Suffix = { "", "", "", "", "" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return String.Format("{0:0.##} {1}", dblSByte, Suffix[i]);
    }
}
