using System.Collections.Generic;
using UnityEngine;

namespace TDU2
{
    /// <summary>
    /// World-space line overlay drawn with <see cref="GL"/> inside OnGUI. Unlike Gizmos this also works in a built
    /// player, which is what the runtime debug views (traffic routes, race route/gates) need. Collect the segments
    /// while handling the GUI event, then call <see cref="Draw"/>; the buffer is cleared afterwards.
    /// </summary>
    public static class TDU2DebugLines
    {
        struct Segment
        {
            public Vector3 A, B;
            public Color C;
        }

        static readonly List<Segment> _segments = new List<Segment>(2048);
        static Material _material;

        public static int Count => _segments.Count;

        public static void Clear() => _segments.Clear();

        public static void Line(Vector3 a, Vector3 b, Color c)
        {
            _segments.Add(new Segment { A = a, B = b, C = c });
        }

        public static void Ray(Vector3 from, Vector3 direction, float length, Color c)
        {
            if (direction.sqrMagnitude < 1e-8f) return;
            Line(from, from + direction.normalized * length, c);
        }

        /// <summary>Horizontal circle (XZ plane) around <paramref name="center"/>.</summary>
        public static void Circle(Vector3 center, float radius, Color c, int steps = 24)
        {
            steps = Mathf.Max(4, steps);
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                var p = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Line(prev, p, c);
                prev = p;
            }
        }

        /// <summary>Line from <paramref name="from"/> along <paramref name="direction"/> with a two-sided arrow head.</summary>
        public static void Arrow(Vector3 from, Vector3 direction, float length, Color c, float head = 1.5f)
        {
            if (direction.sqrMagnitude < 1e-8f) return;
            var dir = direction.normalized;
            var tip = from + dir * length;
            Line(from, tip, c);
            var side = Vector3.Cross(Vector3.up, dir);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            var back = tip - dir * head;
            Line(tip, back + side * head * 0.55f, c);
            Line(tip, back - side * head * 0.55f, c);
        }

        /// <summary>Wire box from eight corners (useful for race gates).</summary>
        public static void Box(Vector3 center, Quaternion rotation, Vector3 size, Color c)
        {
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
            var p000 = center + rotation * new Vector3(-hx, -hy, -hz);
            var p100 = center + rotation * new Vector3(hx, -hy, -hz);
            var p110 = center + rotation * new Vector3(hx, hy, -hz);
            var p010 = center + rotation * new Vector3(-hx, hy, -hz);
            var p001 = center + rotation * new Vector3(-hx, -hy, hz);
            var p101 = center + rotation * new Vector3(hx, -hy, hz);
            var p111 = center + rotation * new Vector3(hx, hy, hz);
            var p011 = center + rotation * new Vector3(-hx, hy, hz);
            Line(p000, p100, c); Line(p100, p110, c); Line(p110, p010, c); Line(p010, p000, c);
            Line(p001, p101, c); Line(p101, p111, c); Line(p111, p011, c); Line(p011, p001, c);
            Line(p000, p001, c); Line(p100, p101, c); Line(p110, p111, c); Line(p010, p011, c);
        }

        /// <summary>Draw the collected segments in the game view. Call from OnGUI.</summary>
        public static void Draw()
        {
            if (_segments.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) { _segments.Clear(); return; }

            if (_material == null)
            {
                // Prefer the shader shipped in Resources: built-in shaders found through Shader.Find can be stripped
                // from a player, which would make the debug lines invisible in a built game.
                var shader = Resources.Load<Shader>("TDU2DebugLines");
                if (shader == null) shader = Shader.Find("TDU2/Debug Lines");
                if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) { _segments.Clear(); return; }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _material.SetInt("_ZWrite", 0);
                _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }

            _material.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.LINES);
            for (int i = 0; i < _segments.Count; i++)
            {
                var s = _segments[i];
                GL.Color(s.C);
                GL.Vertex(s.A);
                GL.Vertex(s.B);
            }
            GL.End();
            GL.PopMatrix();
            _segments.Clear();
        }
    }
}
