using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace StoryLabResearch.PointCloud
{
    public interface IPointCloudDrawable
    {
        void Draw(RasterCommandBuffer cmd);

        // The scene this drawable belongs to. Used to keep the cloud out of cameras that
        // belong to a different scene — e.g. prefab-stage and prefab-icon preview cameras,
        // which set Camera.scene to their own isolated scene.
        bool ShouldRenderTo(Camera camera);
    }
}
