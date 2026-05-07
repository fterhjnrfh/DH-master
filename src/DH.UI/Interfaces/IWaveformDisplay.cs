using System;
using System.Collections.Generic;

namespace NewAvalonia.Interfaces
{
    /// <summary>
    /// 通用波形显示控件接口
    /// </summary>
    public interface IWaveformDisplay
    {
        /// <summary>
        /// 更新波形数据
        /// </summary>
        /// <param name="points">波形数据点</param>
        void UpdateWaveform(IEnumerable<(double X, double Y)> points);

        /// <summary>
        /// 清空波形显示
        /// </summary>
        void ClearWaveform();

        /// <summary>
        /// 更新幅值
        /// </summary>
        double Amplitude { get; set; }
    }
}