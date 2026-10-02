using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AutoCompressor.Core.Models;
using AutoCompressor.Core.Queue;
using AutoCompressor.Desktop.ViewModels;

namespace AutoCompressor.Desktop.Controls;

/// <summary>A flat proportional bar, used for sizes and progress. Cheap enough for thousands of rows.</summary>
public sealed class Bar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(Bar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Bar),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(Bar),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double radius = Math.Min(h / 2, 3);
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        double fraction = double.IsNaN(Fraction) ? 0 : Math.Clamp(Fraction, 0, 1);
        // Anything non-zero gets at least a sliver so small items are still visible.
        double filled = fraction <= 0 ? 0 : Math.Max(w * fraction, Math.Min(3, w));
        if (filled > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, filled, h), radius, radius);
    }
}

public static class Palette
{
    private static SolidColorBrush Frozen(string hex, double opacity = 1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static readonly Dictionary<ContentType, string> TypeColors = new()
    {
        [ContentType.Anime] = "#FF7EB6", [ContentType.Animation] = "#FFA94D", [ContentType.Sitcom] = "#FFD43B", [ContentType.Drama] = "#B197FC",
        [ContentType.Cinematic] = "#FF7B72", [ContentType.Documentary] = "#69DB7C", [ContentType.StillCam] = "#74C0FC", [ContentType.Sports] = "#38D9A9",
        [ContentType.Concert] = "#E599F7", [ContentType.General] = "#ADB5BD", [ContentType.Music] = "#4DABF7", [ContentType.Speech] = "#D8C3A5",
    };

    private static readonly Dictionary<ContentType, (Brush Text, Brush Fill)> TypeBrushes =
        TypeColors.ToDictionary(kv => kv.Key, kv => ((Brush)Frozen(kv.Value), (Brush)Frozen(kv.Value, 0.16)));

    public static Brush TypeText(ContentType type) => TypeBrushes[type].Text;
    public static Brush TypeFill(ContentType type) => TypeBrushes[type].Fill;

    public static readonly Brush Good = Frozen("#5BD08A");
    public static readonly Brush Warn = Frozen("#F0B849");
    public static readonly Brush Bad = Frozen("#FF7B72");
    public static readonly Brush Info = Frozen("#5AA9FF");
    public static readonly Brush Muted = Frozen("#9A9AA6");
    public static readonly Brush Text = Frozen("#ECECF1");
}

/// <summary>Content type to its chip colour. Pass "fill" as the parameter for the translucent background.</summary>
public sealed class TypeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ContentType type ? (parameter as string == "fill" ? Palette.TypeFill(type) : Palette.TypeText(type)) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RowState.Done or RowState.Compressed or RowState.HasCopy or JobStatus.Completed => Palette.Good,
        RowState.Running or RowState.Queued or JobStatus.Running => Palette.Info,
        RowState.Failed or RowState.Unreadable or JobStatus.Failed => Palette.Bad,
        RowState.Skipped or JobStatus.Skipped or JobStatus.Cancelled => Palette.Warn,
        _ => Palette.Muted,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is true, a non-empty string, a non-empty list or a non-zero number. Parameter "not" inverts.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "not") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>0-100 to 0-1, for binding a percentage to a <see cref="Bar"/>.</summary>
public sealed class PercentToFractionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double d ? d / 100.0 : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
