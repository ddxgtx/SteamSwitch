using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SteamSwitcher.Core
{
    public class DragDropHelper
    {
        private readonly ListBox _listBox;
        private readonly FrameworkElement _deleteZone;
        private bool _isDragging;
        private bool _mouseDown;
        private Point _startPoint;
        private int _dragIndex = -1;
        private DispatcherTimer? _longPressTimer;

        public event EventHandler<int>? ItemDeleted;
        public event EventHandler<(int fromIndex, int toIndex)>? ItemMoved;
        public event EventHandler<int>? ItemClicked;

        public DragDropHelper(ListBox listBox, FrameworkElement deleteZone)
        {
            _listBox = listBox;
            _deleteZone = deleteZone;
            _deleteZone.Visibility = Visibility.Collapsed;
        }

        public void Attach()
        {
            // Use AddHandler with HandledEventsToo to capture events even if handled by children
            _listBox.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
            _listBox.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
            _listBox.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(_listBox);
            _startPoint = pos;
            _dragIndex = GetIndexFromPoint(pos);
            _mouseDown = true;
            _isDragging = false;

            if (_dragIndex >= 0)
            {
                _longPressTimer?.Stop();
                _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _longPressTimer.Tick += (s, args) =>
                {
                    _longPressTimer.Stop();
                    if (_mouseDown && _dragIndex >= 0)
                    {
                        _isDragging = true;
                        var container = _listBox.ItemContainerGenerator.ContainerFromIndex(_dragIndex) as UIElement;
                        if (container != null)
                            container.Opacity = 0.3;
                        _deleteZone.Visibility = Visibility.Visible;
                    }
                };
                _longPressTimer.Start();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed)
            {
                Reset();
                return;
            }

            if (_dragIndex < 0) return;

            var currentPos = e.GetPosition(_listBox);
            var diff = currentPos - _startPoint;

            // If moved too much without starting drag, cancel
            if (!_isDragging && (Math.Abs(diff.X) > 15 || Math.Abs(diff.Y) > 15))
            {
                Reset();
                return;
            }

            if (_isDragging)
            {
                var deletePos = e.GetPosition(_deleteZone);
                var deleteBounds = new Rect(0, 0, _deleteZone.ActualWidth, _deleteZone.ActualHeight);
                _deleteZone.Opacity = deleteBounds.Contains(deletePos) ? 1.0 : 0.5;
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _longPressTimer?.Stop();

            if (!_mouseDown)
                return;

            if (_isDragging)
            {
                var deletePos = e.GetPosition(_deleteZone);
                var deleteBounds = new Rect(0, 0, _deleteZone.ActualWidth, _deleteZone.ActualHeight);

                if (deleteBounds.Contains(deletePos))
                {
                    ItemDeleted?.Invoke(this, _dragIndex);
                }
                else
                {
                    var position = e.GetPosition(_listBox);
                    var targetIndex = GetTargetIndex(position);
                    if (targetIndex >= 0 && targetIndex != _dragIndex)
                    {
                        ItemMoved?.Invoke(this, (_dragIndex, targetIndex));
                    }
                }
            }
            else if (_dragIndex >= 0)
            {
                // It was a click
                ItemClicked?.Invoke(this, _dragIndex);
            }

            Reset();
        }

        private void Reset()
        {
            _longPressTimer?.Stop();
            
            if (_dragIndex >= 0)
            {
                var container = _listBox.ItemContainerGenerator.ContainerFromIndex(_dragIndex) as UIElement;
                if (container != null)
                    container.Opacity = 1.0;
            }

            _isDragging = false;
            _mouseDown = false;
            _dragIndex = -1;
            _deleteZone.Visibility = Visibility.Collapsed;
            _deleteZone.Opacity = 0.5;
        }

        private int GetIndexFromPoint(Point point)
        {
            var hitTest = VisualTreeHelper.HitTest(_listBox, point);
            if (hitTest == null) return -1;

            var element = hitTest.VisualHit as DependencyObject;
            while (element != null && element != _listBox)
            {
                if (element is ListBoxItem item)
                {
                    return _listBox.ItemContainerGenerator.IndexFromContainer(item);
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return -1;
        }

        private int GetTargetIndex(Point position)
        {
            for (int i = 0; i < _listBox.Items.Count; i++)
            {
                var container = _listBox.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue;

                var containerPos = container.TranslatePoint(new Point(0, 0), _listBox);
                if (position.Y >= containerPos.Y && position.Y <= containerPos.Y + container.ActualHeight)
                {
                    return i;
                }
            }
            return _listBox.Items.Count;
        }
    }
}
