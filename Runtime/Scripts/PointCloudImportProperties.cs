using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    // Two quality tiers. Quality = desktop/high-end, Performance = Android/Quest/low-power.
    // Resolved at runtime via Application.platform, not compile-time #if, so both assets
    // are imported into the prefab but only the active one is ever loaded into memory.
    [System.Serializable]
    public struct PerPlatformRenderProperties
    {
        [Tooltip("Render properties for high-end platforms (PC, Mac, consoles).")]
        public LazyLoadReference<PointCloudRenderProperties> Quality;

        [Tooltip("Render properties for performance platforms (Android, Quest).")]
        public LazyLoadReference<PointCloudRenderProperties> Performance;

        public PointCloudRenderProperties Resolve(EPlatformTier tier)
        {
            var lazy = tier == EPlatformTier.Performance ? Performance : Quality;
            return lazy.isSet ? lazy.asset : null;
        }
    }

    // Which platform tier is active.
    public enum EPlatformTier { Quality, Performance }

    // Per-platform point cloud assets.
    // Both assets are sub-assets of the imported .ply prefab; only the active tier's
    // .bin data is ever loaded into memory (OctreeAsset.Load() is called on demand).
    [System.Serializable]
    public struct PerPlatformAssets
    {
        [Tooltip("Octree asset for high-end platforms (PC, Mac, consoles).")]
        public OctreeAsset Quality;

        [Tooltip("Octree asset for performance platforms (Android, Quest).")]
        public OctreeAsset Performance;

        public OctreeAsset Resolve(EPlatformTier tier) =>
            tier == EPlatformTier.Performance ? Performance : Quality;
    }

    // Per-platform materials — separate slots so each tier can have different point sizes etc.
    [System.Serializable]
    public struct PerPlatformMaterials
    {
        public Material Quality;
        public Material Performance;

        public Material Resolve(EPlatformTier tier) =>
            tier == EPlatformTier.Performance ? Performance : Quality;
    }

    // One import tier: everything needed to build and configure one platform variant.
    [System.Serializable]
    public struct PlatformImportTier
    {
        [Tooltip("Cull points closer together than this distance. 0 = disabled.")]
        public float MinPointSpacing;

        [Tooltip("Source material for this tier. Leave empty to use the pipeline default.")]
        public Material Material;

        [Tooltip("Shared: use the material as-is.\n" +
                 "Instantiated: embed a copy inside this asset.\n" +
                 "Extracted: write a standalone .mat file next to the .ply.")]
        public PointCloudImportProperties.EMaterialMode MaterialMode;

        [Tooltip("Render properties applied to the imported OctreeRenderer for this tier. " +
                 "Leave unset to use OctreeRenderer built-in defaults.")]
        public LazyLoadReference<PointCloudRenderProperties> RenderProperties;
    }

    [CreateAssetMenu(
        fileName = "PointCloudImportProperties",
        menuName = "Point Cloud/Import Properties")]
    public class PointCloudImportProperties : ScriptableObject
    {
        public enum EMaterialMode { Shared, Instantiated, Extracted }

        [Header("Quality Tier (PC / Mac / Consoles)")]
        public PlatformImportTier Quality;

        [Header("Performance Tier (Android / Quest)")]
        public PlatformImportTier Performance;
    }
}
