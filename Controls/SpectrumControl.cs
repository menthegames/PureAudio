using System;
using System.Windows;
using System.Windows.Media;

namespace PureAudio.Controls;

/// <summary>
/// Custom FrameworkElement that renders FFT spectrum bars and peak indicators
/// directly via OnRender for maximum performance.
/// </summary>
public class SpectrumControl : FrameworkElement
{
    // Cached brushes to avoid allocations in OnRender
    // Initialize with default colors matching the DP defaults
    private readonly SolidColorBrush _barBrush = new(Color.FromArgb(0x99, 0xC0, 0x7A, 0x30));
    private readonly SolidColorBrush _peakBrush = new(Color.FromArgb(0x99, 0xAA, 0x55, 0x33));
    private readonly SolidColorBrush _bgBrush = new(Color.FromRgb(0x1A, 0x1A, 0x1A));

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(float[]), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakDataProperty =
        DependencyProperty.Register(nameof(PeakData), typeof(float[]), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarColorProperty =
        DependencyProperty.Register(nameof(BarColor), typeof(Color), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0xA5, 0x00), FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public static readonly DependencyProperty PeakColorProperty =
        DependencyProperty.Register(nameof(PeakColor), typeof(Color), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x44, 0x44), FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public static readonly DependencyProperty BackgroundColorProperty =
        DependencyProperty.Register(nameof(BackgroundColor), typeof(Color), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(Color.FromRgb(0x1A, 0x1A, 0x1A), FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public float[]? Data
    {
        get => (float[]?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public float[]? PeakData
    {
        get => (float[]?)GetValue(PeakDataProperty);
        set => SetValue(PeakDataProperty, value);
    }

    public Color BarColor
    {
        get => (Color)GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public Color PeakColor
    {
        get => (Color)GetValue(PeakColorProperty);
        set => SetValue(PeakColorProperty, value);
    }

    public Color BackgroundColor
    {
        get => (Color)GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    // Bar dimensions
    private const double MinBarHeight = 2.0;
    private const double BarWidth = 5.0;
    private const double PeakWidth = 3.5;

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SpectrumControl)d;
        if (e.Property == BarColorProperty)
            control._barBrush.Color = (Color)e.NewValue;
        else if (e.Property == PeakColorProperty)
            control._peakBrush.Color = (Color)e.NewValue;
        else if (e.Property == BackgroundColorProperty)
            control._bgBrush.Color = (Color)e.NewValue;
        control.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = RenderSize.Width;
        double height = RenderSize.Height;

        if (width <= 0 || height <= 0)
            return;

        // Use 95% of container height as max bar height so bars fill most of the space
        double maxBarHeight = height * 0.95;

        // Draw background
        dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, width, height));

        var data = Data;
        var peaks = PeakData;

        if (data == null || data.Length == 0)
            return;

        int binCount = data.Length;

        // Calculate total width per bar group (bar + gap)
        double totalStep = width / binCount;

        // Center bars within each step
        double barOffset = (totalStep - BarWidth) / 2.0;

        for (int i = 0; i < binCount; i++)
        {
            float value = Math.Clamp(data[i], 0f, 1f);
            double barHeight = Math.Max(MinBarHeight, value * maxBarHeight);
            barHeight = Math.Min(barHeight, height);

            double x = i * totalStep + barOffset;
            double y = height - barHeight;

            dc.DrawRectangle(_barBrush, null, new Rect(x, y, BarWidth, barHeight));

            if (peaks != null && i < peaks.Length)
            {
                float peakValue = Math.Clamp(peaks[i], 0f, 1f);
                double peakHeight = Math.Max(MinBarHeight, peakValue * maxBarHeight);
                peakHeight = Math.Min(peakHeight, height);

                double peakY = height - peakHeight;
                double peakX = x + (BarWidth - PeakWidth) / 2.0;

                dc.DrawRectangle(_peakBrush, null, new Rect(peakX, peakY, PeakWidth, 2));
            }
        }
    }
}
