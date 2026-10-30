using UnityEngine;

// Animates a dropped item, captures it near Brighton's foot capsule, and credits inventory once.
public sealed class WorldLoot : MonoBehaviour
{
    // The first phase arcs from origin to destination; the second shrinks toward the chest.
    Vector3 origin, destination;
    float age;
    WorldsidesGameplay game;
    Vector3 originalScale;
    bool collecting, collected;
    float captureDistance, pickupSpeed, pickupScale = 1;
    public bool IsStarter { get; set; }
    public string Item { get; private set; }
    public int Quantity { get; private set; }

    // Remember the drop endpoints and original size, and disable unused physical collisions.
    public void Initialize(string item, int quantity, Vector3 from, Vector3 to, WorldsidesGameplay owner = null)
    {
        Item = item;
        Quantity = quantity;
        origin = from;
        destination = to;
        game = owner ? owner : FindAnyObjectByType<WorldsidesGameplay>();
        originalScale = transform.localScale;
        age = from == to ? 0.45f : 0;

        // Loot uses the pickup-radius test below rather than collider-trigger callbacks.
        foreach (var collider in GetComponentsInChildren<Collider>())
            collider.enabled = false;
    }

    // Use gameplay time so loot animations freeze when menus pause the world.
    void Update()
    {
        Advance(Time.deltaTime);
    }

    // Separate landing, capture, and collection so each item can be credited exactly once.
    public void Advance(float seconds)
    {
        // Do not animate or collect during menus/death, or after this item was already credited.
        if (!game || !game.Player || game.Dead || GameMenus.Blocking || collected || seconds <= 0)
            return;

        // Measure only horizontal distance from the shadow capsule's center, not sprite height.
        age += seconds;
        Vector3 groundDelta = transform.position - game.PickupOrigin;
        groundDelta.y = 0;

        // Wait for the landing arc to finish before latching onto a nearby player.
        if (!collecting && age >= 0.45f && groundDelta.magnitude <= game.PickupRadius)
        {
            collecting = true;
            pickupSpeed = 8;
            pickupScale = 1;
            captureDistance =
                Mathf.Max(0.01f, Vector3.Distance(transform.position, game.Player.position + Vector3.up * 0.24f));
        }

        // Once captured, keep following Brighton even if he moves outside the initial radius.
        if (collecting)
        {
            Vector3 center = game.Player.position + Vector3.up * 0.24f;
            // Constant acceleration never resets when Brighton moves away; even initial speed exceeds sprinting.
            const float acceleration = 20;
            float travel = pickupSpeed * seconds + 0.5f * acceleration * seconds * seconds;
            pickupSpeed += acceleration * seconds;
            transform.position = Vector3.MoveTowards(transform.position, center, travel);
            float remaining = Vector3.Distance(transform.position, center);

            // Moving away must not make a captured item grow back to its original size.
            pickupScale = Mathf.Min(pickupScale, Mathf.Clamp01(remaining / captureDistance));
            transform.localScale = originalScale * pickupScale;

            // Credit the stack at arrival; deactivate immediately to prevent duplicate collection.
            if (remaining <= 0.015f)
            {
                collected = true;
                game.Inventory.Add(Item, Quantity);
                GameAudio.Play(GameAudio.Cue.ItemPickup);
                gameObject.SetActive(false);
                Destroy(gameObject);
            }
            return;
        }

        // Uncaptured loot follows a short sine-shaped pop and then rests at its destination.
        float t = Mathf.Clamp01(age / 0.45f);
        transform.position = Vector3.Lerp(origin, destination, t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * 0.3f);
    }

    // Respawning leaves ordinary loot on the ground and restores any partially shrunk visual.
    public void CancelPickup()
    {
        collecting = false;
        transform.localScale = originalScale;
        destination = transform.position;
        destination.y = 0;
        origin = destination;
        age = 0.45f;
    }
}
