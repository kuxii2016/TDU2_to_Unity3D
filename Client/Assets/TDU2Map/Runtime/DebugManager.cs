using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TDU2.Map;
using TDU2.Map.Traffic;
using UnityEngine;

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
    public bool enableTelnet = false;
    public static DebugManager Instance { get; private set; }
    public bool isSectorDebug = false;
    public bool isVehicleDebug = false;
    public bool isPOIMarker = false;
    public bool isAllFree = false;
    public Islands StartIsland = Islands.ibiza;
    public string StartMap => StartIsland.ToString();
    public string StartVehicle = "delta_integrale_evo_2";

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

    public static void Debug(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "DEBUG");
    public static void Info(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "INFO");
    public static void Warning(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "WARNING");
    public static void Error(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "ERROR");
    public static void Other(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "Other");
    public static void INIT(string Message) => Tracer.Print(NameOfCallingClass() + "()=>" + Message, "INIT");
    public static void Clear() => Tracer.Print("", "Cls");

    public static string NameOfCallingClass()
    {
        string fullName;
        Type declaringType;
        int skipFrames = 2;
        do
        {
            MethodBase method = new StackFrame(skipFrames, false).GetMethod();
            declaringType = method.DeclaringType;
            if (declaringType == null)
            {
                return method.Name;
            }
            skipFrames++;
            fullName = declaringType.FullName;
        }
        while (declaringType.Module.Name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase));

        return fullName;
    }
}


public class Tracer
{
    public static void Print(string condition, string type = "DEBUG")
    {
        switch (type)
        {
            case "ERROR":
                UnityEngine.Debug.LogError("<color=red>Error</color>: <color=#a93226>" + condition + "</color>");
                break;
            case "INFO":
                UnityEngine.Debug.Log("<color=green>Info</color>: <color=#27ae60>" + condition + "</color>");
                break;
            case "WARNING":
                UnityEngine.Debug.LogWarning("<color=orange>Warning</color>: <color=#b9770e>" + condition + "</color>");
                break;
            case "DEBUG":
                UnityEngine.Debug.Log("<color=yellow>Debug</color>: <color=#34495e>" + condition + "</color>");
                break;
            case "Other":
                ///Do we need Log here?
                break;
            case "INIT":
                ///Do we need Log here?
                break;
        }
        SendInfo("[" + type + "]::" + condition);
    }

    private static void SendInfo(string message)
    {
        try
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPAddress broadcast = IPAddress.Parse("127.0.0.1");
            byte[] sendbuf = Encoding.ASCII.GetBytes(message);
            IPEndPoint ep = new IPEndPoint(broadcast, 11000);
            s.SendTo(sendbuf, ep);
        }
        catch (SocketException)
        {
        }
    }
}