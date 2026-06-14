using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SteamSwitcher.Core
{
    public class PanelDragHelper
    {
        private readonly Panel _panel;
        private bool _isDragging;
        private bool _mouseDown;
        private Point _startPoint;
        private int _dragIndex = -1;
        private int _currentIndex = -1;
        private FrameworkElement? _draggedElement;
        private ContentControl? _overlay;
        private Canvas? _overlayCanvas;
        private Panel? _overlayRoot;
        private Window? _window;
        private const double DragStartDistance = 5;
        private const double DeleteOutsideMargin = 36;

        public event EventHandler<int>? ItemDeleted;
        public event EventHandler<(int fromIndex, int toIndex)>? ItemMoved;
        public event EventHandler<int>? ItemClicked;

        public PanelDragHelper(Panel panel)
        {
            _panel = panel;
        }

        public void Attach()
        {
            _panel.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown), true);
            _panel.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), true);
            _panel.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp), true);
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            var panelPos = e.GetPosition(_panel);
            _dragIndex = GetIndexFromPoint(panelPos);
            _mouseDown = true;
            _isDragging = false;
            _currentIndex = _dragIndex;

            _window = Window.GetWindow(_panel);
            if (_window != null)
                _startPoint = e.GetPosition(_window);

            if (_dragIndex >= 0)
            {
                _panel.CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_isDragging && _window != null)
                    CompleteDrag(e.GetPosition(_window));
                else
                    EndDrag();
                return;
            }

            if (_dragIndex < 0 || _window == null) return;

            var winPos = e.GetPosition(_window);
            var dx = winPos.X - _startPoint.X;
            var dy = winPos.Y - _startPoint.Y;

            if (!_isDragging && (Math.Abs(dx) > DragStartDistance || Math.Abs(dy) > DragStartDistance))
                StartDrag(winPos);

            if (!_isDragging) return;

            e.Handled = true;

            // Move overlay to follow mouse
            MoveOverlay(winPos);

            // Visual feedback: dim when outside
            bool outside = IsOutsideWindow(winPos);
            if (_overlay != null)
                _overlay.Opacity = outside ? 0.3 : 0.9;

            // Real-time reorder only when inside
            if (!outside)
            {
                var panelPos = e.GetPosition(_panel);
                var target = GetTargetIndex(panelPos);
                if (_currentIndex >= 0 &&
                    _currentIndex < _panel.Children.Count &&
                    target >= 0 &&
                    target != _currentIndex &&
                    target <= _panel.Children.Count)
                {
                    var dragged = _panel.Children[_currentIndex];
                    _panel.Children.RemoveAt(_currentIndex);
                    var insertAt = target > _currentIndex ? target - 1 : target;
                    _panel.Children.Insert(Math.Min(insertAt, _panel.Children.Count), dragged);
                    _currentIndex = insertAt;
                }
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_mouseDown) return;

            if (_isDragging)
            {
                e.Handled = true;
                var winPos = _window != null ? e.GetPosition(_window) : new Point();
                CompleteDrag(winPos);
                return;
            }
            else if (_dragIndex >= 0)
            {
                ItemClicked?.Invoke(this, _dragIndex);
            }

            EndDrag();
        }

        private void CompleteDrag(Point windowPosition)
        {
            bool outside = IsOutsideWindow(windowPosition);
            int fromIndex = _dragIndex;
            int moveTargetIndex = GetMoveTargetIndex();
            bool moved = !outside && moveTargetIndex != fromIndex;

            EndDrag(restoreOriginalPosition: outside || !moved);
            if (outside)
                ItemDeleted?.Invoke(this, fromIndex);
            else if (moved)
                ItemMoved?.Invoke(this, (fromIndex, moveTargetIndex));
        }

        private bool IsOutsideWindow(Point windowPosition)
        {
            if (_window == null) return false;

            var width = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
            var height = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
            if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
                return false;

            return windowPosition.X < -DeleteOutsideMargin ||
                   windowPosition.X > width + DeleteOutsideMargin ||
                   windowPosition.Y < -DeleteOutsideMargin ||
                   windowPosition.Y > height + DeleteOutsideMargin;
        }

        private void StartDrag(Point startPos)
        {
            _draggedElement = _panel.Children[_dragIndex] as FrameworkElement;
            if (_draggedElement == null || _window == null) return;

            _isDragging = true;
            var preview = CreateDragPreview(_draggedElement);
            _draggedElement.Opacity = 0.28;

            _overlayRoot = _window.Content as Panel;
            if (_overlayRoot != null && preview != null)
            {
                _overlayCanvas = new Canvas
                {
                    IsHitTestVisible = false,
                    ClipToBounds = false
                };
                Panel.SetZIndex(_overlayCanvas, int.MaxValue);

                _overlay = new ContentControl
                {
                    Content = preview,
                    IsHitTestVisible = false,
                    Opacity = 0.9,
                    Width = _draggedElement.ActualWidth,
                    Height = _draggedElement.ActualHeight
                };

                MoveOverlay(startPos);
                _overlayCanvas.Children.Add(_overlay);
                _overlayRoot.Children.Add(_overlayCanvas);
            }
        }

        private void EndDrag(bool restoreOriginalPosition = true)
        {
            if (restoreOriginalPosition)
                RestoreOriginalPosition();

            _overlay = null;

            if (_overlayCanvas != null && _overlayRoot != null)
            {
                _overlayRoot.Children.Remove(_overlayCanvas);
            }

            if (_draggedElement != null)
                _draggedElement.Opacity = 1.0;

            if (_panel.IsMouseCaptured)
                _panel.ReleaseMouseCapture();

            _isDragging = false;
            _mouseDown = false;
            _dragIndex = -1;
            _currentIndex = -1;
            _draggedElement = null;
            _overlayCanvas = null;
            _overlayRoot = null;
            _window = null;
        }

        private void MoveOverlay(Point position)
        {
            if (_overlay == null || _draggedElement == null)
                return;

            Canvas.SetLeft(_overlay, position.X - _draggedElement.ActualWidth / 2);
            Canvas.SetTop(_overlay, position.Y - _draggedElement.ActualHeight / 2);
        }

        private int GetMoveTargetIndex()
        {
            if (_dragIndex < 0 || _currentIndex < 0)
                return _dragIndex;

            return _currentIndex > _dragIndex ? _currentIndex + 1 : _currentIndex;
        }

        private void RestoreOriginalPosition()
        {
            if (_draggedElement == null ||
                _dragIndex < 0 ||
                _currentIndex < 0 ||
                _currentIndex == _dragIndex ||
                !_panel.Children.Contains(_draggedElement))
            {
                return;
            }

            _panel.Children.Remove(_draggedElement);
            _panel.Children.Insert(Math.Min(_dragIndex, _panel.Children.Count), _draggedElement);
            _currentIndex = _dragIndex;
        }

        private static Image? CreateDragPreview(FrameworkElement element)
        {
            var width = element.ActualWidth;
            var height = element.ActualHeight;
            if (width <= 0 || height <= 0)
                return null;

            element.UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(element);
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(element);
            bitmap.Freeze();

            return new Image
            {
                Source = bitmap,
                Width = width,
                Height = height,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.04, 1.04),
                SnapsToDevicePixels = true
            };
        }

        private int GetIndexFromPoint(Point point)
        {
            for (int i = 0; i < _panel.Children.Count; i++)
            {
                if (_panel.Children[i] is UIElement el)
                {
                    var origin = el.TranslatePoint(new Point(0, 0), _panel);
                    var rect = new Rect(origin.X, origin.Y, el.RenderSize.Width, el.RenderSize.Height);
                    if (rect.Contains(point))
                        return i;
                }
            }
            return -1;
        }

        private int GetTargetIndex(Point position)
        {
            for (int i = 0; i < _panel.Children.Count; i++)
            {
                if (_panel.Children[i] is FrameworkElement child)
                {
                    var origin = child.TranslatePoint(new Point(0, 0), _panel);
                    var midX = origin.X + child.ActualWidth / 2;
                    if (position.X < midX)
                        return i;
                }
            }
            return _panel.Children.Count;
        }
    }
}
