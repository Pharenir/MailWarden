using System.Runtime.InteropServices;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// What .NET does not tell about a link: which kind it is. The attributes only say that an entry is a reparse point.
/// </summary>
internal static class NativeLinks
{
    private const uint MountPointReparseTag = 0xA0000003;
    private const uint ReparsePointAttribute = 0x400;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Checks whether an entry is a junction. It looks at the entry itself and never at what it points to.</summary>
    /// <param name="path">Path of the entry; a <c>\\?\</c> path reaches every name.</param>
    /// <returns>True for a junction; false for anything else, also when the entry cannot be read.</returns>
    public static bool IsJunction(string path)
    {
        IntPtr search = FindFirstFile(path, out FindData found);
        if (search == InvalidHandle)
        {
            return false;
        }

        _ = FindClose(search);
        return (found.Attributes & ReparsePointAttribute) != 0 && found.ReparseTag == MountPointReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFile(string fileName, out FindData found);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr search);

    // WIN32_FIND_DATAW: for a reparse point dwReserved0 holds its tag. Its times are two 32-bit values each, hence the packing.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FindData
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint SizeHigh;
        public uint SizeLow;
        public uint ReparseTag;
        public uint Reserved;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }
}
