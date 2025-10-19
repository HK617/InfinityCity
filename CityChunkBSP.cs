using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class CityChunkBSP : IDisposable
{
    // ===== 設定（Manager から転送） =====
    [Header("World / Chunk")]
    public int chunkTiles;
    public float cellSize;
    public int tilesPerFrame = 1500;

    [Header("Prefabs")]
    public GameObject wayPrefab;         // Layer=Way 推奨
    public GameObject blockPrefab;       // 区画のベース（任意）
    [Tooltip("Lot内にランダム配置する建物プレハブ（元サイズのまま）")]
    public GameObject[] buildingPrefabs; // ★追加：3種類などを登録

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 0.90f;
    [Range(0.05f, 3f)] public float blockFillY = 1.20f;

    [Header("Road Generation (BSP + Global Arterials)")]
    [Tooltip("グローバル幹線の間隔（世界座標セル）。境界が必ず繋がる")]
    public int globalArterialPeriod = 8;  // ★追加：チャンク継ぎ目の道路整合
    [Range(1, 3)]
    public int globalArterialWidth = 1;  // 幅
    [Tooltip("BSP分割の最小サイズ。小さいほど道が増える (0=自動)")]
    public int minPartitionSize = 0;
    [Range(0f, 1f)]
    public float extraCrossChance = 0.35f;
    [Range(0, 3)]
    public int extraCrossWidth = 1;

    [Header("Lots (空き地区画)")]
    public bool placeLotBaseBlock = true;  // ベースの大ブロックを置くか
    public int minLotAreaCells = 3;
    public bool mergeDiagonals = false;
    [Tooltip("Lotあたりの建物数 [min,max]")]
    public Vector2Int buildingsPerLot = new Vector2Int(1, 3); // ★追加

    [Header("Ground Sampling")]
    public LayerMask groundMask = 0;

    [Header("NavMesh (Runtime)")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;

    // ===== 内部 =====
    public enum CellType { Empty, Way }
    CellType[,] grid;
    System.Random rng;
    public Vector2Int chunkIndex;
    GameObject container;

    // NavMesh
    NavMeshData navData;
    NavMeshDataInstance navInstance;
    AsyncOperation navOp;
    bool navReady;

    public bool NavReady => navReady;

    public CityChunkBSP(
        int seed, Vector2Int index, int chunkTiles, float cellSize,
        GameObject wayPrefab, GameObject blockPrefab, Transform parent,
        int tilesPerFrame = 1500
    )
    {
        this.chunkTiles = chunkTiles;
        this.cellSize = cellSize;
        this.wayPrefab = wayPrefab;
        this.blockPrefab = blockPrefab;
        this.tilesPerFrame = tilesPerFrame;
        this.chunkIndex = index;

        rng = new System.Random(seed ^ (index.x * 73856093) ^ (index.y * 19349663));
        grid = new CellType[chunkTiles, chunkTiles];

        container = new GameObject($"Chunk_{index.x}_{index.y}");
        container.transform.position = new Vector3(OriginX, 0f, OriginZ);
        if (parent) container.transform.SetParent(parent, false);
    }

    public IEnumerator GenerateAsync()
    {
        // 1) グローバル幹線（継ぎ目が必ず揃う）
        CarveGlobalArterials();

        // 2) その上でBSP風にランダム道路を追加
        CarveRoadsBspStyle();

        // 3) 道タイル配置
        yield return StampWaysCoroutine();

        // 4) 空き地→区画、区画中央にベースブロック＆建物を配置
        var lots = BuildLotsFromRemaining(touchesWayOnly: true);
        yield return StampLotsAndBuildingsCoroutine(lots);

        // 5) 道レイヤーだけ NavMesh（チャンク境界を少しはみ出すバウンズ）
        if (bakeNavMeshPerChunk) BuildChunkNavMeshAsync();
    }

    public void Dispose()
    {
        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        if (container != null) UnityEngine.Object.Destroy(container);
    }

    // ====== 道：グローバル幹線 ======
    void CarveGlobalArterials()
    {
        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
                grid[x, z] = CellType.Empty;

        int period = Mathf.Max(1, globalArterialPeriod);
        int width = Mathf.Clamp(globalArterialWidth, 1, 3);

        // 世界セル座標（チャンクまたぎで同一の行列になる）
        for (int lx = 0; lx < chunkTiles; lx++)
        {
            int wxCell = WorldCellX(lx);
            if (Mathf.Abs(Mod(wxCell, period)) == 0)
                CarveV(0, chunkTiles, lx, width);
        }
        for (int lz = 0; lz < chunkTiles; lz++)
        {
            int wzCell = WorldCellZ(lz);
            if (Mathf.Abs(Mod(wzCell, period)) == 0)
                CarveH(0, chunkTiles, lz, width);
        }
    }

    // ====== 道：BSPランダム ======
    void CarveRoadsBspStyle()
    {
        var stack = new Stack<RectInt>();
        stack.Push(new RectInt(0, 0, chunkTiles, chunkTiles));

        int minSize = (minPartitionSize > 0) ? minPartitionSize : Mathf.Max(6, chunkTiles / 6);

        while (stack.Count > 0)
        {
            var r = stack.Pop();
            if (r.width < 3 || r.height < 3) continue;

            bool canSplitH = r.height >= minSize * 2;
            bool canSplitV = r.width >= minSize * 2;

            if (!canSplitH && !canSplitV)
            {
                // ランダムに1本
                if (rng.NextDouble() < 0.5 && r.height >= 3)
                    CarveH(r.xMin, r.xMax, rng.Next(r.yMin + 1, r.yMax - 1), 1);
                else if (r.width >= 3)
                    CarveV(r.yMin, r.yMax, rng.Next(r.xMin + 1, r.xMax - 1), 1);
                continue;
            }

            bool splitH = (!canSplitV) ? true : (!canSplitH ? false : (rng.NextDouble() < 0.5));
            if (splitH)
            {
                int splitZ = rng.Next(r.yMin + minSize, r.yMax - minSize);
                CarveH(r.xMin, r.xMax, splitZ, 1);
                stack.Push(new RectInt(r.xMin, r.yMin, r.width, splitZ - r.yMin));
                stack.Push(new RectInt(r.xMin, splitZ, r.width, r.yMax - splitZ));
            }
            else
            {
                int splitX = rng.Next(r.xMin + minSize, r.xMax - minSize);
                CarveV(r.yMin, r.yMax, splitX, 1);
                stack.Push(new RectInt(r.xMin, r.yMin, splitX - r.xMin, r.height));
                stack.Push(new RectInt(splitX, r.yMin, r.xMax - splitX, r.height));
            }
        }

        // ランダム交差を追加
        int extra = Mathf.Max(1, chunkTiles / 3);
        for (int i = 0; i < extra; i++)
        {
            int x = rng.Next(1, chunkTiles - 1);
            int z = rng.Next(1, chunkTiles - 1);
            if (rng.NextDouble() < extraCrossChance)
            {
                CarveH(Mathf.Max(0, x - extraCrossWidth), Mathf.Min(chunkTiles, x + extraCrossWidth + 1), z, 1);
                CarveV(Mathf.Max(0, z - extraCrossWidth), Mathf.Min(chunkTiles, z + extraCrossWidth + 1), x, 1);
            }
        }
    }

    // carve helpers
    void CarveH(int xMin, int xMax, int z, int width)
    {
        int half = Mathf.Max(0, (width - 1) / 2);
        for (int dz = -half; dz <= half; dz++)
        {
            int zz = z + dz;
            if (zz < 0 || zz >= chunkTiles) continue;
            for (int x = xMin; x < xMax; x++)
                grid[x, zz] = CellType.Way;
        }
    }
    void CarveV(int zMin, int zMax, int x, int width)
    {
        int half = Mathf.Max(0, (width - 1) / 2);
        for (int dx = -half; dx <= half; dx++)
        {
            int xx = x + dx;
            if (xx < 0 || xx >= chunkTiles) continue;
            for (int z = zMin; z < zMax; z++)
                grid[xx, z] = CellType.Way;
        }
    }

    // ====== 道タイル配置 ======
    IEnumerator StampWaysCoroutine()
    {
        int budget = tilesPerFrame;
        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
            {
                if (grid[x, z] != CellType.Way) continue;

                Vector3 wc = LocalToWorldCenter(x, z);
                float gy = SampleGroundY(wc.x, wc.z);
                float yCenter = gy + (cellSize * wayTileFillY) * 0.5f;

                if (wayPrefab)
                {
                    var go = UnityEngine.Object.Instantiate(
                        wayPrefab,
                        new Vector3(wc.x, yCenter, wc.z),
                        Quaternion.identity,
                        container.transform
                    );
                    Fit(go, cellSize * wayTileFillXZ, cellSize * wayTileFillY, cellSize * wayTileFillXZ);
                }

                if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
            }
    }

    // ====== 区画化＋建物配置 ======
    struct Lot { public List<Vector2Int> cells; public int minX, minZ, maxX, maxZ; public bool touchesWay; }

    List<Lot> BuildLotsFromRemaining(bool touchesWayOnly)
    {
        var lots = new List<Lot>();
        var visited = new bool[chunkTiles, chunkTiles];

        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
            {
                if (grid[x, z] != CellType.Empty || visited[x, z]) continue;

                bool seedTouches = TouchesWay4(x, z);
                if (touchesWayOnly && !seedTouches) continue;

                var q = new Queue<Vector2Int>();
                var cells = new List<Vector2Int>();
                q.Enqueue(new Vector2Int(x, z));
                visited[x, z] = true;

                int minX = x, maxX = x, minZ = z, maxZ = z;
                bool touches = seedTouches;

                while (q.Count > 0)
                {
                    var p = q.Dequeue();
                    cells.Add(p);
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);

                    foreach (var n in Neigh4(p.x, p.y))
                    {
                        if (!InRange(n.x, n.y) || visited[n.x, n.y]) continue;
                        if (grid[n.x, n.y] != CellType.Empty) continue;
                        if (TouchesWay4(n.x, n.y)) touches = true;

                        visited[n.x, n.y] = true;
                        q.Enqueue(n);
                    }
                    if (mergeDiagonals)
                    {
                        foreach (var n in NeighDiag(p.x, p.y))
                        {
                            if (!InRange(n.x, n.y) || visited[n.x, n.y]) continue;
                            if (grid[n.x, n.y] != CellType.Empty) continue;
                            if (TouchesWay4(n.x, n.y)) touches = true;

                            visited[n.x, n.y] = true;
                            q.Enqueue(n);
                        }
                    }
                }

                if (cells.Count < Mathf.Max(1, minLotAreaCells)) continue;

                lots.Add(new Lot { cells = cells, minX = minX, maxX = maxX, minZ = minZ, maxZ = maxZ, touchesWay = touches });
            }

        lots.RemoveAll(l => !l.touchesWay);
        return lots;
    }

    IEnumerator StampLotsAndBuildingsCoroutine(List<Lot> lots)
    {
        int budget = tilesPerFrame;

        foreach (var lot in lots)
        {
            int wCells = lot.maxX - lot.minX + 1;
            int hCells = lot.maxZ - lot.minZ + 1;

            float cx = (lot.minX + lot.maxX + 1) * 0.5f;
            float cz = (lot.minZ + lot.maxZ + 1) * 0.5f;
            Vector3 wc = LocalToWorldCenterFloat(cx, cz);

            float ground = SampleGroundY(wc.x, wc.z);

            // 1) Lotベースブロック（任意）
            if (placeLotBaseBlock && blockPrefab)
            {
                float lotSizeX = wCells * cellSize;
                float lotSizeZ = hCells * cellSize;

                float sizeX = lotSizeX * Mathf.Clamp01(blockFillXZ);
                float sizeZ = lotSizeZ * Mathf.Clamp01(blockFillXZ);
                float sizeY = cellSize * Mathf.Max(0.05f, blockFillY);

                float yCenter = ground + sizeY * 0.5f;

                var baseGo = UnityEngine.Object.Instantiate(
                    blockPrefab,
                    new Vector3(wc.x, yCenter, wc.z),
                    Quaternion.identity,
                    container.transform
                );
                Fit(baseGo, sizeX, sizeY, sizeZ);
            }

            // 2) Building複数（元サイズのまま、回転だけ）
            if (buildingPrefabs != null && buildingPrefabs.Length > 0)
            {
                int minB = Mathf.Max(0, buildingsPerLot.x);
                int maxB = Mathf.Max(minB, buildingsPerLot.y);
                int count = UnityEngine.Random.Range(minB, maxB + 1);

                for (int i = 0; i < count; i++)
                {
                    var prefab = buildingPrefabs[rng.Next(buildingPrefabs.Length)];
                    if (!prefab) continue;

                    // Lotの矩形内でランダム座標
                    float rx = UnityEngine.Random.Range(lot.minX + 0.2f, lot.maxX + 0.8f);
                    float rz = UnityEngine.Random.Range(lot.minZ + 0.2f, lot.maxZ + 0.8f);
                    Vector3 pos = LocalToWorldCenterFloat(rx, rz);

                    float gy = SampleGroundY(pos.x, pos.z);

                    var b = UnityEngine.Object.Instantiate(
                        prefab,
                        new Vector3(pos.x, gy, pos.z),
                        Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f),
                        container.transform
                    );
                    // ★スケールはいじらない（元サイズのまま）
                }
            }

            if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
        }
    }

    // ====== NavMesh（Wayのみ / バウンズに余白） ======
    void BuildChunkNavMeshAsync()
    {
        // Wayが無ければ何もしない（Empty回避）
        if (!AnyWay()) { navReady = false; return; }

        var sources = new List<NavMeshBuildSource>();
        var markups = new List<NavMeshBuildMarkup>();
        int wayMask = LayerMask.GetMask("Way");

        NavMeshBuilder.CollectSources(
            container.transform,
            wayMask,
            NavMeshCollectGeometry.RenderMeshes,
            0,
            markups,
            sources
        );
        if (sources.Count == 0) { navReady = false; return; }

        var settings = NavMesh.GetSettingsByID(agentTypeId);

        // チャンク範囲 + 1セルの余白（継ぎ目サンプリング安定）
        float w = chunkTiles * cellSize;
        float d = chunkTiles * cellSize;
        var center = new Vector3(OriginX + w * 0.5f, 0f, OriginZ + d * 0.5f);
        var bounds = new Bounds(center, new Vector3(w + cellSize, 1000f, d + cellSize));

        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        navData = new NavMeshData(settings.agentTypeID);
        navInstance = NavMesh.AddNavMeshData(navData);

        navReady = false;
        navOp = NavMeshBuilder.UpdateNavMeshDataAsync(navData, settings, sources, bounds);
        if (navOp != null)
            navOp.completed += _ => { navReady = true; };
    }

    // ====== 補助 ======
    float OriginX => (chunkIndex.x * chunkTiles) * cellSize;
    float OriginZ => (chunkIndex.y * chunkTiles) * cellSize;

    int WorldCellX(int localX) => chunkIndex.x * chunkTiles + localX;
    int WorldCellZ(int localZ) => chunkIndex.y * chunkTiles + localZ;
    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    Vector3 LocalToWorldCenter(int gx, int gz)
    {
        float wx = OriginX + (gx + 0.5f) * cellSize;
        float wz = OriginZ + (gz + 0.5f) * cellSize;
        return new Vector3(wx, 0f, wz);
    }
    Vector3 LocalToWorldCenterFloat(float gx, float gz)
    {
        float wx = OriginX + gx * cellSize;
        float wz = OriginZ + gz * cellSize;
        return new Vector3(wx, 0f, wz);
    }

    float SampleGroundY(float x, float z)
    {
        if (groundMask.value == 0) return 0f;
        Vector3 origin = new Vector3(x, 10000f, z);
        if (Physics.Raycast(origin, Vector3.down, out var hit, 20000f, groundMask, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return 0f;
    }

    bool AnyWay()
    {
        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
                if (grid[x, z] == CellType.Way) return true;
        return false;
    }

    bool TouchesWay4(int x, int z)
    {
        foreach (var n in Neigh4(x, z))
        {
            if (!InRange(n.x, n.y)) continue;
            if (grid[n.x, n.y] == CellType.Way) return true;
        }
        return false;
    }

    static readonly Vector2Int[] _N4 = {
        new Vector2Int( 1, 0), new Vector2Int(-1, 0),
        new Vector2Int( 0, 1), new Vector2Int( 0,-1),
    };
    static readonly Vector2Int[] _ND = {
        new Vector2Int( 1, 1), new Vector2Int(-1, 1),
        new Vector2Int( 1,-1), new Vector2Int(-1,-1),
    };
    IEnumerable<Vector2Int> Neigh4(int x, int z) { for (int i = 0; i < _N4.Length; i++) yield return new Vector2Int(x + _N4[i].x, z + _N4[i].y); }
    IEnumerable<Vector2Int> NeighDiag(int x, int z) { for (int i = 0; i < _ND.Length; i++) yield return new Vector2Int(x + _ND[i].x, z + _ND[i].y); }
    bool InRange(int x, int z) => (x >= 0 && x < chunkTiles && z >= 0 && z < chunkTiles);

    void Fit(GameObject go, float sizeX, float sizeY, float sizeZ)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;

        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

        Vector3 cur = b.size;
        if (cur.x < 1e-4f || cur.y < 1e-4f || cur.z < 1e-4f) return;

        Vector3 s = go.transform.localScale;
        go.transform.localScale = new Vector3(
            s.x * (sizeX / cur.x),
            s.y * (sizeY / cur.y),
            s.z * (sizeZ / cur.z)
        );
    }
}
