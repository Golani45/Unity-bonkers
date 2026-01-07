using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SphereMovement : MonoBehaviour
{
    [Header("Movement")]
    public float moveForce = 25f;
    public float maxSpeed = 10f;
    public float airControlMultiplier = 0.4f;

    [Header("Jump")]
    public float jumpForce = 6f;
    public LayerMask groundMask;

    [Header("Camera")]
    public Transform cameraTransform;

    private Rigidbody rb;
    private bool isGrounded;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        CheckGrounded();
        Move();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space) && isGrounded)
        {
            Jump();
        }
    }

    void Move()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 camForward = Vector3.Scale(cameraTransform.forward, new Vector3(1, 0, 1)).normalized;
        Vector3 camRight = cameraTransform.right;

        Vector3 moveDir = (camForward * v + camRight * h).normalized;

        if (moveDir.magnitude < 0.1f)
            return;

        float control = isGrounded ? 1f : airControlMultiplier;
        rb.AddForce(moveDir * moveForce * control, ForceMode.Force);

        // Clamp max speed
        Vector3 flatVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        if (flatVel.magnitude > maxSpeed)
        {
            Vector3 limitedVel = flatVel.normalized * maxSpeed;
            rb.linearVelocity = new Vector3(limitedVel.x, rb.linearVelocity.y, limitedVel.z);
        }
    }

    void Jump()
    {
        rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
    }

    void CheckGrounded()
    {
        float rayDist = 0.7f;

        Collider c = GetComponent<Collider>();
        if (c != null)
            rayDist = c.bounds.extents.y + 0.15f; // small buffer

        isGrounded = Physics.Raycast(transform.position, Vector3.down, rayDist, groundMask, QueryTriggerInteraction.Ignore);
    }

}
