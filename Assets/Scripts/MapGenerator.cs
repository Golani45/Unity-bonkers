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

    [Header("Ground Tile")]
    public GameObject groundTilePrefab;
    public Material groundMaterial;

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

    [Header("Environment")]
    public GameObject[] treePrefabs;
    public GameObject[] rockPrefabs;
    public GameObject[] bushPrefabs;

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

        ClearOld();

        if (useRandomSeed)
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _rng = new System.Random(seed);

        _root = new GameObject($"GeneratedMap_10x10_seed_{seed}").transform;
        _tilesRoot = new GameObject("Tiles").transform; _tilesRoot.SetParent(_root, false);
        _envRoot = new GameObject("Environment").transform; _envRoot.SetParent(_root, false);

        MeasureGroundPrefab();     // ✅ FIXED (respects prefab scale)
        ComputeEffectiveRadii();

        _offX = NextFloat(_rng, -10000f, 10000f);
        _offZ = NextFloat(_rng, -10000f, 10000f);

        SpawnTiles();
        SpawnEnvironmentEveryTile();

        Debug.Log($"Generated map seed={seed} spacing=({_tileFootprintX:F2},{_tileFootprintZ:F2}) deform={deformTilesToCreateSlopes}");
    }

    // ----------------------------
    // Measurement (FIXED)
    // ----------------------------
    private void MeasureGroundPrefab()
    {
        GameObject sample = Instantiate(groundTilePrefab);
        sample.name = "__MEASURE_SAMPLE__";
        sample.hideFlags = HideFlags.HideAndDontSave;

        sample.SetActive(true);
        sample.transform.position = Vector3.zero;
        sample.transform.rotation = Quaternion.identity;

        // ✅ IMPORTANT FIX:
        // Do NOT force Vector3.one. Respect prefab scale or you will mis-measure spacing.
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
        float hLarge = (large - 0.5f) * 2f * largeAmp;

        float ridgeBase = Mathf.PerlinNoise(_offX + 2000f + gx * ridgeScale, _offZ + 2000f + gz * ridgeScale);
        float ridge = 1f - Mathf.Abs(ridgeBase * 2f - 1f);
        float hRidge = (ridge - 0.5f) * 2f * ridgeAmp;

        float detail = Mathf.PerlinNoise(_offX + 4000f + gx * detailScale, _offZ + 4000f + gz * detailScale);
        float hDetail = (detail - 0.5f) * 2f * detailAmp;

        float valleyMap = Mathf.PerlinNoise(_offX + 6000f + gx * valleyScale, _offZ + 6000f + gz * valleyScale);
        float valley = Mathf.Pow(valleyMap, 2.2f);
        float valleyDrop = Mathf.Lerp(0f, largeAmp * 0.9f, valleyStrength) * (1f - valley);

        float h = hLarge + hRidge + hDetail - valleyDrop;

        if (heightStep > 0.0001f)
            h = Mathf.Round(h / heightStep) * heightStep;
        
        float flatStart = 3f;     // start flattening around this height (world units)
        float flatRange = 6f;     // how wide the flatten band is
        float flatStrength = 0.6f; // 0..1 how strong the flattening is

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
                TryApplyGroundMaterial(tile, groundMaterial);

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

        // ✅ FIXED collider refresh (correct object, correct condition)
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
    // Environment
    // ----------------------------
    private void SpawnEnvironmentEveryTile()
    {
        if (!HasAtLeastOne(treePrefabs) && !HasAtLeastOne(rockPrefabs) && !HasAtLeastOne(bushPrefabs))
            return;

        int perTile = Mathf.Max(envSpawnsPerTileMin, 0);

        float spacingX = autoSpacingFromPrefab ? _tileFootprintX : 10f;
        float spacingZ = autoSpacingFromPrefab ? _tileFootprintZ : 10f;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                var tileRng = MakeTileRng(x, z, 101);

                Vector3 tileBase = new Vector3(x * spacingX, 0f, z * spacingZ);
                Vector3 center = GetTileSpawnCenter(tileBase, tileRng);

                var used = new List<Vector2>(perTile);

                for (int i = 0; i < perTile; i++)
                {
                    GameObject prefab = PickEnvPrefab(tileRng);
                    if (prefab == null) break;

                    Vector2 offset = RandomPointWithMinSeparation(tileRng, _envRadiusWorld, used, _minSepWorld, placementAttempts);
                    Vector3 pos = center + new Vector3(offset.x, 0f, offset.y);
                    pos = SnapToTerrainY(pos, 0f);

                    var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _envRoot);

                    if (autoPivotCorrection)
                        ApplyAutoPivotCorrection(go, pos.y);
                }
            }
        }
    }

    private GameObject PickEnvPrefab(System.Random r)
    {
        // simple weighted-ish choice
        int options = 0;
        if (HasAtLeastOne(treePrefabs)) options++;
        if (HasAtLeastOne(rockPrefabs)) options++;
        if (HasAtLeastOne(bushPrefabs)) options++;
        if (options == 0) return null;

        int pick = r.Next(0, options);
        if (HasAtLeastOne(treePrefabs))
        {
            if (pick == 0) return treePrefabs[r.Next(0, treePrefabs.Length)];
            pick--;
        }
        if (HasAtLeastOne(rockPrefabs))
        {
            if (pick == 0) return rockPrefabs[r.Next(0, rockPrefabs.Length)];
            pick--;
        }
        return HasAtLeastOne(bushPrefabs) ? bushPrefabs[r.Next(0, bushPrefabs.Length)] : null;
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

        // IMPORTANT: if your groundLayerMask includes trees/rocks colliders, raycast may hit them first.
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

    private void ClearOld()
    {
        var all = GameObject.FindObjectsOfType<Transform>();
        foreach (var t in all)
        {
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
