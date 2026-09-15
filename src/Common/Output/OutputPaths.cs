using System;
using System.IO;
using System.Reflection;

namespace GRAccessTools.Common
{
    /// <summary>Where tools write their results: one new folder per run under output\ (see output\README.md).</summary>
    public static class OutputPaths
    {
        /// <summary>
        /// Creates output\&lt;ToolName&gt;\&lt;yyyyMMdd-HHmmss&gt;_&lt;Galaxy&gt;\ and returns its full path.
        /// output\ is found from the exe (bin\..\output), so it does not depend on the current folder.
        /// </summary>
        public static string CreateRunFolder(string galaxyName)
        {
            string toolName = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location);
            string toolFolder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "output", toolName));
            string runName = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + galaxyName;

            // Never reuse a folder, even when two runs start in the same second
            string folder = Path.Combine(toolFolder, runName);
            for (int i = 2; Directory.Exists(folder); i++)
                folder = Path.Combine(toolFolder, runName + "_" + i);

            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
