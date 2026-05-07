using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.OpenGL;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;
using System.Runtime.InteropServices;

namespace DH.Client.App.Controls
{
    public class CurveRenderer : ICurveRenderer, IDisposable
    {
        private readonly GlInterface _gl;
        private int _vertexBuffer;
        private int _colorBuffer;
        private int _shaderProgram;
        private float _zoomLevel = 1.0f;
        private bool _initialized;
        private readonly Dictionary<int, int> _channelColors = new();

        // 预定义颜色调色板（RGB格式）
        private static readonly float[][] ColorPalette = new float[][]
        {
            new float[] { 0.0f, 1.0f, 0.0f }, // 绿色
            new float[] { 1.0f, 0.0f, 0.0f }, // 红色
            new float[] { 0.0f, 0.0f, 1.0f }, // 蓝色
            new float[] { 1.0f, 1.0f, 0.0f }, // 黄色
            new float[] { 1.0f, 0.0f, 1.0f }, // 洋红
            new float[] { 0.0f, 1.0f, 1.0f }, // 青色
            new float[] { 1.0f, 0.5f, 0.0f }, // 橙色
            new float[] { 0.5f, 0.0f, 1.0f }, // 紫色
            new float[] { 0.0f, 0.8f, 0.4f }, // 海绿色
            new float[] { 0.8f, 0.4f, 0.0f }, // 棕色
            new float[] { 0.6f, 0.6f, 0.6f }, // 灰色
            new float[] { 1.0f, 0.8f, 0.8f }, // 粉色
            new float[] { 0.8f, 1.0f, 0.8f }, // 浅绿色
            new float[] { 0.8f, 0.8f, 1.0f }, // 浅蓝色
            new float[] { 1.0f, 1.0f, 0.8f }, // 浅黄色
            new float[] { 0.9f, 0.9f, 0.9f }  // 浅灰色
        };

        // 着色器源码 - 支持颜色属性（桌面 OpenGL）
        private const string VertexShaderSource = @"
            #version 330 core
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec3 aColor;
            uniform float uZoom;
            out vec3 vColor;
            void main()
            {
                gl_Position = vec4(aPosition.x, aPosition.y * uZoom, 0.0, 1.0);
                vColor = aColor;
            }
        ";

        private const string FragmentShaderSource = @"
            #version 330 core
            in vec3 vColor;
            out vec4 FragColor;
            void main()
            {
                FragColor = vec4(vColor, 1.0);
            }
        ";

        // 着色器源码 - OpenGL ES 回退（Windows/ANGLE 等）
        private const string VertexShaderSourceES = @"
            #version 300 es
            precision mediump float;
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec3 aColor;
            uniform float uZoom;
            out vec3 vColor;
            void main()
            {
                gl_Position = vec4(aPosition.x, aPosition.y * uZoom, 0.0, 1.0);
                vColor = aColor;
            }
        ";

        private const string FragmentShaderSourceES = @"
            #version 300 es
            precision mediump float;
            in vec3 vColor;
            out vec4 FragColor;
            void main()
            {
                FragColor = vec4(vColor, 1.0);
            }
        ";

        public CurveRenderer(GlInterface gl)
        {
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        }

        public void Initialize()
        {
            if (_initialized)
                return;

            // 创建着色器程序（先尝试桌面 OpenGL，失败则回退到 GLES）
            try
            {
                _shaderProgram = CreateShaderProgram(VertexShaderSource, FragmentShaderSource);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CurveRenderer] Core shader failed, fallback to ES: {ex.Message}");
                _shaderProgram = CreateShaderProgram(VertexShaderSourceES, FragmentShaderSourceES);
            }

            // 创建顶点缓冲区和颜色缓冲区
            unsafe
            {
                int* buffers = stackalloc int[2];
                _gl.GenBuffers(2, buffers);
                _vertexBuffer = buffers[0];
                _colorBuffer = buffers[1];
            }

            _initialized = true;
        }

        public void SetZoomLevel(float zoomLevel)
        {
            _zoomLevel = zoomLevel;
        }

        // 获取通道颜色
        private float[] GetChannelColor(int channelId)
        {
            int colorIndex = (channelId - 1) % ColorPalette.Length;
            return ColorPalette[colorIndex];
        }

        // 多通道批量渲染方法
        public void RenderMultiChannel(Dictionary<int, IReadOnlyList<CurvePoint>> channelData)
        {
            if (!_initialized || channelData == null || channelData.Count == 0)
                return;

            // 清除屏幕
            _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            _gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

            // 使用着色器程序
            _gl.UseProgram(_shaderProgram);

            // 设置缩放级别
            unsafe
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes("uZoom\0");
                fixed (byte* namePtr = nameBytes)
                {
                    int zoomLocation = _gl.GetUniformLocation(_shaderProgram, (nint)namePtr);
                    _gl.Uniform1f(zoomLocation, _zoomLevel);
                }
            }

            // 为每个通道渲染曲线
            foreach (var kvp in channelData)
            {
                int channelId = kvp.Key;
                var data = kvp.Value;
                
                if (data == null || data.Count == 0)
                    continue;

                RenderSingleChannel(channelId, data);
            }

            // 清理
            _gl.UseProgram(0);
        }

        // 渲染单个通道
        private void RenderSingleChannel(int channelId, IReadOnlyList<CurvePoint> data)
        {
            if (data.Count < 2)
                return;

            var color = GetChannelColor(channelId);
            
            // 准备顶点数据
            float[] vertices = new float[data.Count * 2];
            float[] colors = new float[data.Count * 3];
            
            for (int i = 0; i < data.Count; i++)
            {
                // 将数据点转换为OpenGL坐标系 (-1 到 1)
                float x = (float)i / data.Count * 2.0f - 1.0f;
                float y = (float)data[i].Y;
                
                vertices[i * 2] = x;
                vertices[i * 2 + 1] = y;
                
                // 设置颜色
                colors[i * 3] = color[0];
                colors[i * 3 + 1] = color[1];
                colors[i * 3 + 2] = color[2];
            }

            // 绑定并上传顶点数据
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);
            var vertexSize = vertices.Length * 4; // sizeof(float) = 4
            IntPtr vertexPtr = Marshal.AllocHGlobal(vertexSize);
            try
            {
                Marshal.Copy(vertices, 0, vertexPtr, vertices.Length);
                _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, (IntPtr)vertexSize, vertexPtr, GlConsts.GL_STATIC_DRAW);
            }
            finally
            {
                Marshal.FreeHGlobal(vertexPtr);
            }
            _gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 8, IntPtr.Zero);
            _gl.EnableVertexAttribArray(0);

            // 绑定并上传颜色数据
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _colorBuffer);
            var colorSize = colors.Length * 4;
            IntPtr colorPtr = Marshal.AllocHGlobal(colorSize);
            try
            {
                Marshal.Copy(colors, 0, colorPtr, colors.Length);
                _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, (IntPtr)colorSize, colorPtr, GlConsts.GL_STATIC_DRAW);
            }
            finally
            {
                Marshal.FreeHGlobal(colorPtr);
            }
            _gl.VertexAttribPointer(1, 3, GlConsts.GL_FLOAT, 0, 12, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);

            // 绘制曲线（GL_LINE_STRIP = 0x0003）
            _gl.DrawArrays(3, 0, (IntPtr)data.Count);

            // 无需禁用属性数组，现代驱动通常无需清理
        }

        public void Render(IReadOnlyList<CurvePoint> data)
        {
            if (!_initialized || data == null || data.Count == 0)
                return;

            // 清除屏幕
            _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            _gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

            // 使用着色器程序
            _gl.UseProgram(_shaderProgram);

            // 设置缩放级别
            unsafe
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes("uZoom\0");
                fixed (byte* namePtr = nameBytes)
                {
                    int zoomLocation = _gl.GetUniformLocation(_shaderProgram, (nint)namePtr);
                    _gl.Uniform1f(zoomLocation, _zoomLevel);
                }
            }

            // 准备顶点数据
            float[] vertices = new float[data.Count * 2];
            for (int i = 0; i < data.Count; i++)
            {
                // 将数据点转换为OpenGL坐标系 (-1 到 1)
                float x = (float)i / data.Count * 2.0f - 1.0f;
                float y = (float)data[i].Y;
                
                vertices[i * 2] = x;
                vertices[i * 2 + 1] = y;
            }

            // 绑定顶点缓冲区
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vertexBuffer);

            // 将数据复制到缓冲区
            var vSize = vertices.Length * 4;
            IntPtr vPtr = Marshal.AllocHGlobal(vSize);
            try
            {
                Marshal.Copy(vertices, 0, vPtr, vertices.Length);
                _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, (IntPtr)vSize, vPtr, GlConsts.GL_STATIC_DRAW);
            }
            finally
            {
                Marshal.FreeHGlobal(vPtr);
            }

            // 设置顶点属性
            _gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 8, IntPtr.Zero);
            _gl.EnableVertexAttribArray(0);

            // 设置单通道颜色为调色板第一种（绿色）
            float[] color = ColorPalette[0];
            float[] colors = new float[data.Count * 3];
            for (int i = 0; i < data.Count; i++)
            {
                colors[i * 3] = color[0];
                colors[i * 3 + 1] = color[1];
                colors[i * 3 + 2] = color[2];
            }
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _colorBuffer);
            var cSize = colors.Length * 4;
            IntPtr cPtr = Marshal.AllocHGlobal(cSize);
            try
            {
                Marshal.Copy(colors, 0, cPtr, colors.Length);
                _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, (IntPtr)cSize, cPtr, GlConsts.GL_STATIC_DRAW);
            }
            finally
            {
                Marshal.FreeHGlobal(cPtr);
            }
            _gl.VertexAttribPointer(1, 3, GlConsts.GL_FLOAT, 0, 12, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);

            // 绘制曲线（GL_LINE_STRIP = 0x0003）
            _gl.DrawArrays(3, 0, (IntPtr)data.Count);

            // 清理
            _gl.UseProgram(0);
        }

        private int CreateShaderProgram(string vertexSource, string fragmentSource)
        {
            // 创建顶点着色器
            int vertexShader = _gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
            unsafe
            {
                var vsBytes = System.Text.Encoding.UTF8.GetBytes(vertexSource);
                fixed (byte* vs = vsBytes)
                {
                    byte** list = stackalloc byte*[1];
                    list[0] = vs;
                    int* lengths = stackalloc int[1];
                    lengths[0] = vsBytes.Length;
                    _gl.ShaderSource(vertexShader, 1, (nint)list, (nint)lengths);
                }
            }
            _gl.CompileShader(vertexShader);
            CheckShaderCompileStatus(vertexShader);

            // 创建片段着色器
            int fragmentShader = _gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
            unsafe
            {
                var fsBytes = System.Text.Encoding.UTF8.GetBytes(fragmentSource);
                fixed (byte* fs = fsBytes)
                {
                    byte** list = stackalloc byte*[1];
                    list[0] = fs;
                    int* lengths = stackalloc int[1];
                    lengths[0] = fsBytes.Length;
                    _gl.ShaderSource(fragmentShader, 1, (nint)list, (nint)lengths);
                }
            }
            _gl.CompileShader(fragmentShader);
            CheckShaderCompileStatus(fragmentShader);

            // 创建着色器程序
            int program = _gl.CreateProgram();
            _gl.AttachShader(program, vertexShader);
            _gl.AttachShader(program, fragmentShader);
            _gl.LinkProgram(program);
            CheckProgramLinkStatus(program);

            // 删除着色器
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            return program;
        }

        private void CheckShaderCompileStatus(int shader)
        {
            unsafe
            {
                int status = 0;
                _gl.GetShaderiv(shader, GlConsts.GL_COMPILE_STATUS, (int*)(&status));
                 if (status != 1)
                 {
                     _gl.GetShaderiv(shader, GlConsts.GL_INFO_LOG_LENGTH, (int*)(&status));
                     int len = status;
                     if (len <= 0)
                     {
                         throw new Exception("Shader compilation failed: unknown error");
                     }
                     byte* buffer = stackalloc byte[len];
                     int written = 0;
                     _gl.GetShaderInfoLog(shader, len, out written, (void*)buffer);
                     var log = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer, Math.Max(written, 0)));
                     throw new Exception($"Shader compilation failed: {log}");
                 }
            }
        }

        private void CheckProgramLinkStatus(int program)
        {
            unsafe
            {
                int status = 0;
                _gl.GetProgramiv(program, GlConsts.GL_LINK_STATUS, (int*)(&status));
                 if (status != 1)
                 {
                     _gl.GetProgramiv(program, GlConsts.GL_INFO_LOG_LENGTH, (int*)(&status));
                     int len = status;
                     if (len <= 0)
                     {
                         throw new Exception("Program linking failed: unknown error");
                     }
                     byte* buffer = stackalloc byte[len];
                     int written = 0;
                     _gl.GetProgramInfoLog(program, len, out written, (void*)buffer);
                     var log = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer, Math.Max(written, 0)));
                     throw new Exception($"Program linking failed: {log}");
                 }
            }
        }

        public void Dispose()
        {
            if (_initialized)
            {
                unsafe
                {
                    int* buf = stackalloc int[1];
                    buf[0] = _vertexBuffer;
                    _gl.DeleteBuffers(1, buf);
                    buf[0] = _colorBuffer;
                    _gl.DeleteBuffers(1, buf);
                }
                _gl.DeleteProgram(_shaderProgram);
                _initialized = false;
            }
        }
        
        public IReadOnlyList<CurvePoint> ToCurvePoints(IReadOnlyList<IDataFrame> frames)
        {
            if (frames == null || frames.Count == 0)
                return Array.Empty<CurvePoint>();
                
            // 将数据帧转换为曲线点
            var points = new List<CurvePoint>();
            
            foreach (var frame in frames)
            {
                var samples = frame.Samples;
                for (int i = 0; i < samples.Length; i++)
                {
                    points.Add(new CurvePoint(
                        i, // X坐标使用样本索引
                        samples.Span[i] // Y坐标使用样本值
                    ));
                }
            }
            
            return points;
        }
    }
}