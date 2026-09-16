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
            "  -Galaxy <name>   Galaxy to connect to (default: Galaxy in config\\defaults.ini)\r\n" +
            "  -Node <name>     Galaxy Repository node (default: Node in config\\defaults.ini, else this computer)\r\n" +
            "  -User <name>     Galaxy user (default: User for the galaxy in config\\credentials.local.ini). The\r\n" +
            "                   password comes from the same file when the user matches; otherwise it is prompted.\r\n" +
            "                   Omit when galaxy security is off.\r\n" +
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
        /// Opens the galaxy and logs in. The galaxy and node come from -Galaxy/-Node or config\defaults.ini.
        /// The user comes from -User or the galaxy's section in config\credentials.local.ini; the password from that
        /// file is used when the user matches, otherwise the password is prompted.
        /// Use it in a using block so the session always logs out.
        /// </summary>
        public static GalaxySession OpenGalaxy(ToolArgs args)
        {
            string node, galaxyName;
            ResolveGalaxy(args, out node, out galaxyName);

            string savedUser, savedPassword;
            bool saved = CredentialsFile.TryRead(galaxyName, out savedUser, out savedPassword);

            string user = args.Get("User", saved ? savedUser : "");
            bool useSaved = saved && string.Equals(user, savedUser, StringComparison.OrdinalIgnoreCase);
            string password = useSaved ? savedPassword : (user.Length > 0 ? PromptPassword(user) : "");

            try
            {
                return GalaxySession.Open(node, galaxyName, user, password);
            }
            catch (GalaxyLoginException ex)
            {
                if (useSaved)
                    throw new GRAccessException(ex.Message + Environment.NewLine +
                        "The user and password came from [" + galaxyName + "] in " + CredentialsFile.FilePath + ".");
                if (!saved && user.Length == 0)
                    throw new GRAccessException(ex.Message + Environment.NewLine +
                        "No login is saved for " + galaxyName + ". Add a [" + galaxyName + "] section with User= and Password= to " +
                        CredentialsFile.FilePath + " (see credentials.example.ini), or pass -User.");
                throw;
            }
        }

        /// <summary>The node and galaxy from -Node and -Galaxy, falling back to config\defaults.ini.</summary>
        public static void ResolveGalaxy(ToolArgs args, out string node, out string galaxyName)
        {
            ToolDefaults defaults = ToolDefaults.Load();
            node = args.Get("Node", defaults.Node ?? Environment.MachineName);
            galaxyName = args.Get("Galaxy", defaults.Galaxy);
            if (galaxyName == null)
                throw new UsageException("No galaxy given. Use -Galaxy, or set Galaxy in " + ToolDefaults.FilePath + ". Galaxies on " + node + ": " + string.Join(", ", GalaxySession.ListGalaxies(node)));
        }

        /// <summary>Asks for a password with masked input. Fails if input is redirected.</summary>
        public static string PromptPassword(string user)
        {
            if (Console.IsInputRedirected)
                throw new UsageException("Cannot prompt for the password of user '" + user + "' because input is redirected. Run the tool from an interactive console, or add the login to " + CredentialsFile.FilePath + ".");

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
