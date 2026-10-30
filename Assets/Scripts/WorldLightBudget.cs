using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Keep URP's finite light list local to the camera. Extra point lights still
// illuminate our ground/sprite shaders through an unshadowed fallback list.
[DefaultExecutionOrder(1000)]
public sealed class WorldLightBudget : MonoBehaviour
{
    // Native lights can cast shadows; these extra shader slots supply unshadowed illumination.
    const int OverflowCapacity = 128;
    readonly HashSet<Light> managed = new HashSet<Light>();
    readonly List<Light> visible = new List<Light>();
    readonly Vector4[] positions = new Vector4[OverflowCapacity];
    readonly Vector4[] colors = new Vector4[OverflowCapacity];
    readonly Plane[] planes = new Plane[6];
    int rotation;

    void LateUpdate()
    {
        var camera = Camera.main;

        // No camera means there is no visible region to prioritize.
        if (!camera)
            return;

        // Build six view-frustum planes to reject lights whose range misses the camera.
        GeometryUtility.CalculateFrustumPlanes(camera, planes);
        visible.Clear();

        // Disabled components are included; an inactive/destroyed bullet is not.
        foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            // Manage point lights only, and leave intentionally disabled lights disabled.
            if (light.type != LightType.Point)
                continue;
            if (light.enabled)
                managed.Add(light);
            if (!managed.Contains(light))
                continue;
            light.enabled = false;

            // A point light's bounding box conservatively covers its full radius.
            if (light.intensity <= 0 ||
                !GeometryUtility.TestPlanesAABB(planes,
                                                new Bounds(light.transform.position, Vector3.one * light.range * 2)))
                continue;
            visible.Add(light);
        }

        // Prioritize lights nearest the camera's ground intersection, not its elevated position.
        Vector3 center =
            camera.transform.position +
            camera.transform.forward * (camera.transform.position.y / Mathf.Max(0.05f, -camera.transform.forward.y));
        visible.Sort((a, b) =>
                     {
                         int distance = (a.transform.position - center)
                                            .sqrMagnitude.CompareTo((b.transform.position - center).sqrMagnitude);
                         return distance;
                     });

        // Leave space for directional/spot lights outside this manager.
        int budget = Mathf.Max(1, UniversalRenderPipeline.maxVisibleAdditionalLights - 8);
        int nativeCount = Mathf.Min(budget, visible.Count);

        // Re-enable only the closest lights that fit URP's finite list.
        for (int i = 0; i < nativeCount; i++)
            visible[i].enabled = true;
        int overflowCount = visible.Count - nativeCount;
        int uploaded = Mathf.Min(OverflowCapacity, overflowCount);

        // If extremely dense lights exceed the fallback list, rotate and weight
        // samples so later placements still contribute rather than staying dark.
        for (int i = 0; i < uploaded; i++)
        {
            var light = visible[nativeCount + (i + rotation) % overflowCount];

            // Pack inverse squared range into W to avoid recomputing it in every shader pixel.
            Vector3 p = light.transform.position;
            positions[i] = new Vector4(p.x, p.y, p.z, 1 / Mathf.Max(0.0001f, light.range * light.range));

            // Match the renderer's color space and compensate when sampling a crowded light list.
            Color color = QualitySettings.activeColorSpace == ColorSpace.Linear ? light.color.linear : light.color;
            color *= light.intensity * Mathf.Max(1, (float)overflowCount / OverflowCapacity);
            colors[i] = new Vector4(color.r, color.g, color.b, 0);
        }

        // Upload the active list; the shader ignores unused entries beyond the count.
        rotation = overflowCount > 0 ? (rotation + uploaded) % overflowCount : 0;
        Shader.SetGlobalVectorArray("_WorldsidesOverflowPositions", positions);
        Shader.SetGlobalVectorArray("_WorldsidesOverflowColors", colors);
        Shader.SetGlobalInt("_WorldsidesOverflowCount", uploaded);
        managed.RemoveWhere(light => !light);
    }

    // Release global shader state and restore lights when this manager stops running.
    void OnDisable()
    {
        Shader.SetGlobalInt("_WorldsidesOverflowCount", 0);
        foreach (var light in managed)
            if (light)
                light.enabled = true;
        managed.Clear();
    }
}
