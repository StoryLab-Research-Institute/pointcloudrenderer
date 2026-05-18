using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    [CreateAssetMenu(
        fileName = "PointCloudImportProperties",
        menuName = "StoryLab Point Cloud/Import Properties")]
    public class PointCloudImportProperties : ScriptableObject
    {
        public enum EMaterialMode { Shared, Instantiated, Extracted }

        [Tooltip("Ordered list of variants to build. Index 0 = highest quality / first priority. " +
                 "Each variant produces one BVH asset; at runtime the renderer picks the first " +
                 "variant whose Platforms flag matches the current build target.")]
        public PointCloudVariant[] Variants = System.Array.Empty<PointCloudVariant>();
    }
}
