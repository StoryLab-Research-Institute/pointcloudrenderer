using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(
        fileName = "PointCloudRenderProperties",
        menuName = "Point Cloud/Render Properties")]
    public class PointCloudRenderProperties : ScriptableObject
    {
        [Tooltip("Controls LOD traversal depth. Higher values expand the octree further before stopping, " +
                 "increasing detail and triangle count. Actual triangles rendered will exceed this value — " +
                 "tune alongside ScreenErrorThreshold to hit your target frame budget on target hardware.")]
        public int PointBudget = 2_000_000;

        [Tooltip("Stop refining a node when its angular size drops below this. " +
                 "Higher values cull distant nodes more aggressively. Try 0.1–0.5.")]
        public float ScreenErrorThreshold = 0.2f;

        [Tooltip("Skip drawing nodes whose screen error is below this fraction of ScreenErrorThreshold. " +
                 "Culls sub-pixel nodes that contribute overdraw with no visual benefit. 0 = disabled.")]
        public float MinDrawErrorFraction = 0.1f;

        [Tooltip("Global point size multiplier on top of the material's _PointSize. " +
                 "The per-node scale is derived from the subsampling ratio.")]
        public float PointSizeScale = 1.0f;

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
                 "Nodes whose projected extent overlaps this zone are never penalised. " +
                 "0 = effect starts at centre. 0.2 = reasonable protected zone.")]
        [Range(0f, 1f)]
        public float FoveationInnerRadius = 0.2f;

    }
}
