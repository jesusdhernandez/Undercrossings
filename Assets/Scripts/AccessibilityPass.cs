using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

// Inserts a whole-camera color-assistance effect after URP finishes normal rendering.
public sealed class AccessibilityPass : ScriptableRenderPass
{
    readonly Material material;

    // Request a readable intermediate image instead of sampling the display backbuffer.
    public AccessibilityPass(Material value)
    {
        material = value;
        requiresIntermediateTexture = true;
        renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    // Copy the camera image through the filter, then hand the filtered image back to URP.
    public override void RecordRenderGraph(RenderGraph graph, ContextContainer context)
    {
        var resources = context.Get<UniversalResourceData>();

        // A backbuffer cannot be read as a normal texture; skip this unsupported target.
        if (resources.isActiveTargetBackBuffer)
            return;

        // Match the source image's dimensions/format without clearing the new texture first.
        var source = resources.activeColorTexture;
        var desc = graph.GetTextureDesc(source);
        desc.name = "Color assistance";
        desc.clearBuffer = false;
        var destination = graph.CreateTexture(desc);

        // The material's fragment shader performs the per-pixel color correction.
        graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0),
                          "Color assistance");
        resources.cameraColor = destination;
    }
}
