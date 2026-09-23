using System;
using System.Globalization;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The on-screen direction arrow, drawn with IMGUI so it needs no asset bundle.
    /// The arrow texture is generated once at runtime; colour follows the TomTom convention of shifting
    /// from green when you face the waypoint through yellow to red when you face away from it.
    /// </summary>
    public static class ArrowHud
    {
        private const int TextureResolution = 256;

        private static Texture2D _arrow;
        private static GUIStyle _labelStyle;

        // Parsed arrow colours, refreshed only when the configured hex strings change.
        private static string _goodSource, _middleSource, _badSource;
        private static Color _goodColor, _middleColor, _badColor;

        /// <summary>Last heading that was far enough away to be meaningful, on the horizontal plane.</summary>
        private static Vector3 _lastDirection;

        // Arrow outline in normalised texture space, y pointing up, as a kite with a notched tail.
        private static readonly float[] PolyX = new float[] { 0.50f, 0.97f, 0.50f, 0.03f };
        private static readonly float[] PolyY = new float[] { 0.97f, 0.04f, 0.33f, 0.04f };

        public static void InvalidateTextures()
        {
            if (_arrow != null)
            {
                UnityEngine.Object.Destroy(_arrow);
                _arrow = null;
            }
        }

        public static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!Plugin.ShowArrow.Value) return;
            if (!ShouldShow()) return;

            Waypoint wp = WaypointManager.Active;
            Player player = Player.m_localPlayer;
            if (wp == null || player == null) return;

            Vector3 playerPos = player.transform.position;

            float bearing;
            if (!TryGetRelativeBearing(playerPos, wp.Pos, out bearing)) return;

            float distance = WaypointManager.HorizontalDistance(playerPos, wp.Pos);

            EnsureTexture();
            if (_arrow == null) return;

            float size = Plugin.ArrowSize.Value;
            float cx = Screen.width * Mathf.Clamp01(Plugin.ArrowScreenX.Value);
            float cy = Screen.height * Mathf.Clamp01(Plugin.ArrowScreenY.Value);
            Rect rect = new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size);

            Color tint = AlignmentColor(bearing);
            tint.a = Mathf.Clamp01(Plugin.ArrowOpacity.Value);

            Color previousColor = GUI.color;
            Matrix4x4 previousMatrix = GUI.matrix;

            // Positive bearing means the target is to the right, and a positive IMGUI rotation turns
            // clockwise on screen, so the angle can be applied directly.
            GUIUtility.RotateAroundPivot(bearing, rect.center);
            GUI.color = tint;
            GUI.DrawTexture(rect, _arrow, ScaleMode.StretchToFill, true);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;

            DrawCaptions(rect, wp, distance);
        }

        private static void DrawCaptions(Rect arrowRect, Waypoint wp, float distance)
        {
            EnsureStyle();

            float y = arrowRect.yMax + 2f;
            float width = Mathf.Max(arrowRect.width * 3f, 260f);
            float x = arrowRect.center.x - width * 0.5f;

            if (Plugin.ShowWaypointName.Value)
            {
                string name = wp.DisplayName;
                if (WaypointManager.Queue.Count > 1)
                    name += string.Format(CultureInfo.InvariantCulture, "  ({0} left)", WaypointManager.Queue.Count);
                DrawOutlinedLabel(new Rect(x, y, width, 22f), name, Color.white);
                y += 20f;
            }

            if (Plugin.ShowDistance.Value)
            {
                DrawOutlinedLabel(new Rect(x, y, width, 22f), FormatDistance(distance), new Color(0.88f, 0.88f, 0.88f, 1f));
                y += 20f;
            }

            if (Plugin.ShowEta.Value)
            {
                string eta = FormatEta(distance, WaypointManager.SmoothedSpeed);
                if (eta != null)
                    DrawOutlinedLabel(new Rect(x, y, width, 22f), eta, new Color(0.75f, 0.75f, 0.75f, 1f));
            }
        }

        /// <summary>Draws text with a cheap black outline so it stays readable against snow or sky.</summary>
        private static void DrawOutlinedLabel(Rect rect, string text, Color color)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), text, _labelStyle);
            GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), text, _labelStyle);
            GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), text, _labelStyle);
            GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text, _labelStyle);
            GUI.color = color;
            GUI.Label(rect, text, _labelStyle);
            GUI.color = previous;
        }

        private static void EnsureStyle()
        {
            if (_labelStyle != null) return;
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.alignment = TextAnchor.UpperCenter;
            _labelStyle.fontSize = 15;
            _labelStyle.fontStyle = FontStyle.Bold;
            _labelStyle.wordWrap = false;
        }

        private static bool ShouldShow()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return false;
            if (!WaypointManager.HasActive) return false;

            try
            {
                if (Hud.IsUserHidden()) return false;
                if (player.IsDead() || player.InIntro() || player.IsTeleporting()) return false;
                if (Plugin.HideArrowWhenMapOpen.Value && Minimap.instance != null
                    && Minimap.instance.m_mode == Minimap.MapMode.Large) return false;
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Signed angle in degrees on the horizontal plane between where the view is pointing and the
        /// waypoint. Positive means the waypoint is to the right.
        /// </summary>
        private static bool TryGetRelativeBearing(Vector3 playerPos, Vector3 targetPos, out float bearing)
        {
            bearing = 0f;

            Vector3 forward = Vector3.zero;
            if (GameCamera.instance != null)
                forward = GameCamera.instance.transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                // Looking straight up or down: fall back to the direction the character faces.
                Player player = Player.m_localPlayer;
                if (player == null) return false;
                forward = player.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f) return false;
            }
            forward.Normalize();

            Vector3 toTarget = targetPos - playerPos;
            toTarget.y = 0f;

            // Very close to the target the direction becomes meaningless - a single sidestep would swing
            // it wildly - so the last meaningful heading is reused. The arrow still turns with the
            // camera, it just stops chasing noise. This matters when 3D distance is in use and the
            // target is far above or below, since horizontal distance can reach zero without arriving.
            const float freezeRadius = 1.5f;
            if (toTarget.sqrMagnitude < freezeRadius * freezeRadius)
            {
                if (_lastDirection.sqrMagnitude < 0.0001f) return false;
                toTarget = _lastDirection;
            }
            else
            {
                _lastDirection = toTarget;
            }

            if (toTarget.sqrMagnitude < 0.0001f) return false;
            toTarget.Normalize();

            float angle = Vector3.Angle(forward, toTarget);
            Vector3 cross = Vector3.Cross(forward, toTarget);
            if (cross.y < 0f) angle = -angle;

            bearing = angle;
            return true;
        }

        /// <summary>Green when aimed at the waypoint, yellow to the side, red when facing away.</summary>
        private static Color AlignmentColor(float bearingDegrees)
        {
            float alignment = 1f - Mathf.Clamp01(Mathf.Abs(bearingDegrees) / 180f);

            Color good = CachedColor(Plugin.ArrowColorGood.Value, ref _goodSource, ref _goodColor,
                                     new Color(0.30f, 0.88f, 0.30f));
            Color middle = CachedColor(Plugin.ArrowColorMiddle.Value, ref _middleSource, ref _middleColor,
                                       new Color(0.91f, 0.82f, 0.29f));
            Color bad = CachedColor(Plugin.ArrowColorBad.Value, ref _badSource, ref _badColor,
                                    new Color(0.88f, 0.31f, 0.31f));

            if (alignment >= 0.5f)
                return Color.Lerp(middle, good, (alignment - 0.5f) * 2f);
            return Color.Lerp(bad, middle, alignment * 2f);
        }

        /// <summary>Parses a hex colour only when the configured string actually changes.</summary>
        private static Color CachedColor(string hex, ref string cachedSource, ref Color cached, Color fallback)
        {
            if (!string.Equals(hex, cachedSource))
            {
                cachedSource = hex;
                Color parsed;
                if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out parsed))
                    cached = parsed;
                else
                    cached = fallback;
            }
            return cached;
        }

        private static string FormatDistance(float metres)
        {
            if (metres >= 1000f)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.00} km", metres / 1000f);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} m", metres);
        }

        /// <summary>Null when we are not closing on the target fast enough for an estimate to mean anything.</summary>
        private static string FormatEta(float distance, float speed)
        {
            if (speed <= 0.3f) return null;
            float seconds = distance / speed;
            if (seconds <= 0f || seconds > 3600f) return null;

            int total = Mathf.RoundToInt(seconds);
            int minutes = total / 60;
            int secs = total % 60;
            if (minutes > 0)
                return string.Format(CultureInfo.InvariantCulture, "~{0}m {1:00}s", minutes, secs);
            return string.Format(CultureInfo.InvariantCulture, "~{0}s", secs);
        }

        // ------------------------------------------------------------------ texture

        private static void EnsureTexture()
        {
            if (_arrow != null) return;
            _arrow = BuildArrowTexture(TextureResolution);
        }

        /// <summary>
        /// Rasterises the arrow: a white body with a black rim, everything else transparent.
        /// The rim is black so that tinting the texture (which multiplies) leaves the outline black
        /// while colouring the body, which lets one texture serve every arrow colour.
        ///
        /// The rim is produced from the distance to the nearest outline edge rather than by shrinking
        /// the polygon towards its centroid: a centroid shrink collapses to nothing near the tail
        /// notch, which sits almost on the centroid, and would leave that part of the arrow unoutlined.
        /// </summary>
        private static Texture2D BuildArrowTexture(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;

            Color32[] pixels = new Color32[size * size];
            const int samples = 4;                 // 4x4 supersampling for smooth outer edges
            float step = 1f / (samples + 1);
            const float rim = 0.055f;              // rim thickness in normalised units
            float feather = 1.5f / size;           // soften the body edge over ~1.5 pixels

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 1; sy <= samples; sy++)
                    {
                        for (int sx = 1; sx <= samples; sx++)
                        {
                            float sampleX = (x + sx * step) / size;
                            float sampleY = (y + sy * step) / size;
                            if (PointInPolygon(sampleX, sampleY, PolyX, PolyY)) hits++;
                        }
                    }

                    float alpha = (float)hits / (samples * samples);

                    float centreX = (x + 0.5f) / size;
                    float centreY = (y + 0.5f) / size;
                    float edgeDistance = DistanceToPolygonEdge(centreX, centreY);
                    float body = Mathf.Clamp01((edgeDistance - rim) / feather);

                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                    byte c = (byte)Mathf.RoundToInt(body * 255f);
                    pixels[y * size + x] = new Color32(c, c, c, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Shortest distance from a point to the arrow outline, in normalised units.</summary>
        private static float DistanceToPolygonEdge(float x, float y)
        {
            float best = float.MaxValue;
            int count = PolyX.Length;
            int j = count - 1;
            for (int i = 0; i < count; i++)
            {
                float d = DistanceToSegment(x, y, PolyX[j], PolyY[j], PolyX[i], PolyY[i]);
                if (d < best) best = d;
                j = i;
            }
            return best;
        }

        private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lengthSqr = dx * dx + dy * dy;

            float t = 0f;
            if (lengthSqr > 0f)
                t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / lengthSqr);

            float qx = ax + t * dx;
            float qy = ay + t * dy;
            float ex = px - qx;
            float ey = py - qy;
            return Mathf.Sqrt(ex * ex + ey * ey);
        }

        /// <summary>Even-odd containment test, which handles the concave notch in the arrow tail.</summary>
        private static bool PointInPolygon(float x, float y, float[] polyX, float[] polyY)
        {
            bool inside = false;
            int count = polyX.Length;
            int j = count - 1;
            for (int i = 0; i < count; i++)
            {
                if (((polyY[i] > y) != (polyY[j] > y)) &&
                    (x < (polyX[j] - polyX[i]) * (y - polyY[i]) / (polyY[j] - polyY[i]) + polyX[i]))
                {
                    inside = !inside;
                }
                j = i;
            }
            return inside;
        }
    }
}
