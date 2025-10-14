using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class PlayerMove : MonoBehaviour
{
    void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        if (sprintAction) sprintAction.action.Enable();
    }
    void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        if (sprintAction) sprintAction.action.Disable();
    }

    [Header("Move / Jump")]
    public float moveSpeed = 5f;
    public float jumpForce = 5f;

    [Header("Sprint (Input Action)")]
    public InputActionReference sprintAction;       // ← ここに Input Actions の Sprint を割り当てる
    public float sprintMultiplier = 1.8f;           // ダッシュ倍率
    public bool allowBackwardSprint = false;        // 後退ダッシュを許可するなら true

    [Header("Facing")]
    [Tooltip("true: 常にカメラの向きを向く（ストレイフ移動） / false: 移動方向へ向く")]
    public bool strafeMode = true;
    [Tooltip("回転の追従速度（度/秒）")]
    public float turnSpeed = 1000f;

    [Header("Refs")]
    [Tooltip("実際のレンダーカメラ（Main Camera）のTransform")]
    public Transform cameraTransform;

    Rigidbody rb;
    bool isGrounded = true;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        if (!cameraTransform) cameraTransform = Camera.main ? Camera.main.transform : null;
    }

    void Update()
    {
        var k = Keyboard.current; if (k == null) return;
        if (!cameraTransform) return;

        // カメラ基準の前・右（水平化）
        Vector3 camF = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        Vector3 camR = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;

        float v = (k.wKey.isPressed ? 1 : 0) + (k.sKey.isPressed ? -1 : 0);
        float h = (k.dKey.isPressed ? 1 : 0) + (k.aKey.isPressed ? -1 : 0);
        Vector3 moveDir = (camF * v + camR * h).normalized;

        // 向き
        if (strafeMode)
        {
            if (camF.sqrMagnitude > 1e-6f) RotateTowards(camF);
        }
        else if (moveDir.sqrMagnitude > 1e-4f)
        {
            RotateTowards(moveDir);
        }

        // --- XZ速度を一度だけ反映（Input Action でダッシュ） ---
        float speed = moveSpeed;

        bool sprintHeld = sprintAction && sprintAction.action.IsPressed();
        bool isForward = Vector3.Dot(moveDir, camF) > 0.1f;

        if (sprintHeld && (allowBackwardSprint || isForward))
            speed *= sprintMultiplier;

        Vector3 vel = rb.linearVelocity;
        vel.x = moveDir.x * speed;
        vel.z = moveDir.z * speed;
        rb.linearVelocity = vel;

        // ジャンプ（キーボードのまま）
        if (k.spaceKey.wasPressedThisFrame && isGrounded)
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
            isGrounded = false;
        }
    }

    void RotateTowards(Vector3 dir)
    {
        if (dir.sqrMagnitude <= 1e-6f) return;
        Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
    }

    void OnCollisionEnter(Collision col)
    {
        if (col.gameObject.CompareTag("Ground")) isGrounded = true;
    }

    // 敵を水平に押す処理（必要なら残す）
    void OnCollisionStay(Collision collision)
    {
        var enemy = collision.gameObject.GetComponent<EnemySenseChaseAgent>();
        if (!enemy) return;

        Collider myCol = GetComponent<Collider>();
        Collider otherCol = collision.collider;
        if (!myCol || !otherCol) return;

        if (Physics.ComputePenetration(
            myCol, transform.position, transform.rotation,
            otherCol, otherCol.transform.position, otherCol.transform.rotation,
            out Vector3 dir, out float dist))
        {
            Vector3 disp = (-dir) * Mathf.Min(dist, 0.5f) * 0.9f;
            disp = Vector3.ProjectOnPlane(disp, Vector3.up);
            if (disp.sqrMagnitude > 1e-6f) enemy.PushFromPlayer(disp);
        }
    }
}
