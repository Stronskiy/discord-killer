using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DiscordKiller.Windows.ProcessControl;

/// <summary>Diagnostic facts about the on-disk file only. These do not establish the identity of a running image.</summary>
internal sealed record DiscordStableEvidence(bool StablePathMatch, bool AuthenticodeValid, string? SignerSubject)
{
    public bool RunningImageBindingVerified => false;
}

/// <summary>
/// Evidence-gathering policy, intentionally unusable for process authorization.
/// Verifying a file by its pathname does not bind that file to an already-running
/// process image, and a matching certificate subject alone is not publisher proof.
/// Do not enable until running-image binding and signer policy have been audited.
/// </summary>
internal sealed class DiscordStableEvidencePolicy : IWindowsProcessTrustPolicy
{
    public bool IsReady => false;
    public bool IsTrusted(WindowsProcessEvidence evidence) => false;

    /// <summary>
    /// Read only on-disk diagnostic evidence. Caller must not execute on the UI thread:
    /// WinVerifyTrust and the certificate provider are system operations which may block.
    /// No result from this method grants permission to terminate a process.
    /// </summary>
    public static DiscordStableEvidence InspectFile(string path, string localAppData)
    {
        bool pathMatch = IsStablePath(path, localAppData);
        if (!pathMatch) return new DiscordStableEvidence(false, false, null);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool valid = Signature.Verify(path, stream.SafeFileHandle.DangerousGetHandle());
            if (!valid) return new DiscordStableEvidence(true, false, null);
            // This legacy API extracts the embedded signer from a signed PE;
            // X509CertificateLoader reads certificate containers rather than PE signatures.
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new DiscordStableEvidence(true, true, certificate.Subject);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          CryptographicException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return new DiscordStableEvidence(true, false, null);
        }
    }

    public static bool IsStablePath(string path, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(localAppData)) return false;
        try
        {
            string root = Path.GetFullPath(Path.Combine(localAppData, "Discord"));
            string file = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(file), "Discord.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string? folder = Path.GetDirectoryName(file);
            if (folder is null || !string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(folder);
            return name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Version.TryParse(name[4..], out _);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static class Signature
    {
        private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static bool Verify(string path, IntPtr fileHandle)
        {
            IntPtr pathPtr = Marshal.StringToCoTaskMemUni(path);
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                var info = new WinTrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = pathPtr,
                    FileHandle = fileHandle,
                    KnownSubject = IntPtr.Zero
                };
                infoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(info, infoPtr, false);
                var data = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2, // WTD_UI_NONE
                    RevocationChecks = 0, // WTD_REVOKE_NONE
                    UnionChoice = 1, // WTD_CHOICE_FILE
                    FileInfo = infoPtr,
                    StateAction = 0, // WTD_STATEACTION_IGNORE
                    ProviderFlags = 0x1000 // WTD_CACHE_ONLY_URL_RETRIEVAL
                };
                Guid action = VerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
            }
            finally
            {
                if (infoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(infoPtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
    }
}
