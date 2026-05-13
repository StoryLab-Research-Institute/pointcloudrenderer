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
            }

            public PointCloudRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            }

            public static void Register(IPointCloudDrawable drawable)
            {
                if (!_drawables.Contains(drawable))
                    _drawables.Add(drawable);
            }

            public static void Deregister(IPointCloudDrawable drawable)
            {
                _drawables.Remove(drawable);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_drawables.Count == 0) return;

                using var builder = renderGraph.AddUnsafePass<PassData>("PointCloud", out var passData);
                passData.Drawables = _drawables;

                // No declared resource handles — we draw into whatever the current render target is.
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    foreach (var drawable in data.Drawables)
                        drawable.Draw(cmd);
                });
            }
        }
    }
}
