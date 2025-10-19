using System.Collections.Generic;
using UnityEngine;

public class InfiniteCityBSPManager : MonoBehaviour
{
    [Header("Player / Parents / Prefabs")]
    public Transform player;
    public Transform chunkParent;
    public GameObject wayPrefab;     // Layer=Way（MeshRenderer か Collider を持たせる）
    public GameObject blockPrefab;   // Layer=Block（ベース台座）

    [Header("Buildings")]
    public GameObject[] buildingPrefabs;    // 建物3種など
    public bool placeLotBaseBlock = true;

    // 敷き詰め系
    public bool packBuildingsInGrid = true;
    public float buildingGridPadding = 0.5f;
    public float lotEdgeMargin = 0.5f;
    public int maxBuildingsPerLot = 200;
    public bool staticCombinePerLot = true;

    [Header("World / Chunk")]
    [Min(1f)] public float cellSize = 10f;
    [Min(8)] public int chunkTiles = 48;
    [Min(1)] public int activeRange = 2;
    public int seed = 12345;
    [Min(64)] public int tilesPerFrame = 1500;

    [Header("Road Generation (Global+Random BSP)")]
    public int globalArterialPeriod = 8;
    [Range(1, 3)] public int globalArterialWidth = 1;
    public int minPartitionSize = 0;
    [Range(0f, 1f)] public float extraCrossChance = 0.35f;
    [Range(0, 3)] public int extraCrossWidth = 1;

    [Header("Lots")]
    public int minLotAreaCells = 3;
    public bool mergeDiagonals = false;

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 0.90f;
    [Range(0.05f, 3f)] public float blockFillY = 1.20f;

    [Header("Ground (0=未使用)")]
    public LayerMask groundMask = 0;

    [Header("NavMesh")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;
    [Tooltip("道だけでなく区画ブロックも NavMesh に含める")]
    public bool includeBlocksInNavMesh = true;

    [Header("Performance / Scale")]
    [Range(0.01f, 1f)] public float globalScale = 1f;

    readonly Dictionary<Vector2Int, CityChunkBSP> live = new();
    readonly Dictionary<Vector2Int, Coroutine> running = new();
    readonly List<Vector2Int> tmpNeeded = new();

    void Update()
    {
        if (!player || !wayPrefab) return;

        Vector2Int pc = WorldToChunk(player.position);

        tmpNeeded.Clear();
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
                tmpNeeded.Add(new Vector2Int(pc.x + dx, pc.y + dz));

        foreach (var key in tmpNeeded)
            if (!live.ContainsKey(key)) CreateChunk(key);

        var toRemove = new List<Vector2Int>();
        foreach (var kv in live)
            if (!tmpNeeded.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var key in toRemove) DestroyChunk(key);
    }

    void CreateChunk(Vector2Int index)
    {
        // チャンク座標ごとに確実に異なる乱数系列を作成
        int chunkSeed = unchecked(seed + (index.x * 92821) + (index.y * 68917) + (index.x * index.y * 19349663));
        chunkSeed ^= (int)(Time.realtimeSinceStartup * 1000f) & 0xFFFF;

        var chunk = new CityChunkBSP(
            chunkSeed, index, chunkTiles, cellSize,
            wayPrefab, blockPrefab,
            chunkParent ? chunkParent : transform,
            tilesPerFrame
        );

        // 転送
        chunk.buildingPrefabs = buildingPrefabs;
        chunk.placeLotBaseBlock = placeLotBaseBlock;

        chunk.packBuildingsInGrid = packBuildingsInGrid;
        chunk.buildingGridPadding = buildingGridPadding;
        chunk.lotEdgeMargin = lotEdgeMargin;
        chunk.maxBuildingsPerLot = maxBuildingsPerLot;
        chunk.staticCombinePerLot = staticCombinePerLot;

        chunk.globalArterialPeriod = globalArterialPeriod;
        chunk.globalArterialWidth = globalArterialWidth;
        chunk.minPartitionSize = minPartitionSize;
        chunk.extraCrossChance = extraCrossChance;
        chunk.extraCrossWidth = extraCrossWidth;

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
        var co = StartCoroutine(chunk.GenerateAsync());
        running[index] = co;
    }

    void DestroyChunk(Vector2Int index)
    {
        if (running.TryGetValue(index, out var co) && co != null)
        {
            StopCoroutine(co);
            running.Remove(index);
        }
        if (live.TryGetValue(index, out var chunk))
        {
            chunk.Dispose();
            live.Remove(index);
        }
    }

    Vector2Int WorldToChunk(Vector3 worldPos)
    {
        float size = (chunkTiles * cellSize) * Mathf.Max(0.01f, globalScale);
        int cx = Mathf.FloorToInt(worldPos.x / size);
        int cz = Mathf.FloorToInt(worldPos.z / size);
        return new Vector2Int(cx, cz);
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = new Color(0, 0.5f, 1f, 0.15f);

        float size = (chunkTiles * cellSize) * Mathf.Max(0.01f, globalScale);
        Vector2Int pc = WorldToChunk(player ? player.position : Vector3.zero);
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
            {
                var origin = new Vector3((pc.x + dx) * size, 0f, (pc.y + dz) * size);
                var sz = new Vector3(size, 0f, size);
                Gizmos.DrawWireCube(origin + new Vector3(sz.x * 0.5f, 0f, sz.z * 0.5f), sz);
            }
    }
}
