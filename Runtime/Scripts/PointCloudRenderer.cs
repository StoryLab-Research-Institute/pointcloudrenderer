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
        // One entry per variant, parallel to the PointCloudVariant[] on the importer.
        // The variant SO carries runtime params; the BVHAsset is the built artefact for that variant.
        [Serializable]
        public struct VariantEntry
        {
            public PointCloudVariant Variant;
            [HideInInspector] public BVHAsset Asset;

            [HideInInspector]
            public Material ResolvedMaterial;
        }

        [SerializeField] private VariantEntry[] _variants = Array.Empty<VariantEntry>();

        [Tooltip("Index into the variant list. 0 = first (highest priority) entry. " +
                 "Set at runtime via SetVariantByIndex / SetVariantByName. " +
                 "Clamped to the available range if the list is shorter than expected " +
                 "(e.g. after build stripping).")]
        [SerializeField] private int _activeVariantIndex;

        [Tooltip("Multiplier on the render properties PointBudget.")]
        public float PointBudgetMultiplier = 1f;

        [Tooltip("Multiplier on the render properties PointSizeScale.")]
        public float PointSizeMultiplier = 1f;

        [Tooltip("Multiplier on the render properties ScreenErrorThreshold. " +
                 "Increase to reduce detail (coarser LOD), decrease for more detail.")]
        public float ScreenErrorMultiplier = 1f;

        [Tooltip("Multiplier on the render properties MinDrawErrorFraction.")]
        public float MinDrawErrorMultiplier = 1f;

        [Tooltip("Use Unity's occlusion bake to skip nodes hidden behind scene geometry. " +
                 "Useful when the camera is inside a dense cloud with many occluders. " +
                 "Has no effect in the editor — the editor does not run the occlusion rasteriser.")]
        public bool OcclusionCulling = true;

        // ----- Variant resolution -----

        private int ClampedVariantIndex =>
            _variants is not { Length: > 0 } ? -1
            : Mathf.Clamp(_activeVariantIndex, 0, _variants.Length - 1);

        private VariantEntry? ActiveEntry
        {
            get
            {
                int i = ClampedVariantIndex;
                return i < 0 ? (VariantEntry?)null : _variants[i];
            }
        }

        private BVHAsset           ActiveAsset      => ActiveEntry?.Asset;
        private Material           ActiveMaterial   => ActiveEntry?.ResolvedMaterial;
        private PointCloudVariant  ActiveVariant    => ActiveEntry?.Variant;

        // ----- Public API -----

        public int VariantCount => _variants?.Length ?? 0;

        public int ActiveVariantIndex
        {
            get => ClampedVariantIndex;
            set => _activeVariantIndex = value;
        }

        /// <summary>
        /// Selects the first variant whose VariantName contains <paramref name="name"/>
        /// (case-insensitive). Returns true if a match was found.
        /// </summary>
        public bool SetVariantByName(string name)
        {
            if (_variants == null) return false;
            for (int i = 0; i < _variants.Length; i++)
            {
                if (_variants[i].Variant != null &&
                    _variants[i].Variant.VariantName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _activeVariantIndex = i;
                    return true;
                }
            }
            return false;
        }

        // ----- Editor support -----

#if UNITY_EDITOR
        public VariantEntry[] VariantsForBuild
        {
            get => _variants;
            set => _variants = value;
        }

        public void SetImportedVariants(VariantEntry[] variants)
        {
            _variants            = variants;
            _activeVariantIndex  = 0;
        }

        public void RestoreVariantAsset(int index, BVHAsset asset)
        {
            if (_variants == null || index >= _variants.Length) return;
            _variants[index].Asset = asset;
        }

        public void RestoreVariantMaterial(int index, Material material)
        {
            if (_variants == null || index >= _variants.Length) return;
            _variants[index].ResolvedMaterial = material;
        }
#endif

        // ----- Diagnostics -----

#if UNITY_EDITOR
        [Tooltip("In edit mode, follow the scene view camera instead of Camera.main.")]
        [SerializeField] private bool UseSceneCameraInEditMode = true;
#endif

        // Fovea position in normalised viewport space. Defaults to screen centre.
        [NonSerialized] public Vector2 FoveationCentre = new Vector2(0.5f, 0.5f);

        // Diagnostic — updated each frame.
        [NonSerialized] public int LastFramePointsDrawn;

        // ----- Default fallbacks when no variant is assigned -----

        private const int   DefaultPointBudget          = 2_000_000;
        private const float DefaultScreenErrorThreshold = 0.2f;
        private const float DefaultMinDrawErrorFraction = 0.1f;
        private const float DefaultPointSizeScale       = 0.01f;

        private int   P_PointBudget          => Mathf.Max(1, Mathf.RoundToInt(
                                                    (ActiveVariant?.PointBudget ?? DefaultPointBudget)
                                                    * PointBudgetMultiplier));
        private float P_ScreenErrorThreshold => (ActiveVariant?.ScreenErrorThreshold ?? DefaultScreenErrorThreshold)
                                                    * ScreenErrorMultiplier;
        private float P_MinDrawErrorFraction => (ActiveVariant?.MinDrawErrorFraction ?? DefaultMinDrawErrorFraction)
                                                    * MinDrawErrorMultiplier;
        private float P_PointSizeScale       => (ActiveVariant?.PointSizeScale ?? DefaultPointSizeScale)
                                                    * PointSizeMultiplier;

        private bool  P_OcclusionCulling     => OcclusionCulling;
        private bool  P_FoveationEnabled     => ActiveVariant?.FoveationEnabled        ?? false;
        private float P_FoveationStrength    => ActiveVariant?.FoveationStrength       ?? 32f;
        private float P_FoveationInnerRadius => ActiveVariant?.FoveationInnerRadius    ?? 0.2f;
        private float P_LodHysteresis        => ActiveVariant?.LodHysteresis           ?? 0.2f;

        private static readonly int PropPoints          = Shader.PropertyToID("_Points");
        private static readonly int PropNodeDescriptors = Shader.PropertyToID("_NodeDescriptors");

#if UNITY_EDITOR
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
#endif

        private BVHAsset  _lastActiveAsset;
        private Material  _activeMaterialInstance;
        private Material  _lastSourceMaterial;
        private int       _lastSourceMaterialCRC;

        // CullingGroup state.
        private CullingGroup     _cullingGroup;
        private BVHNode[]        _cullingNodes;
        private BoundingSphere[] _cullingSpheres;
        private int[]            _cullingResults;
        private Camera           _cullingCamera;
        private Matrix4x4        _lastLocalToWorld;

        // Per-node indexed state — parallel arrays over _cullingNodes, looked up via _nodeIndex.
        private Dictionary<BVHNode, int> _nodeIndex = new();
        private bool[]           _occludedFlags     = new bool[0];
        private bool[]           _expandedFlags     = new bool[0];
        private bool[]           _prevExpandedFlags = new bool[0];
        // _selectedError[i] >= 0 means node i is selected this frame; -1f means not selected.
        private float[]          _selectedError     = new float[0];
        private int[]            _selectedIndices   = Array.Empty<int>();
        private int              _selectedCount     = 0;

        private QueueEntry[] _heap      = Array.Empty<QueueEntry>();
        private int          _heapCount = 0;

        private readonly Plane[] _frustumPlanes  = new Plane[6];
        private readonly Plane[] _frustumPlanesR = new Plane[6];
        // Frustum plane components extracted to flat floats — avoids Plane.normal/distance property
        // interop in the HeapPush hot path (called once per node visited, 6 planes each).
        private readonly float[] _fpNx  = new float[6], _fpNy  = new float[6], _fpNz  = new float[6], _fpD  = new float[6];
        private readonly float[] _fpNxR = new float[6], _fpNyR = new float[6], _fpNzR = new float[6], _fpDR = new float[6];
        private bool _stereoFrustum;

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
        private GraphicsBuffer _lastBoundPointBuffer; // the GlobalPointBuffer instance currently bound to the material
        private IndirectDrawable _indirectDrawable;   // single registered drawable

        private void DestroyMaterialInstance()
        {
            if (_activeMaterialInstance == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(_activeMaterialInstance);
            else
#endif
                UnityEngine.Object.Destroy(_activeMaterialInstance);
            _activeMaterialInstance = null;
            _lastSourceMaterial     = null;
            _lastBoundPointBuffer   = null;
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
            DestroyMaterialInstance();
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (this == null || Application.isPlaying || !isActiveAndEnabled) return;
            Update();
        }
#endif

        private void Update()
        {
            var asset = ActiveAsset;
            asset?.Load();
            if (asset?.Root == null)
            {
                DeregisterIndirect();
                return;
            }

            var cam = ResolveCamera();
            if (cam == null)
            {
                DeregisterIndirect();
                return;
            }

            if (asset != _lastActiveAsset)
            {
                DisposeCullingGroup();
                DisposeIndirectBuffers();
                DestroyMaterialInstance();
                _selectedCount   = 0;
                _lastActiveAsset = asset;
            }

            if (P_OcclusionCulling)
                RefreshCullingGroup(cam, asset);
            else
                EnsureNodeIndex(asset);

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
            _nodeIndex = new Dictionary<BVHNode, int>(n);
            for (int i = 0; i < n; i++)
                _nodeIndex[_cullingNodes[i]] = i;

            _occludedFlags = new bool[n];
            _expandedFlags = new bool[n];
            _selectedError = new float[n];
            Array.Fill(_selectedError, -1f);
            // Treat all nodes as previously expanded on first frame so the LOD boundary starts
            // conservative rather than over-expanding into nodes too small to pass minDrawError.
            _prevExpandedFlags = new bool[n];
            Array.Fill(_prevExpandedFlags, true);

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
            _cullingNodes     = null;
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

            var   camPos     = cam.transform.position;
            // Derive halfFovTan from the projection matrix so it remains correct even when
            // cam.fieldOfView hasn't been updated by the XR runtime yet (e.g. Quest Link init).
            float halfFovTan = 1f / cam.projectionMatrix.m11;

            bool stereoInstanced = XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced;
            _stereoFrustum = stereoInstanced;
            Matrix4x4 vpMatrix;
            if (stereoInstanced)
            {
                var leftVP  = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)  * cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                var rightVP = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
                // Average the two eye VP matrices for foveation — the IPD offset is negligible
                // at typical viewing distances and foveation is already a soft approximation.
                vpMatrix = Matrix4x4.zero;
                for (int mi = 0; mi < 16; mi++)
                    vpMatrix[mi] = (leftVP[mi] + rightVP[mi]) * 0.5f;
                GeometryUtility.CalculateFrustumPlanes(leftVP,  _frustumPlanes);
                GeometryUtility.CalculateFrustumPlanes(rightVP, _frustumPlanesR);
                for (int pi = 0; pi < 6; pi++)
                {
                    var ln     = _frustumPlanes[pi].normal;
                    _fpNx[pi]  = ln.x; _fpNy[pi]  = ln.y; _fpNz[pi]  = ln.z; _fpD[pi]  = _frustumPlanes[pi].distance;
                    var rn     = _frustumPlanesR[pi].normal;
                    _fpNxR[pi] = rn.x; _fpNyR[pi] = rn.y; _fpNzR[pi] = rn.z; _fpDR[pi] = _frustumPlanesR[pi].distance;
                }
            }
            else
            {
                vpMatrix = cam.projectionMatrix * cam.worldToCameraMatrix;
                GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
                for (int pi = 0; pi < 6; pi++)
                {
                    var n      = _frustumPlanes[pi].normal;
                    _fpNx[pi]  = n.x; _fpNy[pi]  = n.y; _fpNz[pi]  = n.z; _fpD[pi]  = _frustumPlanes[pi].distance;
                }
            }

            bool  fovEnabled     = P_FoveationEnabled;
            float fovStrength    = P_FoveationStrength;
            float fovInner       = P_FoveationInnerRadius;
            float errorThreshold = P_ScreenErrorThreshold;
            float minDrawFrac    = P_MinDrawErrorFraction;
            float lodHysteresis  = P_LodHysteresis;
            bool  occlusionCull  = P_OcclusionCulling;
            int   remaining      = P_PointBudget;

            float sqErrorThreshold    = errorThreshold * errorThreshold;
            float hysteresisMul       = 1f - lodHysteresis;
            float sqMinEffThreshold   = sqErrorThreshold * (hysteresisMul * hysteresisMul);

            var sourceMat = ActiveMaterial;
            int sourceCRC = sourceMat.ComputeCRC();
            if (_activeMaterialInstance == null || sourceMat != _lastSourceMaterial || sourceCRC != _lastSourceMaterialCRC)
            {
                DestroyMaterialInstance();
                _activeMaterialInstance = new Material(sourceMat) { hideFlags = HideFlags.HideAndDontSave };
                _lastSourceMaterial     = sourceMat;
                _lastSourceMaterialCRC  = sourceCRC;
            }
            var   mat           = _activeMaterialInstance;
            float pointSizeBase = P_PointSizeScale * 0.5f;

            if (asset.GlobalPointBuffer != null && asset.GlobalPointBuffer != _lastBoundPointBuffer)
            {
                mat.SetBuffer(PropPoints, asset.GlobalPointBuffer);
                _lastBoundPointBuffer = asset.GlobalPointBuffer;
            }

            for (int i = 0; i < _selectedCount; i++)
                _selectedError[_selectedIndices[i]] = -1f;
            _selectedCount = 0;

            Array.Clear(_expandedFlags, 0, _expandedFlags.Length);

            _heapCount = 0;
            HeapPush(asset.Root, null, camPos, halfFovTan, localToWorld, occlusionCull, _stereoFrustum);

            while (_heapCount > 0)
            {
                var entry = HeapPop();
                var node  = entry.Node;

                if (!node.IsLoaded)
                {
                    var parent = entry.Parent;
                    if (parent != null && parent.IsLoaded)
                    {
                        int pi = _nodeIndex[parent];
                        if (_selectedError[pi] < 0f)
                            _selectedIndices[_selectedCount++] = pi;
                        _selectedError[pi] = entry.ScreenError;
                    }
                    continue;
                }

                if (entry.ScreenError < sqMinEffThreshold || remaining <= 0)
                {
                    int nodeIdx = _nodeIndex[node];
                    if (_selectedError[nodeIdx] < 0f)
                        _selectedIndices[_selectedCount++] = nodeIdx;
                    _selectedError[nodeIdx] = entry.ScreenError;
                    remaining -= node.PointCount;

                    while (_heapCount > 0)
                    {
                        var e = HeapPop();
                        var n = e.Node;
                        if (!n.IsLoaded) continue;
                        int ni = _nodeIndex[n];
                        if (_selectedError[ni] < 0f)
                            _selectedIndices[_selectedCount++] = ni;
                        _selectedError[ni] = e.ScreenError;
                    }
                    break;
                }

                float sqEffectiveThreshold = sqErrorThreshold;
                if (fovEnabled)
                {
                    float t = FoveationT(vpMatrix, fovInner, entry.WorldCenter, entry.WorldExtentX, entry.WorldExtentY, entry.WorldExtentZ);
                    float mul = 1f + (fovStrength - 1f) * t;
                    sqEffectiveThreshold *= mul * mul;
                }

                int  nIdx        = _nodeIndex[node];
                bool wasExpanded = _prevExpandedFlags[nIdx];
                float sqThreshold = wasExpanded
                    ? sqEffectiveThreshold * (hysteresisMul * hysteresisMul)
                    : sqEffectiveThreshold;

                if (entry.ScreenError >= sqThreshold && !node.IsLeaf && remaining > 0)
                {
                    _expandedFlags[nIdx] = true;
                    if (node.Left  != null) HeapPush(node.Left,  node, camPos, halfFovTan, localToWorld, occlusionCull, _stereoFrustum);
                    if (node.Right != null) HeapPush(node.Right, node, camPos, halfFovTan, localToWorld, occlusionCull, _stereoFrustum);
                    continue;
                }

                if (_selectedError[nIdx] < 0f)
                    _selectedIndices[_selectedCount++] = nIdx;
                _selectedError[nIdx] = entry.ScreenError;
                remaining -= node.PointCount;
            }

            LastFramePointsDrawn = 0;
            float sqMinDrawError = sqErrorThreshold * (minDrawFrac * minDrawFrac);
            int   selectedCount  = _selectedCount;

            _bufferIndex = 1 - _bufferIndex;
            int bi = _bufferIndex;

            if (selectedCount > _descriptorCapacity[bi])
            {
                int newCapacity = selectedCount + selectedCount / 2;
                _nodeDescriptorBuffer[bi]?.Release();
                _nodeDescriptorBuffer[bi] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity * 3, 16);
                _descriptorCapacity[bi]   = newCapacity;
                int stagingSize = newCapacity * 12;
                if (_nodeDescriptorData == null || _nodeDescriptorData.Length < stagingSize)
                    _nodeDescriptorData = new float[stagingSize];
            }
            if (_indirectArgsBuffer[bi] == null)
            {
                _indirectArgsBuffer[bi] = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));
                _indirectArgsData       = new uint[4];
            }

            int  validCount    = 0;
            int  maxPointCount = 0;
            for (int s = 0; s < selectedCount; s++)
            {
                int   idx         = _selectedIndices[s];
                var   node        = _cullingNodes[idx];
                float sqError     = _selectedError[idx];
                if (node.PointCount == 0) continue;
                if (minDrawFrac > 0f && sqError < sqMinDrawError) continue;
                bool leftSelected  = node.Left  != null && _selectedError[_nodeIndex[node.Left]]  >= 0f;
                bool rightSelected = node.Right != null && _selectedError[_nodeIndex[node.Right]] >= 0f;
                if (leftSelected && rightSelected) continue;

                if (node.PointCount > maxPointCount) maxPointCount = node.PointCount;

                float lodScale = node.LodScaleBase * pointSizeBase;
                int   o        = validCount * 12;

                _nodeDescriptorData[o + 0] = node.BoundsMin.x;
                _nodeDescriptorData[o + 1] = node.BoundsMin.y;
                _nodeDescriptorData[o + 2] = node.BoundsMin.z;
                _nodeDescriptorData[o + 3] = lodScale;
                _nodeDescriptorData[o + 4] = node.BoundsSize.x;
                _nodeDescriptorData[o + 5] = node.BoundsSize.y;
                _nodeDescriptorData[o + 6] = node.BoundsSize.z;
                _nodeDescriptorData[o + 7] = 0f;
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
            _indirectArgsData[0] = (uint)(maxPointCount * 6);
            _indirectArgsData[1] = (uint)(stereoInstanced ? validCount * 2 : validCount);
            _indirectArgsData[2] = 0;
            _indirectArgsData[3] = 0;
            _indirectArgsBuffer[bi].SetData(_indirectArgsData);

            mat.SetBuffer(PropNodeDescriptors, _nodeDescriptorBuffer[bi]);

            if (_indirectDrawable == null)
            {
                _indirectDrawable = new IndirectDrawable();
                PointCloudRenderFeature.PointCloudRenderPass.Register(_indirectDrawable);
#if UNITY_EDITOR
                SceneView.RepaintAll();
#endif
            }
            _indirectDrawable.Set(mat, _indirectArgsBuffer[bi], localToWorld);

            (_prevExpandedFlags, _expandedFlags) = (_expandedFlags, _prevExpandedFlags);
        }


        // ----- Foveation helpers -----

        private float FoveationT(Matrix4x4 vp, float fovInner, Vector3 worldCenter,
            float wex, float wey, float wez)
        {
            float hx = vp.m00 * worldCenter.x + vp.m01 * worldCenter.y + vp.m02 * worldCenter.z + vp.m03;
            float hy = vp.m10 * worldCenter.x + vp.m11 * worldCenter.y + vp.m12 * worldCenter.z + vp.m13;
            float hw = vp.m30 * worldCenter.x + vp.m31 * worldCenter.y + vp.m32 * worldCenter.z + vp.m33;
            if (hw <= 0f) return 0f;
            float invW = 1f / hw;
            float vpx  = hx * invW * 0.5f + 0.5f;
            float vpy  = hy * invW * 0.5f + 0.5f;

            float m00 = vp.m00, m01 = vp.m01, m02 = vp.m02;
            float m10 = vp.m10, m11 = vp.m11, m12 = vp.m12;
            float sex = ((m00 >= 0f ? m00 : -m00) * wex + (m01 >= 0f ? m01 : -m01) * wey + (m02 >= 0f ? m02 : -m02) * wez) * invW * 0.5f;
            float sey = ((m10 >= 0f ? m10 : -m10) * wex + (m11 >= 0f ? m11 : -m11) * wey + (m12 >= 0f ? m12 : -m12) * wez) * invW * 0.5f;

            float sminX = vpx - sex, smaxX = vpx + sex;
            float sminY = vpy - sey, smaxY = vpy + sey;

            float fcx = FoveationCentre.x, fcy = FoveationCentre.y;
            float closestX = fcx < sminX ? sminX : fcx > smaxX ? smaxX : fcx;
            float closestY = fcy < sminY ? sminY : fcy > smaxY ? smaxY : fcy;
            float dx = closestX - fcx; if (dx < 0f) dx = -dx;
            float dy = closestY - fcy; if (dy < 0f) dy = -dy;
            float r  = dx > dy ? dx : dy;

            if (r <= fovInner)            return 0f;
            if (r >= fovInner + fovInner) return 1f;
            float t = (r - fovInner) / fovInner;
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        // ----- Heap push/pop (max-heap on ScreenError) -----

        private void HeapPush(BVHNode node, BVHNode parent, Vector3 camPos, float halfFovTan,
            Matrix4x4 m, bool occlusionCull, bool stereoFrustum)
        {
            if (node == null) return;
            if (occlusionCull && _occludedFlags[_nodeIndex[node]]) return;

            float lx = node.BoundsMin.x,  ly = node.BoundsMin.y,  lz = node.BoundsMin.z;
            float sx = node.BoundsSize.x, sy = node.BoundsSize.y, sz = node.BoundsSize.z;
            float cx = lx + sx * 0.5f,   cy = ly + sy * 0.5f,    cz = lz + sz * 0.5f;
            float wcx = m.m00 * cx + m.m01 * cy + m.m02 * cz + m.m03;
            float wcy = m.m10 * cx + m.m11 * cy + m.m12 * cz + m.m13;
            float wcz = m.m20 * cx + m.m21 * cy + m.m22 * cz + m.m23;
            float ex  = sx * 0.5f, ey = sy * 0.5f, ez = sz * 0.5f;
            float m00 = m.m00, m01 = m.m01, m02 = m.m02;
            float m10 = m.m10, m11 = m.m11, m12 = m.m12;
            float m20 = m.m20, m21 = m.m21, m22 = m.m22;
            float wex = (m00 >= 0f ? m00 : -m00) * ex + (m01 >= 0f ? m01 : -m01) * ey + (m02 >= 0f ? m02 : -m02) * ez;
            float wey = (m10 >= 0f ? m10 : -m10) * ex + (m11 >= 0f ? m11 : -m11) * ey + (m12 >= 0f ? m12 : -m12) * ez;
            float wez = (m20 >= 0f ? m20 : -m20) * ex + (m21 >= 0f ? m21 : -m21) * ey + (m22 >= 0f ? m22 : -m22) * ez;
            float wminX = wcx - wex, wminY = wcy - wey, wminZ = wcz - wez;
            float wmaxX = wcx + wex, wmaxY = wcy + wey, wmaxZ = wcz + wez;

            float nx, ny, nz, d;
            // In stereo mode cull only if outside BOTH eye frustums.
            if (stereoFrustum)
            {
                nx = _fpNx[0]; ny = _fpNy[0]; nz = _fpNz[0]; d = _fpD[0];
                bool l0 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[0]; ny = _fpNyR[0]; nz = _fpNzR[0]; d = _fpDR[0];
                if (l0 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
                nx = _fpNx[1]; ny = _fpNy[1]; nz = _fpNz[1]; d = _fpD[1];
                bool l1 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[1]; ny = _fpNyR[1]; nz = _fpNzR[1]; d = _fpDR[1];
                if (l1 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
                nx = _fpNx[2]; ny = _fpNy[2]; nz = _fpNz[2]; d = _fpD[2];
                bool l2 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[2]; ny = _fpNyR[2]; nz = _fpNzR[2]; d = _fpDR[2];
                if (l2 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
                nx = _fpNx[3]; ny = _fpNy[3]; nz = _fpNz[3]; d = _fpD[3];
                bool l3 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[3]; ny = _fpNyR[3]; nz = _fpNzR[3]; d = _fpDR[3];
                if (l3 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
                nx = _fpNx[4]; ny = _fpNy[4]; nz = _fpNz[4]; d = _fpD[4];
                bool l4 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[4]; ny = _fpNyR[4]; nz = _fpNzR[4]; d = _fpDR[4];
                if (l4 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
                nx = _fpNx[5]; ny = _fpNy[5]; nz = _fpNz[5]; d = _fpD[5];
                bool l5 = nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f;
                nx = _fpNxR[5]; ny = _fpNyR[5]; nz = _fpNzR[5]; d = _fpDR[5];
                if (l5 && nx * (nx >= 0f ? wmaxX : wminX) + ny * (ny >= 0f ? wmaxY : wminY) + nz * (nz >= 0f ? wmaxZ : wminZ) + d < 0f) return;
            }
            else
            {
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
            }

            float ddx = camPos.x < wminX ? wminX - camPos.x : camPos.x > wmaxX ? camPos.x - wmaxX : 0f;
            float ddy = camPos.y < wminY ? wminY - camPos.y : camPos.y > wmaxY ? camPos.y - wmaxY : 0f;
            float ddz = camPos.z < wminZ ? wminZ - camPos.z : camPos.z > wmaxZ ? camPos.z - wmaxZ : 0f;
            float sqrDist   = ddx * ddx + ddy * ddy + ddz * ddz;
            float maxExtent = wex > wey ? (wex > wez ? wex : wez) : (wey > wez ? wey : wez);
            float sqError   = sqrDist > 0.000001f
                ? (maxExtent * maxExtent) / (sqrDist * halfFovTan * halfFovTan)
                : float.MaxValue;

            var worldCenter = new Vector3(wcx, wcy, wcz);
            _heap[_heapCount] = new QueueEntry(node, parent, sqError, worldCenter, wex, wey, wez);
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
            _nodeDescriptorData   = null;
            _indirectArgsData     = null;
            _bufferIndex          = 0;
            _lastBoundPointBuffer = null;
        }

        // Used only by the cold-path culling group sphere rebuild.
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
            public readonly Vector3 WorldCenter;
            public readonly float   WorldExtentX;
            public readonly float   WorldExtentY;
            public readonly float   WorldExtentZ;
            public QueueEntry(BVHNode node, BVHNode parent, float screenError,
                Vector3 worldCenter, float wex, float wey, float wez)
            {
                Node = node; Parent = parent; ScreenError = screenError;
                WorldCenter = worldCenter;
                WorldExtentX = wex; WorldExtentY = wey; WorldExtentZ = wez;
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
            var variant = ActiveVariant;
            if (variant == null || !variant.FoveationEnabled) return;

            var cam = Camera.current;
            if (cam == null) return;

            float   inner  = variant.FoveationInnerRadius;
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
