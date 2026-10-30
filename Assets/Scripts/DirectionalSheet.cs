using UnityEngine;
using UnityEngine.Rendering;

// Animates quads without slicing/importing Sprite assets or changing shared materials.
public sealed class DirectionalSheet : MonoBehaviour
{
    // Body and equipment share a frame/row but use separate textures on stacked quads.
    MeshRenderer body, overlay;
    Texture2D sheet, equipment;
    MaterialPropertyBlock properties;
    int frame, row;
    float elapsed;
    bool wasMoving;
    public System.Action Step;
    public float PixelSize => body.transform.lossyScale.x / 32f;

    // Test the actual animated card, not the ground capsule, so right-clicking Brighton feels direct.
    public bool ScreenHit(Camera camera, Vector2 screen)
    {
        if (!body || !camera || !sheet)
            return false;
        Ray ray = camera.ScreenPointToRay(screen);
        if (!new Plane(body.transform.forward, body.transform.position).Raycast(ray, out float distance))
            return false;
        Vector3 point = body.transform.InverseTransformPoint(ray.GetPoint(distance));
        var mesh = body.GetComponent<MeshFilter>();
        if (!mesh || !mesh.sharedMesh)
            return false;
        Bounds bounds = mesh.sharedMesh.bounds;
        if (point.x < bounds.min.x || point.x > bounds.max.x || point.y < bounds.min.y || point.y > bounds.max.y)
            return false;

        // Ignore transparent sprite pixels so empty corners still allow lamp placement.
        if (!sheet.isReadable)
            return true;
        int width = sheet.width / 4, height = sheet.height / 4;
        int x =
            Mathf.Clamp(Mathf.FloorToInt(Mathf.InverseLerp(bounds.min.x, bounds.max.x, point.x) * width), 0, width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.InverseLerp(bounds.min.y, bounds.max.y, point.y) * height), 0,
                            height - 1);
        return sheet.GetPixel(frame * width + x, (3 - row) * height + y).a >= 0.5f;
    }

    // Find the visible card, optionally add an equipment layer, and apply the first UV frame.
    public void Initialize(Texture2D texture, bool addOverlay)
    {
        properties = new MaterialPropertyBlock();

        // Ignore shadow-only proxies; they must not be animated as the visible character.
        foreach (var renderer in GetComponentsInChildren<MeshRenderer>())
            if (renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
            {
                body = renderer;
                break;
            }
        sheet = texture;

        // A prefab without a visible card cannot be animated safely.
        if (body == null)
        {
            enabled = false;
            return;
        }

        // Reuse the body's mesh/material; per-renderer properties will choose the overlay texture.
        if (addOverlay)
        {
            var layer = new GameObject("Equipment animation");
            layer.transform.SetParent(body.transform, false);
            layer.AddComponent<MeshFilter>().sharedMesh = body.GetComponent<MeshFilter>().sharedMesh;
            overlay = layer.AddComponent<MeshRenderer>();
            overlay.sharedMaterial = body.sharedMaterial;
            overlay.shadowCastingMode = ShadowCastingMode.Off;
            overlay.receiveShadows = false;
        }

        // Visual card colliders do not represent an actor's ground footprint.
        foreach (var collider in GetComponentsInChildren<Collider>())
            collider.enabled = false;
        Render();
    }

    // Compare ground speed, not distance per frame, so tiny high-FPS steps still count as walking.
    public static bool IsMoving(Vector3 displacement, float seconds)
    {
        if (seconds <= 0)
            return false;
        displacement.y = 0;
        const float minimumSpeed = 0.01f;
        return displacement.sqrMagnitude > minimumSpeed * minimumSpeed * seconds * seconds;
    }

    // Gameplay supplies scaled time so animation and footsteps pause with the world.
    public void Tick(Vector2 screenDirection, bool moving, float secondsPerFrame, Texture2D equipmentSheet = null)
    {
        Advance(screenDirection, moving, secondsPerFrame, Time.deltaTime, equipmentSheet);
    }

    // An explicit elapsed time also lets regression checks exercise the real animation at any frame rate.
    public void Advance(Vector2 screenDirection, bool moving, float secondsPerFrame, float seconds,
                        Texture2D equipmentSheet = null)
    {
        // A paused/zero-time frame must not reset the walk cycle or emit a new starting footstep.
        if (seconds <= 0)
            return;

        // Choose whichever screen-space axis is strongest: horizontal or vertical.
        if (screenDirection.sqrMagnitude > 0.0001f)
            row = Mathf.Abs(screenDirection.x) > Mathf.Abs(screenDirection.y) ? (screenDirection.x > 0 ? 1 : 3)
                                                                              : (screenDirection.y > 0 ? 2 : 0);

        // Advance all elapsed frames while moving; an idle character returns to frame zero.
        if (moving)
        {
            // The first displayed walking frame and each later frame change count as a step.
            if (!wasMoving)
                Step?.Invoke();
            elapsed += seconds;
            float duration = Mathf.Max(0.02f, secondsPerFrame);
            int steps = Mathf.FloorToInt(elapsed / duration);
            frame = (frame + steps) % 4;
            elapsed -= steps * duration;

            // Emit once for every advanced animation frame, including frames skipped by a slow render.
            for (int i = 0; i < steps; i++)
                Step?.Invoke();
        }
        else
        {
            frame = 0;
            elapsed = 0;
        }
        wasMoving = moving;

        // Render body and equipment together so their animation cannot drift out of sync.
        equipment = equipmentSheet;
        Render();
    }

    // Select one cell from the 4x4 sheet without cloning or modifying the shared material.
    void Render()
    {
        // Initialization may have failed or the sheet may be missing.
        if (!body || !sheet)
            return;

        // Texture UVs start at bottom-left; artwork rows start at top-left.
        var uv = new Vector4(0.25f, 0.25f, frame * 0.25f, (3 - row) * 0.25f);
        properties.SetVector("_BaseMap_ST", uv);
        properties.SetTexture("_BaseMap", sheet);
        body.SetPropertyBlock(properties);

        // Only display the equipment card when a corresponding sheet was supplied.
        if (overlay)
        {
            overlay.enabled = equipment != null;
            if (equipment)
            {
                properties.SetTexture("_BaseMap", equipment);
                overlay.SetPropertyBlock(properties);
            }
        }
    }

    // Nudge equipment toward the camera to prevent depth flicker over the body card.
    void LateUpdate()
    {
        if (overlay && Camera.main)
            overlay.transform.position = body.transform.position - Camera.main.transform.forward * 0.002f;
    }
}
