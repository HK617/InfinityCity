using System.Collections.Generic;
using UnityEngine;

public class InfiniteCityBSPManager : MonoBehaviour
{
    [Header("Player / Parents / Prefabs")]
    public Transform player;
    public Transform chunkParent;
    public GameObject wayPrefab;   // Layer=Way 推奨
    public GameObject blockPrefab;

    [Header("Buildings")]
    public GameObject[] buildingPrefabs;          // Building1/2/3 をここへ
    public bool placeLotBaseBlock = true;  // 区画ベースブロックを置くか

    // ★ CityChunkBSP の敷き詰め用フィールドに対応（buildingsPerLot は廃止）
    public bool packBuildingsInGrid = true;      // グリッドで整列配置
    public float buildingGridPadding = 0.5f;      // 建物同士の隙間(ワールド)
    public float lotEdgeMargin = 0.5f;      // 道へはみ出さない外周余白
    public int maxBuildingsPerLot = 200;       // 1区画あたりの上限
    public bool staticCombinePerLot = true;      // 合体して軽量化

    [Header("World / Chunk")]
    [Min(1f)] public float cellSize = 10f;   // 1セルの実サイズ
    [Min(8)] public int chunkTiles = 48;    // 1チャンク=chunkTiles×chunkTiles
    [Min(1)] public int activeRange = 2;    // ±range チャンクを保持
    public int seed = 12345;
    [Min(64)] public int tilesPerFrame = 1500;

    [Header("Road Generation (Global+Random BSP)")]
    public int globalArterialPeriod = 8;
    [Range(1, 3)] public int globalArterialWidth = 1;
    [Tooltip("BSP分割の最小サイズ。小さいほど道が増える (0=自動)")]
    public int minPartitionSize = 0;
    [Range(0f, 1f)] public float extraCrossChance = 0.35f;
    [Range(0, 3)] public int extraCrossWidth = 1;

    [Header("Lots (空き地区画)")]
    public int minLotAreaCells = 3;   // 小さすぎる区画を除外
    public bool mergeDiagonals = false;

    [Header("Visual Fill")]
    [Range(0.1f, 1f)] public float wayTileFillXZ = 1.0f;
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f;
    [Range(0.1f, 1f)] public float blockFillXZ = 0.90f;
    [Range(0.05f, 3f)] public float blockFillY = 1.20f;

    [Header("Ground Sampling (0=地形なしで高速)")]
    public LayerMask groundMask = 0;

    [Header("NavMesh")]
    public bool bakeNavMeshPerChunk = true;
    public int agentTypeId = 0;

    // チャンク管理
    readonly Dictionary<Vector2Int, CityChunkBSP> live = new();
    readonly List<Vector2Int> tmpNeeded = new();

    void Update()
    {
        if (!player || !wayPrefab) return;

        Vector2Int pc = WorldToChunk(player.position);

        // 必要チャンク集合
        tmpNeeded.Clear();
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
                tmpNeeded.Add(new Vector2Int(pc.x + dx, pc.y + dz));

        // 生成
        foreach (var key in tmpNeeded)
            if (!live.ContainsKey(key)) CreateChunk(key);

        // 破棄
        var toRemove = new List<Vector2Int>();
        foreach (var kv in live)
            if (!tmpNeeded.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var key in toRemove) DestroyChunk(key);
    }

    void CreateChunk(Vector2Int index)
    {
        int chunkSeed = seed ^ (index.x * 73856093) ^ (index.y * 19349663);

        var chunk = new CityChunkBSP(
            chunkSeed,
            index,
            chunkTiles,
            cellSize,
            wayPrefab,
            blockPrefab,
            chunkParent ? chunkParent : transform,
            tilesPerFrame
        );

        // === CityChunkBSP の公開フィールドに転送 ===
        chunk.buildingPrefabs = buildingPrefabs;
        chunk.placeLotBaseBlock = placeLotBaseBlock;

        // 建物敷き詰め系（※ buildingsPerLot は使いません）
        chunk.packBuildingsInGrid = packBuildingsInGrid;
        chunk.buildingGridPadding = buildingGridPadding;
        chunk.lotEdgeMargin = lotEdgeMargin;
        chunk.maxBuildingsPerLot = maxBuildingsPerLot;
        chunk.staticCombinePerLot = staticCombinePerLot;

        // 道生成パラメータ
        chunk.globalArterialPeriod = globalArterialPeriod;
        chunk.globalArterialWidth = globalArterialWidth;
        chunk.minPartitionSize = minPartitionSize;
        chunk.extraCrossChance = extraCrossChance;
        chunk.extraCrossWidth = extraCrossWidth;

        // ロット・見た目
        chunk.minLotAreaCells = minLotAreaCells;
        chunk.mergeDiagonals = mergeDiagonals;
        chunk.wayTileFillXZ = wayTileFillXZ;
        chunk.wayTileFillY = wayTileFillY;
        chunk.blockFillXZ = blockFillXZ;
        chunk.blockFillY = blockFillY;

        // 地形/ナビ
        chunk.groundMask = groundMask;
        chunk.bakeNavMeshPerChunk = bakeNavMeshPerChunk;
        chunk.agentTypeId = agentTypeId;

        live[index] = chunk;
        StartCoroutine(chunk.GenerateAsync());
    }

    void DestroyChunk(Vector2Int index)
    {
        if (live.TryGetValue(index, out var chunk))
        {
            chunk.Dispose();
            live.Remove(index);
        }
    }

    Vector2Int WorldToChunk(Vector3 worldPos)
    {
        float size = chunkTiles * cellSize;
        int cx = Mathf.FloorToInt(worldPos.x / size);
        int cz = Mathf.FloorToInt(worldPos.z / size);
        return new Vector2Int(cx, cz);
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = new Color(0, 0.5f, 1f, 0.15f);

        Vector2Int pc = WorldToChunk(player ? player.position : Vector3.zero);
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
            {
                var origin = new Vector3(
                    (pc.x + dx) * (chunkTiles * cellSize), 0f,
                    (pc.y + dz) * (chunkTiles * cellSize)
                );
                var size = new Vector3(chunkTiles * cellSize, 0f, chunkTiles * cellSize);
                Gizmos.DrawWireCube(origin + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f), size);
            }
    }
}
