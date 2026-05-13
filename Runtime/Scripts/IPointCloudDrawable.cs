using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace StoryLabResearch.PointCloud
{
    public interface IPointCloudDrawable
    {
        void Draw(RasterCommandBuffer cmd);
    }
}
