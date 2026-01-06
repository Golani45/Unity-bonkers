using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// MVP Map Generator (fixed 10x10):
/// - Spawns a 10x10 grid of ground tiles in X/Z (horizontal grid)
/// - Adds elevation via Perlin / Fractal Perlin (hills)
/// - Auto-detects tile footprint (width/depth/thickness) from groundTilePrefab so tiles don't overlap
/// - Spawns per-tile loot (jars + chests) with neighbor bias (3..5)
/// - Spawns per-tile environment (trees/rocks/bushes required; dirt/grass optional)
/// - Improved spawn randomness:
///     * Per-tile RNG (prevents repeating “same random” patterns across tiles)
///     * Per-tile jitter (each tile has a different spawn "center")
///     * Minimum separation within a tile to avoid stacking/clumping
/// - Keeps hierarchy clean (Tiles / Environment / Loot)
/// </summary>
public class MapGenerator : MonoBehaviour
{
    private const int MAP_SIZE = 10;

    [Header("Map Dimensions (forced to 10x10 at runtime)")]
    [Min(1)] public int width = MAP_SIZE;
    [Min(1)] public int height = MAP_SIZE;

    [Header("Ground Tile")]
    [Tooltip("Prefab used as the ground tile.")]
    public GameObject groundTilePrefab;

    [Tooltip("Optional material applied to the ground tile renderer (sharedMaterial).")]
    public Material groundMaterial;

    [Header("Tile Spacing (IMPORTANT)")]
    [Tooltip("If ON, we measure the prefab bounds and use that as spacing so tiles don't overlap (recommended).")]
    public bool autoSpacingFromPrefab = true;

    [Tooltip("Extra spacing multiplier. 1 = flush tiles, 1.05 adds a small gap.")]
    public float spacingMultiplier = 1.0f;

    [Tooltip("If your prefab pivot is centered (typical cube), we add half thickness so it sits correctly. If pivot is already at bottom, turn this OFF.")]
    public bool tilePivotIsCentered = true;

    [Header("Seed (repeatable runs)")]
    public bool useRandomSeed = true;
    public int seed = 12345;

    [Header("Height / Terrain Noise (Hills)")]
    [Tooltip("Noise scale: higher = more frequent hills on a small map.")]
    public float heightNoiseScale = 0.25f;

    [Tooltip("Max height amplitude in world units. Increase for stronger hills.")]
    public float heightAmplitude = 0.8f;

    [Tooltip("Optional stepping of height (0 = none). Keep 0 for smooth hills.")]
    public float heightStep = 0f;

    [Header("Fractal Noise (recommended for nicer hills)")]
    [Tooltip("If ON, uses multiple Perlin octaves to create richer hills.")]
    public bool useFractalNoise = true;

    [Range(1, 8)] public int octaves = 4;
    [Range(0.3f, 0.8f)] public float persistence = 0.5f;
    [Range(1.5f, 3.5f)] public float lacunarity = 2.0f;

    [Header("Chunk Slope Feature (optional)")]
    [Tooltip("Chunk size in tiles.")]
    public int chunkSize = 8;

    [Range(0f, 1f)]
    [Tooltip("Chance each chunk gets a slope feature.")]
    public float slopeFeatureChancePerChunk = 0.25f;

    [Tooltip("Strength of slope feature (world units).")]
    public float slopeFeatureStrength = 0.4f;

    [Header("Loot: Jars (XP/Gold)")]
    public GameObject[] xpJarPrefabs;
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
    [Range(0f, 1f)] public float neighborFiveChance = 0.10f;
    [Range(0f, 1f)] public float neighborThreeChance = 0.80f;
    // remaining probability => 4

    [Header("Environment (trees/rocks/bushes required; dirt/grass optional)")]
    public GameObject[] treePrefabs;
    public GameObject[] rockPrefabs;
    public GameObject[] bushPrefabs;
    public GameObject[] dirtPatchPrefabs;   // optional
    public GameObject[] grassPatchPrefabs;  // optional

    [Min(0)]
    [Tooltip("Minimum number of environment spawns per tile.")]
    public int envSpawnsPerTileMin = 6;

    public float envScatterRadius = 0.9f;
    public float envYOffset = 0.0f;
    public Vector2 envScaleRange = new Vector2(0.9f, 1.2f);

    [Header("Spawn Randomness Improvements")]
    [Tooltip("Shifts each tile's spawn center randomly so patterns don't repeat across the grid.")]
    public float perTileJitterRadius = 0.5f;

    [Tooltip("Minimum distance between spawned objects within the same tile (prevents stacking/clumping).")]
    public float minSeparationInTile = 0.35f;

    [Tooltip("Attempts to find a non-overlapping spot before placing anyway.")]
    public int placementAttempts = 12;

    // Generated data / runtime
    private Transform _root;
    private Transform _tilesRoot;
    private Transform _envRoot;
    private Transform _lootRoot;

    private float[,] _heights;
    private System.Random _rng; // global RNG for map-level decisions (heights/chunks/etc)

    // measured from prefab
    private float _tileFootprintX = 2f;
    private float _tileFootprintZ = 2f;
    private float _tileThicknessY = 0.2f;

    // offsets for noise
    private float _offX;
    private float _offZ;

    [ContextMenu("Generate")]
    public void Generate()
    {
        width = MAP_SIZE;
        height = MAP_SIZE;

        chunkSize = Mathf.Clamp(chunkSize, 1, MAP_SIZE);

        if (groundTilePrefab == null)
        {
            Debug.LogError("MapGenerator missing groundTilePrefab.");
            return;
        }

        if (!HasAtLeastOne(treePrefabs) || !HasAtLeastOne(rockPrefabs) || !HasAtLeastOne(bushPrefabs))
        {
            Debug.LogError("Missing environment prefabs. Assign at least one prefab for: trees, rocks, bushes. Dirt/grass are optional.");
            return;
        }

        ClearOld();

        if (useRandomSeed)
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _rng = new System.Random(seed);

        _root = new GameObject($"GeneratedMap_10x10_seed_{seed}").transform;
        _tilesRoot = new GameObject("Tiles").transform; _tilesRoot.SetParent(_root, false);
        _envRoot = new GameObject("Environment").transform; _envRoot.SetParent(_root, false);
        _lootRoot = new GameObject("Loot").transform; _lootRoot.SetParent(_root, false);

        MeasureGroundPrefab();

        _heights = new float[width, height];

        _offX = NextFloat(_rng, -10000f, 10000f);
        _offZ = NextFloat(_rng, -10000f, 10000f);

        BuildHeightmap();
        ApplyChunkSlopeFeatures();

        SpawnTiles();
        SpawnEnvironmentEveryTile();
        SpawnLootEveryTile();

        Debug.Log($"Generated 10x10 grid with seed {seed}. Tile spacing: {_tileFootprintX:F2} x {_tileFootprintZ:F2}");
    }

    // ----------------------------
    // Per-tile RNG (prevents repeating patterns)
    // ----------------------------
    private System.Random MakeTileRng(int x, int z, int salt)
    {
        unchecked
        {
            int h = seed;
            h = h * 486187739 + x * 73856093;
            h = h * 486187739 + z * 19349663;
            h = h * 486187739 + salt * 83492791;
            return new System.Random(h);
        }
    }

    // RNG helpers (tile or global)
    private float NextFloat01(System.Random r) => (float)r.NextDouble();
    private bool NextBool(System.Random r) => r.Next(0, 2) == 0;
    private float NextFloat(System.Random r, float min, float max) => min + NextFloat01(r) * (max - min);
    private int NextInt(System.Random r, int minInclusive, int maxExclusive) => r.Next(minInclusive, maxExclusive);

    // ----------------------------
    // Prefab measurement & spacing
    // ----------------------------
    private void MeasureGroundPrefab()
    {
        GameObject sample = Instantiate(groundTilePrefab);
        sample.name = "__MEASURE_SAMPLE__";
        sample.hideFlags = HideFlags.HideAndDontSave;
        sample.SetActive(false);
        sample.transform.position = Vector3.zero;
        sample.transform.rotation = Quaternion.identity;
        sample.transform.localScale = Vector3.one;

        Bounds b;
        if (!TryGetWorldBounds(sample, out b))
        {
            Debug.LogWarning("Could not measure groundTilePrefab bounds. Falling back to 2x2 footprint.");
            _tileFootprintX = 2f;
            _tileFootprintZ = 2f;
            _tileThicknessY = 0.2f;
        }
        else
        {
            _tileFootprintX = Mathf.Max(0.01f, b.size.x) * spacingMultiplier;
            _tileFootprintZ = Mathf.Max(0.01f, b.size.z) * spacingMultiplier;
            _tileThicknessY = Mathf.Max(0.001f, b.size.y);
        }

#if UNITY_EDITOR
        DestroyImmediate(sample);
#else
        Destroy(sample);
#endif

        if (!autoSpacingFromPrefab)
            Debug.LogWarning("autoSpacingFromPrefab is OFF. If your tiles overlap, turn it ON. Default Unity Plane is 10x10 units.");
    }

    private bool TryGetWorldBounds(GameObject go, out Bounds bounds)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            bounds = new Bounds(go.transform.position, Vector3.zero);
            return false;
        }

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return true;
    }

    // ----------------------------
    // Heightmap / Hills
    // ----------------------------
    private void BuildHeightmap()
    {
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                float n = useFractalNoise
                    ? FractalPerlin(_offX, _offZ, x, z)
                    : Mathf.PerlinNoise(_offX + x * heightNoiseScale, _offZ + z * heightNoiseScale);

                float h = (n - 0.5f) * 2f * heightAmplitude;

                if (heightStep > 0.0001f)
                    h = Mathf.Round(h / heightStep) * heightStep;

                _heights[x, z] = h;
            }
        }
    }

    private float FractalPerlin(float offX, float offZ, int x, int z)
    {
        float amp = 1f;
        float freq = 1f;
        float sum = 0f;
        float norm = 0f;

        for (int i = 0; i < octaves; i++)
        {
            float nx = offX + (x * heightNoiseScale * freq);
            float nz = offZ + (z * heightNoiseScale * freq);

            float p = Mathf.PerlinNoise(nx, nz);
            sum += p * amp;
            norm += amp;

            amp *= persistence;
            freq *= lacunarity;
        }

        return sum / Mathf.Max(0.0001f, norm);
    }

    private void ApplyChunkSlopeFeatures()
    {
        int chunksX = Mathf.CeilToInt(width / (float)chunkSize);
        int chunksZ = Mathf.CeilToInt(height / (float)chunkSize);

        for (int cz = 0; cz < chunksZ; cz++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                if (NextFloat01(_rng) > slopeFeatureChancePerChunk)
                    continue;

                bool slopeAlongX = NextBool(_rng);

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
    // Tile spawning
    // ----------------------------
    private void SpawnTiles()
    {
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 pos = GridToWorld(x, z, _heights[x, z]);
                var tile = Instantiate(groundTilePrefab, pos, Quaternion.identity, _tilesRoot);
                TryApplyGroundMaterial(tile, groundMaterial);
            }
        }
    }

    private Vector3 GridToWorld(int x, int z, float y)
    {
        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 2f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 2f;

        float yOffset = tilePivotIsCentered ? (_tileThicknessY * 0.5f) : 0f;

        return new Vector3(
            x * spacingX,
            y + yOffset,
            z * spacingZ
        );
    }

    private void TryApplyGroundMaterial(GameObject tile, Material mat)
    {
        if (mat == null) return;
        var r = tile.GetComponentInChildren<Renderer>();
        if (r != null) r.sharedMaterial = mat;
    }

    // ----------------------------
    // Random placement helpers (tile RNG aware)
    // ----------------------------
    private Vector2 RandomInsideCircle(System.Random r, float radius)
    {
        float ang = NextFloat(r, 0f, Mathf.PI * 2f);
        float rr = Mathf.Sqrt(NextFloat01(r)) * radius;
        return new Vector2(Mathf.Cos(ang) * rr, Mathf.Sin(ang) * rr);
    }

    private Vector3 GetTileSpawnCenter(Vector3 tileBase, System.Random tileRng)
    {
        Vector2 jitter = RandomInsideCircle(tileRng, perTileJitterRadius);
        return tileBase + new Vector3(jitter.x, 0f, jitter.y);
    }

    private Vector2 RandomPointWithMinSeparation(System.Random r, float radius, List<Vector2> used, float minDist, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            Vector2 p = RandomInsideCircle(r, radius);

            bool ok = true;
            for (int j = 0; j < used.Count; j++)
            {
                if (Vector2.Distance(p, used[j]) < minDist)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                used.Add(p);
                return p;
            }
        }

        Vector2 fallback = RandomInsideCircle(r, radius);
        used.Add(fallback);
        return fallback;
    }

    // ----------------------------
    // Environment spawning (dirt/grass optional) - per-tile RNG + jitter + separation
    // ----------------------------
    private void SpawnEnvironmentEveryTile()
    {
        int perTile = Mathf.Max(envSpawnsPerTileMin, 0);

        var categories = new List<GameObject[]>();
        if (HasAtLeastOne(treePrefabs)) categories.Add(treePrefabs);
        if (HasAtLeastOne(rockPrefabs)) categories.Add(rockPrefabs);
        if (HasAtLeastOne(bushPrefabs)) categories.Add(bushPrefabs);
        if (HasAtLeastOne(dirtPatchPrefabs)) categories.Add(dirtPatchPrefabs);
        if (HasAtLeastOne(grassPatchPrefabs)) categories.Add(grassPatchPrefabs);

        if (categories.Count == 0 || perTile <= 0) return;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                // Per-tile RNG removes repeating patterns.
                var tileRng = MakeTileRng(x, z, 101);

                Vector3 tileBase = GridToWorld(x, z, _heights[x, z]);
                Vector3 center = GetTileSpawnCenter(tileBase, tileRng);

                // Keep objects from stacking on top of each other inside this tile
                var used = new List<Vector2>(Mathf.Max(perTile, categories.Count));

                // One of each category
                for (int i = 0; i < categories.Count; i++)
                    SpawnEnvFromCategory(categories[i], center, used, tileRng);

                int spawned = categories.Count;

                // Fill remaining randomly
                while (spawned < perTile)
                {
                    var chosen = categories[NextInt(tileRng, 0, categories.Count)];
                    SpawnEnvFromCategory(chosen, center, used, tileRng);
                    spawned++;
                }
            }
        }
    }

    private void SpawnEnvFromCategory(GameObject[] prefabs, Vector3 center, List<Vector2> used, System.Random tileRng)
    {
        if (!HasAtLeastOne(prefabs)) return;

        GameObject prefab = prefabs[NextInt(tileRng, 0, prefabs.Length)];

        Vector2 offset = RandomPointWithMinSeparation(
            tileRng,
            envScatterRadius,
            used,
            minSeparationInTile,
            placementAttempts
        );

        Vector3 pos = center + new Vector3(offset.x, envYOffset, offset.y);

        var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _envRoot);

        float s = NextFloat(tileRng, envScaleRange.x, envScaleRange.y);
        go.transform.localScale *= s;
    }

    // ----------------------------
    // Loot spawning (per tile) with neighbor bias - per-tile RNG + jitter + separation
    // ----------------------------
    private void SpawnLootEveryTile()
    {
        int[,] jarCounts = new int[width, height];
        int[,] chestCounts = new int[width, height];

        // Decide counts (global RNG, stable scan order)
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

        // Spawn using per-tile RNG so placement doesn't repeat
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                var tileRng = MakeTileRng(x, z, 202); // different salt from env

                Vector3 tileBase = GridToWorld(x, z, _heights[x, z]);
                Vector3 center = GetTileSpawnCenter(tileBase, tileRng);

                var used = new List<Vector2>(12);

                SpawnJarsOnTile(center, used, jarCounts[x, z], tileRng);
                SpawnChestsOnTile(center, used, chestCounts[x, z], tileRng);
            }
        }
    }

    private int DecideCount3to5(bool hasNeighborWithFive)
    {
        if (!hasNeighborWithFive)
            return NextInt(_rng, 3, 6);

        float r = NextFloat01(_rng);
        if (r < neighborThreeChance) return 3;
        if (r < neighborThreeChance + neighborFiveChance) return 5;
        return 4;
    }

    private bool HasPriorNeighborWithFive(int[,] counts, int x, int z)
    {
        if (x - 1 >= 0 && counts[x - 1, z] == 5) return true;
        if (z - 1 >= 0 && counts[x, z - 1] == 5) return true;
        if (x - 1 >= 0 && z - 1 >= 0 && counts[x - 1, z - 1] == 5) return true;
        if (x + 1 < width && z - 1 >= 0 && counts[x + 1, z - 1] == 5) return true;
        return false;
    }

    private void SpawnJarsOnTile(Vector3 center, List<Vector2> used, int count, System.Random tileRng)
    {
        bool hasXp = HasAtLeastOne(xpJarPrefabs);
        bool hasGold = HasAtLeastOne(goldJarPrefabs);
        if (!hasXp && !hasGold) return;

        count = Mathf.Clamp(count, 3, 5);

        for (int i = 0; i < count; i++)
        {
            bool spawnXp = hasXp && (!hasGold || NextBool(tileRng));
            GameObject[] pool = spawnXp ? xpJarPrefabs : goldJarPrefabs;
            GameObject prefab = pool[NextInt(tileRng, 0, pool.Length)];

            Vector2 offset = RandomPointWithMinSeparation(
                tileRng,
                jarScatterRadius,
                used,
                minSeparationInTile * 0.8f,
                placementAttempts
            );

            Vector3 pos = center + new Vector3(offset.x, jarYOffset, offset.y);

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _lootRoot);
            float s = NextFloat(tileRng, jarScaleRange.x, jarScaleRange.y);
            go.transform.localScale *= s;
        }
    }

    private void SpawnChestsOnTile(Vector3 center, List<Vector2> used, int count, System.Random tileRng)
    {
        if (!HasAtLeastOne(chestPrefabs)) return;

        count = Mathf.Clamp(count, 3, 5);

        for (int i = 0; i < count; i++)
        {
            GameObject prefab = chestPrefabs[NextInt(tileRng, 0, chestPrefabs.Length)];

            Vector2 offset = RandomPointWithMinSeparation(
                tileRng,
                chestScatterRadius,
                used,
                minSeparationInTile,
                placementAttempts
            );

            Vector3 pos = center + new Vector3(offset.x, chestYOffset, offset.y);

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _lootRoot);
            float s = NextFloat(tileRng, chestScaleRange.x, chestScaleRange.y);
            go.transform.localScale *= s;
        }
    }

    // ----------------------------
    // Utility
    // ----------------------------
    private bool HasAtLeastOne(GameObject[] arr) => arr != null && arr.Length > 0;

    private void ClearOld()
    {
        var all = GameObject.FindObjectsOfType<Transform>();
        foreach (var t in all)
        {
            if (!t.name.StartsWith("GeneratedMap_")) continue;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(t.gameObject);
            else
                Destroy(t.gameObject);
#else
            Destroy(t.gameObject);
#endif
        }
    }
}
