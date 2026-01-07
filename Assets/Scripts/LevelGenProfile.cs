using UnityEngine;

[CreateAssetMenu(menuName = "ProcGen/Level Gen Profile", fileName = "LevelGenProfile")]
public class LevelGenProfile : ScriptableObject
{
    [Header("References (required)")]
    public BiomeData biome;

    [Header("Map Size (tiles)")]
    [Min(1)] public int width = 10;
    [Min(1)] public int height = 10;

    [Header("Tile World Size (units)")]
    [Tooltip("Old Unity Plane was 10x10 units, so 10 is a good default.")]
    public float tileSize = 10f;

    [Header("Mesh Resolution")]
    [Tooltip("Verts per tile edge. 8 = good MVP. 12-16 = smoother but heavier collider.")]
    [Min(2)] public int vertsPerTile = 8;

    [Header("Terrain Shape")]
    public float largeScale = 0.08f;
    public float largeAmp = 12f;

    public float ridgeScale = 0.17f;
    public float ridgeAmp = 4f;

    public float detailScale = 0.55f;
    public float detailAmp = 0.6f;

    [Range(0f, 1f)] public float valleyStrength = 0.70f;
    public float valleyScale = 0.05f;

    [Header("Optional Domain Warp")]
    public bool useDomainWarp = true;
    public float warpScale = 0.12f;
    public float warpStrength = 2.5f;

    [Header("Optional stepping (0 = smooth)")]
    public float heightStep = 0f;

    [Header("Playability (prevents walls)")]
    [Tooltip("Max height change allowed between neighboring vertices (in world units). Lower = more rollable.")]
    public float maxDeltaPerVertex = 1.25f;

    [Tooltip("0..5 typical. Smooths noisy spikes while keeping hills.")]
    [Range(0, 10)] public int smoothingPasses = 2;

    [Header("Environment Density")]
    [Min(0)] public int envSpawnsPerTileMin = 6;

    [Header("Spawn Randomness")]
    [Tooltip("Fraction of HALF tile size")]
    [Range(0f, 1f)] public float envScatterRadius = 0.45f;

    [Tooltip("Fraction of HALF tile size")]
    [Range(0f, 1f)] public float perTileJitterRadius = 0.18f;

    [Tooltip("Fraction of HALF tile size")]
    [Range(0f, 1f)] public float minSeparationInTile = 0.10f;

    [Min(1)] public int placementAttempts = 12;

    [Header("POI (spawn once)")]
    public bool spawnPOI = true;
    [Tooltip("If empty, uses biome.poiPrefabs")]
    public GameObject[] poiPrefabsOverride;
    public float poiReserveRadius = 8f;
    public int poiPlacementAttempts = 50;

    [Header("Seed")]
    public bool useRandomSeed = true;
    public int seed = 12345;
}
