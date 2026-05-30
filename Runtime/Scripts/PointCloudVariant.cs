using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [System.Flags]
    public enum EPointCloudPlatform
    {
        Standalone = 1 << 0,
        Android    = 1 << 1,
    }

    [CreateAssetMenu(
        fileName = "PointCloudVariant",
        menuName = "StoryLab Point Cloud/Variant")]
    public class PointCloudVariant : ScriptableObject
    {
        [Tooltip("Display name used for runtime selection (e.g. \"PC\", \"Quest 3\", \"Quest 2\").")]
        public string VariantName;

        [Tooltip("Which build targets include this variant. Entries not matching the build target " +
                 "are stripped at build time.")]
        public EPointCloudPlatform Platforms = (EPointCloudPlatform)~0;

        // ---- Import settings (changing these requires reimporting the .ply) ----------------

        [Header("Import  —  changes require reimport")]
        [Tooltip("Cull points closer together than this distance at import time. 0 = disabled.")]
        public float MinPointSpacing;

        [Tooltip("Maximum side length of a BVH node before forcing a spatial split. 0 = disabled.")]
        public float MaxNodeSideLength;

        // ---- Material ----------------------------------------------------------------------

        [Header("Material")]
        [Tooltip("Material used when rendering this variant. Leave empty to use the pipeline default.")]
        public Material Material;

        [Tooltip("Shared: use the material as-is.\n" +
                 "Instantiated: embed a copy inside the imported asset.\n" +
                 "Extracted: write a standalone .mat file next to the .ply.")]
        public PointCloudImportProperties.EMaterialMode MaterialMode;

        // ---- Render settings (take effect immediately, no reimport needed) -----------------

        [Header("Render  —  no reimport needed")]
        [Tooltip("Controls LOD traversal depth. Higher values expand the BVH further before stopping, " +
                 "increasing detail and point count. Actual points rendered will exceed this value — " +
                 "tune alongside ScreenErrorThreshold to hit your target frame budget on target hardware.")]
        public int PointBudget = 2_000_000;

        [Tooltip("Stop refining a node when its angular size drops below this. " +
                 "Higher values cull distant nodes more aggressively. Try 0.05–0.5.")]
        public float ScreenErrorThreshold = 0.2f;

        [Tooltip("Skip drawing nodes whose screen error is below this fraction of ScreenErrorThreshold. " +
                 "Culls sub-pixel nodes that contribute overdraw with no visual benefit. 0 = disabled.")]
        public float MinDrawErrorFraction = 0.1f;

        [Tooltip("Base point size in screen-space NDC half-extents. " +
                 "The per-node scale is derived from the subsampling ratio and multiplied by this value.")]
        public float PointSizeScale = 0.02f;

[Tooltip("Concentrate the point budget near the screen centre, reducing detail in peripheral vision.")]
        public bool FoveationEnabled = false;

        [Tooltip("How much to raise the LOD stopping threshold for peripheral nodes. " +
                 "A multiplier on ScreenErrorThreshold applied to nodes outside the inner radius. " +
                 "Higher values = more aggressive peripheral reduction. Try 4–64.")]
        [Range(1f, 64f)]
        public float FoveationStrength = 8f;

        [Tooltip("Viewport radius inside which foveation has no effect (full quality). " +
                 "Also controls the width of the transition ramp outside the boundary — " +
                 "t ramps from 0 to 1 over a zone of this width, so larger values give a " +
                 "softer edge that absorbs foveation-centre jitter. 0.2 = reasonable value." +
                 "0.39f is practically imperceptible on Quest 2.")]
        [Range(0f, 1f)]
        public float FoveationInnerRadius = 0.39f;

        [Tooltip("LOD hysteresis dead-band. A node selected last frame is kept until its error " +
                 "falls below ScreenErrorThreshold * (1 - LodHysteresis), requiring a larger " +
                 "change before collapsing back to the parent. Prevents jitter at LOD boundaries. " +
                 "0 = disabled. Try 0.15–0.3.")]
        [Range(0f, 0.5f)]
        public float LodHysteresis = 0.3f;
    }
}
