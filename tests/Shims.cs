// Minimal stand-ins so CoordinateParser.cs and Hotkeys.cs can be compiled and exercised outside Unity.
using System;
using System.Collections.Generic;

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

    // The few KeyCodes the Hotkeys tests use, with Unity's values.
    public enum KeyCode
    {
        None = 0,
        Plus = 43,
        F11 = 292,
        F13 = 294,
        LeftAlt = 308,
        WheelUp = 321
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigDefinition
    {
        public ConfigDefinition(string section, string key) { Section = section; Key = key; }
        public string Section { get; private set; }
        public string Key { get; private set; }
    }

    public class ConfigEntry<T>
    {
        public ConfigEntry(string section, string key, T value)
        {
            Definition = new ConfigDefinition(section, key);
            Value = value;
        }
        public ConfigDefinition Definition { get; private set; }
        public T Value { get; set; }
    }
}

// Stands in for Valheim's ZInput (global namespace, assembly_utils). A key in Unmapped throws the way 1.0.16 does
// for a KeyCode missing from its KeyCode-to-Key table: Keyboard.current[Key.None] -> ArgumentOutOfRangeException.
// A key in Broken fails with another exception type, standing in for any other way a read could fail.
// LastRead records the method, the key and the logWarning argument, e.g. "GetKeyDown:F11:False".
public static class ZInput
{
    public static readonly List<UnityEngine.KeyCode> Unmapped = new List<UnityEngine.KeyCode>();
    public static readonly List<UnityEngine.KeyCode> Broken = new List<UnityEngine.KeyCode>();
    public static readonly List<UnityEngine.KeyCode> Down = new List<UnityEngine.KeyCode>();
    public static int Reads;
    public static string LastRead;

    public static void Reset()
    {
        Unmapped.Clear();
        Unmapped.Add(UnityEngine.KeyCode.Plus);
        Unmapped.Add(UnityEngine.KeyCode.WheelUp);
        Broken.Clear();
        Broken.Add(UnityEngine.KeyCode.F13);
        Down.Clear();
        Reads = 0;
        LastRead = null;
    }

    public static bool GetKeyDown(UnityEngine.KeyCode key, bool logWarning = true) { return Read("GetKeyDown", key, logWarning); }
    public static bool GetKey(UnityEngine.KeyCode key, bool logWarning = true) { return Read("GetKey", key, logWarning); }

    private static bool Read(string how, UnityEngine.KeyCode key, bool logWarning)
    {
        Reads++;
        LastRead = how + ":" + key + ":" + logWarning;
        if (Unmapped.Contains(key)) throw new ArgumentOutOfRangeException("key: None");
        if (Broken.Contains(key)) throw new InvalidOperationException("broken input device");
        return Down.Contains(key);
    }
}

namespace Waypointer
{
    // Stands in for the BepInEx config entry the real Plugin exposes.
    public class FakeEntry
    {
        public bool Value;
    }

    // Stands in for BepInEx's ManualLogSource: keeps the warnings so a test can count them.
    public class FakeLog
    {
        public readonly List<string> Warnings = new List<string>();
        public void LogWarning(object data) { Warnings.Add(data.ToString()); }
    }

    public static class Plugin
    {
        public static FakeEntry RawValheimOrder = new FakeEntry();
        public static FakeLog Log = new FakeLog();
    }
}
