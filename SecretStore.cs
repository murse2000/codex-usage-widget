using System.Runtime.InteropServices;
using System.Text;

namespace CodexUsageWidget;

/// <summary>
/// 사용자가 입력한 API 키를 설정 파일에 평문으로 남기지 않도록 DPAPI(현재 사용자)로 감싼다.
/// 다른 사용자나 다른 PC에서는 복호화되지 않으므로 그때는 설정하지 않은 것으로 본다.
/// </summary>
internal static class SecretStore
{
    private const int UiForbidden = 0x1;

    internal static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        var input = DataBlob.From(bytes);
        try
        {
            if (!Native.CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                return null;
            try { return Convert.ToBase64String(output.ToArray()); }
            finally { output.Free(); }
        }
        finally { input.Free(); }
    }

    internal static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
        var input = DataBlob.From(bytes);
        try
        {
            if (!Native.CryptUnprotectData(ref input, out var description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                return null;
            if (description != IntPtr.Zero) Native.LocalFree(description);
            try
            {
                var result = Encoding.UTF8.GetString(output.ToArray());
                return string.IsNullOrEmpty(result) ? null : result;
            }
            finally { output.Free(); }
        }
        finally { input.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;

        internal static DataBlob From(byte[] bytes)
        {
            var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        internal readonly byte[] ToArray()
        {
            var result = new byte[Size];
            Marshal.Copy(Data, result, 0, Size);
            return result;
        }

        internal void Free()
        {
            if (Data == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Size = 0;
        }
    }

    private static class Native
    {
        [DllImport("crypt32.dll", SetLastError = true)]
        internal static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        internal static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob output);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
