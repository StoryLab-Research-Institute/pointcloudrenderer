using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StoryLabResearch.PointCloud
{
    // Before a build: null out the asset and render-property references for the platform tier
    // that won't be used, so Unity's build pipeline doesn't pull the unused .bin into the build.
    // After the build: restore all references from a temp record file.
    //
    // Android builds strip Quality. All other platforms strip Performance.
    class PointCloudBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private static readonly string RecordPath =
            Path.Combine(Application.temporaryCachePath, "PointCloudBuildStrip.json");

        [Serializable]
        private class RendererRecord
        {
            public string scenePath;
            public string gameObjectPath;
            // Render property GUIDs (PointCloudRenderProperties SOs)
            public string qualityRenderGuid;
            public string performanceRenderGuid;
            // Asset GUIDs (BVHAsset sub-assets — identified by asset path + name)
            public string qualityAssetPath;
            public string qualityAssetName;
            public string performanceAssetPath;
            public string performanceAssetName;
        }

        [Serializable]
        private class StripRecord
        {
            public bool strippedQuality;  // true = stripped Quality, false = stripped Performance
            public List<RendererRecord> renderers = new List<RendererRecord>();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            bool stripQuality = report.summary.platform == BuildTarget.Android;
            var record = new StripRecord { strippedQuality = stripQuality };
            var dirty = new HashSet<UnityEngine.Object>();

            foreach (var (scene, scenePath) in OpenBuildScenes())
            {
                foreach (var renderer in FindAll<PointCloudRenderer>(scene))
                {
                    var props  = renderer.SharedRenderPropertiesForBuild;
                    var assets = renderer.AssetsForBuild;

                    var qualityRenderAsset     = props.Quality.isSet     ? props.Quality.asset     : null;
                    var performanceRenderAsset = props.Performance.isSet ? props.Performance.asset : null;
                    var qualityAsset          = assets.Quality;
                    var performanceAsset      = assets.Performance;

                    bool hasQuality     = qualityRenderAsset != null || qualityAsset != null;
                    bool hasPerformance = performanceRenderAsset != null || performanceAsset != null;

                    if (stripQuality && hasQuality)
                    {
                        record.renderers.Add(new RendererRecord
                        {
                            scenePath              = scenePath,
                            gameObjectPath         = GetPath(renderer.gameObject),
                            qualityRenderGuid      = GuidOf(qualityRenderAsset),
                            performanceRenderGuid  = GuidOf(performanceRenderAsset),
                            qualityAssetPath       = qualityAsset != null ? AssetDatabase.GetAssetPath(qualityAsset) : "",
                            qualityAssetName       = qualityAsset != null ? qualityAsset.name : "",
                            performanceAssetPath   = performanceAsset != null ? AssetDatabase.GetAssetPath(performanceAsset) : "",
                            performanceAssetName   = performanceAsset != null ? performanceAsset.name : "",
                        });

                        renderer.SharedRenderPropertiesForBuild = new PerPlatformRenderProperties
                        {
                            Quality     = default,
                            Performance = props.Performance,
                        };
                        renderer.AssetsForBuild = new PerPlatformAssets
                        {
                            Quality     = default,
                            Performance = assets.Performance,
                        };
                        dirty.Add(renderer);
                    }
                    else if (!stripQuality && hasPerformance)
                    {
                        record.renderers.Add(new RendererRecord
                        {
                            scenePath              = scenePath,
                            gameObjectPath         = GetPath(renderer.gameObject),
                            qualityRenderGuid      = GuidOf(qualityRenderAsset),
                            performanceRenderGuid  = GuidOf(performanceRenderAsset),
                            qualityAssetPath       = qualityAsset != null ? AssetDatabase.GetAssetPath(qualityAsset) : "",
                            qualityAssetName       = qualityAsset != null ? qualityAsset.name : "",
                            performanceAssetPath   = performanceAsset != null ? AssetDatabase.GetAssetPath(performanceAsset) : "",
                            performanceAssetName   = performanceAsset != null ? performanceAsset.name : "",
                        });

                        renderer.SharedRenderPropertiesForBuild = new PerPlatformRenderProperties
                        {
                            Quality     = props.Quality,
                            Performance = default,
                        };
                        renderer.AssetsForBuild = new PerPlatformAssets
                        {
                            Quality     = assets.Quality,
                            Performance = default,
                        };
                        dirty.Add(renderer);
                    }
                }
            }

            File.WriteAllText(RecordPath, JsonUtility.ToJson(record, true));

            foreach (var obj in dirty)
                EditorUtility.SetDirty(obj);
            AssetDatabase.SaveAssets();

            if (record.renderers.Count > 0)
                Debug.Log($"[PointCloudBuildProcessor] Stripped {record.renderers.Count} unused " +
                          $"{(stripQuality ? "Quality" : "Performance")} tier reference(s) " +
                          $"for {report.summary.platform} build.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!File.Exists(RecordPath)) return;

            var record = JsonUtility.FromJson<StripRecord>(File.ReadAllText(RecordPath));
            File.Delete(RecordPath);

            var dirty = new HashSet<UnityEngine.Object>();

            var sceneRenderers = new Dictionary<string, Dictionary<string, PointCloudRenderer>>();
            foreach (var (scene, scenePath) in OpenBuildScenes())
            {
                var byPath = new Dictionary<string, PointCloudRenderer>();
                foreach (var r in FindAll<PointCloudRenderer>(scene))
                    byPath[GetPath(r.gameObject)] = r;
                sceneRenderers[scenePath] = byPath;
            }

            foreach (var rec in record.renderers)
            {
                if (!sceneRenderers.TryGetValue(rec.scenePath, out var byPath)) continue;
                if (!byPath.TryGetValue(rec.gameObjectPath, out var renderer)) continue;

                if (record.strippedQuality)
                {
                    if (!string.IsNullOrEmpty(rec.qualityRenderGuid))
                    {
                        var so = LoadByGuid<PointCloudRenderProperties>(rec.qualityRenderGuid);
                        if (so != null) { renderer.RestoreQualityRenderProperties(so); dirty.Add(renderer); }
                    }
                    if (!string.IsNullOrEmpty(rec.qualityAssetPath))
                    {
                        var asset = LoadSubAsset<BVHAsset>(rec.qualityAssetPath, rec.qualityAssetName);
                        if (asset != null) { renderer.RestoreQualityAsset(asset); dirty.Add(renderer); }
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(rec.performanceRenderGuid))
                    {
                        var so = LoadByGuid<PointCloudRenderProperties>(rec.performanceRenderGuid);
                        if (so != null) { renderer.RestorePerformanceRenderProperties(so); dirty.Add(renderer); }
                    }
                    if (!string.IsNullOrEmpty(rec.performanceAssetPath))
                    {
                        var asset = LoadSubAsset<BVHAsset>(rec.performanceAssetPath, rec.performanceAssetName);
                        if (asset != null) { renderer.RestorePerformanceAsset(asset); dirty.Add(renderer); }
                    }
                }
            }

            foreach (var obj in dirty)
                EditorUtility.SetDirty(obj);
            AssetDatabase.SaveAssets();
        }

        // ----- Helpers -----

        private static IEnumerable<(Scene scene, string path)> OpenBuildScenes()
        {
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled) continue;
                var scene = EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
                yield return (scene, s.path);
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
            var t = go.transform.parent;
            while (t != null) { path = t.name + "/" + path; t = t.parent; }
            return path;
        }

        private static string GuidOf(UnityEngine.Object obj)
        {
            if (obj == null) return "";
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj));
        }

        private static T LoadByGuid<T>(string guid) where T : UnityEngine.Object
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static T LoadSubAsset<T>(string assetPath, string assetName) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (var obj in all)
                if (obj is T typed && obj.name == assetName)
                    return typed;
            return null;
        }
    }
}
