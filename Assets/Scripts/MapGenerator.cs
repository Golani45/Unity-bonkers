using UnityEngine;

/// <summary>
/// MVP Map Generator (10x10 fixed):
/// - Generates a 10x10 grid of ground tiles
/// - Perlin noise heightmap + optional chunk slope feature
/// - Spawns per-tile loot:
///   * Jars (XP or Gold): min 3, max 5
///   * Chests: min 3, max 5
///   * Neighbor bias: if a tile has 5, adjacent tiles get biased (10% => 5, 80% => 3, else 4)
/// - Spawns per-tile environment:
///   * Guarantees 5 different categories (trees/rocks/bushes/dirt/grass)
///   * Spawns at least 6 total environment objects per tile (one extra random by default)
/// </summary>
public class MapGenerator : MonoBehaviour
{
    private const int MAP_SIZE = 10;

    [Header("Map Dimensions (forced to 10x10 at runtime)")]
    [Min(1)] public int width = MAP_SIZE;
    [Min(1)] public int height = MAP_SIZE;

    [Tooltip("Distance between tiles in world units.")]
    public float tileSize = 2f;

    [Header("Ground Tile")]
    [Tooltip("Prefab used as the ground tile.")]
    public GameObject groundTilePrefab;

    [Tooltip("Optional material applied to the ground tile renderer (sharedMaterial).")]
    public Material groundMaterial;

    [Header("Seed (repeatable runs)")]
    public bool useRandomSeed = true;
    public int seed = 12345;

    [Header("Height / Terrain Noise")]
    [Tooltip("Noise scale (higher = smoother, lower = more variation).")]
    public float heightNoiseScale = 0.12f;

    [Tooltip("Max height amplitude in world units.")]
    public float heightAmplitude = 2.0f;

    [Tooltip("Optional stepping of height (0 = none). Example 0.5 makes heights snap to 0.5 increments.")]
    public float heightStep = 0.5f;

    [Header("Chunk Slope Feature (optional variation)")]
    [Tooltip("Chunk size in tiles.")]
    public int chunkSize = 8;

    [Range(0f, 1f)]
    [Tooltip("Chance each chunk gets a slope feature.")]
    public float slopeFeatureChancePerChunk = 0.35f;

    [Tooltip("Strength of slope feature (world units).")]
    public float slopeFeatureStrength = 1.25f;

    [Header("Loot: Jars (XP/Gold)")]
    [Tooltip("Small jar prefabs that represent XP jars.")]
    public GameObject[] xpJarPrefabs;

    [Tooltip("Small jar prefabs that represent Gold jars.")]
    public GameObject[] goldJarPrefabs;

    public float jarScatterRadius = 0.7f;
    public float jarYOffset = 0.05f;
    public Vector2 jarScaleRange = new Vector2(0.9f, 1.1f);

    [Header("Loot: Chests")]
    public GameObject[] chestPrefabs;

    public float chestScatterRadius = 0.6f;
    public float chestYOffset = 0.05f;
    public Vector2 chestScaleRange = new Vector2(0.95f, 1.05f);

    [Header("Neighbor Bias (applies to BOTH jars and chests)")]
    [Range(0f, 1f)] public float neighborFiveChance = 0.10f;   // 10% chance to spawn 5
    [Range(0f, 1f)] public float neighborThreeChance = 0.80f;  // 80% chance to spawn 3
    // remaining probability => spawn 4

    [Header("Environment (per tile)")]
    public GameObject[] treePrefabs;
    public GameObject[] rockPrefabs;
    public GameObject[] bushPrefabs;
    public GameObject[] dirtPatchPrefabs;
    public GameObject[] grassPatchPrefabs;

    [Min(6)]
    [Tooltip("Minimum number of environment spawns per tile. Must be >= 6 to satisfy requirements.")]
    public int envSpawnsPerTileMin = 6;

    public float envScatterRadius = 0.9f;
    public float envYOffset = 0.0f;
    public Vector2 envScaleRange = new Vector2(0.9f, 1.2f);

    // Generated data
    private Transform _root;
    private float[,] _heights;
    private System.Random _rng;

    [ContextMenu("Generate")]
    public void Generate()
    {
        // Force fixed size every time
        width = MAP_SIZE;
        height = MAP_SIZE;

        // Clamp chunk size for 10x10
        chunkSize = Mathf.Clamp(chunkSize, 1, MAP_SIZE);

        if (groundTilePrefab == null)
        {
            Debug.LogError("MapGenerator missing groundTilePrefab.");
            return;
        }

        // Validate environment categories (required by spec)
        if (!HasAtLeastOne(treePrefabs) ||
            !HasAtLeastOne(rockPrefabs) ||
            !HasAtLeastOne(bushPrefabs) ||
            !HasAtLeastOne(dirtPatchPrefabs) ||
            !HasAtLeastOne(grassPatchPrefabs))
        {
            Debug.LogError(
                "Missing environment prefabs. To guarantee 5 different environment types per tile, " +
                "assign at least one prefab for: trees, rocks, bushes, dirt patches, grass patches."
            );
            return;
        }

        // Enforce minimum environment spawns per tile
        envSpawnsPerTileMin = Mathf.Max(envSpawnsPerTileMin, 6);

        ClearOld();

        if (useRandomSeed)
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _rng = new System.Random(seed);

        _root = new GameObject($"GeneratedMap_10x10_seed_{seed}").transform;

        _heights = new float[width, height];

        float offX = NextFloat(-10000f, 10000f);
        float offZ = NextFloat(-10000f, 10000f);

        // STEP 1: Heightmap via Perlin noise
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                float n = Mathf.PerlinNoise(offX + x * heightNoiseScale,
                                            offZ + z * heightNoiseScale);

                float h = (n - 0.5f) * 2f * heightAmplitude;

                if (heightStep > 0.0001f)
                    h = Mathf.Round(h / heightStep) * heightStep;

                _heights[x, z] = h;
            }
        }

        // STEP 2: Optional chunk slope features
        ApplyChunkSlopeFeatures();

        // STEP 3: Spawn ground tiles
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 pos = GridToWorld(x, z, _heights[x, z]);
                var tile = Instantiate(groundTilePrefab, pos, Quaternion.identity, _root);
                TryApplyGroundMaterial(tile, groundMaterial);
            }
        }

        // STEP 4: Environment (guaranteed per tile)
        SpawnEnvironmentEveryTile();

        // STEP 5: Loot (jars + chests per tile) with neighbor bias
        SpawnLootEveryTile();

        Debug.Log($"Generated FIXED 10x10 map with seed {seed}");
    }

    // ----------------------------
    // Terrain features
    // ----------------------------
    private void ApplyChunkSlopeFeatures()
    {
        int chunksX = Mathf.CeilToInt(width / (float)chunkSize);
        int chunksZ = Mathf.CeilToInt(height / (float)chunkSize);

        for (int cz = 0; cz < chunksZ; cz++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                if (NextFloat01() > slopeFeatureChancePerChunk)
                    continue;

                bool slopeAlongX = NextBool();

                int startX = cx * chunkSize;
                int startZ = cz * chunkSize;
                int endX = Mathf.Min(startX + chunkSize, width);
                int endZ = Mathf.Min(startZ + chunkSize, height);

                for (int z = startZ; z < endZ; z++)
                {
                    for (int x = startX; x < endX; x++)
                    {
                        float t = slopeAlongX
                            ? (x - startX) / Mathf.Max(1f, (endX - startX - 1f))
                            : (z - startZ) / Mathf.Max(1f, (endZ - startZ - 1f));

                        // triangle curve: 0 -> 1 -> 0
                        float tri = 1f - Mathf.Abs(2f * t - 1f);

                        float delta = (tri - 0.5f) * 2f * slopeFeatureStrength;
                        _heights[x, z] += delta;

                        if (heightStep > 0.0001f)
                            _heights[x, z] = Mathf.Round(_heights[x, z] / heightStep) * heightStep;
                    }
                }
            }
        }
    }

    // ----------------------------
    // Environment spawning (per tile)
    // ----------------------------
    private void SpawnEnvironmentEveryTile()
    {
        int perTile = Mathf.Max(envSpawnsPerTileMin, 6);

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 tileBase = GridToWorld(x, z, _heights[x, z]);

                // Guarantee 5 different categories per tile (one of each)
                SpawnEnvFromCategory(treePrefabs, tileBase);
                SpawnEnvFromCategory(rockPrefabs, tileBase);
                SpawnEnvFromCategory(bushPrefabs, tileBase);
                SpawnEnvFromCategory(dirtPatchPrefabs, tileBase);
                SpawnEnvFromCategory(grassPatchPrefabs, tileBase);

                // Spawn additional random environment objects to reach minimum
                int remaining = perTile - 5;
                for (int i = 0; i < remaining; i++)
                {
                    int cat = NextInt(0, 5);
                    switch (cat)
                    {
                        case 0: SpawnEnvFromCategory(treePrefabs, tileBase); break;
                        case 1: SpawnEnvFromCategory(rockPrefabs, tileBase); break;
                        case 2: SpawnEnvFromCategory(bushPrefabs, tileBase); break;
                        case 3: SpawnEnvFromCategory(dirtPatchPrefabs, tileBase); break;
                        default: SpawnEnvFromCategory(grassPatchPrefabs, tileBase); break;
                    }
                }
            }
        }
    }

    private void SpawnEnvFromCategory(GameObject[] prefabs, Vector3 tileBase)
    {
        GameObject prefab = prefabs[NextInt(0, prefabs.Length)];

        Vector2 offset = RandomInsideCircle(envScatterRadius);
        Vector3 pos = tileBase + new Vector3(offset.x, envYOffset, offset.y);

        var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(0f, 360f), 0f), _root);

        float s = NextFloat(envScaleRange.x, envScaleRange.y);
        go.transform.localScale *= s;
    }

    // ----------------------------
    // Loot spawning (per tile) with neighbor bias
    // ----------------------------
    private void SpawnLootEveryTile()
    {
        // We decide counts tile-by-tile using a deterministic scan order.
        // Neighbor bias considers already-decided neighbors in the 8-neighborhood (within scan constraints).
        int[,] jarCounts = new int[width, height];
        int[,] chestCounts = new int[width, height];

        // Decide counts first
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                bool nearFiveJars = HasPriorNeighborWithFive(jarCounts, x, z);
                bool nearFiveChests = HasPriorNeighborWithFive(chestCounts, x, z);

                jarCounts[x, z] = DecideCount3to5(nearFiveJars);
                chestCounts[x, z] = DecideCount3to5(nearFiveChests);
            }
        }

        // Spawn based on decided counts
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 tileBase = GridToWorld(x, z, _heights[x, z]);

                SpawnJarsOnTile(tileBase, jarCounts[x, z]);
                SpawnChestsOnTile(tileBase, chestCounts[x, z]);
            }
        }
    }

    private int DecideCount3to5(bool hasNeighborWithFive)
    {
        // Base case (no neighbor bias): uniform random 3..5
        if (!hasNeighborWithFive)
            return NextInt(3, 6); // 3,4,5

        // Bias case:
        // 80% => 3
        // 10% => 5
        // remaining 10% => 4
        float r = NextFloat01();

        if (r < neighborThreeChance) return 3;
        if (r < neighborThreeChance + neighborFiveChance) return 5;
        return 4;
    }

    /// <summary>
    /// Checks already-decided neighbors (scan-order safe) for a value of 5.
    /// This gives stable MVP behavior without needing an iterative solver.
    /// We include: left, down, down-left, down-right (which covers the local "surrounding" influence during scan).
    /// </summary>
    private bool HasPriorNeighborWithFive(int[,] counts, int x, int z)
    {
        if (x - 1 >= 0 && counts[x - 1, z] == 5) return true;
        if (z - 1 >= 0 && counts[x, z - 1] == 5) return true;
        if (x - 1 >= 0 && z - 1 >= 0 && counts[x - 1, z - 1] == 5) return true;
        if (x + 1 < width && z - 1 >= 0 && counts[x + 1, z - 1] == 5) return true;

        return false;
    }

    private void SpawnJarsOnTile(Vector3 tileBase, int count)
    {
        bool hasXp = HasAtLeastOne(xpJarPrefabs);
        bool hasGold = HasAtLeastOne(goldJarPrefabs);

        if (!hasXp && !hasGold) return;

        count = Mathf.Clamp(count, 3, 5);

        for (int i = 0; i < count; i++)
        {
            bool spawnXp = hasXp && (!hasGold || NextBool());

            GameObject[] pool = spawnXp ? xpJarPrefabs : goldJarPrefabs;
            GameObject prefab = pool[NextInt(0, pool.Length)];

            Vector2 offset = RandomInsideCircle(jarScatterRadius);
            Vector3 pos = tileBase + new Vector3(offset.x, jarYOffset, offset.y);

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(0f, 360f), 0f), _root);

            float s = NextFloat(jarScaleRange.x, jarScaleRange.y);
            go.transform.localScale *= s;
        }
    }

    private void SpawnChestsOnTile(Vector3 tileBase, int count)
    {
        if (!HasAtLeastOne(chestPrefabs)) return;

        count = Mathf.Clamp(count, 3, 5);

        for (int i = 0; i < count; i++)
        {
            GameObject prefab = chestPrefabs[NextInt(0, chestPrefabs.Length)];

            Vector2 offset = RandomInsideCircle(chestScatterRadius);
            Vector3 pos = tileBase + new Vector3(offset.x, chestYOffset, offset.y);

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(0f, 360f), 0f), _root);

            float s = NextFloat(chestScaleRange.x, chestScaleRange.y);
            go.transform.localScale *= s;
        }
    }

    // ----------------------------
    // Utility
    // ----------------------------
    private Vector3 GridToWorld(int x, int z, float y)
    {
        return new Vector3(x * tileSize, y, z * tileSize);
    }

    private void TryApplyGroundMaterial(GameObject tile, Material mat)
    {
        if (mat == null) return;

        var r = tile.GetComponentInChildren<Renderer>();
        if (r != null) r.sharedMaterial = mat;
    }

    private Vector2 RandomInsideCircle(float radius)
    {
        float ang = NextFloat(0f, Mathf.PI * 2f);
        float r = Mathf.Sqrt(NextFloat01()) * radius;
        return new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r);
    }

    private bool HasAtLeastOne(GameObject[] arr) => arr != null && arr.Length > 0;

    // Deterministic RNG helpers
    private float NextFloat01() => (float)_rng.NextDouble();
    private bool NextBool() => _rng.Next(0, 2) == 0;
    private float NextFloat(float min, float max) => min + NextFloat01() * (max - min);
    private int NextInt(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);

    private void ClearOld()
    {
        // MVP: find and remove prior generated maps
        var all = GameObject.FindObjectsOfType<Transform>();
        foreach (var t in all)
        {
            if (t.name.StartsWith("GeneratedMap_"))
                DestroyImmediate(t.gameObject);
        }
    }
}
