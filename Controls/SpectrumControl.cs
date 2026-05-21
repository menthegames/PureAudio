using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PureAudio.Controls;

/// <summary>
/// High-performance spectrum analyzer with segmented bars and spring-physics animation.
/// 
/// Design philosophy (per SKILL.md):
/// • Segmented columns instead of continuous lines
/// • Spring-like motion (fast attack, natural overshoot + settle)
/// • Gold accent on dark background
/// • No red, no blue — only gold, warm white, and grays
/// </summary>
public class SpectrumControl : FrameworkElement
{
    // ── Spring Physics Constants ──
    // Simulates a critically-damped spring for natural-feeling motion
    // Higher stiffness = faster response, higher damping = less overshoot
    private const float SpringStiffness = 180f;
    private const float SpringDamping = 18f;
    private const float MinVelocity = 0.001f;

    // ── Visual Constants ──
    private const int BarCount = 48;
    private const double MinBarHeight = 1.5;
    private const double BarGapRatio = 0.25; // 25% of bar width goes to gap
    private const double SegmentHeight = 3.0; // Height of each segment block
    private const double SegmentGap = 1.0;    // Gap between segments

    // ── Color Palette (gold family, no red/blue) ──
    private static readonly Color BarColorLow = Color.FromRgb(0x55, 0x4A, 0x30);    // Dark gold
    private static readonly Color BarColorMid = Color.FromRgb(0xC9, 0xA8, 0x4C);    // Gold accent
    private static readonly Color BarColorHigh = Color.FromRgb(0xE8, 0xD0, 0x80);   // Light gold
    private static readonly Color PeakColor = Color.FromRgb(0xF0, 0xE0, 0xA0);      // Peak white-gold
    private static readonly Color BgColor = Color.FromRgb(0x0D, 0x0D, 0x0D);         // Off-black

    // ── Cached Brushes ──
    private readonly SolidColorBrush _bgBrush = new(BgColor);
    private readonly SolidColorBrush _peakBrush = new(PeakColor);

    // ── Spring Physics State ──
    // Each bar has: current position, velocity, target position
    private readonly float[] _currentPositions = new float[BarCount];
    private readonly float[] _velocities = new float[BarCount];
    private readonly float[] _targetPositions = new float[BarCount];

    // ── Peak Hold State ──
    private readonly float[] _peakPositions = new float[BarCount];
    private const float PeakDecayRate = 0.92f; // How fast peaks fall

    // ── Animation Timer ──
    private DispatcherTimer? _animTimer;
    private DateTime _lastFrameTime;

    // ── Dependency Properties ──
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(float[]), typeof(SpectrumControl),
            new FrameworkPropertyMetadata(null, OnDataChanged));

    public float[]? Data
    {
        get => (float[]?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SpectrumControl)d;
        if (e.NewValue is float[] newData)
        {
            control.UpdateTargets(newData);
        }
    }

    public SpectrumControl()
    {
        // Initialize with zero positions
        Array.Clear(_currentPositions, 0, BarCount);
        Array.Clear(_velocities, 0, BarCount);
        Array.Clear(_targetPositions, 0, BarCount);
        Array.Clear(_peakPositions, 0, BarCount);

        // Start animation timer (60 FPS)
        _animTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animTimer.Tick += OnAnimationTick;
        _animTimer.Start();
        _lastFrameTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates target positions from incoming FFT data.
    /// </summary>
    private void UpdateTargets(float[] data)
    {
        int len = Math.Min(data.Length, BarCount);
        for (int i = 0; i < len; i++)
        {
            _targetPositions[i] = Math.Clamp(data[i], 0f, 1f);
        }
    }

    /// <summary>
    /// Spring physics tick — updates bar positions with natural motion.
    /// </summary>
    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        // Clamp dt to prevent physics explosion on lag spikes
        if (dt > 0.05f) dt = 0.05f;

        bool anyMoving = false;

        for (int i = 0; i < BarCount; i++)
        {
            // ── Spring Physics ──
            // Force = stiffness * (target - current) - damping * velocity
            float displacement = _targetPositions[i] - _currentPositions[i];
            float force = SpringStiffness * displacement - SpringDamping * _velocities[i];

            // Integrate velocity and position (semi-implicit Euler)
            _velocities[i] += force * dt;
            _currentPositions[i] += _velocities[i] * dt;

            // Clamp position
            if (_currentPositions[i] < 0f) _currentPositions[i] = 0f;
            if (_currentPositions[i] > 1f) _currentPositions[i] = 1f;

            // Stop if nearly at rest
            if (Math.Abs(displacement) < 0.001f && Math.Abs(_velocities[i]) < MinVelocity)
            {
                _currentPositions[i] = _targetPositions[i];
                _velocities[i] = 0f;
            }
            else
            {
                anyMoving = true;
            }

            // ── Peak Hold ──
            if (_currentPositions[i] > _peakPositions[i])
            {
                _peakPositions[i] = _currentPositions[i];
            }
            else
            {
                _peakPositions[i] *= PeakDecayRate;
                if (_peakPositions[i] < 0.001f) _peakPositions[i] = 0f;
            }
        }

        // Only invalidate if something is moving or peaks are changing
        if (anyMoving)
        {
            InvalidateVisual();
        }
        else
        {
            // Still invalidate periodically for peak decay
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = RenderSize.Width;
        double height = RenderSize.Height;

        if (width <= 0 || height <= 0)
            return;

        // Draw background
        dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, width, height));

        // Calculate bar dimensions
        double totalBarWidth = width / BarCount;
        double barWidth = totalBarWidth * (1.0 - BarGapRatio);
        double gapWidth = totalBarWidth * BarGapRatio;
        double barOffset = gapWidth / 2.0;

        // Max bar height (leave 5% margin at top for peaks)
        double maxBarHeight = height * 0.92;

        // Pre-create brushes for this frame (avoid allocation per bar)
        var barBrushLow = new SolidColorBrush(BarColorLow);
        var barBrushMid = new SolidColorBrush(BarColorMid);
        var barBrushHigh = new SolidColorBrush(BarColorHigh);

        for (int i = 0; i < BarCount; i++)
        {
            float normalizedValue = _currentPositions[i];
            float peakValue = _peakPositions[i];

            // Skip if bar is essentially invisible
            if (normalizedValue < 0.001f && peakValue < 0.001f)
                continue;

            double barHeight = Math.Max(MinBarHeight, normalizedValue * maxBarHeight);
            double peakHeight = Math.Max(MinBarHeight, peakValue * maxBarHeight);

            double x = i * totalBarWidth + barOffset;

            // ── Draw Segmented Bar ──
            // Instead of a solid rectangle, draw stacked segments
            double availableHeight = barHeight;
            double y = height - SegmentHeight; // Start from bottom

            // Choose color based on intensity
            var barBrush = normalizedValue < 0.4f ? barBrushLow :
                           normalizedValue < 0.7f ? barBrushMid :
                           barBrushHigh;

            while (availableHeight > 0)
            {
                double segH = Math.Min(SegmentHeight, availableHeight);
                dc.DrawRectangle(barBrush, null, new Rect(x, y - segH + SegmentHeight, barWidth, segH));

                availableHeight -= (SegmentHeight + SegmentGap);
                y -= (SegmentHeight + SegmentGap);
            }

            // ── Draw Peak Indicator ──
            if (peakValue > 0.01f)
            {
                double peakY = height - peakHeight;
                dc.DrawRectangle(_peakBrush, null, new Rect(x, peakY - 1, barWidth, 2));
            }
        }
    }

    /// <summary>
    /// Clean up resources.
    /// </summary>
    public void Dispose()
    {
        _animTimer?.Stop();
        _animTimer = null;
    }
}
