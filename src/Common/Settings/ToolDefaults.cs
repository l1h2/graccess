using System;
using System.IO;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Defaults for the standard options, read from config\defaults.ini (found from the exe: bin\..\config).
    /// Lines are Name=Value; lines starting with ';' or '#' are comments. No file means no defaults.
    /// </summary>
    public sealed class ToolDefaults
    {
        public string Galaxy { get; private set; }
        public string Node { get; private set; }

        public static string FilePath
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "config", "defaults.ini")); }
        }

        public static ToolDefaults Load()
        {
            ToolDefaults defaults = new ToolDefaults();
            string path = FilePath;
            if (!File.Exists(path))
                return defaults;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new UsageException(path + " line " + (i + 1) + ": expected Name=Value.");

                string name = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (value.Length == 0)
                    value = null;

                if (string.Equals(name, "Galaxy", StringComparison.OrdinalIgnoreCase))
                    defaults.Galaxy = value;
                else if (string.Equals(name, "Node", StringComparison.OrdinalIgnoreCase))
                    defaults.Node = value;
                else
                    throw new UsageException(path + " line " + (i + 1) + ": unknown setting '" + name + "'. Known settings: Galaxy, Node.");
            }
            return defaults;
        }
    }
}
