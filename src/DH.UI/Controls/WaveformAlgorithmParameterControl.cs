using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using NewAvalonia.Services;

namespace NewAvalonia.Controls
{
    /// <summary>
    /// 波形算法参数配置控件
    /// </summary>
    public partial class WaveformAlgorithmParameterControl : UserControl
    {
        private ComboBox? _algorithmComboBox;
        private StackPanel? _parameterPanel;
        private Button? _applyButton;
        private Button? _resetButton;
        
        private RealtimeWaveformProcessor? _processor;
        private Dictionary<string, Control>? _parameterControls;
        private Dictionary<string, object>? _currentParameters;
        
        public event Action<string>? AlgorithmChanged;
        public event Action? ParametersApplied;

        public WaveformAlgorithmParameterControl()
        {
            _processor = new RealtimeWaveformProcessor();
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _algorithmComboBox = this.FindControl<ComboBox>("AlgorithmComboBox");
            _parameterPanel = this.FindControl<StackPanel>("ParameterPanel");
            _applyButton = this.FindControl<Button>("ApplyButton");
            _resetButton = this.FindControl<Button>("ResetButton");

            if (_algorithmComboBox != null)
            {
                // 加载可用算法
                var algorithms = _processor!.GetAvailableAlgorithms();
                _algorithmComboBox.Items.Clear();
                foreach (var algorithm in algorithms)
                {
                    _algorithmComboBox.Items.Add(algorithm);
                }

                _algorithmComboBox.SelectionChanged += OnAlgorithmSelectionChanged;
            }

            if (_applyButton != null)
            {
                _applyButton.Click += OnApplyButtonClick;
            }

            if (_resetButton != null)
            {
                _resetButton.Click += OnResetButtonClick;
            }
        }

        private void OnAlgorithmSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_algorithmComboBox?.SelectedItem is string selectedAlgorithm)
            {
                LoadAlgorithmParameters(selectedAlgorithm);
                AlgorithmChanged?.Invoke(selectedAlgorithm);
            }
        }

        private void OnApplyButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_processor != null && _algorithmComboBox?.SelectedItem is string algorithmName)
            {
                var parameters = GetParametersFromControls();
                _processor.SetAlgorithm(algorithmName);
                _processor.SetAlgorithmParameters(parameters);
                
                ParametersApplied?.Invoke();
            }
        }

        private void OnResetButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_algorithmComboBox?.SelectedItem is string selectedAlgorithm)
            {
                _processor?.SetAlgorithm(selectedAlgorithm);
                LoadAlgorithmParameters(selectedAlgorithm);
            }
        }

        private void LoadAlgorithmParameters(string algorithmName)
        {
            if (_processor == null || _parameterPanel == null) return;

            _processor.SetAlgorithm(algorithmName);
            var parameters = _processor.GetAlgorithmParameters();
            _currentParameters = new Dictionary<string, object>(parameters);

            _parameterPanel.Children.Clear();
            _parameterControls = new Dictionary<string, Control>();

            foreach (var param in parameters)
            {
                var panel = CreateParameterControl(param.Key, param.Value);
                if (panel != null)
                {
                    _parameterPanel.Children.Add(panel);
                }
            }
        }

        private Control? CreateParameterControl(string paramName, object paramValue)
        {
            var panel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Thickness(0, 2)
            };

            var label = new TextBlock
            {
                Text = $"{paramName}:",
                Width = 120,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = Brushes.White
            };

            Control control;

            if (paramValue is int intValue)
            {
                var textBox = new TextBox
                {
                    Text = intValue.ToString(),
                    Width = 100,
                    Watermark = intValue.ToString()
                };
                
                textBox.TextChanged += (s, e) =>
                {
                    if (int.TryParse(textBox.Text, out _))
                    {
                        _parameterControls![paramName] = textBox;
                    }
                };

                control = textBox;
                _parameterControls![paramName] = textBox;
            }
            else if (paramValue is double doubleValue)
            {
                var textBox = new TextBox
                {
                    Text = doubleValue.ToString("F3"),
                    Width = 100,
                    Watermark = doubleValue.ToString("F3")
                };
                
                textBox.TextChanged += (s, e) =>
                {
                    if (double.TryParse(textBox.Text, out _))
                    {
                        _parameterControls![paramName] = textBox;
                    }
                };

                control = textBox;
                _parameterControls![paramName] = textBox;
            }
            else
            {
                var textBox = new TextBox
                {
                    Text = paramValue.ToString(),
                    Width = 100
                };
                
                control = textBox;
                _parameterControls![paramName] = textBox;
            }

            panel.Children.Add(label);
            panel.Children.Add(control);

            return panel;
        }

        private Dictionary<string, object> GetParametersFromControls()
        {
            var parameters = new Dictionary<string, object>();

            if (_parameterControls != null)
            {
                foreach (var kvp in _parameterControls)
                {
                    var control = kvp.Value;
                    var paramName = kvp.Key;

                    if (control is TextBox textBox)
                    {
                        if (_currentParameters != null && 
                            _currentParameters.ContainsKey(paramName))
                        {
                            var originalValue = _currentParameters[paramName];

                            if (originalValue is int)
                            {
                                parameters[paramName] = int.TryParse(textBox.Text, out var intResult)
                                    ? intResult
                                    : originalValue;
                            }
                            else if (originalValue is double)
                            {
                                parameters[paramName] = double.TryParse(textBox.Text, out var doubleResult)
                                    ? doubleResult
                                    : originalValue;
                            }
                            else
                            {
                                parameters[paramName] = textBox.Text;
                            }
                        }
                        else
                        {
                            parameters[paramName] = textBox.Text;
                        }
                    }
                }
            }

            return parameters;
        }

        public void SetAlgorithm(string algorithmName)
        {
            if (_algorithmComboBox != null)
            {
                _algorithmComboBox.SelectedItem = algorithmName;
                LoadAlgorithmParameters(algorithmName);
            }
        }

        public string? GetSelectedAlgorithm()
        {
            return _algorithmComboBox?.SelectedItem as string;
        }

        public void ApplyParameters()
        {
            OnApplyButtonClick(null, new Avalonia.Interactivity.RoutedEventArgs());
        }

        public Dictionary<string, object> GetAppliedParameters()
        {
            if (_processor != null && _algorithmComboBox?.SelectedItem is string algorithmName)
            {
                _processor.SetAlgorithm(algorithmName);
                return _processor.GetAlgorithmParameters();
            }
            return new Dictionary<string, object>();
        }
    }
}
