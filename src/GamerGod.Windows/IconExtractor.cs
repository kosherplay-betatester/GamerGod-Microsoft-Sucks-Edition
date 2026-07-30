using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GamerGod.Windows;

/// <summary>
/// An icon handle that frees itself.
///
/// <para>
/// Icons are a GDI resource with a hard per-process limit, and a library page holding forty of
/// them is exactly the shape of leak that surfaces as "the interface stopped drawing" an hour
/// later rather than as an exception anyone can trace.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeIconHandle(IntPtr icon)
        : base(ownsHandle: true) => SetHandle(icon);

    protected override bool ReleaseHandle() => IconExtractor.DestroyIcon(handle);
}

/// <summary>
/// Reads the icon out of an executable.
///
/// <para>
/// This is the whole reason installed programs can show their real icon rather than a generated
/// stand-in: Windows already has the artwork, embedded in the binary, and no network or vendor
/// lookup is involved in getting it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class IconExtractor
{
    /// <summary>
    /// The first icon in a file, at the large size the shell reports (normally 32 or 48 px,
    /// larger on a high-DPI setup), or null when the file has none.
    ///
    /// <para>
    /// Returning null is ordinary. Plenty of installers point DisplayIcon at a path that no
    /// longer exists, and a program without an icon is not a program with a problem.
    /// </para>
    /// </summary>
    public static SafeIconHandle? TryExtract(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // nIconIndex 0 is the icon Explorer shows for the file. Small is requested as
            // IntPtr.Zero so the shell does not allocate a second handle nobody frees.
            var extracted = ExtractIconEx(path, 0, out var large, out _, 1);

            if (extracted <= 0 || large == IntPtr.Zero)
            {
                return null;
            }

            return new SafeIconHandle(large);
        }
        catch (Exception)
        {
            // A file locked, on a disconnected network share, or of a format the shell will
            // not parse. The interface draws its generated tile.
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ExtractIconEx(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
