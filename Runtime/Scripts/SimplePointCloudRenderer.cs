using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace StoryLabResearch.PointCloud
{
    // Test harness: validates the URP Octree shader and PointCloudRenderFeature against an
    // existing imported mesh. Not part of the final octree pipeline.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class SimplePointCloudRenderer : MonoBehaviour, IPointCloudDrawable
    {
        [SerializeField] private Material _material;
        [SerializeField] private MeshFilter _source;

        private ComputeBuffer _pointBuffer;
        private MaterialPropertyBlock _propertyBlock;
        private int _pointCount;

        private void OnEnable()
        {
            if (_source == null)
                _source = GetComponent<MeshFilter>();

            var mesh = _source.sharedMesh;
            if (mesh == null) return;

            var localVertices = mesh.vertices;
            _pointCount = localVertices.Length;
            var matrix = transform.localToWorldMatrix;

            var colors32 = mesh.colors32;
            bool hasColors = colors32 != null && colors32.Length == _pointCount;

            // Compute world-space bounds for quantization.
            var worldBounds = new Bounds(matrix.MultiplyPoint3x4(localVertices[0]), Vector3.zero);
            for (int i = 1; i < _pointCount; i++)
                worldBounds.Encapsulate(matrix.MultiplyPoint3x4(localVertices[i]));
            var bMin  = worldBounds.min;
            var bSize = worldBounds.size;
            float invX = bSize.x > 0 ? 65535f / bSize.x : 0f;
            float invY = bSize.y > 0 ? 65535f / bSize.y : 0f;
            float invZ = bSize.z > 0 ? 65535f / bSize.z : 0f;

            // Pack into uint3 (12 bytes): same format as OctreeAsset.
            // All points use octant 0; _ActiveOctantMask = 0xFF enables all octants.
            var packed = new uint[_pointCount * 3];
            for (int i = 0; i < _pointCount; i++)
            {
                var wp = matrix.MultiplyPoint3x4(localVertices[i]) - bMin;
                uint qx = (uint)Mathf.Clamp(Mathf.RoundToInt(wp.x * invX), 0, 65535);
                uint qy = (uint)Mathf.Clamp(Mathf.RoundToInt(wp.y * invY), 0, 65535);
                uint qz = (uint)Mathf.Clamp(Mathf.RoundToInt(wp.z * invZ), 0, 65535);
                uint rgb = hasColors
                    ? (uint)colors32[i].r | ((uint)colors32[i].g << 8) | ((uint)colors32[i].b << 16)
                    : 0x00FFFFFFu;
                packed[i * 3 + 0] = (qy << 16) | qx;
                packed[i * 3 + 1] = rgb; // octant bits 24-26 = 0 (octant 0)
                packed[i * 3 + 2] = qz;
            }

            _pointBuffer = new ComputeBuffer(_pointCount, 12);
            _pointBuffer.SetData(packed);

            _propertyBlock = new MaterialPropertyBlock();
            _propertyBlock.SetBuffer("_Points", _pointBuffer);
            _propertyBlock.SetVector("_BoundsMin",  new Vector4(bMin.x,  bMin.y,  bMin.z,  0));
            _propertyBlock.SetVector("_BoundsSize", new Vector4(bSize.x, bSize.y, bSize.z, 0));
            _propertyBlock.SetInt("_ActiveOctantMask", 0xFF);

            PointCloudRenderFeature.PointCloudRenderPass.Register(this);
        }

        private void OnDisable()
        {
            PointCloudRenderFeature.PointCloudRenderPass.Deregister(this);
            _pointBuffer?.Release();
            _pointBuffer = null;
        }

        public void Draw(RasterCommandBuffer cmd)
        {
            cmd.DrawProcedural(Matrix4x4.identity, _material, 0,
                MeshTopology.Triangles, _pointCount * 6, 1, _propertyBlock);
        }
    }
}
