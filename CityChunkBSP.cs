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
    public GameObject wayPrefab;         // Layer=Way 推奨（MeshRenderer か Collider が必要）
    public GameObject blockPrefab;       // 区画のベース（任意）
    public GameObject[] buildingPrefabs; // 3種などを登録（元スケールのまま）

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 0.90f;
    [Range(0.05f, 3f)] public float blockFillY = 1.20f;

    [Header("Road Generation (Global+Random BSP)")]
    public int globalArterialPeriod = 8;
    [Range(1, 3)] public int globalArterialWidth = 1;
    public int minPartitionSize = 0;          // 0=自動（chunkTiles/6）
    [Range(0f, 1f)] public float extraCrossChance = 0.35f;
    [Range(0, 3)] public int extraCrossWidth = 1;

    [Header("Lots / Buildings Packing")]
    public bool placeLotBaseBlock = true;
    public int minLotAreaCells = 3;
    public bool mergeDiagonals = false;

    [Tooltip("Lot を建物の元サイズでグリッド敷きする（等間隔・整列）")]
    public bool packBuildingsInGrid = true;

    [Tooltip("グリッド間の余白（ワールド単位）")]
    public float buildingGridPadding = 0.5f;

    [Tooltip("Lot の外周マージン（建物が道にはみ出さない余白）")]
    public float lotEdgeMargin = 0.5f;

    [Tooltip("Lot あたりの建物最大数（安全上限）")]
    public int maxBuildingsPerLot = 200;

    [Tooltip("敷いた建物を静的化＋（可能なら）合体して軽量化")]
    public bool staticCombinePerLot = true;

    [Header("Ground Sampling")]
    public LayerMask groundMask = 0;     // 0=地形なし（高速）

    [Header("NavMesh (Runtime)")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;

    // ===== 内部 =====
    public enum CellType { Empty, Way }
    CellType[,] grid;
    System.Random rng;
    public Vector2Int chunkIndex;
    GameObject container;

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
        // 1) 境界連続の幹線 → 2) BSPランダム道
        CarveGlobalArterials();
        CarveRoadsBspStyle();

        // 3) 道タイル配置
        yield return StampWaysCoroutine();

        // 4) 空き地→区画、区画ベースと Buildings 敷き詰め
        var lots = BuildLotsFromRemaining(touchesWayOnly: true);
        yield return StampLotsAndBuildingsCoroutine(lots);

        // 5) NavMesh（Wayのみ／メッシュ→無ければコライダー収集）
        if (bakeNavMeshPerChunk) BuildChunkNavMeshAsync();
    }

    public void Dispose()
    {
        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        if (container != null) UnityEngine.Object.Destroy(container);
    }

    // ====== 道：幹線（世界座標で period を共有 → チャンク継ぎ目連続） ======
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

        // 交差の追加
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

    // ====== 区画化＋建物（グリッド敷き詰め） ======
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
            // Lotの外接矩形（ワールド）
            float lotMinX = OriginX + lot.minX * cellSize + lotEdgeMargin;
            float lotMaxX = OriginX + (lot.maxX + 1) * cellSize - lotEdgeMargin;
            float lotMinZ = OriginZ + lot.minZ * cellSize + lotEdgeMargin;
            float lotMaxZ = OriginZ + (lot.maxZ + 1) * cellSize - lotEdgeMargin;
            float lotW = Mathf.Max(0f, lotMaxX - lotMinX);
            float lotD = Mathf.Max(0f, lotMaxZ - lotMinZ);

            // 1) ベースブロック（敷地台座：任意）
            if (placeLotBaseBlock && blockPrefab && lotW > 0f && lotD > 0f)
            {
                float sizeX = lotW * Mathf.Clamp01(blockFillXZ);
                float sizeZ = lotD * Mathf.Clamp01(blockFillXZ);
                float sizeY = cellSize * Mathf.Max(0.05f, blockFillY);

                Vector3 center = new Vector3((lotMinX + lotMaxX) * 0.5f, 0f, (lotMinZ + lotMaxZ) * 0.5f);
                float ground = SampleGroundY(center.x, center.z);
                var baseGo = UnityEngine.Object.Instantiate(
                    blockPrefab,
                    new Vector3(center.x, ground + sizeY * 0.5f, center.z),
                    Quaternion.identity,
                    container.transform
                );
                Fit(baseGo, sizeX, sizeY, sizeZ);
                baseGo.isStatic = true;
            }

            // 2) Buildings を「元サイズのまま」グリッド敷き
            if (buildingPrefabs != null && buildingPrefabs.Length > 0 && packBuildingsInGrid && lotW > 0f && lotD > 0f)
            {
                // 代表プレハブのフットプリント（XY=上下、XZ=平面）
                Vector2 fp = EstimatePrefabFootprintXZ(buildingPrefabs[0]); // 代表値
                // 代表値が小さすぎるとおかしくなるのでクランプ
                fp.x = Mathf.Max(0.1f, fp.x);
                fp.y = Mathf.Max(0.1f, fp.y);

                float pitchX = fp.x + buildingGridPadding;
                float pitchZ = fp.y + buildingGridPadding;

                int cols = Mathf.FloorToInt(lotW / pitchX);
                int rows = Mathf.FloorToInt(lotD / pitchZ);
                cols = Mathf.Clamp(cols, 0, 512);
                rows = Mathf.Clamp(rows, 0, 512);

                int placed = 0;
                // 左下基準の開始位置
                float startX = (lotMinX + lotMaxX) * 0.5f - (cols * pitchX) * 0.5f + pitchX * 0.5f;
                float startZ = (lotMinZ + lotMaxZ) * 0.5f - (rows * pitchZ) * 0.5f + pitchZ * 0.5f;

                // まとめ用の親
                GameObject lotGroup = new GameObject("BuildingsLot");
                lotGroup.transform.SetParent(container.transform, false);
                lotGroup.transform.position = new Vector3(OriginX, 0f, OriginZ);

                for (int rz = 0; rz < rows; rz++)
                {
                    for (int cx = 0; cx < cols; cx++)
                    {
                        if (placed >= maxBuildingsPerLot) break;

                        var prefab = buildingPrefabs[rng.Next(buildingPrefabs.Length)];
                        if (!prefab) continue;

                        float x = startX + cx * pitchX;
                        float z = startZ + rz * pitchZ;
                        float y = SampleGroundY(x, z);

                        // 角度は固定（揃える）※90°回転に切替たい場合はここで Quaternion.Euler(0,90,0)
                        var b = UnityEngine.Object.Instantiate(
                            prefab,
                            new Vector3(x, y, z),
                            Quaternion.identity,
                            lotGroup.transform
                        );
                        b.isStatic = staticCombinePerLot; // 後で合体するなら静的化

                        placed++;
                    }
                    if (placed >= maxBuildingsPerLot) break;
                }

                // 軽量化：静的合体（可能な環境で）
#if !UNITY_EDITOR
                if (staticCombinePerLot) TryStaticCombine(lotGroup);
#else
                if (staticCombinePerLot) TryStaticCombine(lotGroup);
#endif
            }

            if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
        }
    }

    // プレハブのXZフットプリント推定（Renderer合成）
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
        // MeshCombine（単純版）
        var filters = groupRoot.GetComponentsInChildren<MeshFilter>();
        if (filters.Length <= 1) return;

        var combines = new List<CombineInstance>(filters.Length);
        foreach (var mf in filters)
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (!mf.sharedMesh || !mr) continue;
            var ci = new CombineInstance
            {
                mesh = mf.sharedMesh,
                transform = mf.transform.localToWorldMatrix
            };
            combines.Add(ci);
            mr.enabled = false;
        }

        var combined = new GameObject("Combined");
        combined.transform.SetParent(groupRoot.transform, false);
        var mfCombined = combined.AddComponent<MeshFilter>();
        var mrCombined = combined.AddComponent<MeshRenderer>();
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(combines.ToArray(), true, true);
        mfCombined.sharedMesh = mesh;
        // マテリアルは適当に1つに（複数マテリアルを厳密保持するなら CombineMeshes(false,false) など別設計）
        if (filters.Length > 0)
        {
            var mrs = filters[0].GetComponent<MeshRenderer>();
            if (mrs) mrCombined.sharedMaterials = mrs.sharedMaterials;
        }
    }

    // ====== NavMesh（Wayのみ、Mesh→無ければCollider収集、境界+1セル余白） ======
    void BuildChunkNavMeshAsync()
    {
        navReady = false;

        if (!AnyWay()) return;

        var sources = new List<NavMeshBuildSource>();
        var markups = new List<NavMeshBuildMarkup>();
        int wayMask = LayerMask.GetMask("Way");

        // まず RenderMeshes で収集（道プレハブに MeshRenderer がある場合）
        NavMeshBuilder.CollectSources(
            container.transform,
            wayMask,
            NavMeshCollectGeometry.RenderMeshes,
            0, markups, sources
        );

        // メッシュが無ければ Collider で再収集（BoxCollider だけの道でもOKにする）
        if (sources.Count == 0)
        {
            NavMeshBuilder.CollectSources(
                container.transform,
                wayMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0, markups, sources
            );
        }

        if (sources.Count == 0) return;

        var settings = NavMesh.GetSettingsByID(agentTypeId);

        // チャンク範囲 + 1セルの余白（継ぎ目サンプリング安定）
        float w = chunkTiles * cellSize;
        float d = chunkTiles * cellSize;
        var center = new Vector3(OriginX + w * 0.5f, 0f, OriginZ + d * 0.5f);
        var bounds = new Bounds(center, new Vector3(w + cellSize, 1000f, d + cellSize));

        if (navInstance.valid) NavMesh.RemoveNavMeshData(navInstance);
        navData = new NavMeshData(settings.agentTypeID);
        navInstance = NavMesh.AddNavMeshData(navData);

        navOp = NavMeshBuilder.UpdateNavMeshDataAsync(navData, settings, sources, bounds);
        if (navOp != null) navOp.completed += _ => { navReady = true; };
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
