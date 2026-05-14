using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.XR;
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
        private const float DefaultPointSizeScale       = 0.01f;

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

        // Diagnostic — updated each frame.
        [NonSerialized] public int LastFramePointsDrawn;

        private bool  P_OcclusionCulling     => ActiveProperties?.OcclusionCullingEnabled ?? true;
        private bool  P_FoveationEnabled     => ActiveProperties?.FoveationEnabled        ?? false;
        private float P_FoveationStrength    => ActiveProperties?.FoveationStrength       ?? 32f;
        private float P_FoveationInnerRadius => ActiveProperties?.FoveationInnerRadius    ?? 0.2f;
        private float P_LodHysteresis        => ActiveProperties?.LodHysteresis           ?? 0.2f;

        private static readonly int PropPoints          = Shader.PropertyToID("_Points");
        private static readonly int PropNodeDescriptors = Shader.PropertyToID("_NodeDescriptors");

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

        private BVHAsset _lastActiveAsset;

        // CullingGroup state.
        private CullingGroup     _cullingGroup;
        private BVHNode[]        _cullingNodes;
        private BoundingSphere[] _cullingSpheres;
        private int[]            _cullingResults;
        private Camera           _cullingCamera;
        private Matrix4x4        _lastLocalToWorld;

        // Per-node indexed state — parallel arrays over _cullingNodes, indexed by BVHNode.IndexInRenderer.
        private bool[]           _occludedFlags     = new bool[0];
        private bool[]           _expandedFlags     = new bool[0];
        private bool[]           _prevExpandedFlags = new bool[0];
        // _selectedError[i] >= 0 means node i is selected this frame; -1f means not selected.
        private float[]          _selectedError     = new float[0];
        private readonly List<int> _selectedIndices = new List<int>();

        private readonly List<QueueEntry> _heap = new List<QueueEntry>();

        private readonly Plane[] _frustumPlanes = new Plane[6];

        // Indirect draw state.
        // NodeDescriptor layout (48 bytes = 3 float4s):
        //   float4 a: boundsMin.xyz, lodScale
        //   float4 b: boundsSize.xyz, <pad>
        //   uint4  c: pointOffset, pointCount, 0, 0  (reinterpreted as float bits)
        private GraphicsBuffer _nodeDescriptorBuffer; // StructuredBuffer<float4> on GPU (validCount*3 elements)
        private GraphicsBuffer _indirectArgsBuffer;   // args for DrawProceduralIndirect
        private float[]        _nodeDescriptorData;   // CPU-side staging array
        private uint[]         _indirectArgsData;     // [vertexCount, instanceCount, startVertex, startInstance]
        private int            _descriptorCapacity;   // allocated node capacity of _nodeDescriptorBuffer
        private bool           _globalBufferBound;    // whether GlobalPointBuffer is bound to the material
        private bool           _descriptorBufferBound;// whether _nodeDescriptorBuffer is bound to the material
        private IndirectDrawable _indirectDrawable;   // single registered drawable

        private static bool TestAABBFrustum(float minX, float minY, float minZ,
                                             float maxX, float maxY, float maxZ,
                                             Plane[] planes)
        {
            for (int i = 0; i < 6; i++)
            {
                var   n  = planes[i].normal;
                float d  = planes[i].distance;
                float px = n.x >= 0f ? maxX : minX;
                float py = n.y >= 0f ? maxY : minY;
                float pz = n.z >= 0f ? maxZ : minZ;
                if (n.x * px + n.y * py + n.z * pz + d < 0f) return false;
            }
            return true;
        }

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
            DeregisterIndirect();
            DisposeCullingGroup();
            DisposeIndirectBuffers();
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

            if (asset != _lastActiveAsset)
            {
                DisposeCullingGroup();
                DisposeIndirectBuffers();
                _selectedIndices.Clear();
                _globalBufferBound    = false;
                _descriptorBufferBound = false;
                _lastActiveAsset = asset;
            }

            if (P_OcclusionCulling)
                RefreshCullingGroup(cam, asset);
            else
            {
                EnsureNodeIndex(asset);
            }

            SelectNodes(cam, asset);
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

        // ----- Node index (always maintained, regardless of occlusion culling) -----

        // Populates _cullingNodes and all parallel arrays when occlusion culling is off.
        // When occlusion culling is on, RefreshCullingGroup handles this during its rebuild.
        private void EnsureNodeIndex(BVHAsset asset)
        {
            if (_cullingNodes != null) return;

            var nodes = new List<BVHNode>();
            CollectNodes(asset.Root, nodes);
            _cullingNodes = nodes.ToArray();
            RebuildNodeArrays();
        }

        private void RebuildNodeArrays()
        {
            int n = _cullingNodes.Length;
            for (int i = 0; i < n; i++)
                _cullingNodes[i].IndexInRenderer = i;

            _occludedFlags = new bool[n];
            _expandedFlags = new bool[n];
            _selectedError = new float[n];
            Array.Fill(_selectedError, -1f);
            // Treat all nodes as previously expanded on first frame so the LOD boundary starts
            // conservative rather than over-expanding into nodes too small to pass minDrawError.
            _prevExpandedFlags = new bool[n];
            Array.Fill(_prevExpandedFlags, true);
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

                RebuildNodeArrays();
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

            Array.Clear(_occludedFlags, 0, _occludedFlags.Length);
            int hiddenCount = _cullingGroup.QueryIndices(false, _cullingResults, 0);
            for (int i = 0; i < hiddenCount; i++)
                _occludedFlags[_cullingResults[i]] = true;
        }

        private void DisposeCullingGroup()
        {
            _cullingGroup?.Dispose();
            _cullingGroup     = null;
            _cullingCamera    = null;
            _cullingNodes     = null; // null signals EnsureNodeIndex to rebuild when occlusion culling is off
            _cullingSpheres   = null;
            _cullingResults   = null;
            _lastLocalToWorld = Matrix4x4.zero;
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

            bool  fovEnabled     = P_FoveationEnabled;
            float fovStrength    = P_FoveationStrength;
            float fovInner       = P_FoveationInnerRadius;
            float errorThreshold = P_ScreenErrorThreshold;
            float minDrawFrac    = P_MinDrawErrorFraction;
            bool  occlusionCull  = P_OcclusionCulling;
            var   vpMatrix       = cam.projectionMatrix * cam.worldToCameraMatrix;

            var   mat           = ActiveMaterial;
            float pointSizeBase = P_PointSizeScale * 0.5f;

            if (!_globalBufferBound && asset.GlobalPointBuffer != null)
            {
                mat.SetBuffer(PropPoints, asset.GlobalPointBuffer);
                _globalBufferBound = true;
            }

            // Reset per-frame selection state — only touch indices written last frame.
            foreach (int idx in _selectedIndices)
                _selectedError[idx] = -1f;
            _selectedIndices.Clear();

            // _expandedFlags was swapped in from _prevExpandedFlags last frame; clear it for reuse.
            Array.Clear(_expandedFlags, 0, _expandedFlags.Length);

            _heap.Clear();
            HeapPush(asset.Root, null, camPos, halfFovTan, localToWorld, occlusionCull);

            int remaining = P_PointBudget;

            while (_heap.Count > 0)
            {
                var entry = HeapPop();
                var node  = entry.Node;

                if (!node.IsLoaded)
                {
                    var parent = entry.Parent;
                    if (parent != null && parent.IsLoaded)
                    {
                        int pi = parent.IndexInRenderer;
                        if (_selectedError[pi] < 0f)
                            _selectedIndices.Add(pi);
                        _selectedError[pi] = entry.ScreenError;
                    }
                    continue;
                }

                float effectiveThreshold = errorThreshold;
                if (fovEnabled)
                {
                    float t = FoveationT(vpMatrix, fovInner, entry.WorldCenter, entry.ScreenError, halfFovTan);
                    effectiveThreshold *= Mathf.Lerp(1f, fovStrength, t);
                }

                // Hysteresis: expanded nodes use a lower collapse threshold to prevent LOD flicker.
                int   nodeIdx     = node.IndexInRenderer;
                bool  wasExpanded = _prevExpandedFlags[nodeIdx];
                float activeThreshold = wasExpanded
                    ? effectiveThreshold * (1f - P_LodHysteresis)
                    : effectiveThreshold;
                bool tooSmall = entry.ScreenError < activeThreshold;

                if (!tooSmall && !node.IsLeaf && remaining > 0)
                {
                    _expandedFlags[nodeIdx] = true;
                    if (node.Left  != null) HeapPush(node.Left,  node, camPos, halfFovTan, localToWorld, occlusionCull);
                    if (node.Right != null) HeapPush(node.Right, node, camPos, halfFovTan, localToWorld, occlusionCull);
                    continue;
                }

                if (_selectedError[nodeIdx] < 0f)
                    _selectedIndices.Add(nodeIdx);
                _selectedError[nodeIdx] = entry.ScreenError;
                remaining -= node.PointCount;
            }

            // Build indirect draw — one instanced call covering all selected nodes.
            // NodeDescriptor layout in _nodeDescriptorBuffer (48 bytes = 3 float4s):
            //   float4 a: boundsMin.xyz, lodScale
            //   float4 b: boundsSize.xyz, <pad>
            //   uint4  c: pointOffset, pointCount, 0, 0  (reinterpreted as float bits)
            LastFramePointsDrawn = 0;
            float minDrawError   = errorThreshold * minDrawFrac;
            int   selectedCount  = _selectedIndices.Count;

            // Ensure GPU descriptor buffer has capacity for worst-case (all selected nodes valid).
            // Grows with 50% headroom; never shrinks to avoid thrashing.
            if (selectedCount > _descriptorCapacity)
            {
                int newCapacity = selectedCount + selectedCount / 2;
                _nodeDescriptorBuffer?.Release();
                _nodeDescriptorBuffer  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity * 3, 16);
                _nodeDescriptorData    = new float[newCapacity * 12];
                _descriptorCapacity    = newCapacity;
                _descriptorBufferBound = false;
            }
            if (_indirectArgsBuffer == null)
            {
                _indirectArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));
                _indirectArgsData   = new uint[4];
            }

            // Single pass: fill descriptor array, tracking valid count and max point count inline.
            int  validCount    = 0;
            int  maxPointCount = 0;
            for (int s = 0; s < selectedCount; s++)
            {
                int   idx        = _selectedIndices[s];
                var   node       = _cullingNodes[idx];
                float screenError = _selectedError[idx];
                if (node.PointCount == 0) continue;
                if (minDrawFrac > 0f && screenError < minDrawError) continue;
                bool leftSelected  = node.Left  != null && _selectedError[node.Left.IndexInRenderer]  >= 0f;
                bool rightSelected = node.Right != null && _selectedError[node.Right.IndexInRenderer] >= 0f;
                if (leftSelected && rightSelected) continue;

                if (node.PointCount > maxPointCount) maxPointCount = node.PointCount;

                float lodScale = node.LodScaleBase * pointSizeBase;
                int   o        = validCount * 12;

                // float4 a
                _nodeDescriptorData[o + 0] = node.BoundsMin.x;
                _nodeDescriptorData[o + 1] = node.BoundsMin.y;
                _nodeDescriptorData[o + 2] = node.BoundsMin.z;
                _nodeDescriptorData[o + 3] = lodScale;
                // float4 b
                _nodeDescriptorData[o + 4] = node.BoundsSize.x;
                _nodeDescriptorData[o + 5] = node.BoundsSize.y;
                _nodeDescriptorData[o + 6] = node.BoundsSize.z;
                _nodeDescriptorData[o + 7] = 0f;
                // uint4 c — reinterpret int bits into the float array
                _nodeDescriptorData[o +  8] = BitConverter.Int32BitsToSingle(node.GlobalBufferOffset);
                _nodeDescriptorData[o +  9] = BitConverter.Int32BitsToSingle(node.PointCount);
                _nodeDescriptorData[o + 10] = 0f;
                _nodeDescriptorData[o + 11] = 0f;

                validCount++;
                LastFramePointsDrawn += node.PointCount;
            }

            if (validCount == 0)
            {
                DeregisterIndirect();
                (_prevExpandedFlags, _expandedFlags) = (_expandedFlags, _prevExpandedFlags);
                return;
            }

            _nodeDescriptorBuffer.SetData(_nodeDescriptorData, 0, 0, validCount * 12);
            _indirectArgsData[0] = (uint)(maxPointCount * 6); // 6 verts per point (2 triangles)
            // Under single-pass instanced stereo, Unity cannot auto-double the instance count for
            // indirect draws (the args buffer is opaque to it). We double manually so SV_InstanceID
            // covers [0, validCount*2), letting UNITY_SETUP_INSTANCE_ID decode eye (bit 0) and
            // node index (>> 1) correctly.
            bool stereoInstanced = XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced;
            _indirectArgsData[1] = (uint)(stereoInstanced ? validCount * 2 : validCount);
            _indirectArgsData[2] = 0;
            _indirectArgsData[3] = 0;
            _indirectArgsBuffer.SetData(_indirectArgsData);

            if (!_descriptorBufferBound)
            {
                mat.SetBuffer(PropNodeDescriptors, _nodeDescriptorBuffer);
                _descriptorBufferBound = true;
            }

            if (_indirectDrawable == null)
            {
                _indirectDrawable = new IndirectDrawable();
                PointCloudRenderFeature.PointCloudRenderPass.Register(_indirectDrawable);
            }
            _indirectDrawable.Set(mat, _indirectArgsBuffer, localToWorld);

            // Swap expanded-flags arrays: this frame's expansions become next frame's hysteresis reference.
            (_prevExpandedFlags, _expandedFlags) = (_expandedFlags, _prevExpandedFlags);
        }


        // ----- Foveation helpers -----

        // Returns t in [0,1]: 0 = inside foveal zone (no penalty), 1 = full peripheral penalty.
        // Uses Chebyshev distance in viewport space. Nodes whose projected extent overlaps the
        // inner zone are protected even if their centre lies outside it.
        // t ramps linearly from 0 at the inner boundary to 1 over a zone of width fovInner,
        // so fovInner controls both the protected radius and the transition speed.
        private float FoveationT(Matrix4x4 vpMatrix, float fovInner,
            Vector3 worldCenter, float screenError, float halfFovTan)
        {
            var  h = vpMatrix * new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, 1f);
            if (h.w <= 0f) return 0f; // centre behind camera — never penalise
            float invW = 1f / h.w;
            float vpx  = h.x * invW * 0.5f + 0.5f;
            float vpy  = h.y * invW * 0.5f + 0.5f;
            float dx   = Mathf.Abs(Mathf.Clamp(vpx, 0f, 1f) - FoveationCentre.x);
            float dy   = Mathf.Abs(Mathf.Clamp(vpy, 0f, 1f) - FoveationCentre.y);
            float r    = Mathf.Max(dx, dy);
            // Subtract the node's projected half-extent so nodes straddling the boundary are protected.
            float nodeRadius = screenError * halfFovTan * 0.5f;
            float excess = (r - nodeRadius) - fovInner;
            return fovInner > 0f ? Mathf.Clamp01(excess / fovInner) : (excess > 0f ? 1f : 0f);
        }

        // ----- Heap push/pop (max-heap on ScreenError) -----

        private void HeapPush(BVHNode node, BVHNode parent, Vector3 camPos, float halfFovTan,
            Matrix4x4 m, bool occlusionCull)
        {
            if (node == null) return;
            if (occlusionCull && _occludedFlags[node.IndexInRenderer]) return;

            // Inline TransformBounds using BoundsMin/BoundsSize — no Bounds construction, no property getters.
            float lx = node.BoundsMin.x, ly = node.BoundsMin.y, lz = node.BoundsMin.z;
            float sx = node.BoundsSize.x, sy = node.BoundsSize.y, sz = node.BoundsSize.z;
            // Centre in local space = min + size*0.5
            float cx = lx + sx * 0.5f, cy = ly + sy * 0.5f, cz = lz + sz * 0.5f;
            // Transform centre
            float wcx = m.m00 * cx + m.m01 * cy + m.m02 * cz + m.m03;
            float wcy = m.m10 * cx + m.m11 * cy + m.m12 * cz + m.m13;
            float wcz = m.m20 * cx + m.m21 * cy + m.m22 * cz + m.m23;
            // Transform extents (half-size) — axis-aligned so take abs of each column
            float ex = sx * 0.5f, ey = sy * 0.5f, ez = sz * 0.5f;
            float wex = Mathf.Abs(m.m00) * ex + Mathf.Abs(m.m01) * ey + Mathf.Abs(m.m02) * ez;
            float wey = Mathf.Abs(m.m10) * ex + Mathf.Abs(m.m11) * ey + Mathf.Abs(m.m12) * ez;
            float wez = Mathf.Abs(m.m20) * ex + Mathf.Abs(m.m21) * ey + Mathf.Abs(m.m22) * ez;
            float wminX = wcx - wex, wminY = wcy - wey, wminZ = wcz - wez;
            float wmaxX = wcx + wex, wmaxY = wcy + wey, wmaxZ = wcz + wez;

            // Inline frustum test — all floats, no Bounds/Plane property access in the loop.
            var planes = _frustumPlanes;
            for (int pi = 0; pi < 6; pi++)
            {
                var   n  = planes[pi].normal;
                float d  = planes[pi].distance;
                float px = n.x >= 0f ? wmaxX : wminX;
                float py = n.y >= 0f ? wmaxY : wminY;
                float pz = n.z >= 0f ? wmaxZ : wminZ;
                if (n.x * px + n.y * py + n.z * pz + d < 0f) return;
            }

            // Screen error: closest point on AABB to camera, then maxExtent / (dist * halfFovTan).
            float clampX  = camPos.x < wminX ? wminX : camPos.x > wmaxX ? wmaxX : camPos.x;
            float clampY  = camPos.y < wminY ? wminY : camPos.y > wmaxY ? wmaxY : camPos.y;
            float clampZ  = camPos.z < wminZ ? wminZ : camPos.z > wmaxZ ? wmaxZ : camPos.z;
            float dx = camPos.x - clampX, dy = camPos.y - clampY, dz = camPos.z - clampZ;
            float sqrDist   = dx * dx + dy * dy + dz * dz;
            float maxExtent = wex > wey ? (wex > wez ? wex : wez) : (wey > wez ? wey : wez);
            float rawError  = sqrDist > 0.000001f
                ? maxExtent / (Mathf.Sqrt(sqrDist) * halfFovTan)
                : float.MaxValue;

            var worldCenter = new Vector3(wcx, wcy, wcz);
            var entry = new QueueEntry(node, parent, rawError, worldCenter);
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

        // ----- Indirect draw helpers -----

        private void DeregisterIndirect()
        {
            if (_indirectDrawable == null) return;
            PointCloudRenderFeature.PointCloudRenderPass.Deregister(_indirectDrawable);
            _indirectDrawable = null;
        }

        private void DisposeIndirectBuffers()
        {
            _nodeDescriptorBuffer?.Release();
            _nodeDescriptorBuffer  = null;
            _indirectArgsBuffer?.Release();
            _indirectArgsBuffer    = null;
            _nodeDescriptorData    = null;
            _indirectArgsData      = null;
            _descriptorCapacity    = 0;
            _descriptorBufferBound = false;
        }

        // ----- Utilities -----

        // Used only by the cold-path culling group sphere rebuild — hot path uses inlined HeapPush.
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

        // ----- Inner types -----

        private readonly struct QueueEntry
        {
            public readonly BVHNode Node;
            public readonly BVHNode Parent;
            public readonly float   ScreenError;
            public readonly Vector3 WorldCenter; // precomputed world-space centre, used by foveation
            public QueueEntry(BVHNode node, BVHNode parent, float screenError, Vector3 worldCenter)
            {
                Node = node; Parent = parent; ScreenError = screenError; WorldCenter = worldCenter;
            }
        }

        private sealed class IndirectDrawable : IPointCloudDrawable
        {
            private Material       _material;
            private GraphicsBuffer _argsBuffer;
            private Matrix4x4      _localToWorld;

            public void Set(Material material, GraphicsBuffer argsBuffer, Matrix4x4 localToWorld)
            {
                _material     = material;
                _argsBuffer   = argsBuffer;
                _localToWorld = localToWorld;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                cmd.DrawProceduralIndirect(_localToWorld, _material, 0,
                    MeshTopology.Triangles, _argsBuffer);
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
