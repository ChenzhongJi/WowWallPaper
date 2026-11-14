using System.Drawing;
using System.Windows.Forms;
using WowWallPaper.Infrastructure;

namespace WowWallPaper.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _nextItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly Icon? _ownedIcon;
    private bool _disposed;

    public TrayIconService()
    {
        _ownedIcon = TryLoadIcon();
        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            BackColor = Color.FromArgb(19, 28, 44),
            ForeColor = Color.FromArgb(245, 248, 255),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(4)
        };

        var openItem = CreateItem("打开 WowWallPaper", bold: true);
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        _nextItem = CreateItem("下一张壁纸");
        _nextItem.Click += (_, _) => NextRequested?.Invoke();

        _pauseItem = CreateItem("暂停动态壁纸");
        _pauseItem.Click += (_, _) => TogglePauseRequested?.Invoke();

        _muteItem = CreateItem("静音：开");
        _muteItem.Click += (_, _) => MuteToggleRequested?.Invoke();

        _stopItem = CreateItem("停止壁纸并恢复系统壁纸");
        _stopItem.Click += (_, _) => StopRequested?.Invoke();

        var exitItem = CreateItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_nextItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_muteItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "WowWallPaper · 桌面壁纸引擎",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? NextRequested;

    public event Action? TogglePauseRequested;

    public event Action? MuteToggleRequested;

    public event Action? StopRequested;

    public event Action? ExitRequested;

    public void UpdateState(bool videoActive, bool videoPaused, bool muted, bool hasWallpaper, bool hasLibrary)
    {
        if (_disposed)
        {
            return;
        }

        _nextItem.Enabled = hasLibrary;
        _pauseItem.Enabled = videoActive;
        _pauseItem.Text = videoPaused ? "继续动态壁纸" : "暂停动态壁纸";
        _muteItem.Text = muted ? "静音：开" : "静音：关";
        _stopItem.Enabled = hasWallpaper;

        var status = videoActive
            ? videoPaused ? "动态壁纸已暂停" : "动态壁纸播放中"
            : hasWallpaper ? "静态壁纸已应用" : "壁纸引擎运行中";
        _notifyIcon.Text = $"WowWallPaper · {status}";
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3200);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }

    private static ToolStripMenuItem CreateItem(string text, bool bold = false)
    {
        return new ToolStripMenuItem(text)
        {
            ForeColor = Color.FromArgb(224, 232, 244),
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular),
            Padding = new Padding(4, 4, 4, 4)
        };
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tray icon could not be loaded: {ex.Message}");
            return null;
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color MenuBackground = Color.FromArgb(19, 28, 44);
        private static readonly Color Border = Color.FromArgb(47, 64, 88);
        private static readonly Color Hover = Color.FromArgb(31, 45, 66);

        public override Color ToolStripDropDownBackground => MenuBackground;

        public override Color ImageMarginGradientBegin => MenuBackground;

        public override Color ImageMarginGradientMiddle => MenuBackground;

        public override Color ImageMarginGradientEnd => MenuBackground;

        public override Color MenuBorder => Border;

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color MenuItemPressedGradientBegin => Hover;

        public override Color MenuItemPressedGradientEnd => Hover;

        public override Color SeparatorDark => Border;

        public override Color SeparatorLight => Border;
    }
}
