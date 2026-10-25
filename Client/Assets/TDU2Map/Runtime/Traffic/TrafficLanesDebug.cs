
using UnityEngine;
using TDU2.Traffic;

public sealed class TrafficLanesDebug : MonoBehaviour
{
    public TrafficLanes Lanes;

    [Header("Anzeige")]
    public bool ShowLanes = true;
    public bool ShowJunctions = true;
    public bool ShowOnlySelected;
    public float LineHeight = 0.25f;
    public float LineWidth = 0f; // Debug.DrawLine hat keine einstellbare Breite
    public float MinSpeedKmh = 0f;
    public float MaxSpeedKmh = 160f;

    [Header("Farben")]
    public Color SlowColor = Color.red;
    public Color MediumColor = new Color(1f, 0.5f, 0f);
    public Color NormalColor = Color.yellow;
    public Color FastColor = Color.green;
    public Color VeryFastColor = Color.cyan;

    void Update()
    {
        if (ShowOnlySelected || Lanes == null)
            return;

        DrawTrafficNetwork();
    }

    void OnDrawGizmos()
    {
        if (!ShowOnlySelected || Lanes == null)
            return;

        DrawTrafficNetwork();
    }

    void DrawTrafficNetwork()
    {
        if (ShowLanes && Lanes.Net != null)
        {
            foreach (var road in Lanes.Net.Roads.Values)
            {
                DrawDirection(road.Id, true);
                DrawDirection(road.Id, false);
            }
        }

        if (ShowJunctions)
        {
            // Zeichnet die bereits erzeugten Kreuzungsverbindungen.
            // Die Verbindungen sind im Dictionary von TrafficLanes gespeichert.
            // Für den Zugriff siehe die öffentliche Methode weiter unten.
            foreach (var junction in Lanes.GetDebugJunctions())
                DrawTrack(junction);
        }
    }

    void DrawDirection(int roadId, bool forward)
    {
        if (!Lanes.Net.Roads.TryGetValue(roadId, out var road))
            return;

        int count = Lanes.LaneCount(road, forward);

        for (int lane = 0; lane < count; lane++)
        {
            var track = Lanes.Lane(roadId, forward, lane);
            if (track != null)
                DrawTrack(track);
        }
    }

    void DrawTrack(TrafficTrack track)
    {
        if (track.P == null || track.Limit == null)
            return;

        for (int i = 0; i < track.P.Length - 1; i++)
        {
            float speedKmh =
                (track.Limit[i] + track.Limit[i + 1]) * 0.5f * 3.6f;

            Color color = SpeedColor(speedKmh);

            Vector3 a = track.P[i] + Vector3.up * LineHeight;
            Vector3 b = track.P[i + 1] + Vector3.up * LineHeight;

            Debug.DrawLine(a, b, color, 0f, false);
        }
    }

    Color SpeedColor(float kmh)
    {
        float t = Mathf.InverseLerp(MinSpeedKmh, MaxSpeedKmh, kmh);

        if (t < 0.2f)
            return SlowColor;

        if (t < 0.4f)
            return MediumColor;

        if (t < 0.6f)
            return NormalColor;

        if (t < 0.8f)
            return FastColor;

        return VeryFastColor;
    }
}
