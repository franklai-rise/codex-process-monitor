using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace Codex.ProcessMonitor.App.Controls;

/// <summary>A tiny dependency-property based chart that keeps all rendering in WPF.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(System.Windows.Media.Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(MediaColor.FromRgb(99, 179, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(System.Windows.Media.Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(MediaColor.FromArgb(38, 99, 179, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observableValues;

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public System.Windows.Media.Brush Stroke
    {
        get => (System.Windows.Media.Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public System.Windows.Media.Brush Fill
    {
        get => (System.Windows.Media.Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var backgroundPen = new MediaPen(new SolidColorBrush(MediaColor.FromArgb(90, 90, 119, 151)), 1);
        for (var row = 1; row < 4; row++)
        {
            var y = height * row / 4;
            drawingContext.DrawLine(backgroundPen, new WpfPoint(0, y), new WpfPoint(width, y));
        }

        var points = (Values ?? Array.Empty<double>()).ToArray();
        if (points.Length == 0)
        {
            return;
        }

        var max = Math.Max(100, points.Max());
        var min = Math.Min(0, points.Min());
        var range = Math.Max(1, max - min);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var x = points.Length == 1 ? 0 : width * index / (points.Length - 1);
                var y = height - ((points[index] - min) / range * (height - 4)) - 2;
                y = Math.Clamp(y, 2, height - 2);
                if (index == 0)
                {
                    context.BeginFigure(new WpfPoint(x, y), true, false);
                }
                else
                {
                    context.LineTo(new WpfPoint(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        var area = geometry.Clone();
        using (var context = area.Open())
        {
            var firstX = points.Length == 1 ? 0 : 0;
            var lastX = points.Length == 1 ? 0 : width;
            var firstY = height - ((points[0] - min) / range * (height - 4)) - 2;
            var lastY = height - ((points[^1] - min) / range * (height - 4)) - 2;
            context.BeginFigure(new WpfPoint(firstX, height), true, false);
            context.LineTo(new WpfPoint(firstX, firstY), true, false);
            for (var index = 1; index < points.Length; index++)
            {
                var x = points.Length == 1 ? 0 : width * index / (points.Length - 1);
                var y = height - ((points[index] - min) / range * (height - 4)) - 2;
                context.LineTo(new WpfPoint(x, Math.Clamp(y, 2, height - 2)), true, false);
            }
            context.LineTo(new WpfPoint(lastX, height), true, false);
            context.Close();
        }

        drawingContext.DrawGeometry(Fill, null, area);
        drawingContext.DrawGeometry(null, new MediaPen(Stroke, 2), geometry);
    }

    private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var sparkline = (Sparkline)dependencyObject;
        if (sparkline._observableValues is not null)
        {
            sparkline._observableValues.CollectionChanged -= sparkline.OnCollectionChanged;
            sparkline._observableValues = null;
        }

        if (args.NewValue is INotifyCollectionChanged observable)
        {
            sparkline._observableValues = observable;
            observable.CollectionChanged += sparkline.OnCollectionChanged;
        }

        sparkline.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => Dispatcher.InvokeAsync(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
}
