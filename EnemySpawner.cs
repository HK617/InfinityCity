using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class EnemySpawner : MonoBehaviour
{
    [Header("Refs")]
    public Transform player;
    public GameObject[] enemyPrefabs;

    [Header("Spawn Timing")]
    public float spawnInterval = 2.0f;

    [Header("Distances")]
    public float minSpawnDistance = 8f;
    public float maxSpawnDistance = 25f;

    [Header("Tries")]
    public int triesPerRing = 16;

    void Start()
    {
        if (player == null)
        {
            var tagged = GameObject.FindGameObjectWithTag("Player");
            player = tagged ? tagged.transform : FindObjectOfType<Transform>();
        }
        StartCoroutine(SpawnLoop());
    }

    IEnumerator SpawnLoop()
    {
        var wait = new WaitForSeconds(spawnInterval);

        while (true)
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                // NavMesh 未生成フレームはスキップ
                yield return wait;
                continue;
            }

            Vector3 spawnPos;
            bool ok = TryFindSpawnOnNavMesh(
                player ? player.position : Vector3.zero,
                minSpawnDistance, maxSpawnDistance,
                triesPerRing, out spawnPos
            );

            if (!ok) ok = TryPickTriangleCentroid(tri, out spawnPos);

            if (ok) SpawnEnemyAt(spawnPos);
            yield return wait;
        }
    }

    bool TryFindSpawnOnNavMesh(Vector3 center, float minR, float maxR, int tries, out Vector3 result)
    {
        float r = Mathf.Max(1f, minR);

        for (int step = 0; step < 3; step++)
        {
            for (int i = 0; i < tries; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                float d = Random.Range(r, maxR);
                Vector3 guess = new Vector3(center.x + dir.x * d, center.y + 5f, center.z + dir.y * d);

                // ★ NavMesh上の厳密座標を取得
                if (NavMesh.SamplePosition(guess, out var hit, Mathf.Max(2f, r), NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            r = Mathf.Min(maxR, r * 1.8f);
        }
        result = Vector3.zero;
        return false;
    }

    bool TryPickTriangleCentroid(NavMeshTriangulation tri, out Vector3 pos)
    {
        if (tri.indices == null || tri.indices.Length < 3) { pos = Vector3.zero; return false; }
        int triCount = tri.indices.Length / 3;
        int t = Random.Range(0, triCount) * 3;

        Vector3 a = tri.vertices[tri.indices[t]];
        Vector3 b = tri.vertices[tri.indices[t + 1]];
        Vector3 c = tri.vertices[tri.indices[t + 2]];

        // 念のためもう一度サンプルしてNavMesh上にスナップ
        Vector3 centroid = (a + b + c) / 3f;
        if (NavMesh.SamplePosition(centroid, out var hit, 5f, NavMesh.AllAreas))
        {
            pos = hit.position;
            return true;
        }
        pos = Vector3.zero;
        return false;
    }

    void SpawnEnemyAt(Vector3 position)
    {
        if (enemyPrefabs == null || enemyPrefabs.Length == 0) return;
        var prefab = enemyPrefabs[Random.Range(0, enemyPrefabs.Length)];
        if (!prefab) return;

        var go = Instantiate(prefab, position, Quaternion.identity);

        // ★ NavMeshAgent を確実に NavMesh 上にワープ（Prefab に付いている場合）
        var agent = go.GetComponent<NavMeshAgent>();
        if (agent)
        {
            if (!agent.Warp(position))
            {
                if (NavMesh.SamplePosition(position, out var hit, 5f, NavMesh.AllAreas))
                {
                    agent.Warp(hit.position);
                }
            }
        }
    }
}
