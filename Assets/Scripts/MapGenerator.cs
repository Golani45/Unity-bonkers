using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MapGenerator : MonoBehaviour
{
    private const int MAP_SIZE = 10;

    [Header("Map Dimensions (forced 10x10 at runtime)")]
    [Min(1)] public int width = MAP_SIZE;
    [Min(1)] public int height = MAP_SIZE;

    [Header("Biome (required)")]
    public BiomeData biome;

    [Header("Ground Tile")]
    public GameObject groundTilePrefab;

    [Header("Tile Spacing (IMPORTANT)")]
    public bool autoSpacingFromPrefab = true;
    [Tooltip("Multiplies measured tile size. Use 1.0 normally.")]
    public float spacingMultiplier = 1.0f;

    [Tooltip("Unity Plane pivot is centered (recommended ON for Plane).")]
    public bool tilePivotIsCentered = true;

    [Header("Terrain Shape (Mega Bonk-ish)")]
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

    [Header("REAL SLOPES (recommended)")]
    public bool deformTilesToCreateSlopes = true;
    public bool recalcNormalsAfterDeform = true;

    [Header("Ground Placement (fix floating objects)")]
    public bool useRaycastGrounding = true;

    [Tooltip("Set this to ONLY your Ground layer for best results.")]
    public LayerMask groundLayerMask = ~0;

    public float groundRaycastStartHeight = 300f;
    public float groundExtraOffset = 0.0f;

    [Header("Auto Pivot Correction (trees with wrong pivots)")]
    public bool autoPivotCorrection = true;

    [Header("Seed (repeatable runs)")]
    public bool useRandomSeed = true;
    public int seed = 12345;

    [Header("Environment (variation)")]
    [Min(0)] public int envSpawnsPerTileMin = 6;

    [Header("Spawn Randomness")]
    [Tooltip("Fraction of HALF tile size if Use Normalized Radii is ON")]
    public float envScatterRadius = 0.45f;

    [Tooltip("Fraction of HALF tile size if Use Normalized Radii is ON")]
    public float perTileJitterRadius = 0.18f;

    [Tooltip("Fraction of HALF tile size if Use Normalized Radii is ON")]
    public float minSeparationInTile = 0.10f;

    public int placementAttempts = 12;

    [Header("Scale Fix (recommended for Unity Plane 10x10)")]
    public bool useNormalizedRadii = true;

    [Header("POI (spawn once)")]
    public bool spawnPOI = true;

    [Tooltip("If empty, uses biome.poiPrefabs")]
    public GameObject[] poiPrefabsOverride;

    [Tooltip("Reserved radius around POI where normal env cannot spawn.")]
    public float poiReserveRadius = 8f;

    public int poiPlacementAttempts = 50;

    // runtime
    private Transform _root, _tilesRoot, _envRoot;
    private System.Random _rng;

    // measured tile
    private float _tileFootprintX = 10f;
    private float _tileFootprintZ = 10f;
    private float _tileThicknessY = 0.0f;

    // noise offsets
    private float _offX, _offZ;

    // effective radii
    private float _envRadiusWorld;
    private float _jitterRadiusWorld;
    private float _minSepWorld;

    // Track reserved zones (POI, later you can add more)
    private readonly List<ReservedZone> _reservedZones = new();

    private struct ReservedZone
    {
        public Vector3 center;
        public float radius;

        public ReservedZone(Vector3 c, float r)
        {
            center = c;
            radius = r;
        }
    }

    private enum TileTheme { Balanced, TreesHeavy, RocksHeavy, Clearing }

    [ContextMenu("Generate")]
    public void Generate()
    {
        width = MAP_SIZE;
        height = MAP_SIZE;

        if (groundTilePrefab == null)
        {
            Debug.LogError("MapGenerator: groundTilePrefab is missing.");
            return;
        }
        if (biome == null)
        {
            Debug.LogError("MapGenerator: biome is missing.");
            return;
        }

        ClearOld(); // now destroys previous root directly

        if (useRandomSeed)
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _rng = new System.Random(seed);

        _root = new GameObject($"GeneratedMap_10x10_seed_{seed}").transform;
        _tilesRoot = new GameObject("Tiles").transform; _tilesRoot.SetParent(_root, false);
        _envRoot = new GameObject("Environment").transform; _envRoot.SetParent(_root, false);

        _reservedZones.Clear();

        MeasureGroundPrefab(); // respects prefab scale
        ComputeEffectiveRadii();

        _offX = NextFloat(_rng, -10000f, 10000f);
        _offZ = NextFloat(_rng, -10000f, 10000f);

        SpawnTiles();

        if (spawnPOI)
            SpawnSinglePOI();

        SpawnEnvironmentEveryTile();

        Debug.Log($"Generated map seed={seed} spacing=({_tileFootprintX:F2},{_tileFootprintZ:F2}) deform={deformTilesToCreateSlopes} biome={biome.biomeName}");
    }

    // ----------------------------
    // Measurement (fixed)
    // ----------------------------
    private void MeasureGroundPrefab()
    {
        GameObject sample = Instantiate(groundTilePrefab);
        sample.name = "__MEASURE_SAMPLE__";
        sample.hideFlags = HideFlags.HideAndDontSave;

        sample.SetActive(true);
        sample.transform.position = Vector3.zero;
        sample.transform.rotation = Quaternion.identity;

        // IMPORTANT: respect prefab scale
        sample.transform.localScale = groundTilePrefab.transform.localScale;

        if (!TryGetWorldBounds(sample, out Bounds b))
        {
            Debug.LogWarning("Could not measure groundTilePrefab bounds. Falling back to Plane-ish 10x10.");
            _tileFootprintX = 10f * spacingMultiplier;
            _tileFootprintZ = 10f * spacingMultiplier;
            _tileThicknessY = 0.01f;
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
    // Noise / height
    // ----------------------------
    private float SampleHeightGrid(float gx, float gz)
    {
        if (useDomainWarp)
        {
            float wx = (Mathf.PerlinNoise(_offX + gx * warpScale, _offZ + gz * warpScale) - 0.5f) * 2f;
            float wz = (Mathf.PerlinNoise(_offX + 999f + gx * warpScale, _offZ + 999f + gz * warpScale) - 0.5f) * 2f;
            gx += wx * warpStrength;
            gz += wz * warpStrength;
        }

        float large = Mathf.PerlinNoise(_offX + gx * largeScale, _offZ + gz * largeScale);
        float hLarge = (large - 0.5f) * 2f * largeAmp * Mathf.Max(0.001f, biome.largeAmpMultiplier);

        float ridgeBase = Mathf.PerlinNoise(_offX + 2000f + gx * ridgeScale, _offZ + 2000f + gz * ridgeScale);
        float ridge = 1f - Mathf.Abs(ridgeBase * 2f - 1f);
        float hRidge = (ridge - 0.5f) * 2f * ridgeAmp * Mathf.Max(0.001f, biome.ridgeAmpMultiplier);

        float detail = Mathf.PerlinNoise(_offX + 4000f + gx * detailScale, _offZ + 4000f + gz * detailScale);
        float hDetail = (detail - 0.5f) * 2f * detailAmp * Mathf.Max(0.001f, biome.detailAmpMultiplier);

        float valleyMap = Mathf.PerlinNoise(_offX + 6000f + gx * valleyScale, _offZ + 6000f + gz * valleyScale);
        float valley = Mathf.Pow(valleyMap, 2.2f);
        float valleyDrop = Mathf.Lerp(0f, largeAmp * 0.9f, valleyStrength) * (1f - valley);

        float h = hLarge + hRidge + hDetail - valleyDrop;

        if (heightStep > 0.0001f)
            h = Mathf.Round(h / heightStep) * heightStep;

        // Gentle flatten band (keeps playable-ish zones, avoids extreme spikes)
        float flatStart = 3f;
        float flatRange = 6f;
        float flatStrength = 0.6f;

        float t = Mathf.InverseLerp(flatStart, flatStart + flatRange, Mathf.Abs(h));
        float flatten = Mathf.SmoothStep(0f, 1f, t);
        h = Mathf.Lerp(h, Mathf.Sign(h) * flatStart, flatStrength * (1f - flatten));

        return h;
    }

    private void WorldToGrid(float wx, float wz, out float gx, out float gz)
    {
        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 10f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 10f;

        gx = wx / Mathf.Max(0.0001f, spacingX);
        gz = wz / Mathf.Max(0.0001f, spacingZ);
    }

    private float SampleHeightWorld(float wx, float wz)
    {
        WorldToGrid(wx, wz, out float gx, out float gz);
        return SampleHeightGrid(gx, gz);
    }

    // ----------------------------
    // Tiles
    // ----------------------------
    private void SpawnTiles()
    {
        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 10f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 10f;

        float yOffset = tilePivotIsCentered ? (_tileThicknessY * 0.5f) : 0f;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 basePos = new Vector3(x * spacingX, yOffset, z * spacingZ);

                var tile = Instantiate(groundTilePrefab, basePos, Quaternion.identity, _tilesRoot);
                TryApplyGroundMaterial(tile, biome.groundMaterial);

                if (deformTilesToCreateSlopes)
                    DeformTileMesh(tile, x, z);
            }
        }
    }

    private void DeformTileMesh(GameObject tile, int tileX, int tileZ)
    {
        var mf = tile.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Mesh mesh = mf.mesh; // instance
        Vector3[] verts = mesh.vertices;

        Bounds b = mesh.bounds;
        float minX = b.min.x;
        float minZ = b.min.z;
        float sizeX = Mathf.Max(0.0001f, b.size.x);
        float sizeZ = Mathf.Max(0.0001f, b.size.z);

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 v = verts[i];

            float u = (v.x - minX) / sizeX; // 0..1
            float w = (v.z - minZ) / sizeZ; // 0..1

            float gx = tileX + (u - 0.5f);
            float gz = tileZ + (w - 0.5f);

            v.y = SampleHeightGrid(gx, gz);
            verts[i] = v;
        }

        mesh.vertices = verts;

        if (recalcNormalsAfterDeform)
        {
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        var mc = mf.GetComponent<MeshCollider>();
        if (mc == null) mc = mf.gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = null;
        mc.sharedMesh = mesh;
    }

    private void TryApplyGroundMaterial(GameObject tile, Material mat)
    {
        if (mat == null) return;
        var r = tile.GetComponentInChildren<Renderer>();
        if (r != null) r.sharedMaterial = mat;
    }

    // ----------------------------
    // POI (spawn exactly one)
    // ----------------------------
    private void SpawnSinglePOI()
    {
        GameObject[] poiPrefabs = (poiPrefabsOverride != null && poiPrefabsOverride.Length > 0)
            ? poiPrefabsOverride
            : biome.poiPrefabs;

        if (!HasAtLeastOne(poiPrefabs))
            return;

        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 10f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 10f;

        var poi = poiPrefabs[_rng.Next(0, poiPrefabs.Length)];

        for (int attempt = 0; attempt < Mathf.Max(1, poiPlacementAttempts); attempt++)
        {
            int tx = _rng.Next(0, width);
            int tz = _rng.Next(0, height);

            Vector3 tileBase = new Vector3(tx * spacingX, 0f, tz * spacingZ);

            float maxOffset = 0.35f * Mathf.Min(spacingX, spacingZ);
            Vector2 offset2 = RandomInsideCircle(_rng, maxOffset);

            Vector3 pos = tileBase + new Vector3(offset2.x, 0f, offset2.y);
            pos = SnapToTerrainY(pos, 0f);

            // Avoid overlapping other reserved zones
            if (IsInsideReserved(pos, poiReserveRadius))
                continue;

            var go = Instantiate(poi, pos, Quaternion.Euler(0f, NextFloat(_rng, 0f, 360f), 0f), _root);
            if (autoPivotCorrection)
                ApplyAutoPivotCorrection(go, pos.y);

            _reservedZones.Add(new ReservedZone(pos, poiReserveRadius));

            Debug.Log($"Spawned POI '{poi.name}' at {pos} radius={poiReserveRadius}");
            return;
        }

        Debug.LogWarning("Failed to place POI after attempts.");
    }

    private bool IsInsideReserved(Vector3 pos, float radius)
    {
        for (int i = 0; i < _reservedZones.Count; i++)
        {
            float rr = _reservedZones[i].radius + radius;
            if ((pos - _reservedZones[i].center).sqrMagnitude <= rr * rr)
                return true;
        }
        return false;
    }

    // ----------------------------
    // Environment (variation + reserved zones)
    // ----------------------------
    private void SpawnEnvironmentEveryTile()
    {
        if (biome == null) return;

        bool hasEnv =
            HasAtLeastOne(biome.treePrefabs) ||
            HasAtLeastOne(biome.rockPrefabs) ||
            HasAtLeastOne(biome.bushPrefabs);

        if (!hasEnv)
            return;

        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 10f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 10f;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                var tileRng = MakeTileRng(x, z, 101);

                Vector3 tileBase = new Vector3(x * spacingX, 0f, z * spacingZ);
                Vector3 center = GetTileSpawnCenter(tileBase, tileRng);

                // Per-tile variation
                var theme = RollTileTheme(tileRng);
                int perTile = RollSpawnCount(tileRng);

                // If the tile's center is inside a reserved POI, make it sparser
                if (IsInsideReserved(center, 0f))
                    perTile = Mathf.Min(perTile, 2);

                var used = new List<Vector2>(perTile);

                for (int i = 0; i < perTile; i++)
                {
                    GameObject prefab = PickEnvPrefabThemed(tileRng, theme);
                    if (prefab == null) break;

                    Vector2 offset = RandomPointWithMinSeparation(tileRng, _envRadiusWorld, used, _minSepWorld, placementAttempts);
                    Vector3 pos = center + new Vector3(offset.x, 0f, offset.y);
                    pos = SnapToTerrainY(pos, 0f);

                    // Respect POI reserved zones
                    if (IsInsideReserved(pos, 0.5f))
                        continue;

                    var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _envRoot);

                    if (autoPivotCorrection)
                        ApplyAutoPivotCorrection(go, pos.y);
                }
            }
        }
    }

    private TileTheme RollTileTheme(System.Random r)
    {
        double t = r.NextDouble();
        if (t < 0.20) return TileTheme.Clearing;   // 20%
        if (t < 0.45) return TileTheme.TreesHeavy; // 25%
        if (t < 0.70) return TileTheme.RocksHeavy; // 25%
        return TileTheme.Balanced;                 // 30%
    }

    private int RollSpawnCount(System.Random r)
    {
        int baseCount = Mathf.Max(envSpawnsPerTileMin, 0);
        int delta = r.Next(-2, 4); // -2..+3
        return Mathf.Max(0, baseCount + delta);
    }

    private GameObject PickEnvPrefabThemed(System.Random r, TileTheme theme)
    {
        var trees = biome.treePrefabs;
        var rocks = biome.rockPrefabs;
        var bushes = biome.bushPrefabs;

        int wTrees = (theme == TileTheme.TreesHeavy) ? 6 : 3;
        int wRocks = (theme == TileTheme.RocksHeavy) ? 6 : 3;
        int wBush = (theme == TileTheme.Clearing) ? 1 : 2;

        if (theme == TileTheme.Clearing)
        {
            wTrees = 0;
            wRocks = 2;
            wBush = 1;
        }

        int total = 0;
        if (HasAtLeastOne(trees)) total += wTrees;
        if (HasAtLeastOne(rocks)) total += wRocks;
        if (HasAtLeastOne(bushes)) total += wBush;
        if (total == 0) return null;

        int roll = r.Next(0, total);

        if (HasAtLeastOne(trees))
        {
            if (roll < wTrees) return trees[r.Next(0, trees.Length)];
            roll -= wTrees;
        }
        if (HasAtLeastOne(rocks))
        {
            if (roll < wRocks) return rocks[r.Next(0, rocks.Length)];
            roll -= wRocks;
        }
        return HasAtLeastOne(bushes) ? bushes[r.Next(0, bushes.Length)] : null;
    }

    private void ApplyAutoPivotCorrection(GameObject go, float groundY)
    {
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0) return;

        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++)
            b.Encapsulate(rends[i].bounds);

        float bottomY = b.min.y;
        float delta = groundY - bottomY;
        go.transform.position += new Vector3(0f, delta, 0f);
    }

    private Vector3 SnapToTerrainY(Vector3 worldPos, float extraYOffset)
    {
        if (!useRaycastGrounding)
        {
            float h = SampleHeightWorld(worldPos.x, worldPos.z);
            worldPos.y = h + extraYOffset + groundExtraOffset;
            return worldPos;
        }

        Vector3 rayStart = new Vector3(worldPos.x, groundRaycastStartHeight, worldPos.z);

        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, groundRaycastStartHeight * 2f, groundLayerMask))
        {
            worldPos.y = hit.point.y + extraYOffset + groundExtraOffset;
            return worldPos;
        }

        float fallback = SampleHeightWorld(worldPos.x, worldPos.z);
        worldPos.y = fallback + extraYOffset + groundExtraOffset;
        return worldPos;
    }

    // ----------------------------
    // Random helpers
    // ----------------------------
    private void ComputeEffectiveRadii()
    {
        float halfMin = 0.5f * Mathf.Min(_tileFootprintX, _tileFootprintZ);

        if (useNormalizedRadii)
        {
            _envRadiusWorld = envScatterRadius * halfMin;
            _jitterRadiusWorld = perTileJitterRadius * halfMin;
            _minSepWorld = minSeparationInTile * halfMin;
        }
        else
        {
            _envRadiusWorld = envScatterRadius;
            _jitterRadiusWorld = perTileJitterRadius;
            _minSepWorld = minSeparationInTile;
        }

        _envRadiusWorld = Mathf.Max(0.01f, _envRadiusWorld);
        _jitterRadiusWorld = Mathf.Max(0f, _jitterRadiusWorld);
        _minSepWorld = Mathf.Max(0f, _minSepWorld);
    }

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

    private float NextFloat(System.Random r, float min, float max) => min + (float)r.NextDouble() * (max - min);

    private Vector2 RandomInsideCircle(System.Random r, float radius)
    {
        float ang = NextFloat(r, 0f, Mathf.PI * 2f);
        float rr = Mathf.Sqrt((float)r.NextDouble()) * radius;
        return new Vector2(Mathf.Cos(ang) * rr, Mathf.Sin(ang) * rr);
    }

    private Vector3 GetTileSpawnCenter(Vector3 tileBaseWorld, System.Random tileRng)
    {
        Vector2 jitter = RandomInsideCircle(tileRng, _jitterRadiusWorld);
        return tileBaseWorld + new Vector3(jitter.x, 0f, jitter.y);
    }

    private Vector2 RandomPointWithMinSeparation(System.Random r, float radius, List<Vector2> used, float minDist, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            Vector2 p = RandomInsideCircle(r, radius);
            bool ok = true;

            for (int j = 0; j < used.Count; j++)
            {
                if (Vector2.Distance(p, used[j]) < minDist) { ok = false; break; }
            }

            if (ok) { used.Add(p); return p; }
        }

        Vector2 fallback = RandomInsideCircle(r, radius);
        used.Add(fallback);
        return fallback;
    }

    private bool HasAtLeastOne(GameObject[] arr) => arr != null && arr.Length > 0;

    // ----------------------------
    // Clear old (fast)
    // ----------------------------
    private void ClearOld()
    {
        // Destroy previous root directly instead of scanning the entire scene
        if (_root != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(_root.gameObject);
            else Destroy(_root.gameObject);
#else
            Destroy(_root.gameObject);
#endif
            _root = null;
        }

        // Also clean up any lingering generated objects by name (safety net)
        // This is much smaller than scanning all transforms; you can remove later.
        var roots = GameObject.FindObjectsOfType<Transform>();
        foreach (var t in roots)
        {
            if (t.parent != null) continue; // only scene roots
            if (!t.name.StartsWith("GeneratedMap_")) continue;

#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(t.gameObject);
            else Destroy(t.gameObject);
#else
            Destroy(t.gameObject);
#endif
        }
    }
}
