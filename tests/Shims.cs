// Minimal stand-ins so CoordinateParser.cs can be compiled and exercised outside Unity.
using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public override string ToString() { return string.Format("({0}, {1}, {2})", x, y, z); }
    }

    public static class Mathf
    {
        public static float Abs(float f) { return Math.Abs(f); }
    }
}

namespace Waypointer
{
    // Stands in for the BepInEx config entry the real Plugin exposes.
    public class FakeEntry
    {
        public bool Value;
    }

    public static class Plugin
    {
        public static FakeEntry RawValheimOrder = new FakeEntry();
    }
}
