using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CityChunkBSP
{
    // ====== 外部から渡す設定 ======
    public struct BspSettings
    {
        public int maxDepth;
        public float roadDensity;
        public int minBlockTilesX;
        public int minBlockTilesZ;
        public float splitJitter;
        public int roadWidthTiles; // >=1
    }
    public struct VisualSettings
    {
        public float wayTileFillXZ; // 0..1
        public float wayTileFillY;  // 0..1
        public float blockFillXZ;   // 0..1
        public float blockBaseH;    // >=0
        public float blockRandH;    // >=0
    }
    public struct TerrainSettings
    {
        public LayerMask groundMask;
        public float skyRayStart;
        public float rayExtraDown;
    }

    // ====== フィールド ======
    public readonly Vector2Int chunkIndex;
    public readonly GameObject container;

    readonly int seed;
    readonly int chunkTiles;
    readonly float cellSize;
    readonly GameObject wayPrefab, blockPrefab;
    readonly Transform parent;
    readonly BspSettings bsp;
    readonly VisualSettings vis;
    readonly TerrainSettings ter;
    readonly int tilesPerFrame; // コルーチン分割
    readonly int arterialPeriodTiles;
    readonly int arterialWidthTiles;

    enum T { Empty = 0, Way = 1 }
    T[,] grid; // ローカル（0..chunkTiles-1）

    System.Random rng;

    // 生成範囲（世界座標）
    float ChunkWorldSize => chunkTiles * cellSize;
    float OriginX => chunkIndex.x * ChunkWorldSize;
    float OriginZ => chunkIndex.y * ChunkWorldSize;

    public CityChunkBSP(
        int seed, Vector2Int index, int chunkTiles, float cellSize,
        GameObject wayPrefab, GameObject blockPrefab, Transform parent,
        BspSettings bsp, VisualSettings vis, TerrainSettings ter, int tilesPerFrame,
        int arterialPeriodTiles, int arterialWidthTiles
    )
    {
        this.seed = seed;
        this.chunkIndex = index;
        this.chunkTiles = Mathf.Max(8, chunkTiles);
        this.cellSize = Mathf.Max(1f, cellSize);
        this.wayPrefab = wayPrefab;
        this.blockPrefab = blockPrefab;
        this.parent = parent;
        this.bsp = bsp;
        this.vis = vis;
        this.ter = ter;
        this.tilesPerFrame = Mathf.Max(64, tilesPerFrame);
        this.arterialPeriodTiles = Mathf.Max(8, arterialPeriodTiles);
        this.arterialWidthTiles = Mathf.Max(1, arterialWidthTiles);

        container = new GameObject($"Chunk_{index.x}_{index.y}");
        container.transform.SetParent(parent, worldPositionStays: false);
        container.transform.position = new Vector3(OriginX, 0f, OriginZ);
    }

    public IEnumerator GenerateAsync()
    {
        rng = new System.Random(seed ^ (chunkIndex.x * 73856093) ^ (chunkIndex.y * 19349663));
        grid = new T[chunkTiles, chunkTiles];

        // 1) BSPでローカル道路を刻む
        CarveBspRoads(0, 0, chunkTiles, chunkTiles, 0);

        // 2) 世界座標ベースの“動脈グリッド”を重ねて、チャンク境界で継続させる
        StampArterials();

        // 3) 実体配置（分割して負荷分散）
        int budget = tilesPerFrame;
        for (int x = 0; x < chunkTiles; x++)
        {
            for (int z = 0; z < chunkTiles; z++)
            {
                Vector3 wc = LocalToWorldCenter(x, z);
                float gy = SampleGroundY(wc.x, wc.z);

                if (grid[x, z] == T.Way)
                {
                    if (wayPrefab)
                    {
                        float yCenter = gy + (cellSize * vis.wayTileFillY) * 0.5f;
                        var go = Object.Instantiate(wayPrefab, new Vector3(wc.x, yCenter, wc.z), Quaternion.identity, container.transform);
                        Fit(go, cellSize * vis.wayTileFillXZ, cellSize * vis.wayTileFillY, cellSize * vis.wayTileFillXZ);
                    }
                }
                else
                {
                    if (blockPrefab)
                    {
                        float h = Mathf.Max(0.05f, vis.blockBaseH + (float)rng.NextDouble() * vis.blockRandH);
                        float yCenter = gy + (cellSize * h) * 0.5f;
                        var go = Object.Instantiate(blockPrefab, new Vector3(wc.x, yCenter, wc.z), Quaternion.identity, container.transform);
                        Fit(go, cellSize * vis.blockFillXZ, cellSize * h, cellSize * vis.blockFillXZ);
                    }
                }

                // コルーチン分割
                if (--budget <= 0) { budget = tilesPerFrame; yield return null; }
            }
        }
    }

    // ===== BSP =====
    void CarveBspRoads(int x0, int z0, int w, int h, int depth)
    {
        if (depth >= bsp.maxDepth || w < bsp.minBlockTilesX || h < bsp.minBlockTilesZ || rng.NextDouble() > bsp.roadDensity)
            return;

        bool canV = (w >= (bsp.minBlockTilesX * 2 + bsp.roadWidthTiles));
        bool canH = (h >= (bsp.minBlockTilesZ * 2 + bsp.roadWidthTiles));
        if (!canV && !canH) return;

        bool splitVert;
        if (canV && canH) splitVert = (w >= h) ? (rng.NextDouble() > 0.3) : (rng.NextDouble() < 0.3);
        else splitVert = canV;

        if (splitVert)
        {
            int minX = x0 + bsp.minBlockTilesX;
            int maxX = x0 + w - bsp.minBlockTilesX - bsp.roadWidthTiles;
            if (minX >= maxX) return;

            int baseSplit = (minX + maxX) / 2;
            int jitter = Mathf.RoundToInt(w * bsp.splitJitter);
            int splitX = Mathf.Clamp(baseSplit + rng.Next(-jitter, jitter + 1), minX, maxX);

            // 道路帯
            for (int ix = splitX; ix < splitX + bsp.roadWidthTiles; ix++)
                for (int iz = z0; iz < z0 + h; iz++)
                    grid[ix, iz] = T.Way;

            // 再帰
            int wL = splitX - x0;
            int wR = x0 + w - (splitX + bsp.roadWidthTiles);
            CarveBspRoads(x0, z0, wL, h, depth + 1);
            CarveBspRoads(splitX + bsp.roadWidthTiles, z0, wR, h, depth + 1);
        }
        else
        {
            int minZ = z0 + bsp.minBlockTilesZ;
            int maxZ = z0 + h - bsp.minBlockTilesZ - bsp.roadWidthTiles;
            if (minZ >= maxZ) return;

            int baseSplit = (minZ + maxZ) / 2;
            int jitter = Mathf.RoundToInt(h * bsp.splitJitter);
            int splitZ = Mathf.Clamp(baseSplit + rng.Next(-jitter, jitter + 1), minZ, maxZ);

            for (int iz = splitZ; iz < splitZ + bsp.roadWidthTiles; iz++)
                for (int ix = x0; ix < x0 + w; ix++)
                    grid[ix, iz] = T.Way;

            int hU = splitZ - z0;
            int hD = z0 + h - (splitZ + bsp.roadWidthTiles);
            CarveBspRoads(x0, z0, w, hU, depth + 1);
            CarveBspRoads(x0, splitZ + bsp.roadWidthTiles, w, hD, depth + 1);
        }
    }

    // ===== 動脈（世界座標で継続する縦横道路） =====
    void StampArterials()
    {
        // 世界タイル座標の0を基準に、period毎に道路化
        for (int lx = 0; lx < chunkTiles; lx++)
        {
            int worldTileX = chunkIndex.x * chunkTiles + lx;
            bool isArterialX = (worldTileX % arterialPeriodTiles) >= 0 &&
                               (worldTileX % arterialPeriodTiles) < arterialWidthTiles;

            for (int lz = 0; lz < chunkTiles; lz++)
            {
                int worldTileZ = chunkIndex.y * chunkTiles + lz;
                bool isArterialZ = (worldTileZ % arterialPeriodTiles) >= 0 &&
                                   (worldTileZ % arterialPeriodTiles) < arterialWidthTiles;

                if (isArterialX || isArterialZ) grid[lx, lz] = T.Way;
            }
        }
    }

    // ===== 配置ユーティリティ =====
    Vector3 LocalToWorldCenter(int lx, int lz)
    {
        float x = OriginX + (lx + 0.5f) * cellSize;
        float z = OriginZ + (lz + 0.5f) * cellSize;
        return new Vector3(x, 0f, z);
    }

    float SampleGroundY(float x, float z)
    {
        // なくても動くフォールバック（=0）
        Vector3 origin = new Vector3(x, 10000f + ter.skyRayStart, z);
        float len = 20000f + ter.skyRayStart + ter.rayExtraDown;
        int mask = (ter.groundMask.value != 0) ? ter.groundMask.value : Physics.DefaultRaycastLayers;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, len, mask, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return 0f;
    }

    void Fit(GameObject go, float sx, float sy, float sz)
    {
        Vector3 size = Approx(go);
        Vector3 mul = new Vector3(
            sx / Mathf.Max(0.0001f, size.x),
            sy / Mathf.Max(0.0001f, size.y),
            sz / Mathf.Max(0.0001f, size.z)
        );
        go.transform.localScale = Vector3.Scale(go.transform.localScale, mul);
    }

    Vector3 Approx(GameObject go)
    {
        var mr = go.GetComponentInChildren<MeshRenderer>();
        if (mr != null) return mr.bounds.size;
        var col = go.GetComponentInChildren<Collider>();
        if (col != null) return col.bounds.size;
        return Vector3.one * cellSize;
    }

    public void Dispose()
    {
        if (container != null) Object.Destroy(container);
    }
}
