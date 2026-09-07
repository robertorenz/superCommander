using System.Runtime.InteropServices;
using System.Text;

namespace SuperCommander.Interop;

/// <summary>
/// User-scoped DPAPI, used so saved FTP passwords are never written to
/// settings.json in the clear.
///
/// P/Invoked rather than taken from System.Security.Cryptography.ProtectedData,
/// which is a separate NuGet package the app deliberately does not depend on.
/// </summary>
internal static class DataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Encrypts to a base64 blob only this Windows user can read back.</summary>
    internal static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var input = new DATA_BLOB();
        var output = new DATA_BLOB();

        try
        {
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            input.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptProtectData(ref input, "SuperCommander", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output))
                return string.Empty;

            var encrypted = new byte[output.cbData];
            Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception)
        {
            return string.Empty;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            Array.Clear(bytes);
        }
    }

    /// <summary>Reverses <see cref="Protect"/>. Returns empty when the blob is not ours.</summary>
    internal static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return string.Empty;

        var input = new DATA_BLOB();
        var output = new DATA_BLOB();

        try
        {
            var bytes = Convert.FromBase64String(base64);
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            input.cbData = bytes.Length;
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output))
                return string.Empty;

            var decrypted = new byte[output.cbData];
            Marshal.Copy(output.pbData, decrypted, 0, output.cbData);

            var text = Encoding.UTF8.GetString(decrypted);
            Array.Clear(decrypted);
            return text;
        }
        catch (Exception)
        {
            // A copied settings file from another machine or user lands here.
            return string.Empty;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
