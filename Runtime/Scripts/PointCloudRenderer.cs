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
        private int[]            _selectedIndices   = Array.Empty<int>();
        private int              _selectedCount     = 0;

        private QueueEntry[] _heap      = Array.Empty<QueueEntry>();
        private int          _heapCount = 0;

        private readonly Plane[] _frustumPlanes = new Plane[6];
        // Frustum plane components extracted to flat floats — avoids Plane.normal/distance property
        // interop in the HeapPush hot path (called once per node visited, 6 planes each).
        private readonly float[] _fpNx = new float[6], _fpNy = new float[6], _fpNz = new float[6], _fpD = new float[6];

        // Indirect draw state.
        // NodeDescriptor layout (48 bytes = 3 float4s):
        //   float4 a: boundsMin.xyz, lodScale
        //   float4 b: boundsSize.xyz, <pad>
        //   uint4  c: pointOffset, pointCount, 0, 0  (reinterpreted as float bits)
        //
        // Both descriptor and args buffers are double-buffered: the CPU writes slot [_bufferIndex]
        // while the GPU reads slot [1 - _bufferIndex] from the previous frame, eliminating the
        // CPU-GPU sync stall that SetData causes when writing into a buffer the GPU is still reading.
        private readonly GraphicsBuffer[] _nodeDescriptorBuffer = new GraphicsBuffer[2];
        private readonly GraphicsBuffer[] _indirectArgsBuffer   = new GraphicsBuffer[2];
        private float[]        _nodeDescriptorData;   // CPU-side staging array (sized to max capacity)
        private uint[]         _indirectArgsData;     // [vertexCount, instanceCount, startVertex, startInstance]
        private readonly int[] _descriptorCapacity    = new int[2];
        private int            _bufferIndex;          // toggles 0/1 each frame
        private bool           _globalBufferBound;    // whether GlobalPointBuffer is bound to the material
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
                _selectedCount     = 0;
                _globalBufferBound = false;
                _lastActiveAsset   = asset;
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

            // Pre-allocate heap and selected-indices to total node count.
            _heap            = new QueueEntry[n];
            _heapCount       = 0;
            _selectedIndices = new int[n];
            _selectedCount   = 0;
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

            // Hoist all camera interop calls — each crosses C++/C# boundary.
            var   camPos     = cam.transform.position;
            float halfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var   vpMatrix   = cam.projectionMatrix * cam.worldToCameraMatrix;

            // Extract frustum planes to flat float arrays — eliminates Plane.normal/distance
            // property interop from the HeapPush inner loop (6 reads × every node visited).
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
            for (int pi = 0; pi < 6; pi++)
            {
                var p      = _frustumPlanes[pi];
                var n      = p.normal;
                _fpNx[pi]  = n.x;
                _fpNy[pi]  = n.y;
                _fpNz[pi]  = n.z;
                _fpD[pi]   = p.distance;
            }

            // Hoist all P_* property reads — each dereferences ActiveProperties.
            bool  fovEnabled     = P_FoveationEnabled;
            float fovStrength    = P_FoveationStrength;
            float fovInner       = P_FoveationInnerRadius;
            float errorThreshold = P_ScreenErrorThreshold;
            float minDrawFrac    = P_MinDrawErrorFraction;
            float lodHysteresis  = P_LodHysteresis;
            bool  occlusionCull  = P_OcclusionCulling;
            int   remaining      = P_PointBudget;

            // Squared threshold — eliminates Sqrt from every HeapPush screen-error calculation.
            // All comparisons in the traversal loop use squared error; actual error is stored
            // squared in QueueEntry.ScreenError throughout the traversal.
            float sqErrorThreshold    = errorThreshold * errorThreshold;
            float hysteresisMul       = 1f - lodHysteresis;
            // Lowest possible effective threshold — below this, no node can ever be expanded
            // regardless of hysteresis or foveation (foveation only raises the threshold).
            // Used to early-drain the heap tail once all remaining entries are guaranteed selectable.
            float sqMinEffThreshold   = sqErrorThreshold * (hysteresisMul * hysteresisMul);

            var   mat           = ActiveMaterial;
            float pointSizeBase = P_PointSizeScale * 0.5f;

            if (!_globalBufferBound && asset.GlobalPointBuffer != null)
            {
                mat.SetBuffer(PropPoints, asset.GlobalPointBuffer);
                _globalBufferBound = true;
            }

            // Reset per-frame selection state — only touch indices written last frame.
            for (int i = 0; i < _selectedCount; i++)
                _selectedError[_selectedIndices[i]] = -1f;
            _selectedCount = 0;

            // _expandedFlags was swapped in from _prevExpandedFlags last frame; clear it for reuse.
            Array.Clear(_expandedFlags, 0, _expandedFlags.Length);

            _heapCount = 0;
            HeapPush(asset.Root, null, camPos, halfFovTan, localToWorld, occlusionCull);

            while (_heapCount > 0)
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
                            _selectedIndices[_selectedCount++] = pi;
                        _selectedError[pi] = entry.ScreenError; // stored squared
                    }
                    continue;
                }

                // Early-drain: heap is max-ordered, so once the top entry is below the minimum
                // possible effective threshold (hysteresis-adjusted, foveation only raises further)
                // every remaining entry is also guaranteed selectable — no need to pop and evaluate.
                if (entry.ScreenError < sqMinEffThreshold || remaining <= 0)
                {
                    // Select this entry and all remaining heap entries without further evaluation.
                    int nodeIdx = node.IndexInRenderer;
                    if (_selectedError[nodeIdx] < 0f)
                        _selectedIndices[_selectedCount++] = nodeIdx;
                    _selectedError[nodeIdx] = entry.ScreenError;
                    remaining -= node.PointCount;

                    while (_heapCount > 0)
                    {
                        var e = HeapPop();
                        var n = e.Node;
                        if (!n.IsLoaded) continue;
                        int ni = n.IndexInRenderer;
                        if (_selectedError[ni] < 0f)
                            _selectedIndices[_selectedCount++] = ni;
                        _selectedError[ni] = e.ScreenError;
                    }
                    break;
                }

                // entry.ScreenError is squared; compare against squared threshold.
                float sqEffectiveThreshold = sqErrorThreshold;
                if (fovEnabled)
                {
                    float t = FoveationT(vpMatrix, fovInner, entry.WorldCenter, entry.ScreenError, halfFovTan);
                    // t in [0,1]; lerp(1, fovStrength, t) = 1 + (fovStrength-1)*t
                    float mul = 1f + (fovStrength - 1f) * t;
                    sqEffectiveThreshold *= mul * mul; // square the linear multiplier
                }

                int  nIdx      = node.IndexInRenderer;
                bool wasExpanded  = _prevExpandedFlags[nIdx];
                float sqThreshold = wasExpanded
                    ? sqEffectiveThreshold * (hysteresisMul * hysteresisMul)
                    : sqEffectiveThreshold;

                if (entry.ScreenError >= sqThreshold && !node.IsLeaf && remaining > 0)
                {
                    _expandedFlags[nIdx] = true;
                    if (node.Left  != null) HeapPush(node.Left,  node, camPos, halfFovTan, localToWorld, occlusionCull);
                    if (node.Right != null) HeapPush(node.Right, node, camPos, halfFovTan, localToWorld, occlusionCull);
                    continue;
                }

                if (_selectedError[nIdx] < 0f)
                    _selectedIndices[_selectedCount++] = nIdx;
                _selectedError[nIdx] = entry.ScreenError; // stored squared
                remaining -= node.PointCount;
            }

            // Build indirect draw — one instanced call covering all selected nodes.
            // NodeDescriptor layout in _nodeDescriptorBuffer (48 bytes = 3 float4s):
            //   float4 a: boundsMin.xyz, lodScale
            //   float4 b: boundsSize.xyz, <pad>
            //   uint4  c: pointOffset, pointCount, 0, 0  (reinterpreted as float bits)
            LastFramePointsDrawn = 0;
            // minDrawError comparison is also in squared space.
            float sqMinDrawError = sqErrorThreshold * (minDrawFrac * minDrawFrac);
            int   selectedCount  = _selectedCount;

            // Double-buffered GPU buffers: CPU writes slot [_bufferIndex], GPU reads slot [1-_bufferIndex].
            _bufferIndex = 1 - _bufferIndex;
            int bi = _bufferIndex;

            // Grow descriptor buffer for this slot if needed (50% headroom, never shrinks).
            if (selectedCount > _descriptorCapacity[bi])
            {
                int newCapacity = selectedCount + selectedCount / 2;
                _nodeDescriptorBuffer[bi]?.Release();
                _nodeDescriptorBuffer[bi] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity * 3, 16);
                _descriptorCapacity[bi]   = newCapacity;
                // Staging array is shared; grow it to the larger of the two slots' capacities.
                int stagingSize = newCapacity * 12;
                if (_nodeDescriptorData == null || _nodeDescriptorData.Length < stagingSize)
                    _nodeDescriptorData = new float[stagingSize];
            }
            if (_indirectArgsBuffer[bi] == null)
            {
                _indirectArgsBuffer[bi] = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));
                _indirectArgsData       = new uint[4];
            }

            // Single pass: fill descriptor array, tracking valid count and max point count inline.
            int  validCount    = 0;
            int  maxPointCount = 0;
            for (int s = 0; s < selectedCount; s++)
            {
                int   idx         = _selectedIndices[s];
                var   node        = _cullingNodes[idx];
                float sqError     = _selectedError[idx]; // stored squared
                if (node.PointCount == 0) continue;
                if (minDrawFrac > 0f && sqError < sqMinDrawError) continue;
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

            _nodeDescriptorBuffer[bi].SetData(_nodeDescriptorData, 0, 0, validCount * 12);
            _indirectArgsData[0] = (uint)(maxPointCount * 6); // 6 verts per point (2 triangles)
            // Under single-pass instanced stereo, Unity cannot auto-double the instance count for
            // indirect draws (the args buffer is opaque to it). We double manually so SV_InstanceID
            // covers [0, validCount*2), letting UNITY_SETUP_INSTANCE_ID decode eye (bit 0) and
            // node index (>> 1) correctly.
            bool stereoInstanced = XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced;
            _indirectArgsData[1] = (uint)(stereoInstanced ? validCount * 2 : validCount);
            _indirectArgsData[2] = 0;
            _indirectArgsData[3] = 0;
            _indirectArgsBuffer[bi].SetData(_indirectArgsData);

            // Bind this frame's buffer slot to the material — cheap with a single draw call per cloud.
            mat.SetBuffer(PropNodeDescriptors, _nodeDescriptorBuffer[bi]);

            if (_indirectDrawable == null)
            {
                _indirectDrawable = new IndirectDrawable();
                PointCloudRenderFeature.PointCloudRenderPass.Register(_indirectDrawable);
            }
            _indirectDrawable.Set(mat, _indirectArgsBuffer[bi], localToWorld);

            // Swap expanded-flags arrays: this frame's expansions become next frame's hysteresis reference.
            (_prevExpandedFlags, _expandedFlags) = (_expandedFlags, _prevExpandedFlags);
        }


        // ----- Foveation helpers -----

        // Returns t in [0,1]: 0 = inside foveal zone (no penalty), 1 = full peripheral penalty.
        // Uses Chebyshev distance in viewport space. Nodes whose projected extent overlaps the
        // inner zone are protected even if their centre lies outside it.
        // t ramps linearly from 0 at the inner boundary to 1 over a zone of width fovInner.
        // sqError is the squared screen error from HeapPush.
        private float FoveationT(Matrix4x4 vpMatrix, float fovInner,
            Vector3 worldCenter, float sqError, float halfFovTan)
        {
            var  h = vpMatrix * new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, 1f);
            if (h.w <= 0f) return 0f; // centre behind camera — never penalise
            float invW = 1f / h.w;
            float vpx  = h.x * invW * 0.5f + 0.5f;
            float vpy  = h.y * invW * 0.5f + 0.5f;
            // Inline Clamp and Abs — no Mathf call overhead.
            float cx   = vpx < 0f ? 0f : vpx > 1f ? 1f : vpx;
            float cy   = vpy < 0f ? 0f : vpy > 1f ? 1f : vpy;
            float dx   = cx - FoveationCentre.x; if (dx < 0f) dx = -dx;
            float dy   = cy - FoveationCentre.y; if (dy < 0f) dy = -dy;
            float r    = dx > dy ? dx : dy;
            // Early exit: centre clearly inside or outside the transition zone without nodeRadius correction.
            if (r <= fovInner)            return 0f;
            if (r >= fovInner + fovInner) return 1f;
            // Near the boundary — apply nodeRadius correction (one Sqrt, justified by rarity).
            float nodeRadius = Mathf.Sqrt(sqError) * halfFovTan * 0.5f;
            float excess = (r - nodeRadius) - fovInner;
            if (fovInner > 0f)
            {
                float t = excess / fovInner;
                return t < 0f ? 0f : t > 1f ? 1f : t;
            }
            return excess > 0f ? 1f : 0f;
        }

        // ----- Heap push/pop (max-heap on ScreenError) -----

        private void HeapPush(BVHNode node, BVHNode parent, Vector3 camPos, float halfFovTan,
            Matrix4x4 m, bool occlusionCull)
        {
            if (node == null) return;
            if (occlusionCull && _occludedFlags[node.IndexInRenderer]) return;

            // Transform AABB — no Bounds construction, no property getters.
            float lx = node.BoundsMin.x,  ly = node.BoundsMin.y,  lz = node.BoundsMin.z;
            float sx = node.BoundsSize.x, sy = node.BoundsSize.y, sz = node.BoundsSize.z;
            float cx = lx + sx * 0.5f,   cy = ly + sy * 0.5f,    cz = lz + sz * 0.5f;
            float wcx = m.m00 * cx + m.m01 * cy + m.m02 * cz + m.m03;
            float wcy = m.m10 * cx + m.m11 * cy + m.m12 * cz + m.m13;
            float wcz = m.m20 * cx + m.m21 * cy + m.m22 * cz + m.m23;
            float ex  = sx * 0.5f, ey = sy * 0.5f, ez = sz * 0.5f;
            // Inline Abs — avoids Mathf.Abs call overhead; compiler sees constant-sign branches.
            float m00 = m.m00, m01 = m.m01, m02 = m.m02;
            float m10 = m.m10, m11 = m.m11, m12 = m.m12;
            float m20 = m.m20, m21 = m.m21, m22 = m.m22;
            float wex = (m00 >= 0f ? m00 : -m00) * ex + (m01 >= 0f ? m01 : -m01) * ey + (m02 >= 0f ? m02 : -m02) * ez;
            float wey = (m10 >= 0f ? m10 : -m10) * ex + (m11 >= 0f ? m11 : -m11) * ey + (m12 >= 0f ? m12 : -m12) * ez;
            float wez = (m20 >= 0f ? m20 : -m20) * ex + (m21 >= 0f ? m21 : -m21) * ey + (m22 >= 0f ? m22 : -m22) * ez;
            float wminX = wcx - wex, wminY = wcy - wey, wminZ = wcz - wez;
            float wmaxX = wcx + wex, wmaxY = wcy + wey, wmaxZ = wcz + wez;

            // Frustum test using pre-extracted plane floats — no Plane property access.
            float nx, ny, nz, d;
            nx = _fpNx[0]; ny = _fpNy[0]; nz = _fpNz[0]; d = _fpD[0];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            nx = _fpNx[1]; ny = _fpNy[1]; nz = _fpNz[1]; d = _fpD[1];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            nx = _fpNx[2]; ny = _fpNy[2]; nz = _fpNz[2]; d = _fpD[2];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            nx = _fpNx[3]; ny = _fpNy[3]; nz = _fpNz[3]; d = _fpD[3];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            nx = _fpNx[4]; ny = _fpNy[4]; nz = _fpNz[4]; d = _fpD[4];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            nx = _fpNx[5]; ny = _fpNy[5]; nz = _fpNz[5]; d = _fpD[5];
            if (nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;

            // Squared screen error — eliminates Sqrt entirely.
            // sqError = (maxExtent / (dist * halfFovTan))² = maxExtent² / (sqrDist * halfFovTan²)
            float ddx = camPos.x < wminX ? wminX - camPos.x : camPos.x > wmaxX ? camPos.x - wmaxX : 0f;
            float ddy = camPos.y < wminY ? wminY - camPos.y : camPos.y > wmaxY ? camPos.y - wmaxY : 0f;
            float ddz = camPos.z < wminZ ? wminZ - camPos.z : camPos.z > wmaxZ ? camPos.z - wmaxZ : 0f;
            float sqrDist   = ddx * ddx + ddy * ddy + ddz * ddz;
            float maxExtent = wex > wey ? (wex > wez ? wex : wez) : (wey > wez ? wey : wez);
            float sqError   = sqrDist > 0.000001f
                ? (maxExtent * maxExtent) / (sqrDist * halfFovTan * halfFovTan)
                : float.MaxValue;

            var worldCenter = new Vector3(wcx, wcy, wcz);
            _heap[_heapCount] = new QueueEntry(node, parent, sqError, worldCenter);
            int i = _heapCount++;
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
            int last = --_heapCount;
            _heap[0] = _heap[last];
            int i     = 0;
            while (true)
            {
                int l       = (i << 1) + 1;
                int r       = l + 1;
                int largest = i;
                if (l < last && _heap[l].ScreenError > _heap[largest].ScreenError) largest = l;
                if (r < last && _heap[r].ScreenError > _heap[largest].ScreenError) largest = r;
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
            for (int i = 0; i < 2; i++)
            {
                _nodeDescriptorBuffer[i]?.Release();
                _nodeDescriptorBuffer[i] = null;
                _indirectArgsBuffer[i]?.Release();
                _indirectArgsBuffer[i]   = null;
                _descriptorCapacity[i]   = 0;
            }
            _nodeDescriptorData = null;
            _indirectArgsData   = null;
            _bufferIndex        = 0;
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
