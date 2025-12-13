using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace inuMixer
{
    public partial class MainWindow : Window
    {
        private MixerViewModel _viewModel;

        // ドラッグ操作用フィールド
        private Point _dragStartPoint;
        private bool _isDragging = false;
        private Border _draggedItemContainer;
        private AudioSessionModel _draggedData;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();

            _viewModel = new MixerViewModel();
            DataContext = _viewModel;

            this.Closed += MainWindow_Closed;
            this.MouseDown += MainWindow_MouseDown;

            // ウィンドウ全体でドラッグ終了を監視 (スタック防止)
            this.PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;

            // 起動完了時に並び順を復元
            this.Loaded += (s, e) => _viewModel.LoadOrder();
        }

        private void LoadSettings()
        {
            try
            {
                var settings = global::inuMixer.Properties.Settings.Default;
                if (settings.WindowLeft > -1 && settings.WindowHeight > 0)
                {
                    this.Left = settings.WindowLeft;
                    this.Top = settings.WindowTop;
                    this.Height = settings.WindowHeight;
                }
                this.Topmost = settings.IsAlwaysOnTop;

                if (settings.WindowOpacity < 0.2) settings.WindowOpacity = 1.0;
                this.Opacity = settings.WindowOpacity;
            }
            catch { }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _viewModel?.SaveOrder();
            _viewModel?.Dispose();

            try
            {
                var settings = global::inuMixer.Properties.Settings.Default;
                settings.WindowLeft = this.Left;
                settings.WindowTop = this.Top;
                settings.WindowHeight = this.Height;
                settings.IsAlwaysOnTop = this.Topmost;
                settings.WindowOpacity = this.Opacity;
                settings.Save();
            }
            catch { }
        }

        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !IsControlUnderMouse(e.OriginalSource as DependencyObject))
            {
                try
                {
                    this.DragMove();
                }
                catch { }
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var currentApps = _viewModel.GetActiveAppNames();
            var settingsWindow = new SettingsWindow(currentApps)
            {
                Owner = this
            };

            if (settingsWindow.ShowDialog() == true)
            {
                _viewModel.SyncSessions();
            }
        }

        #region Drag and Drop Logic

        private void AppFader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsControlUnderMouse(e.OriginalSource as DependencyObject)) return;

            _dragStartPoint = e.GetPosition(null);
            if (sender is Border border && border.DataContext is AudioSessionModel)
            {
                _draggedItemContainer = border;
                _draggedData = border.DataContext as AudioSessionModel;
                _isDragging = false;
            }
        }

        private void AppFader_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItemContainer != null)
            {
                Point currentPoint = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPoint;

                // ドラッグ開始判定
                if (!_isDragging && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
                {
                    _isDragging = true;
                    _draggedItemContainer.Opacity = 0.5;
                    _draggedItemContainer.CaptureMouse();
                    //  カーソルを並び替え用アイコンに変更
                    Mouse.OverrideCursor = Cursors.SizeAll;
                }

                if (_isDragging)
                {
                    PerformDragReorder(e);
                }
            }
        }

        private void PerformDragReorder(MouseEventArgs e)
        {
            ItemsControl itemsControl = FindAncestor<ItemsControl>(_draggedItemContainer);
            if (itemsControl != null)
            {
                Point mouseInList = e.GetPosition(itemsControl);
                foreach (var item in _viewModel.AudioSessions)
                {
                    if (item == _draggedData) continue;

                    var container = itemsControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                    if (container == null) continue;

                    Point relativePoint = container.TranslatePoint(new Point(0, 0), itemsControl);
                    Rect bounds = new Rect(relativePoint, new Size(container.ActualWidth, container.ActualHeight));

                    if (bounds.Contains(mouseInList))
                    {
                        int oldIndex = _viewModel.AudioSessions.IndexOf(_draggedData);
                        int newIndex = _viewModel.AudioSessions.IndexOf(item);
                        if (oldIndex != -1 && newIndex != -1)
                        {
                            _viewModel.AudioSessions.Move(oldIndex, newIndex);
                        }
                        break;
                    }
                }
            }
        }

        private void AppFader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void MainWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging) EndDrag();
        }

        private void EndDrag()
        {
            if (_isDragging && _draggedItemContainer != null)
            {
                // Opacityのローカル値をクリアし、XAMLのスタイル/トリガーに制御を戻す
                _draggedItemContainer.ClearValue(Border.OpacityProperty);

                _draggedItemContainer.ReleaseMouseCapture();
                _isDragging = false;
                //  カーソルをデフォルトに戻す
                Mouse.OverrideCursor = null;
            }
            _draggedItemContainer = null;
            _draggedData = null;
        }

        #endregion

        #region Helpers & UI Events

        private bool IsControlUnderMouse(DependencyObject originalSource)
        {
            var current = originalSource;
            while (current != null)
            {
                if (current == _draggedItemContainer) return false;
                if (current is Slider || current is ToggleButton || current is Button || current is Thumb) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed) return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void OpacitySlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Slider s)
            {
                double c = 0.02;
                if (e.Delta > 0) s.Value = Math.Min(s.Maximum, s.Value + c);
                else s.Value = Math.Max(s.Minimum, s.Value - c);
                e.Handled = true;
            }
        }

        private void Border_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isDragging) return;
            if (sender is Border b && b.DataContext is AudioSessionModel s)
            {
                float c = 0.05f;
                if (e.Delta > 0) s.Volume = Math.Min(1.0f, s.Volume + c);
                else s.Volume = Math.Max(0.0f, s.Volume - c);
                e.Handled = true;
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && sender is Border b && b.DataContext is AudioSessionModel s)
            {
                s.IsMuted = !s.IsMuted;
            }
        }

        private void MasterFader_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border && DataContext is MixerViewModel vm)
            {
                float c = 0.05f;
                if (e.Delta > 0) vm.MasterVolume = Math.Min(1.0f, vm.MasterVolume + c);
                else vm.MasterVolume = Math.Max(0.0f, vm.MasterVolume - c);
                e.Handled = true;
            }
        }

        private void MasterFader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && DataContext is MixerViewModel vm)
            {
                vm.MasterIsMuted = !vm.MasterIsMuted;
            }
        }

        #endregion
    }
}