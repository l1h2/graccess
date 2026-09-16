using System;
using System.IO;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Galaxy logins kept in config\credentials.local.ini (found from the exe: bin\..\config), a file git ignores.
    /// One [GalaxyName] section per galaxy with User= and Password=; lines starting with ';' or '#' are comments.
    /// Leading and trailing spaces around names and values are ignored. No file means no saved logins.
    /// </summary>
    public static class CredentialsFile
    {
        public static string FilePath
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "config", "credentials.local.ini")); }
        }

        /// <summary>The user and password saved for a galaxy. Returns false when there is no file or no section for it.</summary>
        public static bool TryRead(string galaxyName, out string user, out string password)
        {
            user = null;
            password = null;
            string path = FilePath;
            if (!File.Exists(path))
                return false;

            bool found = false;
            string section = null;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;

                string where = path + " line " + (i + 1) + ": ";
                if (line[0] == '[')
                {
                    if (!line.EndsWith("]", StringComparison.Ordinal))
                        throw new UsageException(where + "expected [GalaxyName].");
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (string.Equals(section, galaxyName, StringComparison.OrdinalIgnoreCase))
                        found = true;
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new UsageException(where + "expected Name=Value.");
                if (section == null)
                    throw new UsageException(where + "put User= and Password= under a [GalaxyName] section.");

                string name = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                bool isUser = string.Equals(name, "User", StringComparison.OrdinalIgnoreCase);
                // The name is not echoed: on a mistyped line it can be part of a password
                if (!isUser && !string.Equals(name, "Password", StringComparison.OrdinalIgnoreCase))
                    throw new UsageException(where + "expected User= or Password=.");

                if (!string.Equals(section, galaxyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (isUser)
                    user = value;
                else
                    password = value;
            }

            if (!found)
                return false;
            user = user ?? "";
            password = password ?? "";
            return true;
        }
    }
}
