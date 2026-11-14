using System.Windows.Threading;

namespace WowWallPaper.Services;

public sealed class WallpaperRotationService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private int _intervalMinutes = 30;
    private bool _enabled;
    private bool _disposed;

    public WallpaperRotationService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _timer.Tick += (_, _) =>
        {
            Tick?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Tick;

    public bool IsEnabled => _enabled;

    public void Configure(bool enabled, int intervalMinutes)
    {
        if (_disposed)
        {
            return;
        }

        _enabled = enabled;
        _intervalMinutes = Math.Clamp(intervalMinutes, 1, 1440);
        _timer.Interval = TimeSpan.FromMinutes(_intervalMinutes);

        if (enabled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Reset()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMinutes(_intervalMinutes);
        _timer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
