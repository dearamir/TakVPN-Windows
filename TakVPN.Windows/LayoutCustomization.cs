using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TakVPN.Windows;

public sealed class LayoutCustomization
{
    private readonly Window _window;
    private readonly FrameworkElement[] _panels;
    private readonly WrapPanel _toolbar;
    private readonly Button _editButton;
    private readonly Button _resetButton;
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "layout.json");
    private readonly Dictionary<FrameworkElement, LayoutAdorner> _adorners = new();
    private bool _editing;
    private Button? _dragButton;
    private Point _dragStart;
    private bool _dragStarted;
    private readonly List<string> _defaultToolbarOrder;

    private sealed class LayoutState
    {
        public Dictionary<string, PanelState> Panels { get; set; } = new();
        public List<string> ToolbarOrder { get; set; } = new();
    }
    private sealed class PanelState
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public LayoutCustomization(Window window, FrameworkElement[] panels, WrapPanel toolbar, Button editButton, Button resetButton)
    {
        _window = window;
        _panels = panels;
        _toolbar = toolbar;
        _editButton = editButton;
        _resetButton = resetButton;
        _defaultToolbarOrder = _toolbar.Children.OfType<Button>().Select(GetButtonKey).ToList();
        _editButton.Click += (_, _) => SetEditing(!_editing);
        _resetButton.Click += (_, _) => Reset();
        _window.Loaded += (_, _) => Load();
        _window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _editing)
            {
                Reset();
                SetEditing(false);
                e.Handled = true;
            }
        };
        _toolbar.PreviewMouseLeftButtonDown += ToolbarMouseDown;
        _toolbar.PreviewMouseMove += ToolbarMouseMove;
        _toolbar.PreviewMouseLeftButtonUp += (_, _) => { _dragButton = null; _dragStarted = false; };
        _toolbar.DragOver += ToolbarDragOver;
        _toolbar.Drop += ToolbarDrop;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(_path));
            if (state is null) return;
            foreach (var panel in _panels)
            {
                if (state.Panels.TryGetValue(panel.Name, out var saved))
                {
                    panel.RenderTransform = new TranslateTransform(saved.X, saved.Y);
                    if (saved.Width > 0) panel.Width = saved.Width;
                    if (saved.Height > 0) panel.Height = saved.Height;
                }
            }
            ApplyToolbarOrder(state.ToolbarOrder);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Layout load failed: " + ex.Message); }
    }

    private void SetEditing(bool enabled)
    {
        if (_editing == enabled) return;
        _editing = enabled;
        _editButton.Content = enabled ? "ذخیره چیدمان" : "ویرایش چیدمان";
        _resetButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        foreach (var panel in _panels)
        {
            if (enabled) AddAdorner(panel);
            else RemoveAdorner(panel);
        }
        foreach (var button in _toolbar.Children.OfType<Button>())
        {
            button.Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;
            button.Opacity = enabled ? 0.88 : 1;
        }
        if (!enabled) Save();
    }

    private void AddAdorner(FrameworkElement panel)
    {
        var layer = AdornerLayer.GetAdornerLayer(panel);
        if (layer is null || _adorners.ContainsKey(panel)) return;
        var adorner = new LayoutAdorner(panel, Save);
        _adorners[panel] = adorner;
        layer.Add(adorner);
    }

    private void RemoveAdorner(FrameworkElement panel)
    {
        if (!_adorners.Remove(panel, out var adorner)) return;
        AdornerLayer.GetAdornerLayer(panel)?.Remove(adorner);
    }

    private void Reset()
    {
        foreach (var panel in _panels)
        {
            panel.RenderTransform = Transform.Identity;
            panel.Width = double.NaN;
            panel.Height = double.NaN;
        }
        ApplyToolbarOrder(_defaultToolbarOrder);
        Save();
        MessageBox.Show("چیدمان به حالت پیش‌فرض برگشت. برای ادامه ویرایش، دوباره پنل‌ها را جابه‌جا کن.", "TakVPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save()
    {
        try
        {
            var state = new LayoutState();
            foreach (var panel in _panels)
            {
                var translate = panel.RenderTransform as TranslateTransform;
                state.Panels[panel.Name] = new PanelState
                {
                    X = translate?.X ?? 0,
                    Y = translate?.Y ?? 0,
                    Width = double.IsNaN(panel.Width) ? 0 : panel.Width,
                    Height = double.IsNaN(panel.Height) ? 0 : panel.Height
                };
            }
            state.ToolbarOrder = _toolbar.Children.OfType<Button>().Select(GetButtonKey).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Layout save failed: " + ex.Message); }
    }

    private static string GetButtonKey(Button button) => button.Name.Length > 0 ? button.Name : button.Content?.ToString() ?? "";

    private void ApplyToolbarOrder(List<string> order)
    {
        if (order.Count == 0) return;
        var buttons = _toolbar.Children.OfType<Button>().ToList();
        var sorted = order.Select(key => buttons.FirstOrDefault(b => GetButtonKey(b) == key))
            .Where(b => b is not null).Cast<Button>().ToList();
        sorted.AddRange(buttons.Where(b => !sorted.Contains(b)));
        _toolbar.Children.Clear();
        foreach (var button in sorted) _toolbar.Children.Add(button);
    }

    private void ToolbarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editing || e.OriginalSource is not DependencyObject source) return;
        _dragButton = FindParent<Button>(source);
        if (_dragButton is null) return;
        _dragStart = e.GetPosition(_toolbar);
        _dragStarted = false;
    }

    private void ToolbarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_editing || _dragButton is null || e.LeftButton != MouseButtonState.Pressed || _dragStarted) return;
        var point = e.GetPosition(_toolbar);
        if (Math.Abs(point.X - _dragStart.X) < 5 && Math.Abs(point.Y - _dragStart.Y) < 5) return;
        _dragStarted = true;
        DragDrop.DoDragDrop(_dragButton, _dragButton, DragDropEffects.Move);
    }

    private void ToolbarDragOver(object sender, DragEventArgs e) => e.Effects = _editing && e.Data.GetDataPresent(typeof(Button)) ? DragDropEffects.Move : DragDropEffects.None;

    private void ToolbarDrop(object sender, DragEventArgs e)
    {
        if (!_editing || e.Data.GetData(typeof(Button)) is not Button source || e.OriginalSource is not DependencyObject targetObject) return;
        var target = FindParent<Button>(targetObject);
        if (target is null || target == source) return;
        var oldIndex = _toolbar.Children.IndexOf(source);
        var newIndex = _toolbar.Children.IndexOf(target);
        _toolbar.Children.Remove(source);
        _toolbar.Children.Insert(Math.Clamp(newIndex, 0, _toolbar.Children.Count), source);
        Save();
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T result) return result;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private sealed class LayoutAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly Grid _root;
        private readonly Thumb _move;
        private readonly Thumb _resize;
        private readonly Action _save;

        public LayoutAdorner(UIElement adorned, Action save) : base(adorned)
        {
            _save = save;
            IsHitTestVisible = true;
            _root = new Grid { IsHitTestVisible = true };
            var outline = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(83, 170, 255)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12),
                IsHitTestVisible = false
            };
            _move = new Thumb
            {
                Width = 76, Height = 24, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.SizeAll,
                Background = new SolidColorBrush(Color.FromArgb(235, 30, 105, 180)),
                Template = CreateThumbTemplate("⠿ جابه‌جایی")
            };
            _resize = new Thumb
            {
                Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE,
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 105, 180)),
                Template = CreateThumbTemplate("↘")
            };
            _move.DragDelta += MoveDrag;
            _resize.DragDelta += ResizeDrag;
            _root.Children.Add(outline);
            _root.Children.Add(_move);
            _root.Children.Add(_resize);
            _visuals = new VisualCollection(this) { _root };
        }

        private static ControlTemplate CreateThumbTemplate(string text)
        {
            var template = new ControlTemplate(typeof(Thumb));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextProperty, text);
            label.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            label.SetValue(TextBlock.FontSizeProperty, 10d);
            label.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(label);
            template.VisualTree = border;
            return template;
        }

        private void MoveDrag(object sender, DragDeltaEventArgs e)
        {
            if (AdornedElement is not FrameworkElement element) return;
            var transform = element.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }
            var parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
            var dx = e.HorizontalChange;
            var dy = e.VerticalChange;
            if (parent is not null && parent.ActualWidth > 0 && parent.ActualHeight > 0)
            {
                var bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
                const double visible = 90;
                dx = Math.Clamp(dx, visible - bounds.Right, parent.ActualWidth - visible - bounds.Left);
                dy = Math.Clamp(dy, visible - bounds.Bottom, parent.ActualHeight - visible - bounds.Top);
            }
            transform.X += dx;
            transform.Y += dy;
            _save();
        }

        private void ResizeDrag(object sender, DragDeltaEventArgs e)
        {
            if (AdornedElement is not FrameworkElement element) return;
            if (double.IsNaN(element.Width)) element.Width = element.ActualWidth;
            if (double.IsNaN(element.Height)) element.Height = element.ActualHeight;
            element.Width = Math.Max(180, element.Width + e.HorizontalChange);
            element.Height = Math.Max(70, element.Height + e.VerticalChange);
            _save();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _root.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _root.Arrange(new Rect(finalSize));
            return finalSize;
        }

        protected override Visual GetVisualChild(int index) => _visuals[index];
        protected override int VisualChildrenCount => _visuals.Count;
    }
}
