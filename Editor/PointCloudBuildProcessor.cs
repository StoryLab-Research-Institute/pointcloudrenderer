using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryLabResearch.PointCloud
{
    // Strips BVH assets and materials for variants that don't target the current build platform.
    // IProcessSceneWithReport operates on the scene data Unity is preparing for the build output —
    // the original scene on disk is never touched, so no save prompt and no restore step needed.
    class PointCloudBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return; // not a build (e.g. play mode scene processing)

            var platformFlag = PlatformFlagFor(report.summary.platform);

            foreach (var renderer in FindAll<PointCloudRenderer>(scene))
            {
                var entries = renderer.VariantsForBuild;
                if (entries == null || entries.Length == 0) continue;

                bool anyIncluded = false;
                foreach (var entry in entries)
                    if (entry.Variant != null && (entry.Variant.Platforms & platformFlag) != 0)
                        { anyIncluded = true; break; }

                if (!anyIncluded)
                    Debug.LogWarning($"[PointCloudBuildProcessor] '{GetPath(renderer.gameObject)}' " +
                                     $"has no variants targeting {report.summary.platform} — " +
                                     "it will render nothing at runtime.");

                bool anyStripped = false;
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    if (entry.Variant == null) continue;
                    if ((entry.Variant.Platforms & platformFlag) != 0) continue;

                    entries[i] = new PointCloudRenderer.VariantEntry
                    {
                        Variant          = entry.Variant,
                        Asset            = null,
                        ResolvedMaterial = null,
                    };
                    anyStripped = true;
                }

                if (anyStripped)
                    renderer.VariantsForBuild = entries;
            }
        }

        // ----- Helpers -----

        private static EPointCloudPlatform PlatformFlagFor(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:                    return EPointCloudPlatform.Android;
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                case BuildTarget.StandaloneOSX:             return EPointCloudPlatform.Standalone;
                default:
                    Debug.LogWarning($"[PointCloudBuildProcessor] Unrecognised build target " +
                                     $"'{target}' — including all variants.");
                    return (EPointCloudPlatform)~0;
            }
        }

        private static IEnumerable<T> FindAll<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var c in root.GetComponentsInChildren<T>(true))
                    yield return c;
        }

        private static string GetPath(GameObject go)
        {
            var path = go.name;
            var t    = go.transform.parent;
            while (t != null) { path = t.name + "/" + path; t = t.parent; }
            return path;
        }
    }
}
