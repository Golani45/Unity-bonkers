using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MapGenerator : MonoBehaviour
{
    [Header("Level Profile (required)")]
    public LevelGenProfile profile;

    [Header("Ground Placement (fix floating objects)")]
    public bool useRaycastGrounding = true;
    
    private MeshCollider _terrainCollider;
    private bool _playerPrevKinematic;
    private bool _playerPrevDetectCollisions;


// Map coordinate system (centered)
    private float _mapWorldSizeX;
    private float _mapWorldSizeZ;
    private Vector3 _mapOrigin; // bottom-left corner in world space (centered map => negative half extents)



    [Tooltip("Set this to ONLY your Ground layer for best results.")]
    public LayerMask groundLayerMask = ~0;
    
    [Header("Player Spawn (optional)")]
    public bool snapPlayerAfterGenerate = true;
    public Vector2 spawnNormalized = new Vector2(0.5f, 0.5f); // 0..1 (center by default)
    public float spawnExtraHeight = 2.0f;    // extra height above ground
    public float spawnRayStartHeight = 500f; // how high to raycast from


    public float groundRaycastStartHeight = 300f;
    public float groundExtraOffset = 0.0f;

    [Header("Auto Pivot Correction (trees with wrong pivots)")]
    public bool autoPivotCorrection = true;
    
    [Header("Player Placement")]
    public Transform player;              // drag your sphere here
    public float playerSnapHeight = 200f;  // how high to raycast from
    public float playerGroundClearance = 1.5f; // how far above ground to place

    // runtime
    private Transform _root, _terrainRoot, _envRoot;
    private System.Random _rng;

    // noise offsets
    private float _offX, _offZ;

    // effective radii (world units)
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

    private Coroutine _spawnRoutine;

    [ContextMenu("Generate")]
    public void Generate()
    {
        if (profile == null)
        {
            Debug.LogError("MapGenerator: profile is missing.");
            return;
        }
        if (profile.biome == null)
        {
            Debug.LogError("MapGenerator: profile.biome is missing.");
            return;
        }

        // Stop any previous spawn routine
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;

            // IMPORTANT: if we stopped mid-regeneration, restore player physics
            RestorePlayerPhysics();
        }

        PreparePlayerForRegeneration();

        ClearOld();

        if (profile.useRandomSeed)
            profile.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _rng = new System.Random(profile.seed);

        _root = new GameObject($"GeneratedMap_{profile.width}x{profile.height}_seed_{profile.seed}").transform;
        _terrainRoot = new GameObject("Terrain").transform;
        _terrainRoot.SetParent(_root, false);

        _envRoot = new GameObject("Environment").transform;
        _envRoot.SetParent(_root, false);

        _reservedZones.Clear();

        _offX = NextFloat(_rng, -10000f, 10000f);
        _offZ = NextFloat(_rng, -10000f, 10000f);

        ComputeEffectiveRadii();

        BuildCombinedTerrainMesh();

        if (profile.spawnPOI)
            SpawnSinglePOI();

        Debug.Log("Generate() called. snapPlayerAfterGenerate=" + snapPlayerAfterGenerate);

        Debug.Log($"Generated map seed={profile.seed} sizeTiles=({profile.width},{profile.height}) tileSize={profile.tileSize} vertsPerTile={profile.vertsPerTile} biome={profile.biome.biomeName}");
        Debug.Log("_terrainCollider is " + (_terrainCollider == null ? "NULL" : "OK"));

        SpawnEnvironmentEveryTile();

        if (snapPlayerAfterGenerate)
        {
            if (Application.isPlaying)
            {
                _spawnRoutine = StartCoroutine(SnapPlayerAfterPhysics());
            }
            else
            {
                // In Editor mode, we snap instantly without yielding
                SnapPlayerToGround();
                RestorePlayerPhysics();
            }
        }
        else
        {
            RestorePlayerPhysics();
        }
        
        if (player != null && player.TryGetComponent<Rigidbody>(out var rb))
        {
            rb.isKinematic = false;
            rb.detectCollisions = true;
            rb.useGravity = true;
            rb.WakeUp();
        }
        
    } 
    
    private void PreparePlayerForRegeneration()
    {
        if (player == null) return;
        Debug.Log("Freezing player: " + player.name);

        var rb = player.GetComponent<Rigidbody>();
        if (rb != null)
        {
            _playerPrevKinematic = rb.isKinematic;
            _playerPrevDetectCollisions = rb.detectCollisions;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        // Park above map center-ish
        player.position = new Vector3(0f, spawnRayStartHeight + 50f, 0f);
        Physics.SyncTransforms();
    }
    
    private void RestorePlayerPhysics()
    {
        if (player == null) return;

        if (player.TryGetComponent<Rigidbody>(out var rb))
        {
            rb.isKinematic = false;
            rb.detectCollisions = true;
            rb.useGravity = true;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            rb.WakeUp();
        }
    }


    
    private void SnapPlayerToGround()
    {
        if (player == null || profile == null)
            return;

        // Get spawn XZ in WORLD coordinates (0..map size)
        Vector3 spawnXZ = GetSpawnXZWorld();

        // Start ray high above the terrain
        Vector3 rayStart = new Vector3(
            spawnXZ.x,
            spawnRayStartHeight,
            spawnXZ.z
        );

        // Try raycast first (preferred, uses collider)
        if (Physics.Raycast(
                rayStart,
                Vector3.down,
                out RaycastHit hit,
                spawnRayStartHeight * 2f,
                groundLayerMask,
                QueryTriggerInteraction.Ignore))
        {
            player.position = hit.point + Vector3.up * spawnExtraHeight;
        }
        else
        {
            // Fallback: sample height mathematically (guaranteed)
            float h = SampleHeightWorld(spawnXZ.x, spawnXZ.z);
            player.position = new Vector3(
                spawnXZ.x,
                h + spawnExtraHeight,
                spawnXZ.z
            );

            Debug.LogWarning(
                "Player spawn raycast missed terrain. " +
                "Falling back to height sampling."
            );
        }

        // Reset physics so the player doesn't keep falling
        if (player.TryGetComponent<Rigidbody>(out Rigidbody rb))
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
            rb.WakeUp();
        }
    }
    
    private Vector3 GetSpawnXZWorld()
    {
        if (profile == null) return Vector3.zero;

        float x = _mapOrigin.x + (spawnNormalized.x * _mapWorldSizeX);
        float z = _mapOrigin.z + (spawnNormalized.y * _mapWorldSizeZ);

        // Keep player away from exact edge a bit
        x = Mathf.Clamp(x, _mapOrigin.x + 1f, _mapOrigin.x + _mapWorldSizeX - 1f);
        z = Mathf.Clamp(z, _mapOrigin.z + 1f, _mapOrigin.z + _mapWorldSizeZ - 1f);

        return new Vector3(x, 0f, z);
    }
    



    // ----------------------------
    // Combined mesh terrain
    // ----------------------------
private void BuildCombinedTerrainMesh()
{
    int widthTiles = Mathf.Max(1, profile.width);
    int heightTiles = Mathf.Max(1, profile.height);
    float tileSize = Mathf.Max(0.01f, profile.tileSize);

    int vpt = Mathf.Max(2, profile.vertsPerTile);
    int vertsX = widthTiles * vpt + 1;
    int vertsZ = heightTiles * vpt + 1;

    float worldSizeX = widthTiles * tileSize;
    float worldSizeZ = heightTiles * tileSize;

    // IMPORTANT: origin at (0,0) — consistent with spawn & env logic
    _mapWorldSizeX = worldSizeX;
    _mapWorldSizeZ = worldSizeZ;
    _mapOrigin = Vector3.zero;

    float[] heights = new float[vertsX * vertsZ];
    Vector3[] verts = new Vector3[vertsX * vertsZ];
    Vector2[] uvs = new Vector2[vertsX * vertsZ];
    int[] tris = new int[(vertsX - 1) * (vertsZ - 1) * 6];

    // --- Sample heights ---
    for (int z = 0; z < vertsZ; z++)
    {
        float wz = (z / (float)(vertsZ - 1)) * worldSizeZ;
        for (int x = 0; x < vertsX; x++)
        {
            float wx = (x / (float)(vertsX - 1)) * worldSizeX;
            int i = z * vertsX + x;
            heights[i] = SampleHeightWorld(wx, wz);
        }
    }

    ClampNeighborDeltas(heights, vertsX, vertsZ, profile.maxDeltaPerVertex);

    for (int p = 0; p < Mathf.Clamp(profile.smoothingPasses, 0, 10); p++)
        SmoothHeights5Tap(heights, vertsX, vertsZ);

    // --- Build vertices + UVs ---
    for (int z = 0; z < vertsZ; z++)
    {
        float wz = (z / (float)(vertsZ - 1)) * worldSizeZ;
        for (int x = 0; x < vertsX; x++)
        {
            float wx = (x / (float)(vertsX - 1)) * worldSizeX;
            int i = z * vertsX + x;

            verts[i] = new Vector3(wx, heights[i], wz);
            uvs[i] = new Vector2(wx / worldSizeX, wz / worldSizeZ);
        }
    }

    // --- Triangles (initial winding) ---
    int ti = 0;
    for (int z = 0; z < vertsZ - 1; z++)
    {
        for (int x = 0; x < vertsX - 1; x++)
        {
            int i0 = z * vertsX + x;
            int i1 = i0 + 1;
            int i2 = i0 + vertsX;
            int i3 = i2 + 1;

            tris[ti++] = i0; tris[ti++] = i1; tris[ti++] = i2;
            tris[ti++] = i1; tris[ti++] = i3; tris[ti++] = i2;
        }
    }

    // --- Create GameObject ---
    var terrainGO = new GameObject("TerrainMesh");
    terrainGO.transform.SetParent(_terrainRoot, false);
    terrainGO.transform.position = Vector3.zero;

    var mf = terrainGO.AddComponent<MeshFilter>();
    var mr = terrainGO.AddComponent<MeshRenderer>();
    var mc = terrainGO.AddComponent<MeshCollider>();
    _terrainCollider = mc;

    mc.cookingOptions =
        MeshColliderCookingOptions.CookForFasterSimulation |
        MeshColliderCookingOptions.EnableMeshCleaning |
        MeshColliderCookingOptions.WeldColocatedVertices;

    var mesh = new Mesh();
    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    mesh.vertices = verts;
    mesh.triangles = tris;
    mesh.uv = uvs;

    // ---------- FORCE TERRAIN TO FACE UP ----------
    mesh.RecalculateNormals();
    mesh.RecalculateBounds();

    Vector3 avg = Vector3.zero;
    var normals = mesh.normals;
    int step = Mathf.Max(1, normals.Length / 256);

    for (int i = 0; i < normals.Length; i += step)
        avg += normals[i];

    if (avg.y < 0f)
    {
        int[] t = mesh.triangles;
        for (int i = 0; i < t.Length; i += 3)
        {
            int tmp = t[i + 1];
            t[i + 1] = t[i + 2];
            t[i + 2] = tmp;
        }
        mesh.triangles = t;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }
    // ---------------------------------------------

    mf.sharedMesh = mesh;

    if (profile.biome != null && profile.biome.groundMaterial != null)
        mr.sharedMaterial = profile.biome.groundMaterial;

    // Force collider refresh
    mc.sharedMesh = null;
    mc.sharedMesh = mesh;

    // Set Ground layer
    int groundLayer = LayerMask.NameToLayer("Ground");
    if (groundLayer != -1)
        SetLayerRecursively(terrainGO, groundLayer);

    Physics.SyncTransforms();
}

private float SampleHeightLocal(float localX, float localZ)
{
    // Convert local world coords (0..mapSize) into tile grid coords
    float gx = localX / Mathf.Max(0.0001f, profile.tileSize);
    float gz = localZ / Mathf.Max(0.0001f, profile.tileSize);
    return SampleHeightGrid(gx, gz);
}

private float SampleHeightWorld(float worldX, float worldZ)
{
    // Convert WORLD into LOCAL (0..mapSize)
    float localX = worldX - _mapOrigin.x;
    float localZ = worldZ - _mapOrigin.z;

    // Clamp so we don't sample outside
    localX = Mathf.Clamp(localX, 0f, _mapWorldSizeX);
    localZ = Mathf.Clamp(localZ, 0f, _mapWorldSizeZ);

    return SampleHeightLocal(localX, localZ);
}


private bool IsMeshFacingUp(Mesh m)
{
    // Average normals: if mostly pointing up, y should be positive
    var normals = m.normals;
    if (normals == null || normals.Length == 0) return true;

    float sumY = 0f;
    int step = Mathf.Max(1, normals.Length / 256); // sample up to ~256 normals
    for (int i = 0; i < normals.Length; i += step)
        sumY += normals[i].y;

    return sumY >= 0f;
}

private void FlipMeshWinding(Mesh m)
{
    int[] t = m.triangles;
    for (int i = 0; i < t.Length; i += 3)
    {
        // swap order of two indices
        int tmp = t[i + 1];
        t[i + 1] = t[i + 2];
        t[i + 2] = tmp;
    }
    m.triangles = t;
}


    private void ClampNeighborDeltas(float[] h, int vertsX, int vertsZ, float maxDelta)
    {
        maxDelta = Mathf.Max(0.001f, maxDelta);

        // Forward pass: clamp vs left and down
        for (int z = 0; z < vertsZ; z++)
        {
            for (int x = 0; x < vertsX; x++)
            {
                int i = z * vertsX + x;

                if (x > 0)
                {
                    int il = i - 1;
                    float d = h[i] - h[il];
                    if (Mathf.Abs(d) > maxDelta) h[i] = h[il] + Mathf.Sign(d) * maxDelta;
                }
                if (z > 0)
                {
                    int id = i - vertsX;
                    float d = h[i] - h[id];
                    if (Mathf.Abs(d) > maxDelta) h[i] = h[id] + Mathf.Sign(d) * maxDelta;
                }
            }
        }

        // Backward pass: clamp vs right and up (helps symmetry)
        for (int z = vertsZ - 1; z >= 0; z--)
        {
            for (int x = vertsX - 1; x >= 0; x--)
            {
                int i = z * vertsX + x;

                if (x < vertsX - 1)
                {
                    int ir = i + 1;
                    float d = h[i] - h[ir];
                    if (Mathf.Abs(d) > maxDelta) h[i] = h[ir] + Mathf.Sign(d) * maxDelta;
                }
                if (z < vertsZ - 1)
                {
                    int iu = i + vertsX;
                    float d = h[i] - h[iu];
                    if (Mathf.Abs(d) > maxDelta) h[i] = h[iu] + Mathf.Sign(d) * maxDelta;
                }
            }
        }
    }

    private void SmoothHeights5Tap(float[] h, int vertsX, int vertsZ)
    {
        var copy = (float[])h.Clone();

        for (int z = 1; z < vertsZ - 1; z++)
        {
            for (int x = 1; x < vertsX - 1; x++)
            {
                int i = z * vertsX + x;
                float avg =
                    copy[i] +
                    copy[i - 1] + copy[i + 1] +
                    copy[i - vertsX] + copy[i + vertsX];
                h[i] = avg / 5f;
            }
        }
    }

    private float SampleHeightGrid(float gx, float gz)
    {
        var biome = profile.biome;

        if (profile.useDomainWarp)
        {
            float wx = (Mathf.PerlinNoise(_offX + gx * profile.warpScale, _offZ + gz * profile.warpScale) - 0.5f) * 2f;
            float wz = (Mathf.PerlinNoise(_offX + 999f + gx * profile.warpScale, _offZ + 999f + gz * profile.warpScale) - 0.5f) * 2f;
            gx += wx * profile.warpStrength;
            gz += wz * profile.warpStrength;
        }

        float large = Mathf.PerlinNoise(_offX + gx * profile.largeScale, _offZ + gz * profile.largeScale);
        float hLarge = (large - 0.5f) * 2f * profile.largeAmp * Mathf.Max(0.001f, biome.largeAmpMultiplier);

        float ridgeBase = Mathf.PerlinNoise(_offX + 2000f + gx * profile.ridgeScale, _offZ + 2000f + gz * profile.ridgeScale);
        float ridge = 1f - Mathf.Abs(ridgeBase * 2f - 1f);
        float hRidge = (ridge - 0.5f) * 2f * profile.ridgeAmp * Mathf.Max(0.001f, biome.ridgeAmpMultiplier);

        float detail = Mathf.PerlinNoise(_offX + 4000f + gx * profile.detailScale, _offZ + 4000f + gz * profile.detailScale);
        float hDetail = (detail - 0.5f) * 2f * profile.detailAmp * Mathf.Max(0.001f, biome.detailAmpMultiplier);

        float valleyMap = Mathf.PerlinNoise(_offX + 6000f + gx * profile.valleyScale, _offZ + 6000f + gz * profile.valleyScale);
        float valley = Mathf.Pow(valleyMap, 2.2f);
        float valleyDrop = Mathf.Lerp(0f, profile.largeAmp * 0.9f, profile.valleyStrength) * (1f - valley);

        float h = hLarge + hRidge + hDetail - valleyDrop;

        if (profile.heightStep > 0.0001f)
            h = Mathf.Round(h / profile.heightStep) * profile.heightStep;

        return h;
    }

    // ----------------------------
    // POI (spawn exactly one)
    // ----------------------------
    private void SpawnSinglePOI()
    {
        GameObject[] poiPrefabs = (profile.poiPrefabsOverride != null && profile.poiPrefabsOverride.Length > 0)
            ? profile.poiPrefabsOverride
            : profile.biome.poiPrefabs;

        if (!HasAtLeastOne(poiPrefabs))
            return;

        float tileSize = profile.tileSize;

        var poi = poiPrefabs[_rng.Next(0, poiPrefabs.Length)];

        for (int attempt = 0; attempt < Mathf.Max(1, profile.poiPlacementAttempts); attempt++)
        {
            int tx = _rng.Next(0, profile.width);
            int tz = _rng.Next(0, profile.height);

            Vector3 tileBase = _mapOrigin + new Vector3(tx * tileSize, 0f, tz * tileSize);

            float maxOffset = 0.35f * tileSize;
            Vector2 offset2 = RandomInsideCircle(_rng, maxOffset);

            Vector3 pos = tileBase + new Vector3(offset2.x, 0f, offset2.y);

            // Clamp inside centered map bounds
            pos.x = Mathf.Clamp(pos.x, _mapOrigin.x, _mapOrigin.x + _mapWorldSizeX);
            pos.z = Mathf.Clamp(pos.z, _mapOrigin.z, _mapOrigin.z + _mapWorldSizeZ);

            pos = SnapToTerrainY(pos, 0f);

            if (IsInsideReserved(pos, profile.poiReserveRadius))
                continue;

            var go = Instantiate(poi, pos, Quaternion.Euler(0f, NextFloat(_rng, 0f, 360f), 0f), _root);

            if (autoPivotCorrection)
                ApplyAutoPivotCorrection(go, pos.y);

            _reservedZones.Add(new ReservedZone(pos, profile.poiReserveRadius));

            Debug.Log($"Spawned POI '{poi.name}' at {pos} radius={profile.poiReserveRadius}");
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
    // Environment spawning (per-tile RNG, same concept as before)
    // ----------------------------
    private void SpawnEnvironmentEveryTile()
    {
        var biome = profile.biome;

        bool hasEnv =
            HasAtLeastOne(biome.treePrefabs) ||
            HasAtLeastOne(biome.rockPrefabs) ||
            HasAtLeastOne(biome.bushPrefabs);

        if (!hasEnv)
            return;

        float tileSize = profile.tileSize;

        for (int z = 0; z < profile.height; z++)
        {
            for (int x = 0; x < profile.width; x++)
            {
                var tileRng = MakeTileRng(x, z, 101);

                Vector3 tileBase = _mapOrigin + new Vector3(x * tileSize, 0f, z * tileSize);
                Vector3 center = GetTileSpawnCenter(tileBase, tileRng, tileSize);

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

                    Vector2 offset = RandomPointWithMinSeparation(tileRng, _envRadiusWorld, used, _minSepWorld, profile.placementAttempts);
                    Vector3 pos = center + new Vector3(offset.x, 0f, offset.y);
                    pos = SnapToTerrainY(pos, 0f);

                    // Respect POI reserved zones
                    if (IsInsideReserved(pos, 0.5f))
                        continue;

                    var go = Instantiate(prefab, pos, Quaternion.Euler(0f, NextFloat(tileRng, 0f, 360f), 0f), _envRoot);

                    // Make sure env objects are NOT on Ground layer
                    int envLayer = LayerMask.NameToLayer("Default"); // or "Environment" if you create it
                    if (envLayer != -1)
                        SetLayerRecursively(go, envLayer);

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
        int baseCount = Mathf.Max(profile.envSpawnsPerTileMin, 0);
        int delta = r.Next(-2, 4); // -2..+3
        return Mathf.Max(0, baseCount + delta);
    }

    private GameObject PickEnvPrefabThemed(System.Random r, TileTheme theme)
    {
        var trees = profile.biome.treePrefabs;
        var rocks = profile.biome.rockPrefabs;
        var bushes = profile.biome.bushPrefabs;

        int wTrees = (theme == TileTheme.TreesHeavy) ? 6 : 3;
        int wRocks = (theme == TileTheme.RocksHeavy) ? 6 : 3;
        int wBush  = (theme == TileTheme.Clearing) ? 1 : 2;

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

    // ----------------------------
    // Ground snapping / pivot correction
    // ----------------------------
    private Vector3 SnapToTerrainY(Vector3 worldPos, float extraYOffset)
    {
        if (!useRaycastGrounding)
        {
            float h = SampleHeightWorld(worldPos.x, worldPos.z);
            worldPos.y = h + extraYOffset + groundExtraOffset;
            return worldPos;
        }

        Vector3 rayStart = new Vector3(worldPos.x, groundRaycastStartHeight, worldPos.z);

        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                groundRaycastStartHeight * 2f, groundLayerMask, QueryTriggerInteraction.Ignore))
        {
            worldPos.y = hit.point.y + extraYOffset + groundExtraOffset;
            return worldPos;
        }

        // Fallback
        float fallback = SampleHeightWorld(worldPos.x, worldPos.z);
        worldPos.y = fallback + extraYOffset + groundExtraOffset;
        return worldPos;
    }

    private void ApplyAutoPivotCorrection(GameObject go, float groundY)
    {
        // Prefer collider bounds if available
        var cols = go.GetComponentsInChildren<Collider>();
        if (cols != null && cols.Length > 0)
        {
            Bounds b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);

            float bottomY = b.min.y;
            go.transform.position += new Vector3(0f, groundY - bottomY, 0f);
            return;
        }

        // Fallback to renderer bounds
        var rends = go.GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0) return;

        Bounds rb = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) rb.Encapsulate(rends[i].bounds);

        float rBottomY = rb.min.y;
        go.transform.position += new Vector3(0f, groundY - rBottomY, 0f);
    }

    private void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // ----------------------------
    // Random helpers
    // ----------------------------
    private void ComputeEffectiveRadii()
    {
        float halfTile = 0.5f * Mathf.Max(0.01f, profile.tileSize);

        _envRadiusWorld = Mathf.Max(0.01f, profile.envScatterRadius * halfTile);
        _jitterRadiusWorld = Mathf.Max(0f, profile.perTileJitterRadius * halfTile);
        _minSepWorld = Mathf.Max(0f, profile.minSeparationInTile * halfTile);
    }

    private System.Random MakeTileRng(int x, int z, int salt)
    {
        unchecked
        {
            int h = profile.seed;
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

    private Vector3 GetTileSpawnCenter(Vector3 tileBaseWorld, System.Random tileRng, float tileSize)
    {
        Vector2 jitter = RandomInsideCircle(tileRng, _jitterRadiusWorld);
        Vector3 p = tileBaseWorld + new Vector3(jitter.x, 0f, jitter.y);

        // Clamp within tile bounds loosely (optional safety)
        p.x = Mathf.Clamp(p.x, tileBaseWorld.x, tileBaseWorld.x + tileSize);
        p.z = Mathf.Clamp(p.z, tileBaseWorld.z, tileBaseWorld.z + tileSize);

        return SnapToTerrainY(p, 0f);
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
    
  private System.Collections.IEnumerator SnapPlayerAfterPhysics()
    {
        Debug.Log("SnapPlayerAfterPhysics ENTER");

        if (player == null || profile == null) 
            yield break;

        Rigidbody rb = player.GetComponent<Rigidbody>();
        Collider playerCol = player.GetComponent<Collider>();

        try 
        {
            // 1. Wait for physics to stabilize and mesh collider to "cook"
            yield return new WaitForSeconds(0.1f); 
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            // Freeze while we move it to prevent falling through the floor
            if (rb != null) 
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }

            // Get spawn XZ in WORLD coordinates
            Vector3 spawnXZ = GetSpawnXZWorld();
            float bottomOffset = (playerCol != null) ? playerCol.bounds.extents.y : 0.5f;

            Vector3 rayStart = new Vector3(spawnXZ.x, spawnRayStartHeight, spawnXZ.z);
            
            // Raycast down to find the ground
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, spawnRayStartHeight * 2f, groundLayerMask)) 
            {
                player.position = hit.point + Vector3.up * (bottomOffset + 0.1f);
                Debug.Log($"Snapped to raycast hit point: {hit.point}");
            } 
            else 
            {
                // Fallback to mathematical sampling if raycast misses
                float h = SampleHeightWorld(spawnXZ.x, spawnXZ.z);
                player.position = new Vector3(spawnXZ.x, h + bottomOffset + 0.1f, spawnXZ.z);
                Debug.LogWarning($"Raycast missed! Falling back to height sampling: {h}");
            }
            
            Physics.SyncTransforms();
            
            // Wait one final fixed update for the transform to register
            yield return new WaitForFixedUpdate(); 
        }
        finally
        {
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
                rb.useGravity = true;

                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;

                rb.WakeUp();
            }
        }

    }    private bool HasAtLeastOne(GameObject[] arr) => arr != null && arr.Length > 0;

    // ----------------------------
    // Clear old
    // ----------------------------
    private void ClearOld()
    {
        // If we have a direct reference, destroy it
        if (_root != null)
        {
            if (Application.isPlaying) Destroy(_root.gameObject);
            else DestroyImmediate(_root.gameObject);
            _root = null;
        }

        // Safety net: Only find and destroy other maps if we are NOT in Play Mode
        // or if we are explicitly calling a full regeneration.
        if (!Application.isPlaying)
        {
            var roots = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
            foreach (var t in roots)
            {
                if (t == null || t.parent != null) continue;
                if (!t.name.StartsWith("GeneratedMap_")) continue;

                DestroyImmediate(t.gameObject);
            }
        }
    }
}
