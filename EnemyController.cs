using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemySenseChaseAgent : MonoBehaviour
{
    public Transform target;

    [Header("Sense")]
    public float detectRadius = 15f;
    public float loseRadius = 22f;
    public bool requireLineOfSight = true;
    public LayerMask obstacleMask;
    public float eyeHeight = 1.0f;
    public float targetEyeHeight = 1.6f;

    [Header("Chase")]
    public float updateRate = 0.2f;
    public float stopDistance = 1.2f;
    public float ringOffset = 0.5f;
    public float ringRadius = 0.6f;

    [Header("Separation")]
    public LayerMask enemyMask;
    public float separationRadius = 2.0f;
    public float separationForce = 0.8f;

    [Header("Recovery")]
    public float relinkRadius = 8f;
    public int failedRelinkMaxFrames = 60;

    [Header("External Push (from Player)")]
    [SerializeField] Vector3 externalPush;
    [SerializeField] float pushDecay = 8f;
    [SerializeField] float maxPushPerFrame = 0.2f;

    NavMeshAgent agent;
    float nextUpdate;
    bool hasTargetInSight;
    int relinkFailFrames;

    // Player からの押し戻し入力
    public void PushFromPlayer(Vector3 worldDisplacement)
    {
        externalPush += worldDisplacement;
    }

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.stoppingDistance = 0f;
        agent.autoBraking = true;
        agent.enabled = false; // NavMesh準備前の例外回避
    }

    void OnEnable()
    {
        StartCoroutine(EnsureLinkedAndEnable());
        nextUpdate = 0f;
        relinkFailFrames = 0;
    }

    System.Collections.IEnumerator EnsureLinkedAndEnable()
    {
        // Triangulation 準備待ち
        while (true)
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices != null && tri.vertices.Length > 0) break;
            yield return null;
        }
        // 足元近傍の NavMesh へスナップ
        NavMeshHit hit; // ← スコープを外へ
        while (!NavMesh.SamplePosition(transform.position, out hit, Mathf.Max(0.5f, relinkRadius), NavMesh.AllAreas))
            yield return null;

        agent.enabled = true;
        agent.Warp(hit.position);
    }

    void Update()
    {
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return;
        if (!target) return;

        float dist = Vector3.Distance(target.position, transform.position);
        bool inDetect = dist <= detectRadius;
        bool inLose = dist <= loseRadius;

        if (!hasTargetInSight)
            hasTargetInSight = inDetect && (!requireLineOfSight || HasLineOfSight());
        else if (!inLose)
            hasTargetInSight = false;

        if (hasTargetInSight)
        {
            if (Time.time >= nextUpdate)
            {
                Vector3 dir = (transform.position - target.position); dir.y = 0f;
                dir = (dir.sqrMagnitude > 0.001f) ? dir.normalized : -target.forward;
                Vector3 ring = target.position + Quaternion.Euler(0f, ringOffset * 180f, 0f) * (dir * Mathf.Max(stopDistance * ringRadius, 0.5f));
                SafeSetDestination(ring);
                nextUpdate = Time.time + updateRate;
            }

            if (!agent.pathPending && agent.remainingDistance <= Mathf.Max(0.05f, stopDistance))
            { SafeSetStopped(true); agent.velocity = Vector3.zero; }
            else
            { SafeSetStopped(false); }
        }
        else
        {
            SafeSetStopped(true);
            agent.velocity = Vector3.zero;
        }

        // 外力適用＆減衰
        if (externalPush.sqrMagnitude > 0f)
        {
            Vector3 step = Vector3.ClampMagnitude(externalPush, maxPushPerFrame);
            transform.position += step;
            externalPush = Vector3.MoveTowards(externalPush, Vector3.zero, pushDecay * Time.deltaTime);
        }
    }

    void SafeSetStopped(bool stop)
    {
        if (agent && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = stop;
    }

    bool SafeSetDestination(Vector3 worldPos)
    {
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return false;
        if (NavMesh.SamplePosition(worldPos, out var hit, 2.5f, NavMesh.AllAreas))
            return agent.SetDestination(hit.position);
        return agent.SetDestination(worldPos);
    }

    bool HasLineOfSight()
    {
        if (!requireLineOfSight) return true;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 tgt = target.position + Vector3.up * targetEyeHeight;
        Vector3 dir = tgt - eye; float d = dir.magnitude;
        return !Physics.Raycast(eye, dir / d, d, obstacleMask, QueryTriggerInteraction.Ignore);
    }
}
