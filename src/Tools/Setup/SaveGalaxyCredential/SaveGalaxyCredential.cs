// SaveGalaxyCredential: tests a galaxy login, then saves the user and password in Windows Credential Manager
// so other tools can log in without asking.

using System;
using GRAccessTools.Common;

namespace GRAccessTools.Setup
{
    class SaveGalaxyCredential
    {
        const string Usage =
            "Tests a galaxy login, then saves the user and password in Windows Credential Manager so other tools\r\n" +
            "log in without asking. Nothing is saved if the login fails. Run it again to change a saved password.\r\n" +
            "\r\n" +
            "Usage: SaveGalaxyCredential.exe -User <name> [-Galaxy <name>]\r\n" +
            "\r\n" +
            "The password is prompted, or read from the first line of standard input when input is redirected.\r\n" +
            "To remove a saved credential: cmdkey /delete:GRAccessTools:<Galaxy>@<Node>";

        static readonly string[] Options = new string[0];

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string node, galaxyName;
            ToolRunner.ResolveGalaxy(args, out node, out galaxyName);
            string user = args.Require("User");
            string password = Console.IsInputRedirected ? ReadPasswordFromInput() : ToolRunner.PromptPassword(user);

            using (GalaxySession session = GalaxySession.Open(node, galaxyName, user, password))
            {
                galaxyName = session.Galaxy.Name;  // save under the galaxy's exact name
            }

            CredentialStore.Save(node, galaxyName, user, password);
            Console.WriteLine("Login OK. Saved " + CredentialStore.TargetName(node, galaxyName) + " for user " + user + ".");
            return ExitCodes.Success;
        }

        static string ReadPasswordFromInput()
        {
            string password = Console.In.ReadLine();
            if (password == null)
                throw new UsageException("Input is redirected but contains no password.");
            return password;
        }
    }
}
