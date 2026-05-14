using System;
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

        // Exposed for editor gizmos (e.g. GazeControllerPrototype) — returns null if no SO assigned.
        public PointCloudRenderProperties GetActivePropertiesForGizmo() => ActiveProperties;

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
        // Fovea position in normalised viewport space. Defaults to screen centre.
        // Assign from an external gaze controller to drive foveated LOD from gaze input.
        [NonSerialized] public Vector2 FoveationCentre = new Vector2(0.5f, 0.5f);

        private bool  P_OcclusionCulling     => ActiveProperties?.OcclusionCullingEnabled ?? true;
        private bool  P_FoveationEnabled     => ActiveProperties?.FoveationEnabled        ?? false;
        private float P_FoveationStrength    => ActiveProperties?.FoveationStrength       ?? 2f;
        private float P_FoveationInnerRadius => ActiveProperties?.FoveationInnerRadius    ?? 0.1f;
        private float P_FoveationOuterRadius => ActiveProperties?.FoveationOuterRadius    ?? 0.5f;


        private static readonly int PropActiveOctantMask = Shader.PropertyToID("_ActiveOctantMask");
        private static readonly int PropLodScale         = Shader.PropertyToID("_LodScale");
        private static readonly int PropPointSize        = Shader.PropertyToID("_PointSize");

        [Tooltip("Gizmo colour for this renderer. Leave alpha=0 to generate a random colour on first use.")]
        [SerializeField] private Color _gizmoColor = Color.clear;

        private Color GizmoColor
        {
            get
            {
                if (_gizmoColor.a == 0f)
                    _gizmoColor = Color.HSVToRGB(UnityEngine.Random.value, 0.85f, 1f);
                return new Color(_gizmoColor.r, _gizmoColor.g, _gizmoColor.b, 1f);
            }
        }

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

        // Inline AABB-vs-frustum test — avoids a managed→native roundtrip per node.
        // Each plane is (normal.xyz, distance) where the plane equation is dot(normal, p) + d >= 0 for inside.
        // We test the positive vertex (the AABB corner most aligned with the plane normal) — if that's outside,
        // the whole box is outside.
        private static bool TestAABBFrustum(Bounds b, Plane[] planes)
        {
            float minX = b.min.x, minY = b.min.y, minZ = b.min.z;
            float maxX = b.max.x, maxY = b.max.y, maxZ = b.max.z;
            for (int i = 0; i < 6; i++)
            {
                var n = planes[i].normal;
                float d = planes[i].distance;
                // Positive vertex: pick the corner most in the direction of the plane normal.
                float px = n.x >= 0f ? maxX : minX;
                float py = n.y >= 0f ? maxY : minY;
                float pz = n.z >= 0f ? maxZ : minZ;
                if (n.x * px + n.y * py + n.z * pz + d < 0f) return false;
            }
            return true;
        }

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
            var   localToWorld  = transform.localToWorldMatrix;
            float halfFovTan    = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var   camPos        = cam.transform.position;
            float camAspect     = cam.aspect;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Cache foveation properties — these resolve ActiveProperties on every access
            // and are called O(nodes) times per frame inside FoveationThresholdMultiplier.
            bool  fovEnabled     = P_FoveationEnabled;
            float fovStrength    = P_FoveationStrength;
            float fovInner       = P_FoveationInnerRadius;
            float fovOuter       = P_FoveationOuterRadius;
            float errorThreshold = P_ScreenErrorThreshold;
            float minDrawFrac    = P_MinDrawErrorFraction;
            bool  occlusionCull  = P_OcclusionCulling;

            // Precompute log(fovStrength+1) so FoveationThresholdMultiplier can use
            // Exp(t * logBase) instead of Pow(base, t) — avoids a log per node.
            float fovLogBase = fovEnabled && fovStrength > 0f ? Mathf.Log(fovStrength + 1f) : 0f;

            // Cache the VP matrix for manual WorldToViewport transform — avoids a Unity
            // interop call (cam.WorldToViewportPoint) on every node in the foveation path.
            var vpMatrix = cam.projectionMatrix * cam.worldToCameraMatrix;

            // _LodScale = _PointSize * lodScale * 0.5 — the projection factors are applied
            // per-eye in the shader using UNITY_MATRIX_P, so the CPU only supplies the scale.
            var mat = ActiveMaterial;
            float pointSizeBase = mat.GetFloat(PropPointSize) * P_PointSizeScale * 0.5f;

            // Max-heap: enqueue nodes sorted by screen error (largest first).
            // Pop nodes one at a time; if within budget and above error threshold, expand children.
            // Otherwise, mark node as selected (frontier).
            _heap.Clear();
            _selectedNodes.Clear();
            HeapPush(asset.Root, null, camPos, halfFovTan, localToWorld, occlusionCull);

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

                // Per-node threshold: foveal nodes use the base threshold (expand as far as error
                // allows), peripheral nodes use a raised threshold (stop expanding sooner).
                // Budget freed by early peripheral stopping is naturally available for foveal expansion.
                // WorldBounds is cached in the entry from HeapPush — no recomputation needed.
                float fovMult    = FoveationThresholdMultiplier(entry.WorldBounds.center, entry.ScreenError,
                                       vpMatrix, camAspect, halfFovTan, fovEnabled, fovLogBase, fovInner, fovOuter);
                bool tooSmall    = entry.ScreenError < errorThreshold * fovMult;
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
                            HeapPush(node.Children[o], node, camPos, halfFovTan, localToWorld, occlusionCull);
                    }
                }
            }

            // Emit one draw call per selected node.
            float minDrawError = errorThreshold * minDrawFrac;
            foreach (var kvp in _selectedNodes)
            {
                var node          = kvp.Key;
                float screenError = kvp.Value;

                if (node.TotalPointCount == 0) continue;

                // Skip nodes too small to contribute visibly — reduces overdraw from tiny distant nodes.
                if (minDrawFrac > 0 && screenError < minDrawError) continue;

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

        // ----- Foveation threshold multiplier -----

        // Returns a value >= 1 representing how much to raise the stopping threshold for this node.
        // t=0 (fovea): multiplier=1 — threshold unchanged, node expands as far as error allows.
        // t=1 (periphery): multiplier=(FoveationStrength+1) — threshold raised, expansion stops sooner.
        // Applied to the per-node stopping test in SelectNodes, not to heap ordering, so the error
        // metric governs selection order purely on geometric grounds. Budget redistributes naturally:
        // peripheral nodes stop earlier, freeing budget for foveal nodes to expand deeper.
        private float FoveationThresholdMultiplier(Vector3 worldCenter, float rawError,
            Matrix4x4 vpMatrix, float camAspect, float halfFovTan,
            bool fovEnabled, float fovLogBase, float fovInner, float fovOuter)
        {
            if (!fovEnabled || fovLogBase <= 0f) return 1f;

            // Manual clip-space transform — avoids Unity interop overhead of cam.WorldToViewportPoint.
            var clip = vpMatrix.MultiplyPoint(worldCenter);
            if (clip.z <= 0f) return 1f; // behind camera — no penalty
            float vpx = clip.x * 0.5f + 0.5f;
            float vpy = clip.y * 0.5f + 0.5f;

            float screenHalfH = rawError * halfFovTan * 0.5f / camAspect;
            float screenHalfV = rawError * halfFovTan * 0.5f;
            // dx and dy both in raw viewport space (x: 0-1 = screen width, y: 0-1 = screen height).
            // Mathf.Max gives Chebyshev distance — fovea zone is a square in viewport space,
            // which is naturally wider than tall in pixels for landscape aspect ratios.
            float dx = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vpx, 0f, 1f) - FoveationCentre.x) - screenHalfH);
            float dy = Mathf.Max(0f, Mathf.Abs(Mathf.Clamp(vpy, 0f, 1f) - FoveationCentre.y) - screenHalfV);
            float r  = Mathf.Max(dx, dy);
            float outer = Mathf.Max(fovOuter, fovInner + 0.001f);
            float t = Mathf.Clamp01((r - fovInner) / (outer - fovInner));
            t = t * t * (3f - 2f * t); // smoothstep
            // Exp(t * log(base)) is equivalent to Pow(base, t) but avoids recomputing log each call.
            return Mathf.Exp(t * fovLogBase);
        }

        // ----- Heap push/pop (max-heap on raw ScreenError) -----

        private void HeapPush(OctreeNode node, OctreeNode parent, Vector3 camPos, float halfFovTan, Matrix4x4 localToWorld, bool occlusionCull)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!TestAABBFrustum(worldBounds, _frustumPlanes)) return;
            if (occlusionCull && _occludedNodes.Contains(node)) return;

            // Distance to nearest point on the AABB, so nodes the camera is inside or
            // behind don't get an inflated screen error and consume the entire point budget.
            var closest = new Vector3(
                Mathf.Clamp(camPos.x, worldBounds.min.x, worldBounds.max.x),
                Mathf.Clamp(camPos.y, worldBounds.min.y, worldBounds.max.y),
                Mathf.Clamp(camPos.z, worldBounds.min.z, worldBounds.max.z));
            float sqrDist  = (camPos - closest).sqrMagnitude;
            // Combine both sqrts: sqrt(extents.sqrMagnitude / sqrDist) = extents.magnitude / dist.
            float rawError = sqrDist > 0.000001f
                ? Mathf.Sqrt(worldBounds.extents.sqrMagnitude / sqrDist) / halfFovTan
                : float.MaxValue;

            var entry = new QueueEntry(node, parent, rawError, worldBounds);
            _heap.Add(entry);
            // Sift up on ScreenError.
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent_ = (i - 1) >> 1;
                if (_heap[parent_].ScreenError >= _heap[i].ScreenError) break;
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
            // Sift down on ScreenError.
            int i = 0;
            int count = _heap.Count;
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int largest = i;
                if (l < count && _heap[l].ScreenError > _heap[largest].ScreenError) largest = l;
                if (r < count && _heap[r].ScreenError > _heap[largest].ScreenError) largest = r;
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
            public readonly float ScreenError;  // raw geometric error, used for heap ordering and LOD size scale
            public readonly Bounds WorldBounds; // cached to avoid recomputing on pop
            public QueueEntry(OctreeNode node, OctreeNode parent, float screenError, Bounds worldBounds)
            {
                Node = node; Parent = parent; ScreenError = screenError; WorldBounds = worldBounds;
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

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!P_FoveationEnabled) return;

            var cam = Camera.current;
            if (cam == null) return;

            float inner = P_FoveationInnerRadius;
            float outer = P_FoveationOuterRadius;
            Vector2 centre = FoveationCentre;
            float depth = (cam.nearClipPlane + cam.farClipPlane) * 0.5f;

            Color solid = GizmoColor;
            Color dim   = new Color(solid.r * 0.5f, solid.g * 0.5f, solid.b * 0.5f, 1f);

            DrawFoveaRect(cam, centre, inner, solid, depth);
            DrawFoveaRect(cam, centre, outer, dim,   depth);
        }

        private static void DrawFoveaRect(Camera cam, Vector2 vp, float r, Color color, float depth)
        {
            if (r <= 0f) return;

            Vector3 tl = cam.ViewportToWorldPoint(new Vector3(vp.x - r, vp.y + r, depth));
            Vector3 tr = cam.ViewportToWorldPoint(new Vector3(vp.x + r, vp.y + r, depth));
            Vector3 br = cam.ViewportToWorldPoint(new Vector3(vp.x + r, vp.y - r, depth));
            Vector3 bl = cam.ViewportToWorldPoint(new Vector3(vp.x - r, vp.y - r, depth));

            UnityEditor.Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.DrawLine(tl, tr);
            UnityEditor.Handles.DrawLine(tr, br);
            UnityEditor.Handles.DrawLine(br, bl);
            UnityEditor.Handles.DrawLine(bl, tl);
        }
#endif
    }
}
