using System.Runtime.InteropServices;

namespace WowWallPaper.Interop;

/// <summary>
/// Reads the Windows mandatory integrity level of the current process. The DSH
/// file sandbox launches child processes at Low integrity, which makes desktop
/// wallpaper APIs fail with access denied even though the UI can start.
/// </summary>
public static class TokenSecurity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelInformation = 25;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryLowRid = 0x1000;

    public static bool IsLowIntegrity => GetIntegrityRid() is { } rid && rid < SecurityMandatoryMediumRid;

    public static string IntegrityLevelLabel
    {
        get
        {
            return GetIntegrityRid() switch
            {
                0x0000 => "不可信",
                SecurityMandatoryLowRid => "低完整性 / 受限沙箱",
                0x2000 => "普通用户",
                0x3000 => "管理员",
                0x4000 => "系统",
                _ => "未知"
            };
        }
    }

    private static int? GetIntegrityRid()
    {
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return null;
            }

            GetTokenInformation(token, TokenIntegrityLevelInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevelInformation, buffer, needed, out _))
            {
                return null;
            }

            var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
            if (label.Label.Sid == IntPtr.Zero)
            {
                return null;
            }

            var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
            if (countPointer == IntPtr.Zero)
            {
                return null;
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return null;
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
            return ridPointer == IntPtr.Zero ? null : Marshal.ReadInt32(ridPointer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
