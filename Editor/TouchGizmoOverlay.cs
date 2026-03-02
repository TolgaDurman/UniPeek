using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Draws touch-position circles on top of the Game View using GL whenever
    /// the phone sends touch events via UniPeek.
    /// Activated automatically via <see cref="InitializeOnLoadAttribute"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class TouchGizmoOverlay
    {
        private struct TouchPoint
        {
            public Vector2 NormalizedPos; // x=0 left, y=0 top
            public double  LastUpdated;   // EditorApplication.timeSinceStartup
        }

        private static readonly Dictionary<int, TouchPoint> _touches = new();
        private static Material _mat;

        // How long (seconds) to keep showing a touch after the last "moved" before
        // auto-removing it (safety net in case "ended" is dropped by the network).
        private const double StaleTimeout = 2.0;

        static TouchGizmoOverlay()
        {
            UniPeekInput.OnTouchDetailed += OnTouchDetailed;
            Camera.onPostRender          += OnPostRender;
            EditorApplication.update     += OnEditorUpdate;
        }

        // ── Touch tracking ────────────────────────────────────────────────────

        private static void OnTouchDetailed(int fingerId, string phase, Vector2 normalizedPos)
        {
            if (phase == "ended" || phase == "canceled" || phase == "cancelled")
            {
                _touches.Remove(fingerId);
                return;
            }

            _touches[fingerId] = new TouchPoint
            {
                NormalizedPos = normalizedPos,
                LastUpdated   = EditorApplication.timeSinceStartup,
            };
        }

        // ── Editor update: stale cleanup + repaint in edit mode ───────────────

        private static void OnEditorUpdate()
        {
            if (_touches.Count == 0) return;

            double now = EditorApplication.timeSinceStartup;
            var toRemove = new List<int>();
            foreach (var kvp in _touches)
                if (now - kvp.Value.LastUpdated > StaleTimeout)
                    toRemove.Add(kvp.Key);
            foreach (var id in toRemove)
                _touches.Remove(id);

            // In edit mode the Game View doesn't repaint automatically.
            if (!Application.isPlaying)
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        // ── GL draw ───────────────────────────────────────────────────────────

        private static void OnPostRender(Camera cam)
        {
            if (_touches.Count == 0) return;

            EnsureMaterial();
            _mat.SetPass(0);

            GL.PushMatrix();
            GL.LoadPixelMatrix(); // x: 0→Screen.width, y: 0(bottom)→Screen.height(top)

            foreach (var kvp in _touches)
            {
                var tp = kvp.Value;
                // Normalised y=0 is TOP; GL pixel matrix y=0 is BOTTOM — flip Y.
                float sx = tp.NormalizedPos.x * Screen.width;
                float sy = (1f - tp.NormalizedPos.y) * Screen.height;

                DrawFilledCircle(new Vector2(sx, sy), 26f, new Color(1f, 0.35f, 0.25f, 0.45f));
                DrawCircleOutline(new Vector2(sx, sy), 26f, new Color(1f, 1f, 1f, 0.90f), 2.5f);
            }

            GL.PopMatrix();
        }

        // ── Primitives ────────────────────────────────────────────────────────

        private static void DrawFilledCircle(Vector2 center, float radius, Color color)
        {
            const int segments = 24;
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            for (int i = 0; i < segments; i++)
            {
                float a1 = i       * Mathf.PI * 2f / segments;
                float a2 = (i + 1) * Mathf.PI * 2f / segments;
                GL.Vertex3(center.x, center.y, 0);
                GL.Vertex3(center.x + Mathf.Cos(a1) * radius, center.y + Mathf.Sin(a1) * radius, 0);
                GL.Vertex3(center.x + Mathf.Cos(a2) * radius, center.y + Mathf.Sin(a2) * radius, 0);
            }
            GL.End();
        }

        private static void DrawCircleOutline(Vector2 center, float radius, Color color, float thickness)
        {
            const int segments = 24;
            float r0 = radius - thickness;
            float r1 = radius;
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            for (int i = 0; i < segments; i++)
            {
                float a1  = i       * Mathf.PI * 2f / segments;
                float a2  = (i + 1) * Mathf.PI * 2f / segments;
                float cx1 = Mathf.Cos(a1), sy1 = Mathf.Sin(a1);
                float cx2 = Mathf.Cos(a2), sy2 = Mathf.Sin(a2);

                GL.Vertex3(center.x + cx1 * r0, center.y + sy1 * r0, 0);
                GL.Vertex3(center.x + cx1 * r1, center.y + sy1 * r1, 0);
                GL.Vertex3(center.x + cx2 * r1, center.y + sy2 * r1, 0);

                GL.Vertex3(center.x + cx1 * r0, center.y + sy1 * r0, 0);
                GL.Vertex3(center.x + cx2 * r1, center.y + sy2 * r1, 0);
                GL.Vertex3(center.x + cx2 * r0, center.y + sy2 * r0, 0);
            }
            GL.End();
        }

        private static void EnsureMaterial()
        {
            if (_mat != null) return;
            _mat = new Material(Shader.Find("Hidden/Internal-Colored"))
                { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            _mat.SetInt("_ZWrite",   0);
            _mat.SetInt("_ZTest",    (int)UnityEngine.Rendering.CompareFunction.Always);
        }
    }
}
