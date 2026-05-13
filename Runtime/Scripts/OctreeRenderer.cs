using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
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
        [SerializeField] private float ScreenErrorThreshold = 0.01f;

        [Tooltip("Global point size multiplier. Tune visually — the per-node scale is derived from the subsampling ratio, so this just sets the overall base size.")]
        [SerializeField] private float PointSizeScale = 1.0f;

        [Tooltip("In edit mode, follow the scene view camera instead of Camera.main.")]
        [SerializeField] private bool UseSceneCameraInEditMode = true;

        private readonly List<NodeDrawable> _activeDrawables = new();
        private readonly List<NodeDrawable> _pendingDrawables = new();
        private readonly List<QueueEntry> _queue = new();
        private readonly HashSet<OctreeNode> _selectedNodes = new();

        private void OnEnable() { }

        private void OnDisable()
        {
            DeregisterAll();
            _asset?.Unload();
        }

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

        public void PrepareView(Vector3 worldPosition, Quaternion orientation)
        {
            var cam = ResolveCamera();
            _asset.PrepareView(worldPosition, orientation, cam != null ? cam.fieldOfView : 60f);
        }

        private void SelectNodes(Camera cam)
        {
            var localToWorld = transform.localToWorldMatrix;
            var camPos = cam.transform.position;
            float halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

            // Priority queue: enqueue nodes sorted by screen error (largest first).
            // Pop nodes one at a time; if within budget and above error threshold, expand children.
            // Otherwise, mark node as selected (frontier).
            _queue.Clear();
            _selectedNodes.Clear();
            Enqueue(_asset.Root, camPos, halfFovTan, frustumPlanes, localToWorld);

            int remaining = PointBudget;
            while (_queue.Count > 0)
            {
                var entry = DequeueMax();
                var node = entry.Node;
                if (!node.IsLoaded) continue;

                bool tooSmall = entry.ScreenError < ScreenErrorThreshold;
                bool outOfBudget = remaining <= 0;

                if (tooSmall || outOfBudget || node.IsLeaf)
                {
                    _selectedNodes.Add(node);
                    remaining -= node.TotalPointCount;
                }
                else
                {
                    // Expand: charge this node's own points (level sample), recurse into children.
                    remaining -= node.TotalPointCount;
                    for (int o = 0; o < 8; o++)
                    {
                        if (node.Children[o] != null)
                            Enqueue(node.Children[o], camPos, halfFovTan, frustumPlanes, localToWorld);
                    }
                }
            }

            // Emit one draw call per selected node.
            // lodScale is derived from the subsampling ratio: sqrt(originalCount / keptCount).
            // This gives each node's points enough screen area to cover what the original density would.
            foreach (var node in _selectedNodes)
            {
                if (node.TotalPointCount == 0) continue;

                // Build active octant bitmask and accumulate subsampling ratio across active octants.
                int activeMask = 0;
                int totalKept = 0;
                int totalOrig = 0;
                for (int o = 0; o < 8; o++)
                {
                    if (node.OctantPointCounts[o] == 0) continue;
                    bool childSelected = !node.IsLeaf
                        && node.Children[o] != null
                        && _selectedNodes.Contains(node.Children[o]);
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

        private QueueEntry DequeueMax()
        {
            // Queue is kept sorted descending by ScreenError, so index 0 is largest.
            var entry = _queue[0];
            _queue.RemoveAt(0);
            return entry;
        }

        private void Enqueue(OctreeNode node, Vector3 camPos, float halfFovTan,
            Plane[] frustumPlanes, Matrix4x4 localToWorld)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds)) return;

            float dist = Vector3.Distance(camPos, worldBounds.center);
            float error = dist > 0.001f
                ? worldBounds.extents.magnitude / dist / halfFovTan
                : float.MaxValue;

            // Insert sorted descending.
            int i = 0;
            while (i < _queue.Count && _queue[i].ScreenError > error) i++;
            _queue.Insert(i, new QueueEntry(node, error));
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
            public readonly float ScreenError;
            public QueueEntry(OctreeNode node, float screenError) { Node = node; ScreenError = screenError; }
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

            public Bounds WorldBounds => _node.Bounds;

            public void Draw(CommandBuffer cmd)
            {
                var block = _node.PropertyBlock;
                block.SetFloat("_LodSizeScale", _lodSizeScale);
                block.SetInt("_ActiveOctantMask", _activeMask);
                cmd.DrawProcedural(_localToWorld, _material, 0,
                    MeshTopology.Triangles, _node.TotalPointCount * 6, 1, block);
            }
        }
    }
}
