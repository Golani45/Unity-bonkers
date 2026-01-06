using UnityEngine;
[CreateAssetMenu(menuName = "ProcGen/Biome Data", fileName = "BiomeData")]
public class BiomeData : ScriptableObject
{
    [Header("Identity")]
    public string biomeName = "Forest";
    [Header("Ground")]
    public Material groundMaterial;
    [Header("Environment Prefabs")]
    public GameObject[] treePrefabs;
    public GameObject[] rockPrefabs;
    public GameObject[] bushPrefabs;
    [Header("Noise tuning (optional)")]
    public float largeAmpMultiplier = 1f;
    public float ridgeAmpMultiplier = 1f;
    public float detailAmpMultiplier = 1f;
    [Header("Optional POIs for this biome")]
    public GameObject[] poiPrefabs;
}