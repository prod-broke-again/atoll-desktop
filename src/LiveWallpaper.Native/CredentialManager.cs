using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveWallpaper.Native;

public static class CredentialManager
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    public const string DefaultTimewebTarget = "LiveWallpaper/Timeweb";
    public const string WeeekTarget = "LiveWallpaper/Weeek";
    public const string GoogleCalendarIcsTarget = "LiveWallpaper/GoogleCalendarIcs";

    public static bool Exists(string target = DefaultTimewebTarget)
    {
        if (CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            CredFree(credPtr);
            return true;
        }
        return false;
    }

    public static string? ReadSecret(string target = DefaultTimewebTarget)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);

            // cmdkey stores password as UTF-16LE. Fallback to UTF-8 if needed.
            if (bytes.Length >= 2 && bytes[1] == 0)
            {
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }

            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static bool WriteSecret(string secret, string target = DefaultTimewebTarget, string userName = "token")
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userName
            };

            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static bool DeleteSecret(string target = DefaultTimewebTarget)
    {
        return CredDelete(target, CRED_TYPE_GENERIC, 0);
    }
}
