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

    [Header("Movement")]
    public float stopDistance = 1.2f;   // プレイヤー周囲の“輪”まで近づく
    public float updateRate = 0.1f;   // 目的地更新の間引き
    public bool useFov = false;
    public float fovAngle = 120f;

    [Header("Separation (密集回避)")]
    public LayerMask enemyMask;          // 敵レイヤ
    public float separationRadius = 1.0f;
    public float separationForce = 1.5f; // 0で無効

    // --- 内部 ---
    NavMeshAgent agent;
    bool chasing;
    float nextUpdate;

    // --- 押し返し（外力） ---
    Vector3 externalPush = Vector3.zero;            // 押しの累積（ワールド座標）
    [SerializeField] float pushDecay = 8f;          // 減衰（大きいほど早く消える）
    [SerializeField] float maxPushPerFrame = 0.2f;  // 1フレームの最大押し量（m）

    // プレイヤー側から呼ぶ：押しベクトルを積む
    public void PushFromPlayer(Vector3 worldDisplacement)
    {
        externalPush += worldDisplacement;
    }

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.stoppingDistance = 0f;   // 自前で距離管理
        agent.autoBraking = true;
        // Rigidbodyは付けない or isKinematic/No Gravity 推奨
    }

    void Update()
    {
        if (!target) return;

        // === 押し戻し反映（水平のみ・少しずつ） ===
        if (externalPush.sqrMagnitude > 1e-6f)
        {
            // 垂直成分を除去して“沈み”を防ぐ
            Vector3 planar = Vector3.ProjectOnPlane(externalPush, Vector3.up);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Vector3 step = Vector3.ClampMagnitude(planar, maxPushPerFrame);
                agent.Move(step); // WarpではなくMoveでスムーズに
            }
            // 減衰
            externalPush = Vector3.Lerp(externalPush, Vector3.zero, pushDecay * Time.deltaTime);
        }

        float dist = Vector3.Distance(transform.position, target.position);

        // === 索敵ON/OFF（ヒステリシス＋視線/視野） ===
        if (!chasing)
        {
            if (dist <= detectRadius && PassesFov() && HasLineOfSight()) chasing = true;
        }
        else
        {
            if (dist >= loseRadius || (requireLineOfSight && !HasLineOfSight())) chasing = false;
        }

        // === 追尾（プレイヤー中心ではなく“輪”の目標点） ===
        if (chasing)
        {
            if (Time.time >= nextUpdate)
            {
                Vector3 toT = target.position - transform.position; toT.y = 0f;
                Vector3 dir = (toT.sqrMagnitude > 0.0001f) ? toT.normalized : transform.forward;
                Vector3 ring = target.position - dir * stopDistance; // 輪上の到達目標
                ring.y = target.position.y;

                // 分離ベクトルを加算（近くの敵から離れる）
                if (separationForce > 0f && separationRadius > 0f)
                {
                    Vector3 sep = Vector3.zero; int count = 0;
                    var hits = Physics.OverlapSphere(transform.position, separationRadius, enemyMask, QueryTriggerInteraction.Ignore);
                    foreach (var h in hits)
                    {
                        if (h.transform == transform) continue;
                        Vector3 away = transform.position - h.transform.position; away.y = 0f;
                        float d = away.magnitude + 1e-3f;
                        sep += away / d; count++;
                    }
                    if (count > 0) sep /= count;
                    ring += sep * separationForce;
                }

                agent.SetDestination(ring);
                nextUpdate = Time.time + updateRate;
            }

            // ほぼ到達なら停止
            if (!agent.pathPending && agent.remainingDistance <= 0.05f)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
            }
            else
            {
                agent.isStopped = false;
            }

            // 見た目回転：至近距離では止める
            Vector3 look = target.position - transform.position; look.y = 0f;
            if (dist > stopDistance * 0.8f && look.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), 10f * Time.deltaTime);
        }
        else
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }

        // NavMesh外なら戻す（水平Moveを使っているので頻度は低い想定）
        if (!agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
                agent.Warp(hit.position);
        }
    }

    // === 視野角チェック ===
    bool PassesFov()
    {
        if (!useFov) return true;
        Vector3 toTarget = target.position - transform.position; toTarget.y = 0f;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (toTarget.sqrMagnitude < 1e-6f) return true;
        return Vector3.Angle(fwd, toTarget) <= (fovAngle * 0.5f);
    }

    // === 視線チェック ===
    bool HasLineOfSight()
    {
        if (!requireLineOfSight) return true;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 tgt = target.position + Vector3.up * targetEyeHeight;
        Vector3 dir = tgt - eye; float d = dir.magnitude;
        return !Physics.Raycast(eye, dir / d, d, obstacleMask, QueryTriggerInteraction.Ignore);
    }

    // === デバッグ ===
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.position, detectRadius);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(transform.position, loseRadius);
        if (separationForce > 0f) { Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(transform.position, separationRadius); }
    }
}
