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
                 "0 = disabled. Higher values = more aggressive peripheral culling.")]
        [SerializeField] private float FoveationStrength = 0f;

        [Tooltip("Normalised viewport radius inside which foveation has no effect (full fidelity zone). " +
                 "0 = no inner window, penalty starts at centre. 0.5 = half the screen width. Default 0.1.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float FoveationInnerRadius = 0.1f;

        [Tooltip("Normalised viewport radius at which the full FoveationStrength penalty is reached. " +
                 "Must be >= FoveationInnerRadius. 0.5 = screen edge. Default 0.5.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float FoveationOuterRadius = 0.5f;


        private static readonly int PropActiveOctantMask = Shader.PropertyToID("_ActiveOctantMask");
        private static readonly int PropScreenExtent     = Shader.PropertyToID("_ScreenExtent");
        private static readonly int PropPointSize        = Shader.PropertyToID("_PointSize");

        private readonly List<NodeDrawable> _activeDrawables  = new();
        private readonly List<NodeDrawable> _pendingDrawables = new();

        // Min-heap ordered by ascending ScreenError (we pop the max, so we invert on insert).
        // We use a flat List<QueueEntry> as a max-heap (largest ScreenError = highest priority).
        private readonly List<QueueEntry> _heap = new();

        private readonly Dictionary<OctreeNode, float> _selectedNodes = new();

        // Reused per-frame allocations.
        private readonly Plane[] _frustumPlanes = new Plane[6];

        // NodeDrawable pool to avoid per-frame GC alloc.
        private readonly List<NodeDrawable> _drawablePool = new();
        private int _poolCursor;

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
            _poolCursor = 0;
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
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Precompute screen extent factor: abs(P._11_22) * _PointSize * 0.5
            // lodScale is per-node and multiplied in below, so fold everything else
            // into a base extent and scale it per node before passing as _ScreenExtent.
            var proj = cam.projectionMatrix;
            float pointSize = _material.GetFloat(PropPointSize) * PointSizeScale * 0.5f;
            var screenExtentBase = new Vector2(
                Mathf.Abs(proj.m00) * pointSize,
                Mathf.Abs(proj.m11) * pointSize);

            // Max-heap: enqueue nodes sorted by screen error (largest first).
            // Pop nodes one at a time; if within budget and above error threshold, expand children.
            // Otherwise, mark node as selected (frontier).
            _heap.Clear();
            _selectedNodes.Clear();
            HeapPush(_asset.Root, null, cam, halfFovTan, localToWorld);

            int remaining = PointBudget;
            while (_heap.Count > 0)
            {
                var entry = HeapPop();

                var node = entry.Node;
                if (!node.IsLoaded)
                {
                    // Fall back to the nearest loaded ancestor so there's no visible hole.
                    if (entry.Parent != null && entry.Parent.IsLoaded)
                        _selectedNodes[entry.Parent] = entry.ScreenError;
                    continue;
                }

                // HeapError = ScreenError / foveationScale, so comparing against the base threshold
                // is equivalent to comparing ScreenError against threshold * foveationScale.
                bool tooSmall    = entry.HeapError < ScreenErrorThreshold;
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
                            HeapPush(node.Children[o], node, cam, halfFovTan, localToWorld);
                    }
                }
            }

            // Emit one draw call per selected node.
            float minDrawError = ScreenErrorThreshold * MinDrawErrorFraction;
            foreach (var kvp in _selectedNodes)
            {
                var node       = kvp.Key;
                float screenError = kvp.Value;

                if (node.TotalPointCount == 0) continue;

                // Skip nodes too small to contribute visibly — reduces overdraw from tiny distant nodes.
                if (MinDrawErrorFraction > 0 && screenError < minDrawError) continue;

                // Build active octant bitmask: skip octants whose child is also selected.
                int activeMask = 0;
                int totalKept  = 0;
                int totalOrig  = 0;
                for (int o = 0; o < 8; o++)
                {
                    if (node.OctantPointCounts[o] == 0) continue;
                    bool childSelected = !node.IsLeaf
                        && node.Children[o] != null
                        && _selectedNodes.ContainsKey(node.Children[o]);
                    if (!childSelected)
                    {
                        activeMask |= (1 << o);
                        totalKept  += node.OctantPointCounts[o];
                        totalOrig  += node.OctantOriginalCounts[o];
                    }
                }

                if (activeMask == 0) continue;

                // sqrt(ratio) converts point-count ratio to linear size ratio (area ∝ size²).
                float ratio    = totalOrig > 0 ? (float)totalOrig / totalKept : 1f;
                float lodScale = Mathf.Sqrt(ratio);

                // Scale the precomputed base extent by this node's lodScale.
                var screenExtent = screenExtentBase * lodScale;

                _pendingDrawables.Add(GetDrawable(node, _material, localToWorld, activeMask, totalKept, screenExtent));
            }
        }

        // ----- Foveation scale -----

        // Returns the foveation threshold multiplier (>= 1) for a node given its viewport-space
        // centre and its raw screen error (used to shrink the projected centre toward screen centre).
        private float FoveationScale(Vector3 worldCenter, float rawError, Camera cam, float halfFovTan)
        {
            if (FoveationStrength <= 0f) return 1f;
            var vp = cam.WorldToViewportPoint(worldCenter);
            if (vp.z <= 0f) return 1f; // behind camera — no penalty

            float screenHalfH = rawError * halfFovTan * 0.5f / cam.aspect;
            float screenHalfV = rawError * halfFovTan * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vp.x, 0f, 1f) - 0.5f) - screenHalfH);
            float dy = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vp.y, 0f, 1f) - 0.5f) - screenHalfV) * cam.aspect;
            float r  = Mathf.Max(dx, dy);
            float outer = Mathf.Max(FoveationOuterRadius, FoveationInnerRadius + 0.001f);
            float t = Mathf.Clamp01((r - FoveationInnerRadius) / (outer - FoveationInnerRadius));
            t = t * t * (3f - 2f * t); // smoothstep
            return 1f + FoveationStrength * t;
        }

        // ----- Heap push/pop (max-heap on foveation-adjusted ScreenError) -----

        private void HeapPush(OctreeNode node, OctreeNode parent, Camera cam, float halfFovTan, Matrix4x4 localToWorld)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, worldBounds)) return;

            // Distance to nearest point on the AABB, so nodes the camera is inside or
            // behind don't get an inflated screen error and consume the entire point budget.
            var camPos = cam.transform.position;
            var closest = new Vector3(
                Mathf.Clamp(camPos.x, worldBounds.min.x, worldBounds.max.x),
                Mathf.Clamp(camPos.y, worldBounds.min.y, worldBounds.max.y),
                Mathf.Clamp(camPos.z, worldBounds.min.z, worldBounds.max.z));
            float dist     = Vector3.Distance(camPos, closest);
            float rawError = dist > 0.001f
                ? worldBounds.extents.magnitude / dist / halfFovTan
                : float.MaxValue;

            // Divide raw error by foveation scale so peripheral nodes sort lower in the heap
            // and get expanded after central nodes. This ensures budget runs out in the right order.
            float fovScale   = FoveationScale(worldBounds.center, rawError, cam, halfFovTan);
            float heapError  = rawError / fovScale;

            var entry = new QueueEntry(node, parent, worldBounds.center, rawError, heapError);
            _heap.Add(entry);
            // Sift up on HeapError.
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent_ = (i - 1) >> 1;
                if (_heap[parent_].HeapError >= _heap[i].HeapError) break;
                (_heap[i], _heap[parent_]) = (_heap[parent_], _heap[i]);
                i = parent_;
            }
        }

        private QueueEntry HeapPop()
        {
            var top = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            // Sift down on HeapError.
            int i = 0;
            int count = _heap.Count;
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int largest = i;
                if (l < count && _heap[l].HeapError > _heap[largest].HeapError) largest = l;
                if (r < count && _heap[r].HeapError > _heap[largest].HeapError) largest = r;
                if (largest == i) break;
                (_heap[i], _heap[largest]) = (_heap[largest], _heap[i]);
                i = largest;
            }
            return top;
        }

        // ----- NodeDrawable pool -----

        private NodeDrawable GetDrawable(OctreeNode node, Material material,
            Matrix4x4 localToWorld, int activeMask, int vertCount, Vector2 screenExtent)
        {
            NodeDrawable d;
            if (_poolCursor < _drawablePool.Count)
            {
                d = _drawablePool[_poolCursor++];
            }
            else
            {
                d = new NodeDrawable();
                _drawablePool.Add(d);
                _poolCursor++;
            }
            d.Set(node, material, localToWorld, activeMask, vertCount, screenExtent);
            return d;
        }

        // ----- Utilities -----

        private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 m)
        {
            var center  = m.MultiplyPoint3x4(localBounds.center);
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

        // ----- Inner types -----

        private readonly struct QueueEntry
        {
            public readonly OctreeNode Node;
            public readonly OctreeNode Parent;   // fallback if Node isn't loaded yet
            public readonly Vector3 WorldCenter; // world-space bounds centre
            public readonly float ScreenError;   // raw geometric error, used for LOD size scale
            public readonly float HeapError;     // ScreenError / foveationScale, used for heap ordering and stop test
            public QueueEntry(OctreeNode node, OctreeNode parent, Vector3 worldCenter, float screenError, float heapError)
            {
                Node = node; Parent = parent; WorldCenter = worldCenter; ScreenError = screenError; HeapError = heapError;
            }
        }

        // Mutable class so it can be pooled and reused across frames.
        private sealed class NodeDrawable : IPointCloudDrawable
        {
            private OctreeNode _node;
            private Material   _material;
            private Matrix4x4  _localToWorld;
            private int        _activeMask;
            private int        _vertCount;    // totalKept * 4 (Quads)
            private Vector2    _screenExtent; // precomputed: abs(P._11_22) * _PointSize * lodScale * 0.5

            public void Set(OctreeNode node, Material material,
                Matrix4x4 localToWorld, int activeMask, int vertCount, Vector2 screenExtent)
            {
                _node         = node;
                _material     = material;
                _localToWorld = localToWorld;
                _activeMask   = activeMask;
                _vertCount    = vertCount * 4;
                _screenExtent = screenExtent;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                var block = _node.PropertyBlock;
                block.SetInt(PropActiveOctantMask, _activeMask);
                block.SetVector(PropScreenExtent,  new Vector4(_screenExtent.x, _screenExtent.y, 0, 0));
                cmd.DrawProcedural(_localToWorld, _material, 0,
                    MeshTopology.Quads, _vertCount, 1, block);
            }
        }
    }
}
