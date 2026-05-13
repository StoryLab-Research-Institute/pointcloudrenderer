using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(
        fileName = "PointCloudRenderProperties",
        menuName = "Point Cloud/Render Properties")]
    public class PointCloudRenderProperties : ScriptableObject
    {
        [Tooltip("Maximum number of points drawn per frame across all LOD levels.")]
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

        [Tooltip("How aggressively peripheral nodes are deprioritised. Exponential: " +
                 "a fully peripheral node is divided by (strength+1) in heap priority.")]
        public float FoveationStrength = 2f;

        [Tooltip("Normalised viewport radius inside which foveation has no effect. " +
                 "0 = penalty starts at centre. 0.5 = half the screen width.")]
        [Range(0f, 0.5f)]
        public float FoveationInnerRadius = 0.1f;

        [Tooltip("Normalised viewport radius at which the full FoveationStrength penalty is reached. " +
                 "Must be >= FoveationInnerRadius. 0.5 = screen edge.")]
        [Range(0f, 0.5f)]
        public float FoveationOuterRadius = 0.5f;
    }
}
