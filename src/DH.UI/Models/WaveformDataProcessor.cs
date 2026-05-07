using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.Models;
using NewAvalonia.Services;

namespace NewAvalonia.Models
{
    /// <summary>
    /// 波形数据处理器
    /// </summary>
    public class WaveformDataProcessor
    {
        private readonly AlgorithmModuleLoader _moduleLoader;
        private double[]? _originalWaveformData;
        private double[]? _processedWaveformData;
        
        public WaveformDataProcessor()
        {
            _moduleLoader = new AlgorithmModuleLoader();
            // 加载内置算法模块
            _moduleLoader.LoadBuiltInModules();
        }
        
        /// <summary>
        /// 获取所有可用的算法模块
        /// </summary>
        /// <returns>算法模块名称列表</returns>
        public List<string> GetAvailableAlgorithms()
        {
            return _moduleLoader.GetModules()
                .Select(m => m.Name)
                .ToList();
        }
        
        /// <summary>
        /// 设置原始波形数据
        /// </summary>
        /// <param name="data">原始波形数据</param>
        public void SetOriginalData(double[] data)
        {
            if (data != null)
            {
                _originalWaveformData = (double[])data.Clone();
                // 如果处理后的数据为空，初始化为原始数据的副本
                if (_processedWaveformData == null)
                {
                    _processedWaveformData = (double[])_originalWaveformData.Clone();
                }
            }
        }
        
        /// <summary>
        /// 获取原始波形数据
        /// </summary>
        /// <returns>原始波形数据</returns>
        public double[]? GetOriginalData()
        {
            return _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : null;
        }
        
        /// <summary>
        /// 获取处理后的波形数据
        /// </summary>
        /// <returns>处理后的波形数据</returns>
        public double[]? GetProcessedData()
        {
            return _processedWaveformData != null ? (double[])_processedWaveformData.Clone() : null;
        }
        
        /// <summary>
        /// 应用算法到波形数据
        /// </summary>
        /// <param name="algorithmName">算法名称</param>
        public void ApplyAlgorithm(string algorithmName)
        {
            if (_originalWaveformData == null)
                return;
                
            var algorithm = _moduleLoader.GetModuleByName(algorithmName);
            if (algorithm != null)
            {
                // 使用处理后的数据作为输入（支持链式处理）
                var inputData = _processedWaveformData != null ? (double[])_processedWaveformData.Clone() : 
                               _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : 
                               null;
                               
                if (inputData != null)
                {
                    _processedWaveformData = algorithm.Process(inputData);
                }
            }
        }
        
        /// <summary>
        /// 重置到原始数据
        /// </summary>
        public void ResetToOriginal()
        {
            if (_originalWaveformData != null)
            {
                _processedWaveformData = (double[])_originalWaveformData.Clone();
            }
        }
    }
}