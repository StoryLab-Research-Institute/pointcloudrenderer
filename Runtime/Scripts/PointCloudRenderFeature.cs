using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace StoryLabResearch.PointCloud
{
    public class PointCloudRenderFeature : ScriptableRendererFeature
    {
        private PointCloudRenderPass _pass;

        public override void Create()
        {
            _pass = new PointCloudRenderPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            // Pass owns no GPU resources — drawables own their buffers.
            _pass = null;
        }

        public class PointCloudRenderPass : ScriptableRenderPass
        {
            private static readonly List<IPointCloudDrawable> _drawables = new();

            // Passed into the render graph lambda via PassData to avoid closures over statics.
            private class PassData
            {
                public List<IPointCloudDrawable> Drawables;
                public Camera Camera;
            }

            public PointCloudRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            }

            public static void Register(IPointCloudDrawable drawable) => _drawables.Add(drawable);

            public static void Deregister(IPointCloudDrawable drawable)
            {
                _drawables.Remove(drawable);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_drawables.Count == 0) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData   = frameData.Get<UniversalCameraData>();

                using var builder = renderGraph.AddRasterRenderPass<PassData>("PointCloud", out var passData);
                passData.Drawables = _drawables;
                passData.Camera    = cameraData.camera;

                // Declare colour and depth attachments so URP can schedule depth priming correctly.
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    foreach (var drawable in data.Drawables)
                        if (drawable.ShouldRenderTo(data.Camera))
                            drawable.Draw(cmd);
                });
            }
        }
    }
}
