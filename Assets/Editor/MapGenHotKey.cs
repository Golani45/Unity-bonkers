#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class MapGenHotkey
{
    // Ctrl + G (Windows) / Cmd + G (Mac)
    [MenuItem("Tools/ProcGen/Generate Map %&g")]

    public static void GenerateMap()
    {
        // Find MapGenerator in the scene
        MapGenerator gen = Object.FindFirstObjectByType<MapGenerator>();

        if (gen == null)
        {
            Debug.LogWarning("No MapGenerator found in the scene.");
            return;
        }

        gen.Generate();

        // Focus it so you can immediately tweak values again
        Selection.activeObject = gen.gameObject;

        Debug.Log("Map regenerated via hotkey");
    }
}
#endif