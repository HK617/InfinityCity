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
    public float stopDistance = 1.2f;   // �v���C���[���͂́g�ցh�܂ŋ߂Â�
    public float updateRate = 0.1f;   // �ړI�n�X�V�̊Ԉ���
    public bool useFov = false;
    public float fovAngle = 120f;

    [Header("Separation (���W���)")]
    public LayerMask enemyMask;          // �G���C��
    public float separationRadius = 1.0f;
    public float separationForce = 1.5f; // 0�Ŗ���

    // --- ���� ---
    NavMeshAgent agent;
    bool chasing;
    float nextUpdate;

    // --- �����Ԃ��i�O�́j ---
    Vector3 externalPush = Vector3.zero;            // �����̗ݐρi���[���h���W�j
    [SerializeField] float pushDecay = 8f;          // �����i�傫���قǑ���������j
    [SerializeField] float maxPushPerFrame = 0.2f;  // 1�t���[���̍ő剟���ʁim�j

    // �v���C���[������ĂԁF�����x�N�g����ς�
    public void PushFromPlayer(Vector3 worldDisplacement)
    {
        externalPush += worldDisplacement;
    }

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.stoppingDistance = 0f;   // ���O�ŋ����Ǘ�
        agent.autoBraking = true;
        // Rigidbody�͕t���Ȃ� or isKinematic/No Gravity ����
    }

    void Update()
    {
        if (!target) return;

        // === �����߂����f�i�����̂݁E�������j ===
        if (externalPush.sqrMagnitude > 1e-6f)
        {
            // �����������������āg���݁h��h��
            Vector3 planar = Vector3.ProjectOnPlane(externalPush, Vector3.up);
            if (planar.sqrMagnitude > 1e-6f)
            {
                Vector3 step = Vector3.ClampMagnitude(planar, maxPushPerFrame);
                agent.Move(step); // Warp�ł͂Ȃ�Move�ŃX���[�Y��
            }
            // ����
            externalPush = Vector3.Lerp(externalPush, Vector3.zero, pushDecay * Time.deltaTime);
        }

        float dist = Vector3.Distance(transform.position, target.position);

        // === ���GON/OFF�i�q�X�e���V�X�{����/����j ===
        if (!chasing)
        {
            if (dist <= detectRadius && PassesFov() && HasLineOfSight()) chasing = true;
        }
        else
        {
            if (dist >= loseRadius || (requireLineOfSight && !HasLineOfSight())) chasing = false;
        }

        // === �ǔ��i�v���C���[���S�ł͂Ȃ��g�ցh�̖ڕW�_�j ===
        if (chasing)
        {
            if (Time.time >= nextUpdate)
            {
                Vector3 toT = target.position - transform.position; toT.y = 0f;
                Vector3 dir = (toT.sqrMagnitude > 0.0001f) ? toT.normalized : transform.forward;
                Vector3 ring = target.position - dir * stopDistance; // �֏�̓��B�ڕW
                ring.y = target.position.y;

                // �����x�N�g�������Z�i�߂��̓G���痣���j
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

            // �قړ��B�Ȃ��~
            if (!agent.pathPending && agent.remainingDistance <= 0.05f)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
            }
            else
            {
                agent.isStopped = false;
            }

            // �����ډ�]�F���ߋ����ł͎~�߂�
            Vector3 look = target.position - transform.position; look.y = 0f;
            if (dist > stopDistance * 0.8f && look.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), 10f * Time.deltaTime);
        }
        else
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }

        // NavMesh�O�Ȃ�߂��i����Move���g���Ă���̂ŕp�x�͒Ⴂ�z��j
        if (!agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
                agent.Warp(hit.position);
        }
    }

    // === ����p�`�F�b�N ===
    bool PassesFov()
    {
        if (!useFov) return true;
        Vector3 toTarget = target.position - transform.position; toTarget.y = 0f;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (toTarget.sqrMagnitude < 1e-6f) return true;
        return Vector3.Angle(fwd, toTarget) <= (fovAngle * 0.5f);
    }

    // === �����`�F�b�N ===
    bool HasLineOfSight()
    {
        if (!requireLineOfSight) return true;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 tgt = target.position + Vector3.up * targetEyeHeight;
        Vector3 dir = tgt - eye; float d = dir.magnitude;
        return !Physics.Raycast(eye, dir / d, d, obstacleMask, QueryTriggerInteraction.Ignore);
    }

    // === �f�o�b�O ===
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.position, detectRadius);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(transform.position, loseRadius);
        if (separationForce > 0f) { Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(transform.position, separationRadius); }
    }
}
