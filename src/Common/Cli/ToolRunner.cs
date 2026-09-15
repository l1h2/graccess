using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Standard entry point for every tool: checks the process is set up for GRAccess, parses the
    /// arguments, handles -Help, and turns exceptions into messages and exit codes.
    /// </summary>
    public static class ToolRunner
    {
        private static readonly string[] StandardOptions = { "Galaxy", "Node", "User", "Help" };

        private const string StandardUsage =
            "Standard options:\r\n" +
            "  -Galaxy <name>   Galaxy to connect to (run without it to list the galaxies)\r\n" +
            "  -Node <name>     Galaxy Repository node (default: this computer)\r\n" +
            "  -User <name>     Galaxy user; the password is prompted. Omit when galaxy security is off.\r\n" +
            "  -Help            Show this help";

        /// <summary>The whole body of a tool's Main: return ToolRunner.Run(args, Usage, Options, Execute);</summary>
        public static int Run(string[] args, string usage, string[] toolOptions, Func<ToolArgs, int> execute)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return Fail("Main must be marked [STAThread]: GRAccess objects are apartment-threaded.");
            if (IntPtr.Size != 4)
                return Fail("This tool must run as a 32-bit process because GRAccess is 32-bit. Build it with build.ps1.");

            try
            {
                List<string> allowed = new List<string>(StandardOptions);
                allowed.AddRange(toolOptions);
                ToolArgs toolArgs = new ToolArgs(args, allowed);

                if (toolArgs.Has("Help"))
                {
                    PrintUsage(Console.Out, usage);
                    return ExitCodes.Success;
                }
                return execute(toolArgs);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine();
                PrintUsage(Console.Error, usage);
                return ExitCodes.Usage;
            }
            catch (GRAccessException ex)
            {
                return Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Unexpected error: " + ex);
            }
        }

        /// <summary>
        /// Opens the galaxy given by -Galaxy on -Node, logging in as -User (password prompted).
        /// Use it in a using block so the session always logs out.
        /// </summary>
        public static GalaxySession OpenGalaxy(ToolArgs args)
        {
            string node = args.Get("Node", Environment.MachineName);
            string galaxyName = args.Get("Galaxy", null);
            if (galaxyName == null)
                throw new UsageException("Missing required option -Galaxy. Galaxies on " + node + ": " + string.Join(", ", GalaxySession.ListGalaxies(node)));

            string user = args.Get("User", "");
            string password = user.Length > 0 ? PromptPassword(user) : "";
            return GalaxySession.Open(node, galaxyName, user, password);
        }

        private static string PromptPassword(string user)
        {
            if (Console.IsInputRedirected)
                throw new UsageException("Cannot prompt for the password of user '" + user + "' because input is redirected. Run the tool from an interactive console.");

            Console.Write("Password for " + user + ": ");
            StringBuilder password = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Length--;
                        Console.Write("\b \b");
                    }
                }
                else if (key.KeyChar != '\0')
                {
                    password.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            Console.WriteLine();
            return password.ToString();
        }

        private static void PrintUsage(TextWriter writer, string usage)
        {
            writer.WriteLine(usage);
            writer.WriteLine();
            writer.WriteLine(StandardUsage);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return ExitCodes.Error;
        }
    }
}
