using UnityEngine;
using UnityEngine.InputSystem; // 新Input System

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class PlayerConroller : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 6f;
    public float sprintMultiplier = 1.5f;
    public float jumpForce = 5f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2f;
    public Vector2 pitchLimits = new Vector2(-80f, 80f);
    public bool lockCursor = true;
    public Camera playerCamera;

    [Header("Ground Check")]
    public LayerMask groundMask = ~0;
    public float groundCheckDistance = 0.2f;
    public float groundCheckRadius = 0.25f; // ← 安定のため小球で判定

    [Header("Input Actions (optional)")]
    public InputActionReference jumpAction; // 任意：ここに Jump アクションを割当て可能

    // 入力バッファ
    Vector2 moveInput;
    bool sprintHeld;
    Vector2 mouseDelta;
    bool jumpQueued;          // ← 押下をキューに入れて取りこぼし防止

    Rigidbody rb;
    Collider col;
    float yaw, pitch;
    bool isGrounded;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        col = GetComponent<Collider>();

        if (!playerCamera) playerCamera = Camera.main;

        yaw = transform.eulerAngles.y;
        pitch = playerCamera ? playerCamera.transform.localEulerAngles.x : 0f;
    }

    void OnEnable()
    {
        if (lockCursor) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        if (jumpAction) jumpAction.action.Enable();
    }

    void OnDisable()
    {
        if (lockCursor) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        if (jumpAction) jumpAction.action.Disable();
    }

    void Update()
    {
        // ====== 入力（新Input System）======
        var kb = Keyboard.current;
        var ms = Mouse.current;

        // 移動
        float x = 0f, y = 0f;
        if (kb != null)
        {
            if (kb.aKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed) x += 1f;
            if (kb.sKey.isPressed) y -= 1f;
            if (kb.wKey.isPressed) y += 1f;

            sprintHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // ジャンプ：スペース or アクション どちらでもキューに積む
            if (kb.spaceKey.wasPressedThisFrame) jumpQueued = true;
        }
        // InputAction 側（任意）
        if (jumpAction && jumpAction.action.WasPressedThisFrame())
            jumpQueued = true;

        moveInput = new Vector2(x, y);
        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();

        // 視点
        if (ms != null)
        {
            Vector2 d = ms.delta.ReadValue();
            mouseDelta = d * mouseSensitivity;
        }
        else mouseDelta = Vector2.zero;

        yaw += mouseDelta.x;
        pitch -= mouseDelta.y;
        pitch = Mathf.Clamp(pitch, pitchLimits.x, pitchLimits.y);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (playerCamera)
            playerCamera.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);

        // 接地
        isGrounded = CheckGrounded();
    }

    void FixedUpdate()
    {
        // 移動（物理）
        Vector3 fwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        Vector3 right = new Vector3(transform.right.x, 0f, transform.right.z).normalized;
        Vector3 move = (fwd * moveInput.y + right * moveInput.x);
        float speed = moveSpeed * (sprintHeld ? sprintMultiplier : 1f);

        Vector3 vel = rb.linearVelocity;
        vel.x = move.x * speed;
        vel.z = move.z * speed;
        rb.linearVelocity = vel;

        // ジャンプ（FixedUpdateで確実に処理）
        if (jumpQueued && isGrounded)
        {
            var v = rb.linearVelocity;
            if (v.y < 0f) v.y = 0f;
            rb.linearVelocity = v;
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        }
        jumpQueued = false; // 消費
    }

    bool CheckGrounded()
    {
        // コライダー下端までの高さを自動で計算
        Bounds b = col.bounds;
        // キャラ中心から少し上にずらして、下方向に「半径付き」の球レイを飛ばす
        Vector3 origin = b.center + Vector3.up * 0.01f;

        // 下端までの距離 + 余白(groundCheckDistance)
        float dist = b.extents.y + Mathf.Max(0.05f, groundCheckDistance);

        // 半径はインスペクタの groundCheckRadius を使用
        float radius = Mathf.Max(0.05f, groundCheckRadius);

        return Physics.SphereCast(
            origin,
            radius,
            Vector3.down,
            out _,
            dist,
            groundMask,
            QueryTriggerInteraction.Ignore
        );
    }


    // 敵に押し戻しを通知（任意）
    void OnCollisionStay(Collision collision)
    {
        var enemy = collision.collider.GetComponentInParent<EnemySenseChaseAgent>();
        if (!enemy) return;

        if (Physics.ComputePenetration(
            col, transform.position, transform.rotation,
            collision.collider, collision.collider.transform.position, collision.collider.transform.rotation,
            out Vector3 dir, out float dist))
        {
            Vector3 disp = (-dir) * Mathf.Min(dist, 0.5f) * 0.9f;
            disp = Vector3.ProjectOnPlane(disp, Vector3.up);
            if (disp.sqrMagnitude > 1e-6f) enemy.PushFromPlayer(disp);
        }
    }
}
