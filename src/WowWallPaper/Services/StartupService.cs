using Microsoft.Win32;
using WowWallPaper.Infrastructure;

namespace WowWallPaper.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WowWallPaper";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read startup registration: {ex.Message}");
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开当前用户的开机启动项。";
                return false;
            }

            if (enabled)
            {
                var command = BuildStartupCommand();
                if (command is null)
                {
                    error = "无法定位应用程序路径。";
                    return false;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
                AppLog.Info($"Startup registration enabled: {command}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("Startup registration disabled.");
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not update startup registration.", ex);
            error = "开机启动设置失败：" + ex.Message;
            return false;
        }
    }

    private static string? BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        // When started through the dotnet host, point at the managed entry assembly instead.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "WowWallPaper.dll");
            if (!File.Exists(assemblyPath))
            {
                return null;
            }

            return $"\"{processPath}\" \"{assemblyPath}\" --startup";
        }

        return $"\"{processPath}\" --startup";
    }
}
