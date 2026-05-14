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
    public class PointCloudRenderer : MonoBehaviour
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

        private BVHAsset  ActiveAsset    => _assets.Resolve(ActiveTier);
        private Material  ActiveMaterial => _materials.Resolve(ActiveTier);

#if UNITY_EDITOR
        public void SetImportedAsset(PerPlatformAssets assets, PerPlatformMaterials materials,
            PerPlatformRenderProperties renderProperties = default)
        {
            _assets                = assets;
            _materials             = materials;
            SharedRenderProperties = renderProperties;
        }

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

        public void RestoreQualityAsset(BVHAsset asset)
        {
            _assets = new PerPlatformAssets
            {
                Quality     = asset,
                Performance = _assets.Performance,
            };
        }

        public void RestorePerformanceAsset(BVHAsset asset)
        {
            _assets = new PerPlatformAssets
            {
                Quality     = _assets.Quality,
                Performance = asset,
            };
        }
#endif

        private PointCloudRenderProperties ActiveProperties => SharedRenderProperties.Resolve(ActiveTier);

        public PointCloudRenderProperties GetActivePropertiesForGizmo() => ActiveProperties;

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
        private float P_PointSizeScale       => (ActiveProperties?.PointSizeScale ?? DefaultPointSizeScale)
                                                    * PointSizeMultiplier;

        // Fovea position in normalised viewport space. Defaults to screen centre.
        [NonSerialized] public Vector2 FoveationCentre = new Vector2(0.5f, 0.5f);

        // Diagnostics — updated each frame.
        [NonSerialized] public int LastFramePointsDrawn;
        [NonSerialized] public int LastFramePointsFoveationSaved;

        private bool  P_OcclusionCulling     => ActiveProperties?.OcclusionCullingEnabled ?? true;
        private bool  P_FoveationEnabled     => ActiveProperties?.FoveationEnabled        ?? false;
        private float P_FoveationStrength    => ActiveProperties?.FoveationStrength       ?? 32f;
        private float P_FoveationInnerRadius => ActiveProperties?.FoveationInnerRadius    ?? 0.2f;
        private float P_LodHysteresis        => ActiveProperties?.LodHysteresis           ?? 0.2f;

        private static readonly int PropLodScale  = Shader.PropertyToID("_LodScale");
        private static readonly int PropPointSize = Shader.PropertyToID("_PointSize");

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

        // CullingGroup state.
        private CullingGroup     _cullingGroup;
        private BVHNode[]        _cullingNodes;
        private BoundingSphere[] _cullingSpheres;
        private int[]            _cullingResults;
        private Camera           _cullingCamera;
        private Matrix4x4        _lastLocalToWorld;
        private readonly HashSet<BVHNode> _occludedNodes = new HashSet<BVHNode>();

        private readonly List<NodeDrawable> _activeDrawables  = new();
        private readonly List<NodeDrawable> _pendingDrawables = new();

        private readonly List<QueueEntry> _heap = new();

        // _selectedNodes is this frame's selection. _prevSelectedNodes carries last frame's
        // selection forward so the traversal can apply a hysteresis dead-band: nodes that were
        // selected last frame stay selected until their error drops below the collapse threshold,
        // preventing LOD toggling at boundaries.
        private Dictionary<BVHNode, float> _selectedNodes     = new Dictionary<BVHNode, float>();
        private Dictionary<BVHNode, float> _prevSelectedNodes = new Dictionary<BVHNode, float>();

        private readonly Plane[] _frustumPlanes = new Plane[6];

        private static bool TestAABBFrustum(Bounds b, Plane[] planes)
        {
            float minX = b.min.x, minY = b.min.y, minZ = b.min.z;
            float maxX = b.max.x, maxY = b.max.y, maxZ = b.max.z;
            for (int i = 0; i < 6; i++)
            {
                var   n = planes[i].normal;
                float d = planes[i].distance;
                float px = n.x >= 0f ? maxX : minX;
                float py = n.y >= 0f ? maxY : minY;
                float pz = n.z >= 0f ? maxZ : minZ;
                if (n.x * px + n.y * py + n.z * pz + d < 0f) return false;
            }
            return true;
        }

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

        private void RefreshCullingGroup(Camera cam, BVHAsset asset)
        {
            var localToWorld = transform.localToWorldMatrix;

            bool needRebuild = _cullingGroup == null
                || _cullingCamera != cam
                || _cullingNodes  == null
                || _cullingNodes.Length == 0;

            if (needRebuild)
            {
                DisposeCullingGroup();

                var nodes = new List<BVHNode>();
                CollectNodes(asset.Root, nodes);

                _cullingNodes   = nodes.ToArray();
                _cullingSpheres = new BoundingSphere[_cullingNodes.Length];

                _cullingGroup = new CullingGroup();
                _cullingGroup.targetCamera = cam;
                _cullingGroup.SetBoundingSpheres(_cullingSpheres);
                _cullingGroup.SetBoundingSphereCount(_cullingSpheres.Length);
                _cullingResults   = new int[_cullingSpheres.Length];
                _cullingCamera    = cam;
                _lastLocalToWorld = Matrix4x4.zero;
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

            _cullingGroup.SetDistanceReferencePoint(cam.transform.position);

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

        private static void CollectNodes(BVHNode root, List<BVHNode> result)
        {
            if (root == null) return;
            var stack = new Stack<BVHNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                result.Add(node);
                if (node.Left  != null) stack.Push(node.Left);
                if (node.Right != null) stack.Push(node.Right);
            }
        }

        private void SelectNodes(Camera cam, BVHAsset asset)
        {
            var   localToWorld = transform.localToWorldMatrix;
            float halfFovTan   = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var   camPos       = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            bool  fovEnabled      = P_FoveationEnabled;
            float fovStrength     = P_FoveationStrength;
            float fovInner        = P_FoveationInnerRadius;
            float errorThreshold  = P_ScreenErrorThreshold;
            float minDrawFrac     = P_MinDrawErrorFraction;
            bool  occlusionCull   = P_OcclusionCulling;
            var vpMatrix = cam.projectionMatrix * cam.worldToCameraMatrix;

            var   mat          = ActiveMaterial;
            float pointSizeBase = mat.GetFloat(PropPointSize) * P_PointSizeScale * 0.5f;

            _heap.Clear();
            _selectedNodes.Clear();
            HeapPush(asset.Root, null, camPos, halfFovTan, localToWorld, occlusionCull);

            int remaining      = P_PointBudget;
            int foveationSaved = 0;

            while (_heap.Count > 0)
            {
                var entry = HeapPop();
                var node  = entry.Node;

                if (!node.IsLoaded)
                {
                    if (entry.Parent != null && entry.Parent.IsLoaded)
                        _selectedNodes[entry.Parent] = entry.ScreenError;
                    continue;
                }

                // Foveation: raise the error threshold for peripheral nodes.
                // t=0 (fovea) → no change. t=1 (periphery) → full fovStrength multiplier.
                // Nodes overlapping the inner zone are never penalised.
                float effectiveThreshold = errorThreshold;
                if (fovEnabled)
                {
                    float t = FoveationT(vpMatrix, fovInner, entry.WorldBounds, entry.ScreenError, halfFovTan);
                    effectiveThreshold *= Mathf.Lerp(1f, fovStrength, t);
                }

                // Hysteresis: nodes selected last frame use a lower collapse threshold —
                // the same foveation-scaled effective threshold, reduced by the hysteresis factor.
                float activeThreshold = _prevSelectedNodes.ContainsKey(node)
                    ? effectiveThreshold * (1f - P_LodHysteresis)
                    : effectiveThreshold;
                bool tooSmall = entry.ScreenError < activeThreshold;

                if (!tooSmall && !node.IsLeaf && remaining > 0)
                {
                    // Track whether foveation is what stopped expansion (for diagnostics).
                    bool wouldExpandWithoutFov = fovEnabled && entry.ScreenError >= errorThreshold;

                    if (node.Left  != null) HeapPush(node.Left,  node, camPos, halfFovTan, localToWorld, occlusionCull);
                    if (node.Right != null) HeapPush(node.Right, node, camPos, halfFovTan, localToWorld, occlusionCull);
                    continue;
                }

                _selectedNodes[node] = entry.ScreenError;
                remaining -= node.PointCount;

                if (fovEnabled && entry.ScreenError >= errorThreshold && tooSmall)
                    foveationSaved += node.PointCount;
            }

            // Emit draw calls.
            LastFramePointsDrawn = 0;
            float minDrawError   = errorThreshold * minDrawFrac;

            foreach (var kvp in _selectedNodes)
            {
                var   node        = kvp.Key;
                float screenError = kvp.Value;

                if (node.PointCount == 0) continue;
                if (minDrawFrac > 0 && screenError < minDrawError) continue;

                // A BVH node draws its own points unless both children are already selected —
                // in that case the children cover the space completely and the parent is skipped.
                bool leftSelected  = node.Left  != null && _selectedNodes.ContainsKey(node.Left);
                bool rightSelected = node.Right != null && _selectedNodes.ContainsKey(node.Right);
                if (leftSelected && rightSelected) continue;

                float ratio    = node.OriginalCount > 0 ? (float)node.OriginalCount / node.PointCount : 1f;
                float lodScale = Mathf.Sqrt(ratio) * pointSizeBase;

                _pendingDrawables.Add(GetDrawable(node, mat, localToWorld, node.PointCount, lodScale));
                LastFramePointsDrawn += node.PointCount;
            }

            LastFramePointsFoveationSaved = foveationSaved;

            // Swap selected-node dicts: this frame becomes the hysteresis reference for next frame.
            var tmp = _prevSelectedNodes;
            _prevSelectedNodes = _selectedNodes;
            _selectedNodes = tmp;
        }

        // ----- Foveation helpers -----

        // Returns t in [0,1]: 0 = inside foveal zone (no penalty), 1 = peripheral (full penalty).
        // Uses Chebyshev distance in viewport space. Nodes whose projected extent overlaps the
        // inner zone are protected even if their centre lies outside it.
        private float FoveationT(Matrix4x4 vpMatrix, float fovInner,
            Bounds worldBounds, float screenError, float halfFovTan)
        {
            var  c = worldBounds.center;
            var  h = vpMatrix * new Vector4(c.x, c.y, c.z, 1f);
            if (h.w <= 0f) return 0f; // centre behind camera — never penalise
            float invW = 1f / h.w;
            float vpx  = h.x * invW * 0.5f + 0.5f;
            float vpy  = h.y * invW * 0.5f + 0.5f;
            float dx   = Mathf.Abs(Mathf.Clamp(vpx, 0f, 1f) - FoveationCentre.x);
            float dy   = Mathf.Abs(Mathf.Clamp(vpy, 0f, 1f) - FoveationCentre.y);
            float r    = Mathf.Max(dx, dy);
            // Subtract the node's projected half-extent so nodes straddling the boundary are protected.
            float nodeRadius = screenError * halfFovTan * 0.5f;
            return (r - nodeRadius) > fovInner ? 1f : 0f;
        }

        // ----- Heap push/pop (max-heap on ScreenError) -----

        private void HeapPush(BVHNode node, BVHNode parent, Vector3 camPos, float halfFovTan,
            Matrix4x4 localToWorld, bool occlusionCull)
        {
            if (node == null) return;
            var worldBounds = TransformBounds(node.Bounds, localToWorld);
            if (!TestAABBFrustum(worldBounds, _frustumPlanes)) return;
            if (occlusionCull && _occludedNodes.Contains(node)) return;

            var closest = new Vector3(
                Mathf.Clamp(camPos.x, worldBounds.min.x, worldBounds.max.x),
                Mathf.Clamp(camPos.y, worldBounds.min.y, worldBounds.max.y),
                Mathf.Clamp(camPos.z, worldBounds.min.z, worldBounds.max.z));
            float sqrDist     = (camPos - closest).sqrMagnitude;
            var   ext         = worldBounds.extents;
            float maxExtent   = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));
            float rawError    = sqrDist > 0.000001f
                ? maxExtent / (Mathf.Sqrt(sqrDist) * halfFovTan)
                : float.MaxValue;

            var entry = new QueueEntry(node, parent, rawError, worldBounds);
            _heap.Add(entry);
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heap[p].ScreenError >= _heap[i].ScreenError) break;
                (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
                i = p;
            }
        }

        private QueueEntry HeapPop()
        {
            var top  = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            int i     = 0;
            int count = _heap.Count;
            while (true)
            {
                int l       = (i << 1) + 1;
                int r       = l + 1;
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

        private NodeDrawable GetDrawable(BVHNode node, Material material,
            Matrix4x4 localToWorld, int vertCount, float lodScale)
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
            d.Set(node, material, localToWorld, vertCount, lodScale);
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
            public readonly BVHNode Node;
            public readonly BVHNode Parent;
            public readonly float   ScreenError;
            public readonly Bounds  WorldBounds;
            public QueueEntry(BVHNode node, BVHNode parent, float screenError, Bounds worldBounds)
            {
                Node = node; Parent = parent; ScreenError = screenError; WorldBounds = worldBounds;
            }
        }

        private sealed class NodeDrawable : IPointCloudDrawable
        {
            private BVHNode   _node;
            private Material  _material;
            private Matrix4x4 _localToWorld;
            private int       _vertCount;
            private float     _lodScale;

            public void Set(BVHNode node, Material material,
                Matrix4x4 localToWorld, int vertCount, float lodScale)
            {
                _node         = node;
                _material     = material;
                _localToWorld = localToWorld;
                _vertCount    = vertCount * 4; // 4 vertices per point quad
                _lodScale     = lodScale;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                var block = _node.PropertyBlock;
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

            float   inner  = P_FoveationInnerRadius;
            Vector2 centre = FoveationCentre;
            float   depth  = (cam.nearClipPlane + cam.farClipPlane) * 0.5f;

            DrawFoveaRect(cam, centre, inner, GizmoColor, depth);
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
