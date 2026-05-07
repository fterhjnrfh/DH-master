using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Controls;
using DH.Contracts.Models;

namespace DH.Client.App.Controls
{
    // OpenGL 曲线视图：使用 CurveRenderer 进行曲线绘制
    public class OpenGlCurveView : OpenGlControlBase
    {
        private CurveRenderer? _renderer;

        // 数据提供委托（与 CurvePanel 对接）
        public Func<IReadOnlyList<CurvePoint>> DataProvider { get; set; } = () => Array.Empty<CurvePoint>();
        public Func<Dictionary<int, IReadOnlyList<CurvePoint>>> MultiChannelDataProvider { get; set; } = () => new Dictionary<int, IReadOnlyList<CurvePoint>>();

        // 仅使用纵向缩放（与 CurveRenderer 的 uZoom 对应）
        public Func<float> GetZoomY { get; set; } = () => 1.0f;

        protected override void OnOpenGlInit(GlInterface gl)
        {
            _renderer = new CurveRenderer(gl);
            _renderer.Initialize();
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _renderer?.Dispose();
            _renderer = null;
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_renderer == null)
                return;

            // 清理背景（深色）
            gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

            // 更新缩放（仅 Y 轴）
            _renderer.SetZoomLevel(GetZoomY?.Invoke() ?? 1.0f);

            // 优先多通道
            var multi = MultiChannelDataProvider?.Invoke();
            if (multi != null && multi.Count > 0)
            {
                _renderer.RenderMultiChannel(multi);
                return;
            }

            // 回退单通道
            var single = DataProvider?.Invoke();
            if (single != null && single.Count > 0)
            {
                _renderer.Render(single);
            }
        }
    }
}