// Assets/scripts/EnemySpawner.cs
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class EnemySpawner : MonoBehaviour
{
    [Header("Spawn")]
    public GameObject enemyPrefab;      // �G�v���n�u
    public Transform player;            // �ǔ��Ώہi�C�ӂ����ݒ萄���j
    public float spawn_interval = 3f;   // ���b���Ƃ�
    public int spawn_number = 3;     // ��x�ɉ���
    public float minSpawnDistance = 3f; // �v���C���[�ɋ߂�������N�����Ȃ�����

    [Header("Area (XZ world)")]
    public float minX = -200f, maxX = 200f;
    public float minZ = -200f, maxZ = 200f;

    [Header("NavMesh sampling")]
    public float sampleMaxDistance = 50f; // �����_���_����NavMesh�ɋz�����鋖�e���a
    public int sampleTries = 30;  // �͈͓��ł̎��s��

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
            if (debugLogs) Debug.LogError("EnemySpawner: enemyPrefab ���ݒ�");
            yield break;
        }

        var wait = new WaitForSeconds(spawn_interval);

        while (true)
        {
            for (int i = 0; i < spawn_number; i++)
            {
                if (!TryGetNavmeshSpawn(out var pos))
                {
                    if (debugLogs) Debug.LogWarning("EnemySpawner: NavMesh ��̃X�|�[���ʒu�擾�Ɏ��s");
                    continue;
                }

                // �v���C���[�ɋ߂�����ꍇ�͈�x������蒼��
                if (player && Vector3.Distance(pos, player.position) < minSpawnDistance)
                {
                    if (!TryGetNavmeshSpawn(out pos)) continue;
                    if (player && Vector3.Distance(pos, player.position) < minSpawnDistance) continue;
                }

                var go = Instantiate(enemyPrefab, pos, Quaternion.identity);
                if (debugLogs) Debug.Log($"EnemySpawner: Spawned at {pos}");

                // ���G�E��������t����AI�Ƀ^�[�Q�b�g��n���i�t���Ă���΁j
                var ai = go.GetComponent<EnemySenseChaseAgent>();
                if (ai && player) ai.target = player;
            }

            yield return wait;
        }
    }

    // --- NavMesh ��̒n�_���擾�i�͈͓������s �� ���s����NavMesh�S�̂���1�_��I�ԁj---
    bool TryGetNavmeshSpawn(out Vector3 result)
    {
        // 1) �w��͈͓��� NavMesh �ɋz��
        for (int t = 0; t < sampleTries; t++)
        {
            var rnd = new Vector3(Random.Range(minX, maxX), 100f, Random.Range(minZ, maxZ));
            if (NavMesh.SamplePosition(rnd, out var hit, sampleMaxDistance, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }
        }

        // 2) �t�H�[���o�b�N�FNavMesh �S�̂̎O�p�`����1�_�������_���Ŏ擾
        var tri = NavMesh.CalculateTriangulation();
        if (tri.vertices != null && tri.vertices.Length >= 3 &&
            tri.indices != null && tri.indices.Length >= 3)
        {
            int idx = Random.Range(0, tri.indices.Length / 3) * 3;
            Vector3 a = tri.vertices[tri.indices[idx]];
            Vector3 b = tri.vertices[tri.indices[idx + 1]];
            Vector3 c = tri.vertices[tri.indices[idx + 2]];

            // �O�p�`�����̈�l�����_�ibarycentric�j
            float r1 = Random.value;
            float r2 = Random.value;
            float s = Mathf.Sqrt(r1);
            Vector3 p = (1 - s) * a + (s * (1 - r2)) * b + (s * r2) * c;

            // �O�̂��ߋߖT�ŋz��
            if (NavMesh.SamplePosition(p + Vector3.up * 10f, out var hit2, 20f, NavMesh.AllAreas))
            {
                result = hit2.position;
                return true;
            }
        }

        result = default;
        return false;
    }

    // �V�[����Ŕ͈͂�����
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        var c = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        var s = new Vector3(Mathf.Abs(maxX - minX), 0.1f, Mathf.Abs(maxZ - minZ));
        Gizmos.DrawWireCube(c, s);
    }
}
