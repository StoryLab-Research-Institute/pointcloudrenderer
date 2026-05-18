using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    // Attach to any GameObject in an Android/Quest scene.
    // On Start(), detects Quest 2 vs Quest 3 and calls SetVariantByName on every
    // PointCloudRenderer in the scene, selecting the first variant whose name contains
    // the detected device string.
    //
    // Name your variants to include "Quest 2" or "Quest 3" for this to work automatically.
    // If no matching variant is found on a renderer the active index is left unchanged.
    [AddComponentMenu("StoryLab Point Cloud/Quest Variant Selector")]
    public class QuestVariantSelector : MonoBehaviour
    {
        [Tooltip("Variant name fragment to select on Quest 3 (e.g. \"Quest 3\").")]
        public string Quest3VariantName = "Quest 3";

        [Tooltip("Variant name fragment to select on Quest 2 (e.g. \"Quest 2\").")]
        public string Quest2VariantName = "Quest 2";

        private void Start()
        {
#if !UNITY_ANDROID
            // No-op on non-Android builds — component can safely live in a shared scene.
            return;
#else
            string deviceModel = SystemInfo.deviceModel;
            string variantName;

            if (deviceModel.Contains("Quest 3"))
                variantName = Quest3VariantName;
            else if (deviceModel.Contains("Quest 2") || deviceModel.Contains("Quest2"))
                variantName = Quest2VariantName;
            else
            {
                // Unknown Quest generation — fall back to index 0 (highest quality entry
                // that survived build stripping for this platform).
                Debug.LogWarning($"[QuestVariantSelector] Unrecognised device '{deviceModel}'. " +
                                 "Leaving renderers at their default variant (index 0).");
                return;
            }

            var renderers = FindObjectsByType<PointCloudRenderer>(FindObjectsSortMode.None);
            int matched = 0;
            foreach (var r in renderers)
            {
                if (r.SetVariantByName(variantName))
                    matched++;
            }

            Debug.Log($"[QuestVariantSelector] Device '{deviceModel}': " +
                      $"selected variant '{variantName}' on {matched}/{renderers.Length} renderer(s).");
#endif
        }
    }
}
