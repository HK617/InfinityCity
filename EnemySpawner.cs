// Assets/scripts/EnemySpawner.cs
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class EnemySpawner : MonoBehaviour
{
    [Header("Spawn")]
    public GameObject enemyPrefab;      // 敵プレハブ
    public Transform player;            // 追尾対象（任意だが設定推奨）
    public float spawn_interval = 3f;   // 何秒ごとに
    public int spawn_number = 3;     // 一度に何体
    public float minSpawnDistance = 3f; // プレイヤーに近すぎたら湧かさない距離

    [Header("Area (XZ world)")]
    public float minX = -200f, maxX = 200f;
    public float minZ = -200f, maxZ = 200f;

    [Header("NavMesh sampling")]
    public float sampleMaxDistance = 50f; // ランダム点からNavMeshに吸着する許容半径
    public int sampleTries = 30;  // 範囲内での試行回数

    [Header("Debug")]
    public bool debugLogs = true;

    Coroutine loop;

    void OnEnable()
    {
        if (loop == null) loop = StartCoroutine(SpawnLoop());
    }

    void OnDisable()
    {
        if (loop != null) StopCoroutine(loop);
        loop = null;
    }

    IEnumerator SpawnLoop()
    {
        if (enemyPrefab == null)
        {
            if (debugLogs) Debug.LogError("EnemySpawner: enemyPrefab 未設定");
            yield break;
        }

        var wait = new WaitForSeconds(spawn_interval);

        while (true)
        {
            for (int i = 0; i < spawn_number; i++)
            {
                if (!TryGetNavmeshSpawn(out var pos))
                {
                    if (debugLogs) Debug.LogWarning("EnemySpawner: NavMesh 上のスポーン位置取得に失敗");
                    continue;
                }

                // プレイヤーに近すぎる場合は一度だけ取り直す
                if (player && Vector3.Distance(pos, player.position) < minSpawnDistance)
                {
                    if (!TryGetNavmeshSpawn(out pos)) continue;
                    if (player && Vector3.Distance(pos, player.position) < minSpawnDistance) continue;
                }

                var go = Instantiate(enemyPrefab, pos, Quaternion.identity);
                if (debugLogs) Debug.Log($"EnemySpawner: Spawned at {pos}");

                // 索敵・視線判定付きのAIにターゲットを渡す（付けていれば）
                var ai = go.GetComponent<EnemySenseChaseAgent>();
                if (ai && player) ai.target = player;
            }

            yield return wait;
        }
    }

    // --- NavMesh 上の地点を取得（範囲内を試行 → 失敗時はNavMesh全体から1点を選ぶ）---
    bool TryGetNavmeshSpawn(out Vector3 result)
    {
        // 1) 指定範囲内で NavMesh に吸着
        for (int t = 0; t < sampleTries; t++)
        {
            var rnd = new Vector3(Random.Range(minX, maxX), 100f, Random.Range(minZ, maxZ));
            if (NavMesh.SamplePosition(rnd, out var hit, sampleMaxDistance, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }
        }

        // 2) フォールバック：NavMesh 全体の三角形から1点をランダムで取得
        var tri = NavMesh.CalculateTriangulation();
        if (tri.vertices != null && tri.vertices.Length >= 3 &&
            tri.indices != null && tri.indices.Length >= 3)
        {
            int idx = Random.Range(0, tri.indices.Length / 3) * 3;
            Vector3 a = tri.vertices[tri.indices[idx]];
            Vector3 b = tri.vertices[tri.indices[idx + 1]];
            Vector3 c = tri.vertices[tri.indices[idx + 2]];

            // 三角形内部の一様乱数点（barycentric）
            float r1 = Random.value;
            float r2 = Random.value;
            float s = Mathf.Sqrt(r1);
            Vector3 p = (1 - s) * a + (s * (1 - r2)) * b + (s * r2) * c;

            // 念のため近傍で吸着
            if (NavMesh.SamplePosition(p + Vector3.up * 10f, out var hit2, 20f, NavMesh.AllAreas))
            {
                result = hit2.position;
                return true;
            }
        }

        result = default;
        return false;
    }

    // シーン上で範囲を可視化
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        var c = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        var s = new Vector3(Mathf.Abs(maxX - minX), 0.1f, Mathf.Abs(maxZ - minZ));
        Gizmos.DrawWireCube(c, s);
    }
}
