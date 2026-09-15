using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Galaxy credentials saved in Windows Credential Manager as generic credentials named
    /// "GRAccessTools:&lt;Galaxy&gt;@&lt;Node&gt;". Windows encrypts them for the current user on this machine,
    /// and they are listed under Control Panel > Credential Manager > Windows Credentials.
    /// </summary>
    public static class CredentialStore
    {
        public static string TargetName(string node, string galaxyName)
        {
            return "GRAccessTools:" + galaxyName + "@" + node;
        }

        /// <summary>Reads the saved user and password for a galaxy. Returns false if none is saved.</summary>
        public static bool TryRead(string node, string galaxyName, out string user, out string password)
        {
            user = null;
            password = null;

            IntPtr credentialPtr;
            if (!CredRead(TargetName(node, galaxyName), CRED_TYPE_GENERIC, 0, out credentialPtr))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_NOT_FOUND)
                    return false;
                throw new Win32Exception(error, "Could not read credential " + TargetName(node, galaxyName));
            }

            try
            {
                CREDENTIAL credential = (CREDENTIAL)Marshal.PtrToStructure(credentialPtr, typeof(CREDENTIAL));
                user = credential.UserName ?? "";
                password = credential.CredentialBlobSize == 0
                    ? ""
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
                return true;
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        /// <summary>Saves (or replaces) the user and password for a galaxy.</summary>
        public static void Save(string node, string galaxyName, string user, string password)
        {
            byte[] blob = Encoding.Unicode.GetBytes(password);
            CREDENTIAL credential = new CREDENTIAL();
            credential.Type = CRED_TYPE_GENERIC;
            credential.TargetName = TargetName(node, galaxyName);
            credential.Comment = "Galaxy login used by GRAccess Tools";
            credential.UserName = user;
            credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
            credential.CredentialBlobSize = (uint)blob.Length;
            credential.CredentialBlob = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));

            try
            {
                Marshal.Copy(blob, 0, credential.CredentialBlob, blob.Length);
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save credential " + credential.TargetName);
            }
            finally
            {
                // Don't leave copies of the password in memory longer than needed
                Marshal.Copy(new byte[blob.Length], 0, credential.CredentialBlob, blob.Length);
                Array.Clear(blob, 0, blob.Length);
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }
        }

        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
