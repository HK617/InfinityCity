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

    [Header("NavMesh checks")]
    public float navmeshCheckRadius = 100f;

    [Header("Tries")]
    public int triesPerRing = 24;

    [Header("Debug")]
    public bool verboseLog = false;

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
            if (tri.vertices == null || tri.vertices.Length == 0) { yield return wait; continue; }
            if (!player) { yield return wait; continue; }

            Vector3 playerPos = player.position;

            if (!NavMesh.SamplePosition(playerPos, out var baseHit, navmeshCheckRadius, NavMesh.AllAreas))
            { if (verboseLog) Debug.Log("[Spawner] no navmesh near player"); yield return wait; continue; }

            Vector3 baseCenter = baseHit.position;

            if (!TryFindSpawnOnNavMesh(baseCenter, minSpawnDistance, Mathf.Max(minSpawnDistance + 5f, maxSpawnDistance), triesPerRing, out var spawnPos))
            {
                if (!TryPickTriangleCentroid(tri, out spawnPos))
                { if (verboseLog) Debug.Log("[Spawner] failed to get spawn pos"); yield return wait; continue; }
            }

            SpawnEnemySafelyAt(spawnPos);

            yield return wait;
        }
    }

    bool TryFindSpawnOnNavMesh(Vector3 centerOnNav, float minR, float maxR, int tries, out Vector3 result)
    {
        float r = Mathf.Max(1f, minR);
        for (int step = 0; step < 3; step++)
        {
            for (int i = 0; i < tries; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                float d = Random.Range(r, maxR);
                Vector3 guess = new(centerOnNav.x + dir.x * d, centerOnNav.y + 2f, centerOnNav.z + dir.y * d);

                if (NavMesh.SamplePosition(guess, out var hit, Mathf.Max(2f, r), NavMesh.AllAreas))
                { result = hit.position; return true; }
            }
            r = Mathf.Min(maxR, r * 1.8f);
        }
        result = Vector3.zero; return false;
    }

    bool TryPickTriangleCentroid(NavMeshTriangulation tri, out Vector3 pos)
    {
        if (tri.indices == null || tri.indices.Length < 3) { pos = Vector3.zero; return false; }
        int triCount = tri.indices.Length / 3;
        int t = Random.Range(0, triCount) * 3;

        Vector3 a = tri.vertices[tri.indices[t]];
        Vector3 b = tri.vertices[tri.indices[t + 1]];
        Vector3 c = tri.vertices[tri.indices[t + 2]];
        Vector3 centroid = (a + b + c) / 3f;

        if (NavMesh.SamplePosition(centroid, out var hit, 10f, NavMesh.AllAreas))
        { pos = hit.position; return true; }

        pos = Vector3.zero; return false;
    }

    void SpawnEnemySafelyAt(Vector3 spawnPos)
    {
        if (enemyPrefabs == null || enemyPrefabs.Length == 0) return;

        var prefab = enemyPrefabs[Random.Range(0, enemyPrefabs.Length)];
        if (!prefab) return;

        var go = Instantiate(prefab);
        go.SetActive(false);

        var agent = go.GetComponent<NavMeshAgent>();
        if (agent) agent.enabled = false;

        if (!NavMesh.SamplePosition(spawnPos, out var hit, 5f, NavMesh.AllAreas))
        { Destroy(go); return; }

        go.transform.SetPositionAndRotation(hit.position, Quaternion.identity);

        if (agent)
        {
            agent.enabled = true;
            if (!agent.Warp(hit.position))
            {
                if (NavMesh.SamplePosition(hit.position, out var hit2, 5f, NavMesh.AllAreas))
                    agent.Warp(hit2.position);
            }
        }

        go.SetActive(true);
        if (verboseLog) Debug.Log("[Spawner] spawned at " + hit.position);
    }
}
