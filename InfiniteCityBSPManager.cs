using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InfiniteCityBSPManager : MonoBehaviour
{
    [Header("Player / Parents / Prefabs")]
    public Transform player;
    public Transform chunkParent;
    public GameObject wayPrefab;
    public GameObject blockPrefab;

    [Header("World / Chunk")]
    [Min(1f)] public float cellSize = 10f;     // 1タイルの一辺（= 道の最小幅）
    [Min(8)] public int chunkTiles = 100;   // 1チャンク = chunkTiles × chunkTiles タイル
    [Min(1)] public int activeRange = 3;    // プレイヤー周囲 ±range のチャンクを保持（3→7×7=49）

    [Header("BSP Settings")]
    [Min(1)] public int maxDepth = 7;
    [Range(0f, 1f)] public float roadDensity = 0.85f;
    [Min(1)] public int minBlockTilesX = 6;
    [Min(1)] public int minBlockTilesZ = 6;
    [Range(0f, 0.45f)] public float splitJitter = 0.10f;
    [Min(1)] public int roadWidthTiles = 1; // 道幅（タイル数）— 最小1

    [Header("Global Arterial Grid (seam continuity)")]
    [Min(8)] public int arterialPeriodTiles = 20; // 何タイル毎に世界共通の“動脈”を走らせるか
    [Min(1)] public int arterialWidthTiles = 1;  // 動脈の幅（タイル数）

    [Header("Visuals")]
    [Range(0.05f, 1f)] public float wayTileFillXZ = 0.35f; // 道の見た目幅（細く）
    [Range(0.05f, 1f)] public float wayTileFillY = 0.10f; // 道の厚み
    [Range(0.05f, 1f)] public float blockFillXZ = 0.95f; // 建物の横幅
    [Range(0.05f, 4f)] public float blockBaseH = 1.00f; // 建物の基準高さ（タイル高倍率）
    [Range(0f, 8f)] public float blockRandH = 3.00f; // 建物の追加ランダム高さ

    [Header("Terrain Raycast (optional)")]
    public LayerMask groundMask;
    public float skyRayStart = 500f;
    public float rayExtraDown = 500f;

    [Header("Generation")]
    public int seed = 12345;
    [Min(64)] public int tilesPerFrame = 500; // 生成コルーチンの1フレーム上限

    // チャンク管理
    readonly Dictionary<Vector2Int, CityChunkBSP> live = new();
    readonly List<Vector2Int> tmpNeeded = new();

    void Update()
    {
        if (!player || !wayPrefab || !blockPrefab) return;

        Vector2Int pc = WorldToChunk(player.position);
        tmpNeeded.Clear();
        for (int dz = -activeRange; dz <= activeRange; dz++)
            for (int dx = -activeRange; dx <= activeRange; dx++)
                tmpNeeded.Add(new Vector2Int(pc.x + dx, pc.y + dz));

        // アンロード
        var keys = ListFromDictKeys(live);
        foreach (var k in keys)
            if (!tmpNeeded.Contains(k)) { live[k].Dispose(); live.Remove(k); }

        // ロード
        foreach (var k in tmpNeeded)
            if (!live.ContainsKey(k))
            {
                var chunk = new CityChunkBSP(
                    seed, k, chunkTiles, cellSize,
                    wayPrefab, blockPrefab, chunkParent,
                    BuildBspSettings(), BuildVisuals(), BuildTerrain(), tilesPerFrame,
                    arterialPeriodTiles, arterialWidthTiles
                );
                live.Add(k, chunk);
                StartCoroutine(chunk.GenerateAsync()); // 非同期生成
            }
    }

    Vector2Int WorldToChunk(Vector3 pos)
    {
        float size = chunkTiles * cellSize;
        int cx = Mathf.FloorToInt(pos.x / size);
        int cz = Mathf.FloorToInt(pos.z / size);
        return new Vector2Int(cx, cz);
    }

    List<Vector2Int> ListFromDictKeys(Dictionary<Vector2Int, CityChunkBSP> d)
    {
        var list = new List<Vector2Int>(d.Count);
        foreach (var k in d.Keys) list.Add(k);
        return list;
    }

    // 設定をまとめて渡す（struct的に）
    public CityChunkBSP.BspSettings BuildBspSettings() => new()
    {
        maxDepth = maxDepth,
        roadDensity = roadDensity,
        minBlockTilesX = minBlockTilesX,
        minBlockTilesZ = minBlockTilesZ,
        splitJitter = splitJitter,
        roadWidthTiles = roadWidthTiles
    };
    public CityChunkBSP.VisualSettings BuildVisuals() => new()
    {
        wayTileFillXZ = wayTileFillXZ,
        wayTileFillY = wayTileFillY,
        blockFillXZ = blockFillXZ,
        blockBaseH = blockBaseH,
        blockRandH = blockRandH
    };
    public CityChunkBSP.TerrainSettings BuildTerrain() => new()
    {
        groundMask = groundMask,
        skyRayStart = skyRayStart,
        rayExtraDown = rayExtraDown
    };
}
