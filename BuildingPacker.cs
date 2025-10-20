using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BuildingPacker
{
    // ====== 外部から渡す設定 ======
    // プレハブ群（サイズ別／複数対応）
    public GameObject[] prefabs50x50;
    public GameObject[] prefabs60x60;
    public GameObject[] prefabs40x60;
    // 後方互換（単品）
    public GameObject prefab10, prefab20, prefab30;

    // ランダム化＆リミット
    public float positionShuffleRate = 1f;
    public float epsilon = 0.05f;
    public int multiStart = 2;
    public int randSeedOffset = 12345;
    public bool deterministicPerLot = true;
    public int maxCellsToScanPerLot = 200000;
    public int maxBuildingsPerLot = 1000;

    // 幾何・挙動（外部から渡される） --------------------------------------
    public struct Context
    {
        public float EffCell;          // 1セル[m]
        public float OriginX, OriginZ; // チャンク原点（m）
        public int tilesPerFrame;      // フレーム分割
        public bool noYield;           // フリーズ回避を無効にするか
        public float lotEdgeMargin;    // ロット端マージン（m）
        public System.Func<float, float, float> SampleSupportTopY;           // (x,z)->Y
        public System.Action<GameObject, float, float, float> FitToSize;      // go, x, y, z
    }

    // CityChunkBSP の Lot を参照できるように public にしておく
    public IEnumerator PlaceOnLot(CityChunkBSP.Lot lot, Transform chunkRoot, Context ctx, bool staticCombinePerLot)
    {
        if (chunkRoot == null) yield break;

        // 1) ロット内セル領域（マージン控除）
        int marginCells = Mathf.CeilToInt(Mathf.Max(0f, ctx.lotEdgeMargin) / ctx.EffCell);
        int innerMinX = lot.minX + marginCells;
        int innerMaxX = lot.maxX - marginCells;
        int innerMinZ = lot.minZ + marginCells;
        int innerMaxZ = lot.maxZ - marginCells;

        if (innerMinX > innerMaxX || innerMinZ > innerMaxZ) yield break;

        int innerW = innerMaxX - innerMinX + 1;
        int innerH = innerMaxZ - innerMinZ + 1;
        if (innerW <= 0 || innerH <= 0) yield break;

        // 2) 候補サイズ（meters）+ プレハブ配列
        var options = new List<SizeOption>();
        void AddOpt(int wM, int dM, GameObject[] set)
        {
            if (set == null || set.Length == 0) return;
            int sx = Mathf.Max(1, Mathf.CeilToInt(wM / ctx.EffCell));
            int sz = Mathf.Max(1, Mathf.CeilToInt(dM / ctx.EffCell));
            options.Add(new SizeOption { wM = wM, dM = dM, sx = sx, sz = sz, prefabs = set });
        }
        AddOpt(60, 60, prefabs60x60);
        AddOpt(50, 50, prefabs50x50);
        AddOpt(40, 60, prefabs40x60); // 回転で60x40も可

        if (prefab30) AddOpt(30, 30, new[] { prefab30 });
        if (prefab20) AddOpt(20, 20, new[] { prefab20 });
        if (prefab10) AddOpt(10, 10, new[] { prefab10 });

        if (options.Count == 0) yield break;
        options.Sort((a, b) => b.AreaM2.CompareTo(a.AreaM2)); // 面積降順

        // 3) 乱数（ロット単位で再現性）
        int lotHash;
        unchecked
        {
            lotHash = innerMinX * 73856093 ^ innerMinZ * 19349663 ^ innerW * 83492791 ^ innerH * 297121507;
        }
        int seed = deterministicPerLot ? unchecked(randSeedOffset ^ lotHash) : UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        var rng = new System.Random(seed);

        // 4) マルチスタートで最良解
        int trials = Mathf.Max(1, multiStart);
        List<PackPlaced> best = null;
        int bestOcc = -1;

        for (int t = 0; t < trials; t++)
        {
            var placed = TryPackOnce(innerW, innerH, options, rng, out int occupiedCells, positionShuffleRate, epsilon, maxCellsToScanPerLot);
            if (occupiedCells > bestOcc) { bestOcc = occupiedCells; best = placed; }
        }
        if (best == null || best.Count == 0) yield break;

        // 5) 実体化
        int localBudget = ctx.noYield ? int.MaxValue : Mathf.Max(1, ctx.tilesPerFrame);
        int count = 0;

        var lotGroup = new GameObject("BuildingsLot");
        lotGroup.transform.SetParent(chunkRoot, false);
        lotGroup.transform.position = new Vector3(ctx.OriginX, 0f, ctx.OriginZ);

        foreach (var p in best)
        {
            float minX = ctx.OriginX + (innerMinX + p.gx) * ctx.EffCell;
            float minZ = ctx.OriginZ + (innerMinZ + p.gz) * ctx.EffCell;

            float widthW = p.sx * ctx.EffCell;
            float depthW = p.sz * ctx.EffCell;
            float cx = minX + widthW * 0.5f;
            float cz = minZ + depthW * 0.5f;
            float gy = ctx.SampleSupportTopY(cx, cz);

            var set = p.option.prefabs;
            GameObject prefab = (set != null && set.Length > 0) ? set[rng.Next(set.Length)] : null;
            if (prefab)
            {
                var go = Object.Instantiate(prefab, new Vector3(cx, gy, cz), Quaternion.identity, lotGroup.transform);
                if (go)
                {
                    var renderers = go.GetComponentsInChildren<Renderer>(true);
                    if (renderers != null && renderers.Length > 0)
                    {
                        Bounds b = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
                        float bottomOffset = b.min.y - go.transform.position.y;
                        go.transform.position = new Vector3(go.transform.position.x, go.transform.position.y - bottomOffset, go.transform.position.z);
                    }

                    float fitY = (go.GetComponentInChildren<Renderer>() != null)
                        ? go.GetComponentInChildren<Renderer>().bounds.size.y
                        : go.transform.localScale.y;
                    ctx.FitToSize(go, widthW, fitY, depthW);

                    go.isStatic = staticCombinePerLot;
                }

                count++;
                if (count >= maxBuildingsPerLot) break;

                if (--localBudget <= 0)
                {
                    localBudget = ctx.tilesPerFrame;
                    yield return null;
                }
            }
        }

        if (staticCombinePerLot) TryStaticCombine(lotGroup);
    }

    // ===== 内部実装 =====

    struct SizeOption
    {
        public int wM, dM;           // m
        public int sx, sz;           // cells
        public GameObject[] prefabs;
        public int AreaM2 => wM * dM;
    }

    struct PackPlaced
    {
        public int gx, gz;  // 左上セル
        public int sx, sz;  // 占有セル
        public SizeOption option;
    }

    static List<PackPlaced> TryPackOnce(
        int W, int H, List<SizeOption> baseOrder, System.Random rng,
        out int occupied, float positionShuffleRate, float epsilon, int maxScan)
    {
        occupied = 0;
        var result = new List<PackPlaced>(128);
        if (W <= 0 || H <= 0) return result;

        bool[,] occ = new bool[W, H];

        var coords = new List<(int x, int z)>(W * H);
        for (int z = 0; z < H; z++) for (int x = 0; x < W; x++) coords.Add((x, z));
        if (positionShuffleRate > 0f)
        {
            int n = coords.Count;
            int shuffleTo = (int)(n * Mathf.Clamp01(positionShuffleRate));
            for (int i = 0; i < shuffleTo; i++) { int j = rng.Next(i, n); (coords[i], coords[j]) = (coords[j], coords[i]); }
        }

        int scanned = 0;
        foreach (var c in coords)
        {
            if (scanned++ > maxScan) break;
            if (occ[c.x, c.z]) continue;

            var order = baseOrder;
            if (epsilon > 0f && rng.NextDouble() < epsilon)
            {
                order = new List<SizeOption>(baseOrder);
                int i = rng.Next(order.Count), j = rng.Next(order.Count);
                (order[i], order[j]) = (order[j], order[i]);
            }

            bool placedHere = false;
            foreach (var opt in order)
            {
                var variants = (opt.sx == opt.sz)
                    ? new (int sx, int sz)[] { (opt.sx, opt.sz) }
                    : new (int sx, int sz)[] { (opt.sx, opt.sz), (opt.sz, opt.sx) };

                for (int v = 0; v < variants.Length; v++)
                {
                    int sx = variants[v].sx, sz = variants[v].sz;
                    if (c.x + sx > W || c.z + sz > H) continue;
                    if (!CellsFree(occ, c.x, c.z, sx, sz)) continue;

                    Mark(occ, c.x, c.z, sx, sz);
                    occupied += sx * sz;
                    result.Add(new PackPlaced { gx = c.x, gz = c.z, sx = sx, sz = sz, option = opt });
                    placedHere = true;
                    break;
                }
                if (placedHere) break;
            }
        }
        return result;

        static bool CellsFree(bool[,] map, int x0, int z0, int sx, int sz)
        { for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) if (map[x0 + x, z0 + z]) return false; return true; }

        static void Mark(bool[,] map, int x0, int z0, int sx, int sz)
        { for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) map[x0 + x, z0 + z] = true; }
    }

    static void TryStaticCombine(GameObject groupRoot)
    {
        if (!groupRoot) return;
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
}
