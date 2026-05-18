using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(
        fileName = "PointCloudRenderProperties",
        menuName = "StoryLab Point Cloud/Render Properties")]
    public class PointCloudRenderProperties : ScriptableObject
    {
        [Tooltip("Controls LOD traversal depth. Higher values expand the BVH further before stopping, " +
                 "increasing detail and point count. Actual points rendered will exceed this value — " +
                 "tune alongside ScreenErrorThreshold to hit your target frame budget on target hardware.")]
        public int PointBudget = 2_000_000;

        [Tooltip("Stop refining a node when its angular size drops below this. " +
                 "Higher values cull distant nodes more aggressively. Try 0.1–0.5.")]
        public float ScreenErrorThreshold = 0.2f;

        [Tooltip("Skip drawing nodes whose screen error is below this fraction of ScreenErrorThreshold. " +
                 "Culls sub-pixel nodes that contribute overdraw with no visual benefit. 0 = disabled.")]
        public float MinDrawErrorFraction = 0.1f;

        [Tooltip("Base point size in screen-space NDC half-extents. " +
                 "The per-node scale is derived from the subsampling ratio and multiplied by this value.")]
        public float PointSizeScale = 0.01f;

        [Tooltip("Use Unity's occlusion bake to skip nodes hidden behind scene geometry. " +
                 "Only active in player builds — the editor does not run the occlusion rasteriser.")]
        public bool OcclusionCullingEnabled = true;

        [Tooltip("Concentrate the point budget near the screen centre, reducing detail in peripheral vision.")]
        public bool FoveationEnabled = false;

        [Tooltip("How much to raise the LOD stopping threshold for peripheral nodes. " +
                 "A multiplier on ScreenErrorThreshold applied to nodes outside the inner radius. " +
                 "Higher values = more aggressive peripheral reduction. Try 16–64.")]
        [Range(1f, 64f)]
        public float FoveationStrength = 32f;

        [Tooltip("Viewport radius inside which foveation has no effect (full quality). " +
                 "Also controls the width of the transition ramp outside the boundary — " +
                 "t ramps from 0 to 1 over a zone of this width, so larger values give a " +
                 "softer edge that absorbs foveation-centre jitter. 0.2 = reasonable value.")]
        [Range(0f, 1f)]
        public float FoveationInnerRadius = 0.2f;

        [Tooltip("LOD hysteresis dead-band. A node selected last frame is kept until its error " +
                 "falls below ScreenErrorThreshold * (1 - LodHysteresis), requiring a larger " +
                 "change before collapsing back to the parent. Prevents jitter at LOD boundaries. " +
                 "0 = disabled. Try 0.15–0.3.")]
        [Range(0f, 0.5f)]
        public float LodHysteresis = 0.2f;

    }
}
