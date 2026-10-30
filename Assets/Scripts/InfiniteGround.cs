using UnityEngine;

// Reuses one large ground plane by snapping it around the player, never the camera peek.
public sealed class InfiniteGround : MonoBehaviour
{
    [SerializeField]
    private Transform player;

    [Tooltip("How far the ground moves at a time.")]
    [SerializeField, Min(0.01f)]
    private float repositionStep = 8f;

    private float groundHeight;

    // Gameplay supplies Brighton after creating or locating the player.
    public void Follow(Transform target)
    {
        player = target;
    }

    // Preserve the authored height while horizontal coordinates move.
    private void Awake()
    {
        groundHeight = transform.position.y;
    }

    private void LateUpdate()
    {
        // Wait until gameplay assigns a player to follow.
        if (player == null)
            return;

        // Snap to coarse cells to avoid moving the mesh on every tiny player step.
        // World-space noise in Ground.shader keeps its pattern stable across these moves.
        float x = Mathf.Floor(player.position.x / repositionStep) * repositionStep;

        float z = Mathf.Floor(player.position.z / repositionStep) * repositionStep;

        transform.position = new Vector3(x, groundHeight, z);
    }
}
