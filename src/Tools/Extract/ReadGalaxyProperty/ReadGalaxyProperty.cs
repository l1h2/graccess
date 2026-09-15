// ReadGalaxyProperty: prints a galaxy's name and version, and one attribute of one object.
// This is the reference tool: copy this folder to start a new tool (see README.md).

using System;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    class ReadGalaxyProperty
    {
        const string Usage =
            "Prints a galaxy's name and version, and the value of one attribute of one object.\r\n" +
            "\r\n" +
            "Usage: ReadGalaxyProperty.exe -Galaxy <name> [-Object <tagname>] [-Attribute <name>] [-User <name>]\r\n" +
            "\r\n" +
            "  -Object <tagname>   Template or instance to read (default: $UserDefined)\r\n" +
            "  -Attribute <name>   Attribute to read (default: SecurityGroup)";

        static readonly string[] Options = { "Object", "Attribute" };

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string objectName = args.Get("Object", "$UserDefined");
            string attributeName = args.Get("Attribute", "SecurityGroup");

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                IGalaxy galaxy = session.Galaxy;
                Console.WriteLine("Galaxy:  " + galaxy.Name);
                Console.WriteLine("Version: " + galaxy.VersionString);

                IgObject obj = session.FindObject(objectName);
                if (obj == null)
                {
                    Console.Error.WriteLine("Object '" + objectName + "' not found.");
                    return ExitCodes.Error;
                }

                IAttribute attribute = obj.Attributes[attributeName];
                if (attribute == null)
                {
                    Console.Error.WriteLine("Attribute '" + attributeName + "' not found on " + obj.Tagname + ".");
                    return ExitCodes.Error;
                }

                Console.WriteLine(obj.Tagname + "." + attribute.Name + " = " + attribute.value.GetString());
                return ExitCodes.Success;
            }
        }
    }
}
