using System.Collections.Generic;
using UnityEngine;

public class InfiniteCityBSPManager : MonoBehaviour
{
    [Header("Player / Parents / Prefabs")]
    public Transform player;
    public Transform chunkParent;
    public GameObject wayPrefab;     // Layer=Way
    public GameObject blockPrefab;   // Layer=Block

    // ✅ 要望：legacy の単品/配列は廃止。使うのは 50/60/40x60 の3枠のみ
    [Header("Prefab Sets (meters)")]
    public GameObject[] prefabs50x50;   // 50×50
    public GameObject[] prefabs60x60;   // 60×60
    public GameObject[] prefabs40x60;   // 40×60（回転で60×40対応）

    [Header("Packing Options")]
    public bool packBuildingsInGrid = true;  // 建物をセルグリッドで詰める
    public float buildingGridPadding = 0f;   // 建物同士の最小クリアランス（m）
    public float lotEdgeMargin = 0f;         // ロット外周のマージン（m）
    public int maxBuildingsPerLot = 100;   // 1ロットの最大配置数
    public bool staticCombinePerLot = false;// 1ロットごとに静的結合

    [Header("World / Chunk")]
    [Min(1f)] public float cellSize = 10f;
    [Min(4)] public int chunkTiles = 10;
    [Min(1)] public int activeRange = 3;
    public int seed = 12345;
    [Min(64)] public int tilesPerFrame = 2000;

    [Header("Road Generation (Global + BSP)")]
    public int globalArterialPeriod = 20;
    [Range(1, 5)] public int globalArterialWidth = 1; // ✅ 大通りの幅（セル）
    public int minPartitionSize = 12;
    [Range(0f, 1f)] public float extraCrossChance = 0.10f;
    [Range(1, 5)] public int extraCrossWidth = 1;

    [Header("Road Widths (Local)")]
    [Range(1, 5)] public int bspRoadWidth = 1;       // ✅ BSPモードの細街路の幅（セル）
    [Range(1, 5)] public int walkerPathWidth = 1;    // ✅ RandomWalkersモードの通路幅（セル）

    [Header("Lots")]
    public int minLotAreaCells = 25;
    public bool mergeDiagonals = true;

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 1.0f;
    [Range(0.05f, 3f)] public float blockFillY = 0.05f;

    [Header("Ground (0=unused)")]
    public LayerMask groundMask = 0;

    [Header("NavMesh")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;
    public bool includeBlocksInNavMesh = true;

    [Header("Performance / Scale")]
    [Range(0.01f, 1f)] public float globalScale = 1f;

    // --- 内部 ---
    readonly Dictionary<Vector2Int, CityChunkBSP> live = new();
    readonly Dictionary<Vector2Int, Coroutine> running = new();
    readonly List<Vector2Int> tmpNeeded = new();

    void Update()
    {
        if (!player || !wayPrefab) return;
        var pc = WorldToChunk(player.position);

        tmpNeeded.Clear();
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
                tmpNeeded.Add(new Vector2Int(pc.x + dx, pc.y + dz));

        foreach (var key in tmpNeeded)
            if (!live.ContainsKey(key)) CreateChunk(key);

        var toRemove = new List<Vector2Int>();
        foreach (var kv in live)
            if (!tmpNeeded.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var k in toRemove) DestroyChunk(k);
    }

    void CreateChunk(Vector2Int index)
    {
        int chunkSeed = unchecked(seed + (index.x * 92821) + (index.y * 68917) + (index.x * index.y * 19349663));

        var chunk = new CityChunkBSP(
            chunkSeed, index, chunkTiles, cellSize,
            wayPrefab, blockPrefab,
            chunkParent ? chunkParent : transform,
            tilesPerFrame
        );

        // --- 建物配列（3枠）だけ渡す ---
        chunk.prefabs50x50 = prefabs50x50;
        chunk.prefabs60x60 = prefabs60x60;
        chunk.prefabs40x60 = prefabs40x60;

        // --- パッキング ---
        chunk.packBuildingsInGrid = packBuildingsInGrid;
        chunk.buildingGridPadding = buildingGridPadding;
        chunk.lotEdgeMargin = lotEdgeMargin;
        chunk.maxBuildingsPerLot = maxBuildingsPerLot;
        chunk.staticCombinePerLot = staticCombinePerLot;

        // --- 道路生成 ---
        chunk.globalArterialPeriod = globalArterialPeriod;
        chunk.globalArterialWidth = globalArterialWidth;
        chunk.minPartitionSize = minPartitionSize;
        chunk.extraCrossChance = extraCrossChance;
        chunk.extraCrossWidth = extraCrossWidth;

        // ✅ 新規：道幅2種を転送
        chunk.bspRoadWidth = bspRoadWidth;
        chunk.walkerPathWidth = walkerPathWidth;

        // --- ロット/見た目/地面/NavMesh/スケール ---
        chunk.minLotAreaCells = minLotAreaCells;
        chunk.mergeDiagonals = mergeDiagonals;
        chunk.wayTileFillXZ = wayTileFillXZ;
        chunk.wayTileFillY = wayTileFillY;
        chunk.blockFillXZ = blockFillXZ;
        chunk.blockFillY = blockFillY;
        chunk.groundMask = groundMask;
        chunk.bakeNavMeshPerChunk = bakeNavMeshPerChunk;
        chunk.agentTypeId = agentTypeId;
        chunk.includeBlocksInNavMesh = includeBlocksInNavMesh;
        chunk.globalScale = globalScale;

        live[index] = chunk;
        running[index] = StartCoroutine(chunk.GenerateAsync());
    }

    void DestroyChunk(Vector2Int index)
    {
        if (running.TryGetValue(index, out var co) && co != null) { StopCoroutine(co); running.Remove(index); }
        if (live.TryGetValue(index, out var c)) { c.Dispose(); live.Remove(index); }
    }

    Vector2Int WorldToChunk(Vector3 wp)
    {
        float size = (chunkTiles * cellSize) * Mathf.Max(0.01f, globalScale);
        return new Vector2Int(Mathf.FloorToInt(wp.x / size), Mathf.FloorToInt(wp.z / size));
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = new Color(0, 0.5f, 1f, 0.15f);
        float size = (chunkTiles * cellSize) * Mathf.Max(0.01f, globalScale);
        var pc = WorldToChunk(player ? player.position : Vector3.zero);
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
            {
                var o = new Vector3((pc.x + dx) * size, 0f, (pc.y + dz) * size);
                Gizmos.DrawWireCube(o + new Vector3(size * 0.5f, 0, size * 0.5f), new Vector3(size, 0, size));
            }
    }
}
