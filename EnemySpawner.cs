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
            var playerObj = GameObject.FindGameObjectWithTag("Player");
            player = playerObj ? playerObj.transform : FindObjectOfType<Transform>();
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
                Debug.LogWarning("EnemySpawner: NavMesh がまだ無いので待機");
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
            else Debug.LogWarning("EnemySpawner: NavMesh 上のスポーン位置取得に失敗");

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
                Vector3 p = new Vector3(center.x + dir.x * d, center.y + 5f, center.z + dir.y * d);

                if (NavMesh.SamplePosition(p, out var hit, Mathf.Max(2f, r), NavMesh.AllAreas))
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

        pos = (a + b + c) / 3f;
        return true;
    }

    void SpawnEnemyAt(Vector3 position)
    {
        if (enemyPrefabs == null || enemyPrefabs.Length == 0) return;
        var prefab = enemyPrefabs[Random.Range(0, enemyPrefabs.Length)];
        if (!prefab) return;

        Instantiate(prefab, position, Quaternion.identity);
    }
}
