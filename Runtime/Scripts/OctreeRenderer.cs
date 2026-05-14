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
        public enum EPlatformOverride { Auto, Quality, Performance }

        [SerializeField] private PerPlatformAssets _assets;
        [SerializeField] private PerPlatformMaterials _materials;

        [Tooltip("Auto: selects Quality on PC/Mac/consoles, Performance on Android/Quest. " +
                 "Override to force a specific tier for debugging.")]
        [SerializeField] private EPlatformOverride PlatformOverride = EPlatformOverride.Auto;

        [Tooltip("Per-platform render properties. The correct tier is selected automatically " +
                 "based on the current platform (or PlatformOverride).")]
        [SerializeField] private PerPlatformRenderProperties SharedRenderProperties;

        [Tooltip("In edit mode, follow the scene view camera instead of Camera.main.")]
        [SerializeField] private bool UseSceneCameraInEditMode = true;

        [Tooltip("Multiplier on the render properties PointBudget.")]
        public float PointBudgetMultiplier = 1f;

        [Tooltip("Multiplier on the render properties PointSizeScale.")]
        public float PointSizeMultiplier = 1f;

        [Tooltip("Multiplier on the render properties ScreenErrorThreshold. " +
                 "Increase to reduce detail (coarser LOD), decrease for more detail.")]
        public float ScreenErrorMultiplier = 1f;

        [Tooltip("Multiplier on the render properties MinDrawErrorFraction.")]
        public float MinDrawErrorMultiplier = 1f;

        // Resolves the active tier based on the override setting and runtime platform.
        private EPlatformTier ActiveTier
        {
            get
            {
                if (PlatformOverride == EPlatformOverride.Quality)     return EPlatformTier.Quality;
                if (PlatformOverride == EPlatformOverride.Performance) return EPlatformTier.Performance;
                return Application.platform == RuntimePlatform.Android
                    ? EPlatformTier.Performance
                    : EPlatformTier.Quality;
            }
        }

        private OctreeAsset ActiveAsset   => _assets.Resolve(ActiveTier);
        private Material    ActiveMaterial => _materials.Resolve(ActiveTier);

#if UNITY_EDITOR
        // Called by PlyImporter to wire up assets and materials on the prefab.
        public void SetImportedAsset(PerPlatformAssets assets, PerPlatformMaterials materials,
            PerPlatformRenderProperties renderProperties = default)
        {
            _assets                = assets;
            _materials             = materials;
            SharedRenderProperties = renderProperties;
        }

        // Build processor access — get/set the full struct and restore individual slots.
        public PerPlatformRenderProperties SharedRenderPropertiesForBuild
        {
            get => SharedRenderProperties;
            set => SharedRenderProperties = value;
        }

        public PerPlatformAssets AssetsForBuild
        {
            get => _assets;
            set => _assets = value;
        }

        public void RestoreQualityRenderProperties(PointCloudRenderProperties so)
        {
            SharedRenderProperties = new PerPlatformRenderProperties
            {
                Quality     = new LazyLoadReference<PointCloudRenderProperties>(so),
                Performance = SharedRenderProperties.Performance,
            };
        }

        public void RestorePerformanceRenderProperties(PointCloudRenderProperties so)
        {
            SharedRenderProperties = new PerPlatformRenderProperties
            {
                Quality     = SharedRenderProperties.Quality,
                Performance = new LazyLoadReference<PointCloudRenderProperties>(so),
            };
        }

        public void RestoreQualityAsset(OctreeAsset asset)
        {
            _assets = new PerPlatformAssets
            {
                Quality     = asset,
                Performance = _assets.Performance,
            };
        }

        public void RestorePerformanceAsset(OctreeAsset asset)
        {
            _assets = new PerPlatformAssets
            {
                Quality     = _assets.Quality,
                Performance = asset,
            };
        }
#endif

        private PointCloudRenderProperties ActiveProperties => SharedRenderProperties.Resolve(ActiveTier);

        // Fallback values used when no render properties SO is assigned.
        private const int   DefaultPointBudget          = 2_000_000;
        private const float DefaultScreenErrorThreshold = 0.2f;
        private const float DefaultMinDrawErrorFraction = 0.1f;
        private const float DefaultPointSizeScale       = 1.0f;

        private int   P_PointBudget          => Mathf.Max(1, Mathf.RoundToInt(
                                                    (ActiveProperties?.PointBudget ?? DefaultPointBudget)
                                                    * PointBudgetMultiplier));
        private float P_ScreenErrorThreshold => (ActiveProperties?.ScreenErrorThreshold ?? DefaultScreenErrorThreshold)
                                                    * ScreenErrorMultiplier;
        private float P_MinDrawErrorFraction => (ActiveProperties?.MinDrawErrorFraction ?? DefaultMinDrawErrorFraction)
                                                    * MinDrawErrorMultiplier;
        private float P_PointSizeScale       => (ActiveProperties?.PointSizeScale     ?? DefaultPointSizeScale)
                                                    * PointSizeMultiplier;
        private bool  P_OcclusionCulling     => ActiveProperties?.OcclusionCullingEnabled ?? true;
        private bool  P_FoveationEnabled     => ActiveProperties?.FoveationEnabled        ?? false;
        private float P_FoveationStrength    => ActiveProperties?.FoveationStrength       ?? 2f;
        private float P_FoveationInnerRadius => ActiveProperties?.FoveationInnerRadius    ?? 0.1f;
        private float P_FoveationOuterRadius => ActiveProperties?.FoveationOuterRadius    ?? 0.5f;


        private static readonly int PropActiveOctantMask = Shader.PropertyToID("_ActiveOctantMask");
        private static readonly int PropLodScale         = Shader.PropertyToID("_LodScale");
        private static readonly int PropPointSize        = Shader.PropertyToID("_PointSize");

        // CullingGroup state — rebuilt whenever the asset or camera changes.
        private CullingGroup      _cullingGroup;
        private OctreeNode[]      _cullingNodes;   // parallel to _cullingSpheres
        private BoundingSphere[]  _cullingSpheres;
        private int[]             _cullingResults; // reused buffer for QueryIndices
        private Camera            _cullingCamera;  // camera the group is currently bound to
        private Matrix4x4         _lastLocalToWorld;
        private readonly HashSet<OctreeNode> _occludedNodes = new();

        private readonly List<NodeDrawable> _activeDrawables  = new();
        private readonly List<NodeDrawable> _pendingDrawables = new();

        // Max-heap on HeapError — highest error (largest, most visible) node is popped first.
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
            DisposeCullingGroup();
            _assets.Quality?.Unload();
            _assets.Performance?.Unload();
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
            var asset = ActiveAsset;
            asset?.Load();
            if (asset?.Root == null) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            if (P_OcclusionCulling)
                RefreshCullingGroup(cam, asset);
            else
                DisposeCullingGroup();

            _pendingDrawables.Clear();
            _poolCursor = 0;
            SelectNodes(cam, asset);

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

        // ----- Occlusion culling (CullingGroup) -----

        private void RefreshCullingGroup(Camera cam, OctreeAsset asset)
        {
            var localToWorld = transform.localToWorldMatrix;

            // Rebuild if asset changed, camera changed, or group not yet created.
            bool needRebuild = _cullingGroup == null
                || _cullingCamera != cam
                || _cullingNodes == null
                || _cullingNodes.Length == 0;

            if (needRebuild)
            {
                DisposeCullingGroup();

                // Collect every node in the tree into a flat list.
                var nodes = new List<OctreeNode>();
                CollectNodes(asset.Root, nodes);

                _cullingNodes   = nodes.ToArray();
                _cullingSpheres = new BoundingSphere[_cullingNodes.Length];

                _cullingGroup = new CullingGroup();
                _cullingGroup.targetCamera = cam;
                _cullingGroup.SetBoundingSpheres(_cullingSpheres);
                _cullingGroup.SetBoundingSphereCount(_cullingSpheres.Length);
                _cullingResults = new int[_cullingSpheres.Length];
                _cullingCamera  = cam;
                _lastLocalToWorld = Matrix4x4.zero; // force sphere recompute below
            }

            if (localToWorld != _lastLocalToWorld)
            {
                for (int i = 0; i < _cullingNodes.Length; i++)
                {
                    var wb = TransformBounds(_cullingNodes[i].Bounds, localToWorld);
                    _cullingSpheres[i] = new BoundingSphere(wb.center, wb.extents.magnitude);
                }
                _cullingGroup.SetBoundingSpheres(_cullingSpheres);
                _lastLocalToWorld = localToWorld;
            }

            // Keep the distance reference point current — required for occlusion to activate.
            _cullingGroup.SetDistanceReferencePoint(cam.transform.position);

            // Query which spheres are not visible (failed frustum OR occlusion).
            // Frustum culling is already handled in HeapPush, so double-culling is harmless.
            // Note: occlusion culling is only active in player builds — in the editor only
            // frustum culling is applied by CullingGroup.
            _occludedNodes.Clear();
            int hiddenCount = _cullingGroup.QueryIndices(false, _cullingResults, 0);
            for (int i = 0; i < hiddenCount; i++)
                _occludedNodes.Add(_cullingNodes[_cullingResults[i]]);
        }

        private void DisposeCullingGroup()
        {
            _cullingGroup?.Dispose();
            _cullingGroup     = null;
            _cullingCamera    = null;
            _cullingNodes     = null;
            _cullingSpheres   = null;
            _cullingResults   = null;
            _lastLocalToWorld = Matrix4x4.zero;
            _occludedNodes.Clear();
        }

        private static void CollectNodes(OctreeNode root, List<OctreeNode> result)
        {
            if (root == null) return;
            var stack = new Stack<OctreeNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                result.Add(node);
                if (node.Children == null) continue;
                for (int o = 0; o < 8; o++)
                    if (node.Children[o] != null) stack.Push(node.Children[o]);
            }
        }

        private void SelectNodes(Camera cam, OctreeAsset asset)
        {
            var localToWorld = transform.localToWorldMatrix;
            float halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // _LodScale = _PointSize * lodScale * 0.5 — the projection factors are applied
            // per-eye in the shader using UNITY_MATRIX_P, so the CPU only supplies the scale.
            var mat = ActiveMaterial;
            float pointSizeBase = mat.GetFloat(PropPointSize) * P_PointSizeScale * 0.5f;

            // Max-heap: enqueue nodes sorted by screen error (largest first).
            // Pop nodes one at a time; if within budget and above error threshold, expand children.
            // Otherwise, mark node as selected (frontier).
            _heap.Clear();
            _selectedNodes.Clear();
            HeapPush(asset.Root, null, cam, halfFovTan, localToWorld);

            int remaining = P_PointBudget;
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
                bool tooSmall    = entry.HeapError < P_ScreenErrorThreshold;
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
            float minDrawError = P_ScreenErrorThreshold * P_MinDrawErrorFraction;
            foreach (var kvp in _selectedNodes)
            {
                var node       = kvp.Key;
                float screenError = kvp.Value;

                if (node.TotalPointCount == 0) continue;

                // Skip nodes too small to contribute visibly — reduces overdraw from tiny distant nodes.
                if (P_MinDrawErrorFraction > 0 && screenError < minDrawError) continue;

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
                float lodScale = Mathf.Sqrt(ratio) * pointSizeBase;

                _pendingDrawables.Add(GetDrawable(node, mat, localToWorld, activeMask, totalKept, lodScale));
            }
        }

        // ----- Foveation scale -----

        // Returns (FoveationStrength+1)^t where t is the node's normalised peripheral position [0,1].
        // Dividing rawError by this gives heapError: peripheral nodes are deprioritised exponentially,
        // so doubling strength compounds rather than adding. t uses the bounds-extent correction so a
        // large node covering screen centre isn't penalised because its centre projects off-axis.
        private float FoveationScale(Vector3 worldCenter, float rawError, Camera cam, float halfFovTan)
        {
            if (!P_FoveationEnabled || P_FoveationStrength <= 0f) return 1f;
            var vp = cam.WorldToViewportPoint(worldCenter);
            if (vp.z <= 0f) return 1f; // behind camera — no penalty

            float screenHalfH = rawError * halfFovTan * 0.5f / cam.aspect;
            float screenHalfV = rawError * halfFovTan * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vp.x, 0f, 1f) - 0.5f) - screenHalfH);
            float dy = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vp.y, 0f, 1f) - 0.5f) - screenHalfV) * cam.aspect;
            float r  = Mathf.Max(dx, dy);
            float outer = Mathf.Max(P_FoveationOuterRadius, P_FoveationInnerRadius + 0.001f);
            float t = Mathf.Clamp01((r - P_FoveationInnerRadius) / (outer - P_FoveationInnerRadius));
            t = t * t * (3f - 2f * t); // smoothstep
            return Mathf.Pow(P_FoveationStrength + 1f, t);
        }

        // ----- Heap push/pop (max-heap on foveation-adjusted ScreenError) -----

        private void HeapPush(OctreeNode node, OctreeNode parent, Camera cam, float halfFovTan, Matrix4x4 localToWorld)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, worldBounds)) return;
            if (P_OcclusionCulling && _occludedNodes.Contains(node)) return;

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

            // heapError = rawError / (strength+1)^t — exponential peripheral penalty so
            // budget runs out in screen-position order, not just distance order.
            float fovScale   = FoveationScale(worldBounds.center, rawError, cam, halfFovTan);
            float heapError  = rawError / fovScale;

            var entry = new QueueEntry(node, parent, rawError, heapError);
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
            Matrix4x4 localToWorld, int activeMask, int vertCount, float lodScale)
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
            d.Set(node, material, localToWorld, activeMask, vertCount, lodScale);
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
            public readonly OctreeNode Parent;  // fallback if Node isn't loaded yet
            public readonly float ScreenError;  // raw geometric error, used for LOD size scale
            public readonly float HeapError;    // ScreenError / foveationScale, used for heap ordering and stop test
            public QueueEntry(OctreeNode node, OctreeNode parent, float screenError, float heapError)
            {
                Node = node; Parent = parent; ScreenError = screenError; HeapError = heapError;
            }
        }

        // Mutable class so it can be pooled and reused across frames.
        private sealed class NodeDrawable : IPointCloudDrawable
        {
            private OctreeNode _node;
            private Material   _material;
            private Matrix4x4  _localToWorld;
            private int        _activeMask;
            private int        _vertCount;  // totalKept * 4 (Quads)
            private float      _lodScale;   // _PointSize * sqrt(orig/kept) * 0.5 — projection applied per-eye in shader

            public void Set(OctreeNode node, Material material,
                Matrix4x4 localToWorld, int activeMask, int vertCount, float lodScale)
            {
                _node         = node;
                _material     = material;
                _localToWorld = localToWorld;
                _activeMask   = activeMask;
                _vertCount    = vertCount * 4;
                _lodScale     = lodScale;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                var block = _node.PropertyBlock;
                block.SetInt(PropActiveOctantMask, _activeMask);
                block.SetFloat(PropLodScale, _lodScale);
                cmd.DrawProcedural(_localToWorld, _material, 0,
                    MeshTopology.Quads, _vertCount, 1, block);
            }
        }
    }
}
