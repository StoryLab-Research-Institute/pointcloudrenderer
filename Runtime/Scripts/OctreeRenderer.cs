using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace StoryLabResearch.PointCloud
{
    [ExecuteAlways]
    public class OctreeRenderer : MonoBehaviour
    {
        [SerializeField] private OctreeAsset _asset;
        [SerializeField] private Material _material;
        [SerializeField] private int PointBudget = 2_000_000;

        [Tooltip("Stop refining a node when its angular size (extents / distance / tan(halfFov)) drops below this. " +
                 "Higher values cull distant nodes more aggressively. Try 0.1–0.5. Default 0.2.")]
        [SerializeField] private float ScreenErrorThreshold = 0.2f;

        [Tooltip("Skip drawing nodes whose screen error is below this fraction of ScreenErrorThreshold. " +
                 "Culls sub-pixel nodes that contribute overdraw with no visual benefit. 0 = disabled.")]
        [SerializeField] private float MinDrawErrorFraction = 0.1f;

        [Tooltip("Global point size multiplier. Tune visually — the per-node scale is derived from the subsampling ratio, so this just sets the overall base size.")]
        [SerializeField] private float PointSizeScale = 1.0f;

        [Tooltip("In edit mode, follow the scene view camera instead of Camera.main.")]
        [SerializeField] private bool UseSceneCameraInEditMode = true;

        [Tooltip("Reduces LOD detail towards the screen periphery, concentrating the point budget near the gaze point. " +
                 "0 = disabled. 1 = moderate. Higher values = more aggressive peripheral culling.")]
        [SerializeField] private float FoveationStrength = 0f;

        [Tooltip("Nodes closer than this distance (world units) are never penalised by foveation, regardless of screen position.")]
        [SerializeField] private float FoveationNearDistance = 5f;

        private static readonly int PropLodSizeScale   = Shader.PropertyToID("_LodSizeScale");
        private static readonly int PropActiveOctantMask = Shader.PropertyToID("_ActiveOctantMask");

        private readonly List<NodeDrawable> _activeDrawables = new();
        private readonly List<NodeDrawable> _pendingDrawables = new();
        private readonly List<QueueEntry> _queue = new();
        private readonly Dictionary<OctreeNode, float> _selectedNodes = new();

        private void OnEnable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            DeregisterAll();
            _asset?.Unload();
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (this == null || Application.isPlaying) return;
            Update();
        }
#endif

        private void Update()
        {
            _asset?.Load();
            if (_asset?.Root == null) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            _pendingDrawables.Clear();
            SelectNodes(cam);

            DeregisterAll();
            foreach (var d in _pendingDrawables)
            {
                PointCloudRenderFeature.PointCloudRenderPass.Register(d);
                _activeDrawables.Add(d);
            }
        }

        private Camera ResolveCamera()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && UseSceneCameraInEditMode)
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv != null) return sv.camera;
            }
#endif
            return Camera.main;
        }

        private void SelectNodes(Camera cam)
        {
            var localToWorld = transform.localToWorldMatrix;
            float halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

            // Priority queue: enqueue nodes sorted by screen error (largest first).
            // Pop nodes one at a time; if within budget and above error threshold, expand children.
            // Otherwise, mark node as selected (frontier).
            _queue.Clear();
            _selectedNodes.Clear();
            Enqueue(_asset.Root, null, cam, halfFovTan, frustumPlanes, localToWorld);

            int remaining = PointBudget;
            int queueHead = 0;
            while (queueHead < _queue.Count)
            {
                // Find the highest-error entry from queueHead onward (linear scan, no alloc).
                int bestIdx = queueHead;
                for (int j = queueHead + 1; j < _queue.Count; j++)
                    if (_queue[j].ScreenError > _queue[bestIdx].ScreenError) bestIdx = j;

                var entry = _queue[bestIdx];
                _queue[bestIdx] = _queue[queueHead];
                queueHead++;

                var node = entry.Node;
                if (!node.IsLoaded)
                {
                    // Fall back to the nearest loaded ancestor so there's no visible hole.
                    if (entry.Parent != null && entry.Parent.IsLoaded)
                        _selectedNodes[entry.Parent] = entry.ScreenError;
                    continue;
                }

                float effectiveThreshold = ScreenErrorThreshold;
                if (FoveationStrength > 0f)
                {
                    float nodeDist = Vector3.Distance(cam.transform.position, entry.WorldCenter);
                    float distBlend = Mathf.Clamp01((nodeDist - FoveationNearDistance) / FoveationNearDistance);
                    if (distBlend > 0f)
                    {
                        var vp = cam.WorldToViewportPoint(entry.WorldCenter);
                        // vp.z < 0 means the point is behind the camera — treat as on-axis so no penalty.
                        if (vp.z > 0f)
                        {
                            float dx = Mathf.Clamp(vp.x, 0f, 1f) - 0.5f;
                            float dy = Mathf.Clamp(vp.y, 0f, 1f) - 0.5f;
                            float r2 = dx * dx + dy * dy;
                            float thresholdScale = 1f + FoveationStrength * r2 * 4f;
                            effectiveThreshold *= Mathf.Lerp(1f, thresholdScale, distBlend);
                        }
                    }
                }
                bool tooSmall = entry.ScreenError < effectiveThreshold;
                bool outOfBudget = remaining <= 0;

                if (tooSmall || outOfBudget || node.IsLeaf)
                {
                    _selectedNodes[node] = entry.ScreenError;
                    remaining -= node.TotalPointCount;
                }
                else
                {
                    for (int o = 0; o < 8; o++)
                    {
                        if (node.Children[o] != null)
                            Enqueue(node.Children[o], node, cam, halfFovTan, frustumPlanes, localToWorld);
                    }
                }
            }

            // Emit one draw call per selected node.
            float minDrawError = ScreenErrorThreshold * MinDrawErrorFraction;
            foreach (var kvp in _selectedNodes)
            {
                var node = kvp.Key;
                float screenError = kvp.Value;

                if (node.TotalPointCount == 0) continue;

                // Skip nodes too small to contribute visibly — reduces overdraw from tiny distant nodes.
                if (MinDrawErrorFraction > 0 && screenError < minDrawError) continue;

                // Build active octant bitmask: skip octants whose child is also selected.
                int activeMask = 0;
                int totalKept = 0;
                int totalOrig = 0;
                for (int o = 0; o < 8; o++)
                {
                    if (node.OctantPointCounts[o] == 0) continue;
                    bool childSelected = !node.IsLeaf
                        && node.Children[o] != null
                        && _selectedNodes.ContainsKey(node.Children[o]);
                    if (!childSelected)
                    {
                        activeMask |= (1 << o);
                        totalKept += node.OctantPointCounts[o];
                        totalOrig += node.OctantOriginalCounts[o];
                    }
                }

                if (activeMask == 0) continue;

                // sqrt(ratio) converts point-count ratio to linear size ratio (area ∝ size²).
                float ratio = totalOrig > 0 ? (float)totalOrig / totalKept : 1f;
                float lodScale = Mathf.Sqrt(ratio) * PointSizeScale;

                _pendingDrawables.Add(new NodeDrawable(node, _material, localToWorld, lodScale, activeMask));
            }
        }

        private void Enqueue(OctreeNode node, OctreeNode parent, Camera cam, float halfFovTan,
            Plane[] frustumPlanes, Matrix4x4 localToWorld)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds)) return;

            float dist = Vector3.Distance(cam.transform.position, worldBounds.center);
            float rawError = dist > 0.001f
                ? worldBounds.extents.magnitude / dist / halfFovTan
                : float.MaxValue;

            _queue.Add(new QueueEntry(node, parent, worldBounds.center, rawError));
        }

        private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 m)
        {
            var center = m.MultiplyPoint3x4(localBounds.center);
            var extents = localBounds.extents;
            var newExtents = new Vector3(
                Mathf.Abs(m.m00) * extents.x + Mathf.Abs(m.m01) * extents.y + Mathf.Abs(m.m02) * extents.z,
                Mathf.Abs(m.m10) * extents.x + Mathf.Abs(m.m11) * extents.y + Mathf.Abs(m.m12) * extents.z,
                Mathf.Abs(m.m20) * extents.x + Mathf.Abs(m.m21) * extents.y + Mathf.Abs(m.m22) * extents.z);
            return new Bounds(center, newExtents * 2f);
        }

        private void DeregisterAll()
        {
            foreach (var d in _activeDrawables)
                PointCloudRenderFeature.PointCloudRenderPass.Deregister(d);
            _activeDrawables.Clear();
        }

        private readonly struct QueueEntry
        {
            public readonly OctreeNode Node;
            public readonly OctreeNode Parent;   // fallback if Node isn't loaded yet
            public readonly Vector3 WorldCenter; // world-space bounds centre, for foveation projection
            public readonly float ScreenError;
            public QueueEntry(OctreeNode node, OctreeNode parent, Vector3 worldCenter, float screenError)
            {
                Node = node; Parent = parent; WorldCenter = worldCenter; ScreenError = screenError;
            }
        }

        private sealed class NodeDrawable : IPointCloudDrawable
        {
            private readonly OctreeNode _node;
            private readonly Material _material;
            private readonly Matrix4x4 _localToWorld;
            private readonly float _lodSizeScale;
            private readonly int _activeMask;

            public NodeDrawable(OctreeNode node, Material material,
                Matrix4x4 localToWorld, float lodSizeScale, int activeMask)
            {
                _node = node;
                _material = material;
                _localToWorld = localToWorld;
                _lodSizeScale = lodSizeScale;
                _activeMask = activeMask;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                var block = _node.PropertyBlock;
                block.SetFloat(PropLodSizeScale, _lodSizeScale);
                block.SetInt(PropActiveOctantMask, _activeMask);
                cmd.DrawProcedural(_localToWorld, _material, 0,
                    MeshTopology.Triangles, _node.TotalPointCount * 6, 1, block);
            }
        }
    }
}
