using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class CityChunkBSP : IDisposable
{
    [Header("World / Chunk")]
    public int chunkTiles;
    public float cellSize;
    public int tilesPerFrame = 1500;

    [Header("Prefabs")]
    public GameObject wayPrefab;         // Layer=Way
    public GameObject blockPrefab;       // Layer=Block
    public GameObject[] buildingPrefabs;

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 0.90f;
    [Range(0.05f, 3f)] public float blockFillY = 1.20f;

    [Header("Road Generation")]
    public int globalArterialPeriod = 8;
    [Range(1, 3)] public int globalArterialWidth = 1;
    public int minPartitionSize = 0;
    [Range(0f, 1f)] public float extraCrossChance = 0.35f;
    [Range(0, 3)] public int extraCrossWidth = 1;

    public enum RoadGenMode { BSP, RotatedGrid, RandomWalkers }
    [Header("Road Generation: Mode & Params")]
    public RoadGenMode roadMode = RoadGenMode.BSP;

    // Rotated grid
    [Range(-89f, 89f)] public float rotatedGridAngleDeg = 30f;
    [Min(4)] public int rotatedGridPeriod = 14;
    [Range(1, 3)] public int rotatedGridWidth = 1;

    // Random walkers
    [Min(1)] public int walkersCount = 6;
    [Min(10)] public int walkerMaxSteps = 500;
    [Range(0f, 1f)] public float walkerTurnBias = 0.25f;    // どれくらい曲がりやすいか
    [Range(0f, 1f)] public float walkerJunctionBias = 0.10f; // Wayに接したら分岐しやすい


    [Header("Lots / Buildings")]
    public bool placeLotBaseBlock = true;
    public int minLotAreaCells = 3;
    public bool mergeDiagonals = false;

    public bool packBuildingsInGrid = true;
    public float buildingGridPadding = 0.5f;
    public float lotEdgeMargin = 0.5f;
    public int maxBuildingsPerLot = 200;
    public bool staticCombinePerLot = true;

    [Header("Hole Coverage")]
    public bool coverAllEmptyLots = true;        // 道に接していない領域もロット化して埋める
    public bool fullCoverBaseBlocks = true;      // 台座ブロックでロット全面を覆う
    [Range(0f, 0.05f)] public float baseBlockEdgeOverlap = 0.01f; // ほんの少しだけ重ねて隙間ゼロ化

    [Header("Ultimate Hole Killer")]
    public bool fallbackFillEveryEmptyCell = true;   // これが true なら空セルを薄板で必ず埋める
    [Range(0.02f, 0.5f)] public float cellFillY = 0.10f; // 薄板の厚み（EffCell × この値）
    [Range(0f, 0.05f)] public float cellFillOverlap = 0.01f; // XZをわずかに広げて隙間ゼロ化

    [Header("Connectors (Way ↔ Block)")]
    public bool ensureSidewalkConnectors = true;
    [Range(1, 3)] public int connectorWidthCells = 1;
    [Range(0.02f, 1f)] public float connectorTileFillY = 0.10f;

    [Header("Ground (0=未使用)")]
    public LayerMask groundMask = 0;

    [Header("NavMesh")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;
    public bool includeBlocksInNavMesh = true;

    [Tooltip("隣接チャンクとオーバーラップさせる余白（セル数）")]
    [Range(0, 4)] public int stitchOverlapCells = 1;
    [Tooltip("NavMesh のタイルサイズを明示上書き（大きいほどタイル境界が減る）")]
    [Range(16, 256)] public int navmeshTileSize = 128;
    [Tooltip("ボクセルを細かくし過ぎると重い。agentRadius/3 が目安")]
    public bool overrideVoxelSize = true;

    [Header("Performance / Scale")]
    [Range(0.01f, 1f)] public float globalScale = 1f;
    [Tooltip("スタンプ処理でフレーム分割しない（小さなチャンク向け）")]
    public bool noYieldDuringStamp = true;

    // 3サイズのビル（セル単位: 10x10 / 20x20 / 30x30 を想定）
    [Header("Packing: Building Prefabs by Size (cells)")]
    public GameObject prefab10;
    public GameObject prefab20;
    public GameObject prefab30;
    // === New: rectangle footprints (world meters) ===
    // 例: 50×50 / 60×60 / 40×60（60×40 は回転で自動対応）
    [Header("Packing: Prefab Sets (meters)")]
    public GameObject[] prefabs50x50;
    public GameObject[] prefabs60x60;
    public GameObject[] prefabs40x60;

    // ランダム化つまみ
    [Header("Packing: Randomization")]
    [Range(0f, 1f)][SerializeField] float positionShuffleRate = 1f; // 位置スキャン順のシャッフル強度（1=完全シャッフル）
    [Range(0f, 0.3f)][SerializeField] float epsilon = 0.05f;        // ε-greedy：稀に優先度を崩す（0=常に30→20→10）
    [Min(1)][SerializeField] int multiStart = 2;                    // マルチスタート試行回数（1で従来同等）
    [SerializeField] int randSeedOffset = 12345;                     // ロット毎の再現性用オフセット
    [SerializeField] bool deterministicPerLot = true;                // 同一ロットは毎回同じ結果に

    // セル配置の上限（安全弁）
    [SerializeField] int maxCellsToScanPerLot = 200000;

    public enum CellType { Empty, Way }
    CellType[,] grid;
    System.Random rng;
    public Vector2Int chunkIndex;
    GameObject container;

    // 連続ナビ床（NavMesh用不可視メッシュ）
    GameObject navFloorGO;
    Mesh navFloorMesh;

    NavMeshData navData;
    NavMeshDataInstance navInstance;
    bool navReady;
    bool _disposed;

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
        container.transform.localScale = Vector3.one * Mathf.Max(0.01f, globalScale);
        if (parent) container.transform.SetParent(parent, false);
    }

    public IEnumerator GenerateAsync()
    {
        switch (roadMode)
        {
            case RoadGenMode.BSP:
                CarveGlobalArterials();
                CarveRoadsBspStyle();
                break;
            case RoadGenMode.RotatedGrid:
                ClearGrid();
                CarveRotatedGrid();
                break;
            case RoadGenMode.RandomWalkers:
                ClearGrid();
                CarveRandomWalkers();
                break;
        }
        container.transform.localScale = Vector3.one * Mathf.Max(0.01f, globalScale);
        container.transform.position = new Vector3(OriginX, 0f, OriginZ);

        CarveGlobalArterials();
        CarveRoadsBspStyle();

        yield return StampWaysCoroutine();

        var lots = BuildLotsFromRemaining(touchesWayOnly: false); // 既にfalse化済み
        yield return StampLotsAndBuildingsCoroutine(lots);

        // ここで NavFloor を作る（既存）
        BuildContinuousNavFloor();

        // 穴つぶしの最終フィル（新規）
        yield return StampFallbackCellFill();

        // その後にベイク
        if (bakeNavMeshPerChunk) BuildChunkNavMeshSync();
    }

    public void Dispose()
    {
        _disposed = true;
        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        if (navData != null) navData = null;
        if (navFloorGO) UnityEngine.Object.Destroy(navFloorGO);
        if (container) UnityEngine.Object.Destroy(container);
    }

    float EffCell => cellSize * Mathf.Max(0.01f, globalScale);
    float OriginX => (chunkIndex.x * chunkTiles) * EffCell;
    float OriginZ => (chunkIndex.y * chunkTiles) * EffCell;
    int WorldCellX(int localX) => chunkIndex.x * chunkTiles + localX;
    int WorldCellZ(int localZ) => chunkIndex.y * chunkTiles + localZ;
    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    // ===== 道生成（略：既存ロジック） =====
    void CarveGlobalArterials()
    {
        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
                grid[x, z] = CellType.Empty;

        int period = Mathf.Max(1, globalArterialPeriod);
        int width = Mathf.Clamp(globalArterialWidth, 1, 3);

        for (int lx = 0; lx < chunkTiles; lx++)
        {
            int wxCell = WorldCellX(lx);
            if (Mod(wxCell, period) == 0)
                CarveV(0, chunkTiles, lx, width);
        }
        for (int lz = 0; lz < chunkTiles; lz++)
        {
            int wzCell = WorldCellZ(lz);
            if (Mod(wzCell, period) == 0)
                CarveH(0, chunkTiles, lz, width);
        }
    }
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

    void ClearGrid()
    {
        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
                grid[x, z] = CellType.Empty;
    }

    void CarveRotatedGrid()
    {
        float theta = rotatedGridAngleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(theta), sin = Mathf.Sin(theta);

        int period = Mathf.Max(2, rotatedGridPeriod);
        int width = Mathf.Clamp(rotatedGridWidth, 1, 3);

        for (int lx = 0; lx < chunkTiles; lx++)
        {
            for (int lz = 0; lz < chunkTiles; lz++)
            {
                int wx = WorldCellX(lx);
                int wz = WorldCellZ(lz);

                float ux = wx * cos + wz * sin;
                float uz = -wx * sin + wz * cos;

                int mx = Mathf.Abs(Mod(Mathf.RoundToInt(ux), period));
                int mz = Mathf.Abs(Mod(Mathf.RoundToInt(uz), period));

                bool onX = (mx == 0);
                bool onZ = (mz == 0);

                if (onX || onZ)
                {
                    if (onX)
                        for (int w = -((width - 1) / 2); w <= (width - 1) / 2; w++)
                        {
                            int zz = lz + w;
                            if (zz >= 0 && zz < chunkTiles)
                                grid[lx, zz] = CellType.Way;
                        }
                    if (onZ)
                        for (int w = -((width - 1) / 2); w <= (width - 1) / 2; w++)
                        {
                            int xx = lx + w;
                            if (xx >= 0 && xx < chunkTiles)
                                grid[xx, lz] = CellType.Way;
                        }
                }
            }
        }
    }

    void CarveRandomWalkers()
    {
        var dirs = new Vector2Int[] { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        for (int i = 0; i < walkersCount; i++)
        {
            int x = rng.Next(1, chunkTiles - 1);
            int z = rng.Next(1, chunkTiles - 1);
            int d = rng.Next(0, dirs.Length);

            for (int step = 0; step < walkerMaxSteps; step++)
            {
                if (!InRange(x, z)) break;
                grid[x, z] = CellType.Way;

                if (TouchesWay4(x, z) && rng.NextDouble() < walkerJunctionBias)
                {
                    int nd = (d + (rng.Next(0, 2) == 0 ? 1 : 3)) & 3;
                    var b = dirs[nd];
                    int bx = x + b.x, bz = z + b.y;
                    if (InRange(bx, bz)) grid[bx, bz] = CellType.Way;
                }

                if (rng.NextDouble() < walkerTurnBias)
                    d = (d + (rng.Next(0, 2) == 0 ? 1 : 3)) & 3;

                var mv = dirs[d];
                x += mv.x; z += mv.y;

                if (x <= 1 || x >= chunkTiles - 2 || z <= 1 || z >= chunkTiles - 2)
                    d = (d + 2) & 3;
            }
        }
    }
    void CarveH(int xMin, int xMax, int z, int width)
    {
        int half = Mathf.Max(0, (width - 1) / 2);
        for (int dz = -half; dz <= half; dz++)
        {
            int zz = z + dz; if (zz < 0 || zz >= chunkTiles) continue;
            for (int x = xMin; x < xMax; x++) grid[x, zz] = CellType.Way;
        }
    }
    void CarveV(int zMin, int zMax, int x, int width)
    {
        int half = Mathf.Max(0, (width - 1) / 2);
        for (int dx = -half; dx <= half; dx++)
        {
            int xx = x + dx; if (xx < 0 || xx >= chunkTiles) continue;
            for (int z = zMin; z < zMax; z++) grid[xx, z] = CellType.Way;
        }
    }

    IEnumerator StampWaysCoroutine()
    {
        int budget = tilesPerFrame;
        if (noYieldDuringStamp) budget = int.MaxValue;

        if (_disposed || container == null) yield break;
        var parent = container.transform;

        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
            {
                if (_disposed || parent == null) yield break;
                if (grid[x, z] != CellType.Way) continue;

                Vector3 wc = LocalToWorldCenter(x, z);
                float gy = SampleGroundY(wc.x, wc.z);
                float yCenter = gy + (EffCell * wayTileFillY) * 0.5f;

                if (wayPrefab)
                {
                    var go = UnityEngine.Object.Instantiate(
                        wayPrefab, new Vector3(wc.x, yCenter, wc.z),
                        Quaternion.identity, parent
                    );
                    if (go) Fit(go, EffCell * wayTileFillXZ, EffCell * wayTileFillY, EffCell * wayTileFillXZ);
                }

                if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
            }
    }

    public struct Lot { public List<Vector2Int> cells; public int minX, minZ, maxX, maxZ; public bool touchesWay; }

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

                lots.Add(new Lot
                {
                    cells = cells,
                    minX = minX,
                    maxX = maxX,
                    minZ = minZ,
                    maxZ = maxZ,
                    touchesWay = touches
                });
            }

        // ★ ここを“条件付き”にする：coverAllEmptyLots が有効のときは削除しない
        if (touchesWayOnly)
            lots.RemoveAll(l => !l.touchesWay);

        return lots;
    }

    IEnumerator StampLotsAndBuildingsCoroutine(List<Lot> lots)
    {
        int budget = tilesPerFrame;
        if (noYieldDuringStamp) budget = int.MaxValue;

        if (_disposed || container == null) yield break;
        var parent = container.transform;

        foreach (var lot in lots)
        {
            if (_disposed || parent == null) yield break;

            // --- 台座ブロック生成部分（穴防止仕様） ---
            float baseMargin = (fullCoverBaseBlocks ? 0f : lotEdgeMargin);
            float lotMinX = OriginX + lot.minX * EffCell + baseMargin;
            float lotMaxX = OriginX + (lot.maxX + 1) * EffCell - baseMargin;
            float lotMinZ = OriginZ + lot.minZ * EffCell + baseMargin;
            float lotMaxZ = OriginZ + (lot.maxZ + 1) * EffCell - baseMargin;
            float lotW = Mathf.Max(0f, lotMaxX - lotMinX);
            float lotD = Mathf.Max(0f, lotMaxZ - lotMinZ);

            if (placeLotBaseBlock && blockPrefab && lotW > 0f && lotD > 0f)
            {
                // わずかに重ねて隙間を消す（例：1% オーバー）
                float over = Mathf.Clamp01(baseBlockEdgeOverlap);
                float sizeX = lotW * (fullCoverBaseBlocks ? (1f + over) : Mathf.Clamp01(blockFillXZ));
                float sizeZ = lotD * (fullCoverBaseBlocks ? (1f + over) : Mathf.Clamp01(blockFillXZ));
                float sizeY = EffCell * Mathf.Max(0.05f, blockFillY);

                Vector3 center = new Vector3((lotMinX + lotMaxX) * 0.5f, 0f, (lotMinZ + lotMaxZ) * 0.5f);
                float ground = SampleGroundY(center.x, center.z);

                var baseGo = UnityEngine.Object.Instantiate(
                    blockPrefab,
                    new Vector3(center.x, ground + sizeY * 0.5f, center.z),
                    Quaternion.identity,
                    parent
                );
                if (baseGo)
                {
                    Fit(baseGo, sizeX, sizeY, sizeZ);
                    baseGo.isStatic = true;
                }
            }


            // 歩道コネクタ（任意）
            if (ensureSidewalkConnectors) AddLotSidewalkConnectors(lot);

            // 建物敷き詰め（BuildingPacker に委譲）
            if (packBuildingsInGrid && lotW > 0f && lotD > 0f)
            {
                var packer = new BuildingPacker
                {
                    // プレハブ群（Manager から受け取っている前提：このクラスの public フィールド）
                    prefab10 = prefab10,
                    prefab20 = prefab20,
                    prefab30 = prefab30,
                    prefabs50x50 = prefabs50x50,
                    prefabs60x60 = prefabs60x60,
                    prefabs40x60 = prefabs40x60,
                    // ランダム＆制限
                    positionShuffleRate = positionShuffleRate,
                    epsilon = epsilon,
                    multiStart = multiStart,
                    randSeedOffset = randSeedOffset,
                    deterministicPerLot = deterministicPerLot,
                    maxCellsToScanPerLot = maxCellsToScanPerLot,
                    maxBuildingsPerLot = maxBuildingsPerLot
                };

                var ctx = new BuildingPacker.Context
                {
                    EffCell = EffCell,
                    OriginX = OriginX,
                    OriginZ = OriginZ,
                    tilesPerFrame = tilesPerFrame,
                    noYield = noYieldDuringStamp,
                    lotEdgeMargin = lotEdgeMargin,
                    SampleSupportTopY = SampleSupportTopY,
                    FitToSize = Fit
                };

                // packer 内部で BuildingsLot を作り、静的結合まで行う
                yield return packer.PlaceOnLot(lot, parent, ctx, staticCombinePerLot);
            }
            if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
        }
    }

    IEnumerator StampFallbackCellFill()
    {
        if (!fallbackFillEveryEmptyCell) yield break;
        if (_disposed || container == null) yield break;

        int budget = tilesPerFrame;
        if (noYieldDuringStamp) budget = int.MaxValue;

        var parent = container.transform;

        float w = EffCell * (1f + Mathf.Clamp01(cellFillOverlap));
        float d = EffCell * (1f + Mathf.Clamp01(cellFillOverlap));
        float ySize = EffCell * Mathf.Max(0.02f, cellFillY);

        for (int x = 0; x < chunkTiles; x++)
        {
            for (int z = 0; z < chunkTiles; z++)
            {
                if (_disposed || parent == null) yield break;

                // 道セル以外（= Empty）は必ず薄板で埋める
                if (grid[x, z] == CellType.Way) continue;

                Vector3 c = LocalToWorldCenter(x, z);
                float gy = SampleGroundY(c.x, c.z);
                float yCenter = gy + ySize * 0.5f;

                if (blockPrefab)
                {
                    var go = UnityEngine.Object.Instantiate(
                        blockPrefab, new Vector3(c.x, yCenter, c.z),
                        Quaternion.identity, parent
                    );
                    if (go)
                    {
                        Fit(go, w, ySize, d);
                        go.isStatic = true;
                    }
                }

                if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
            }
        }
    }

    // ===== 連続ナビ床（不可視）を1枚のメッシュで生成 =====
    void BuildContinuousNavFloor()
    {
        if (navFloorGO == null)
        {
            navFloorGO = new GameObject("__NavFloor");
            navFloorGO.layer = LayerMask.NameToLayer("Way"); // 収集対象
            navFloorGO.transform.SetParent(container.transform, false);
            var mf = navFloorGO.AddComponent<MeshFilter>();
            var mr = navFloorGO.AddComponent<MeshRenderer>();
            mr.enabled = false; // 見た目には表示しない
            navFloorMesh = new Mesh { name = "NavFloorMesh" };
            mf.sharedMesh = navFloorMesh;
        }

        // 道セルを全て 1セル=1クワッドとしてまとめ打ち（わずかに拡張して隙間ゼロに）
        const float epsilon = 0.001f; // ほんの少し広げてエッジの浮動小数誤差を回避
        float w = EffCell + epsilon;
        float d = EffCell + epsilon;

        var verts = new List<Vector3>();
        var tris = new List<int>();

        for (int x = 0; x < chunkTiles; x++)
            for (int z = 0; z < chunkTiles; z++)
            {
                if (grid[x, z] != CellType.Way) continue;

                Vector3 c = LocalToWorldCenter(x, z);
                float gy = SampleGroundY(c.x, c.z);
                float y = gy + (EffCell * connectorTileFillY) * 0.5f; // 道タイルと同じくらいの高さ

                int idx = verts.Count;
                verts.Add(new Vector3(c.x - w * 0.5f, y, c.z - d * 0.5f));
                verts.Add(new Vector3(c.x + w * 0.5f, y, c.z - d * 0.5f));
                verts.Add(new Vector3(c.x + w * 0.5f, y, c.z + d * 0.5f));
                verts.Add(new Vector3(c.x - w * 0.5f, y, c.z + d * 0.5f));

                tris.Add(idx + 0); tris.Add(idx + 2); tris.Add(idx + 1);
                tris.Add(idx + 0); tris.Add(idx + 3); tris.Add(idx + 2);
            }

        navFloorMesh.Clear();
        navFloorMesh.SetVertices(verts);
        navFloorMesh.SetTriangles(tris, 0);
        navFloorMesh.RecalculateBounds();
        navFloorMesh.RecalculateNormals();
    }

    void AddLotSidewalkConnectors(Lot lot)
    {
        int w = connectorWidthCells <= 0 ? 1 : Mathf.Clamp(connectorWidthCells, 1, 3);

        bool touchLeft = false, touchRight = false, touchBottom = false, touchTop = false;

        if (lot.minX - 1 >= 0)
            for (int z = lot.minZ; z <= lot.maxZ; z++)
                if (grid[lot.minX - 1, z] == CellType.Way) { touchLeft = true; break; }

        if (lot.maxX + 1 < chunkTiles)
            for (int z = lot.minZ; z <= lot.maxZ; z++)
                if (grid[lot.maxX + 1, z] == CellType.Way) { touchRight = true; break; }

        if (lot.minZ - 1 >= 0)
            for (int x = lot.minX; x <= lot.maxX; x++)
                if (grid[x, lot.minZ - 1] == CellType.Way) { touchBottom = true; break; }

        if (lot.maxZ + 1 < chunkTiles)
            for (int x = lot.minX; x <= lot.maxX; x++)
                if (grid[x, lot.maxZ + 1] == CellType.Way) { touchTop = true; break; }

        if (touchLeft)
            for (int x = lot.minX; x < Mathf.Min(lot.minX + w, lot.maxX + 1); x++)
                for (int z = lot.minZ; z <= lot.maxZ; z++) StampConnectorCell(x, z);

        if (touchRight)
            for (int x = Mathf.Max(lot.maxX - w + 1, lot.minX); x <= lot.maxX; x++)
                for (int z = lot.minZ; z <= lot.maxZ; z++) StampConnectorCell(x, z);

        if (touchBottom)
            for (int z = lot.minZ; z < Mathf.Min(lot.minZ + w, lot.maxZ + 1); z++)
                for (int x = lot.minX; x <= lot.maxX; x++) StampConnectorCell(x, z);

        if (touchTop)
            for (int z = Mathf.Max(lot.maxZ - w + 1, lot.minZ); z <= lot.maxZ; z++)
                for (int x = lot.minX; x <= lot.maxX; x++) StampConnectorCell(x, z);

        void StampConnectorCell(int gx, int gz)
        {
            if (grid[gx, gz] == CellType.Way) return;
            grid[gx, gz] = CellType.Way;

            Vector3 wc = LocalToWorldCenter(gx, gz);
            float gy = SampleGroundY(wc.x, wc.z);
            float yCenter = gy + (EffCell * connectorTileFillY) * 0.5f;

            if (wayPrefab && !_disposed && container)
            {
                var go = UnityEngine.Object.Instantiate(
                    wayPrefab, new Vector3(wc.x, yCenter, wc.z),
                    Quaternion.identity, container.transform
                );
                if (go) Fit(go, EffCell * wayTileFillXZ, EffCell * connectorTileFillY, EffCell * wayTileFillXZ);
            }
        }
    }

    Vector2 EstimatePrefabFootprintXZ(GameObject prefab)
    {
        var rs = prefab.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Vector2(1f, 1f);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return new Vector2(b.size.x, b.size.z);
    }

    void TryStaticCombine(GameObject groupRoot)
    {
        if (_disposed || !groupRoot) return;
        var filters = groupRoot.GetComponentsInChildren<MeshFilter>();
        if (filters.Length <= 1) return;

        var combines = new List<CombineInstance>(filters.Length);
        foreach (var mf in filters)
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (!mf.sharedMesh || !mr) continue;
            combines.Add(new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix });
            mr.enabled = false;
        }

        var combined = new GameObject("Combined");
        combined.transform.SetParent(groupRoot.transform, false);
        var mfCombined = combined.AddComponent<MeshFilter>();
        var mrCombined = combined.AddComponent<MeshRenderer>();
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(combines.ToArray(), true, true);
        mfCombined.sharedMesh = mesh;

        var anyMr = filters[0].GetComponent<MeshRenderer>();
        if (anyMr) mrCombined.sharedMaterials = anyMr.sharedMaterials;
    }

    // ===== NavMesh（連続床＋ブロックを収集、隣チャンクと重なる余白つき） =====
    void BuildChunkNavMeshSync()
    {
        navReady = false;
        if (_disposed) return;

        var sources = new List<NavMeshBuildSource>();
        var markups = new List<NavMeshBuildMarkup>();

        int collectMask = includeBlocksInNavMesh
            ? LayerMask.GetMask("Way", "Block")
            : LayerMask.GetMask("Way");

        // ★ 親を指定せず、ワールド Bounds 指定で収集 → 隣接チャンク分まで拾える
        float overlap = stitchOverlapCells * EffCell;
        float w = chunkTiles * EffCell + overlap * 2f;
        float d = chunkTiles * EffCell + overlap * 2f;
        var center = new Vector3(OriginX + (chunkTiles * EffCell) * 0.5f, 0f, OriginZ + (chunkTiles * EffCell) * 0.5f);
        var includeBounds = new Bounds(center, new Vector3(w, 1000f, d));

        NavMeshBuilder.CollectSources(
            includeBounds,                 // ← ワールド範囲
            collectMask,
            NavMeshCollectGeometry.RenderMeshes,
            0, markups, sources
        );
        if (sources.Count == 0)
        {
            NavMeshBuilder.CollectSources(
                includeBounds,
                collectMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0, markups, sources
            );
        }
        if (sources.Count == 0) return;

        // 設定を調整：タイルサイズ/ボクセルサイズを明示上書き
        var settings = NavMesh.GetSettingsByID(agentTypeId);
        settings.overrideTileSize = true;
        settings.tileSize = navmeshTileSize;

        if (overrideVoxelSize)
        {
            // 目安：半径の 1/3
            float agentRadius = settings.agentRadius > 0f ? settings.agentRadius : 0.5f;
            settings.overrideVoxelSize = true;
            settings.voxelSize = Mathf.Max(0.02f, agentRadius / 3f);
        }

        // ベイク範囲（収集よりわずかに広め）
        var buildBounds = new Bounds(center, new Vector3(w + EffCell, 1000f, d + EffCell));

        var built = NavMeshBuilder.BuildNavMeshData(settings, sources, buildBounds, Vector3.zero, Quaternion.identity);
        if (built == null) return;

        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        navData = built;
        navInstance = NavMesh.AddNavMeshData(navData);

        navReady = true;
    }

    // ====== 共通ユーティリティ ======
    // 地面(groundMask) または Block レイヤーの「上面」を返す
    // 地面(groundMask) または Block レイヤーの「上面」を返す
    float SampleSupportTopY(float x, float z)
    {
        int blockMask = LayerMask.GetMask("Block");
        int mask = groundMask.value != 0 ? (groundMask.value | blockMask) : blockMask;

        Vector3 origin = new Vector3(x, 10000f, z);
        if (Physics.Raycast(origin, Vector3.down, out var hit, 20000f, mask, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return 0f;
    }

    Vector3 LocalToWorldCenter(int gx, int gz)
    {
        float wx = OriginX + (gx + 0.5f) * EffCell;
        float wz = OriginZ + (gz + 0.5f) * EffCell;
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
    bool TouchesWay4(int x, int z)
    {
        foreach (var n in Neigh4(x, z))
        {
            if (!InRange(n.x, n.y)) continue;
            if (grid[n.x, n.y] == CellType.Way) return true;
        }
        return false;
    }
    bool InRange(int x, int z) => (x >= 0 && x < chunkTiles && z >= 0 && z < chunkTiles);
    static readonly Vector2Int[] _N4 = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
    static readonly Vector2Int[] _ND = { new(1, 1), new(-1, 1), new(1, -1), new(-1, -1) };
    IEnumerable<Vector2Int> Neigh4(int x, int z) { for (int i = 0; i < _N4.Length; i++) yield return new Vector2Int(x + _N4[i].x, z + _N4[i].y); }
    IEnumerable<Vector2Int> NeighDiag(int x, int z) { for (int i = 0; i < _ND.Length; i++) yield return new Vector2Int(x + _ND[i].x, z + _ND[i].y); }

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