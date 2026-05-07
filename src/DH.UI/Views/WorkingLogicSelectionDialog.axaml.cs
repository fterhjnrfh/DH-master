using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NewAvalonia.Models;

namespace NewAvalonia.Views
{
    public partial class WorkingLogicSelectionDialog : Window
    {
        public WorkingLogic? SelectedLogic { get; private set; }
        public bool IsConfirmed { get; private set; } = false;

        public WorkingLogicSelectionDialog()
        {
            InitializeComponent();
            InitializeEvents();
        }

        private void InitializeEvents()
        {
            confirmButton.Click += OnConfirmClick;
            cancelButton.Click += OnCancelClick;
            logicListBox.SelectionChanged += OnSelectionChanged;
        }

        /// <summary>
        /// 设置可用的工作逻辑列表
        /// </summary>
        public void SetAvailableLogics(List<WorkingLogic> logics)
        {
            if (logics == null || !logics.Any())
            {
                // 显示空状态
                logicListBox.IsVisible = false;
                emptyStatePanel.IsVisible = true;
                confirmButton.IsEnabled = false;
            }
            else
            {
                // 显示逻辑列表
                logicListBox.IsVisible = true;
                emptyStatePanel.IsVisible = false;
                logicListBox.ItemsSource = logics;
                
                // 如果只有一个选项，自动选中
                if (logics.Count == 1)
                {
                    logicListBox.SelectedIndex = 0;
                }
            }
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var hasSelection = logicListBox.SelectedItem != null;
            confirmButton.IsEnabled = hasSelection;
            
            if (hasSelection)
            {
                SelectedLogic = logicListBox.SelectedItem as WorkingLogic;
            }
        }

        private void OnConfirmClick(object? sender, RoutedEventArgs e)
        {
            if (logicListBox.SelectedItem is WorkingLogic selectedLogic)
            {
                SelectedLogic = selectedLogic;
                IsConfirmed = true;
                Close();
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            SelectedLogic = null;
            Close();
        }

        /// <summary>
        /// 显示对话框并返回用户选择
        /// </summary>
        public static async System.Threading.Tasks.Task<(bool confirmed, WorkingLogic? selectedLogic)> ShowDialogAsync(
            Window? owner, List<WorkingLogic> availableLogics)
        {
            var dialog = new WorkingLogicSelectionDialog();
            dialog.SetAvailableLogics(availableLogics);
            
            if (owner != null)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                dialog.Show();
            }
            
            return (dialog.IsConfirmed, dialog.SelectedLogic);
        }
    }
}