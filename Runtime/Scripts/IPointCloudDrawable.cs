using UnityEngine;
using UnityEngine.Rendering;

namespace StoryLabResearch.PointCloud
{
    public interface IPointCloudDrawable
    {
        Bounds WorldBounds { get; }
        void Draw(CommandBuffer cmd);
    }
}
