// ExportTemplateAttributes: writes every attribute of one template, or of all templates in a toolset, to a CSV file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    class ExportTemplateAttributes
    {
        const string Usage =
            "Writes every attribute of a template, or of all templates in a toolset, to attributes.csv: toolset path,\r\n" +
            "template, attribute name, description, data type, whether it has I/O enabled, and whether it is a\r\n" +
            "user-defined attribute (UDA).\r\n" +
            "\r\n" +
            "Usage: ExportTemplateAttributes.exe -t <template>\r\n" +
            "       ExportTemplateAttributes.exe -d <toolset path> [-r]\r\n" +
            "\r\n" +
            "  -t <template>       Template name. The leading $ is optional: -t Pump is the same as -t '$Pump'\r\n" +
            "  -d <toolset path>   Full toolset path from the top level, levels separated by / or \\, e.g.\r\n" +
            "                      -d Radix/Equipment/Pump. Quote paths with spaces: -d 'Radix/Equipment/Cooling Towers'\r\n" +
            "  -r                  With -d, also export the templates in every toolset below it";

        static readonly string[] Options = { "t", "d", "r" };

        static readonly string[] Columns = { "path", "template", "name", "description", "dataType", "ioEnabled", "uda" };

        // Attribute extensions that connect an attribute to I/O
        static readonly HashSet<string> IoExtensionTypes = new HashSet<string>(
            new[] { "inputextension", "outputextension", "inputoutputextension" }, StringComparer.OrdinalIgnoreCase);

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string templateName = args.Get("t", null);
            string toolsetPath = args.Get("d", null);
            bool recursive = args.Has("r");
            if ((templateName == null) == (toolsetPath == null))
                throw new UsageException("Give either -t <template> or -d <toolset path>.");
            if (recursive && toolsetPath == null)
                throw new UsageException("-r can only be used with -d.");

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                IGalaxy galaxy = session.Galaxy;
                List<IgObject> templates = templateName != null
                    ? FindTemplate(session, templateName)
                    : FindTemplatesInToolset(galaxy, toolsetPath, recursive);

                string file = Path.Combine(OutputPaths.CreateRunFolder(galaxy.Name), "attributes.csv");
                int rows = 0;
                using (CsvWriter csv = new CsvWriter(file))
                {
                    csv.WriteRow(Columns);
                    foreach (IgObject template in templates)
                    {
                        Console.WriteLine("  " + template.Tagname);
                        rows += WriteAttributes(csv, template);
                    }
                }

                Console.WriteLine("Exported " + rows + " attributes of " + templates.Count + " template(s) to:");
                Console.WriteLine(file);
            }
            return ExitCodes.Success;
        }

        static List<IgObject> FindTemplate(GalaxySession session, string templateName)
        {
            string tagname = templateName.StartsWith("$", StringComparison.Ordinal) ? templateName : "$" + templateName;
            IgObject template = session.FindObject(tagname);
            if (template == null)
                throw new GRAccessException("Template '" + tagname + "' not found in " + session.Galaxy.Name + ".");
            return new List<IgObject> { template };
        }

        // Templates in the toolset (and, when recursive, in every toolset below it), sorted by toolset path and name.
        // GRAccess cannot query templates by toolset, so this reads the toolset of every template in the galaxy.
        static List<IgObject> FindTemplatesInToolset(IGalaxy galaxy, string toolsetPath, bool recursive)
        {
            bool hasSubToolsets;
            string toolset = ResolveToolset(galaxy, toolsetPath, out hasSubToolsets);
            Console.WriteLine("Finding templates in " + DisplayPath(toolset) + (recursive ? " and the toolsets below it" : "") + "...");

            IgObjects allTemplates = galaxy.QueryObjects(EgObjectIsTemplateOrInstance.gObjectIsTemplate, EConditionType.namedLike, "%", EMatch.MatchCondition);
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "QueryObjects (all templates)");

            // Sort key: toolset, then template name ("\0" sorts a toolset's own templates before its sub-toolsets)
            SortedDictionary<string, IgObject> found = new SortedDictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
            foreach (IgObject template in allTemplates)
            {
                string templateToolset = ((ITemplate)template).Toolset ?? "";
                if (string.Equals(templateToolset, toolset, StringComparison.OrdinalIgnoreCase)
                    || (recursive && templateToolset.StartsWith(toolset + "$", StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(templateToolset + "\0" + template.Tagname, template);
                }
            }

            if (found.Count == 0)
            {
                string message = recursive
                    ? "Toolset " + DisplayPath(toolset) + " and the toolsets below it have no templates."
                    : "Toolset " + DisplayPath(toolset) + " has no templates directly in it." + (hasSubToolsets ? " Add -r to include the toolsets below it." : "");
                throw new GRAccessException(message);
            }
            return new List<IgObject>(found.Values);
        }

        // Finds a toolset by its full path: levels separated by / or \, case-insensitive, outer separators ignored.
        // Returns the path the way GRAccess writes it, with $ between levels.
        static string ResolveToolset(IGalaxy galaxy, string toolsetPath, out bool hasSubToolsets)
        {
            string wanted = toolsetPath.Trim().Replace('\\', '$').Replace('/', '$').Trim('$');
            if (wanted.Length == 0)
                throw new UsageException("-d needs a toolset path, e.g. -d Radix/Equipment/Pump.");

            IToolsets toolsets = galaxy.QueryToolsets();
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "QueryToolsets");

            List<string> names = new List<string>();
            foreach (IToolset t in toolsets)
                names.Add(t.Name);

            string match = null;
            List<string> sameLastLevel = new List<string>();
            foreach (string name in names)
            {
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    match = name;
                else if (string.Equals(LastLevel(name), LastLevel(wanted), StringComparison.OrdinalIgnoreCase))
                    sameLastLevel.Add(DisplayPath(name));
            }

            if (match == null)
            {
                string message = "Toolset '" + DisplayPath(wanted) + "' not found in " + galaxy.Name + ". Use the full path from the top level.";
                if (sameLastLevel.Count > 0)
                    message += " Did you mean: " + string.Join(", ", sameLastLevel.ToArray()) + "?";
                throw new GRAccessException(message);
            }

            hasSubToolsets = false;
            foreach (string name in names)
            {
                if (name.StartsWith(match + "$", StringComparison.OrdinalIgnoreCase))
                    hasSubToolsets = true;
            }
            return match;
        }

        static string LastLevel(string toolset)
        {
            return toolset.Substring(toolset.LastIndexOf('$') + 1);
        }

        // GRAccess separates toolset levels with $; the CSV and -d use /
        static string DisplayPath(string toolset)
        {
            return toolset.Replace('$', '/');
        }

        // One row per attribute, sorted by name. Returns the number of rows written.
        static int WriteAttributes(CsvWriter csv, IgObject template)
        {
            string path = DisplayPath(((ITemplate)template).Toolset ?? "");
            HashSet<string> udas = ReadNames(template, new[] { "UDAs", "_InheritedUDAs" }, "/UDAInfo/Attribute", null);
            HashSet<string> ioEnabled = ReadNames(template, new[] { "Extensions", "_InheritedExtensions" }, "/ExtensionInfo/AttributeExtension/Attribute", IoExtensionTypes);

            SortedDictionary<string, IAttribute> attributes = new SortedDictionary<string, IAttribute>(StringComparer.OrdinalIgnoreCase);
            foreach (IAttribute attribute in template.Attributes)
            {
                if (!attributes.ContainsKey(attribute.Name))  // a few internal attributes are listed twice
                    attributes.Add(attribute.Name, attribute);
            }

            foreach (IAttribute attribute in attributes.Values)
            {
                csv.WriteRow(path, template.Tagname, attribute.Name, Description(attribute), DataTypeName(attribute),
                    ioEnabled.Contains(attribute.Name), udas.Contains(attribute.Name));
            }
            return attributes.Count;
        }

        static string Description(IAttribute attribute)
        {
            string description = attribute.Description;
            return description == null || description == "No Data" ? "" : description;
        }

        // MxBoolean -> Boolean; arrays get [] (GRAccess reports UpperBoundDim1 = -1 for non-arrays)
        static string DataTypeName(IAttribute attribute)
        {
            string name = attribute.DataType.ToString();
            if (name.StartsWith("Mx", StringComparison.Ordinal))
                name = name.Substring(2);
            return attribute.UpperBoundDim1 >= 0 ? name + "[]" : name;
        }

        // Attribute names listed in the XML that templates keep in attributes such as UDAs and Extensions.
        // When extensionTypes is given, only entries with one of those ExtensionType values are included.
        static HashSet<string> ReadNames(IgObject template, string[] xmlAttributeNames, string xpath, HashSet<string> extensionTypes)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string xmlAttributeName in xmlAttributeNames)
            {
                IAttribute xmlAttribute = template.Attributes[xmlAttributeName];
                if (xmlAttribute == null)
                    continue;

                string xml = xmlAttribute.value.GetString();
                if (string.IsNullOrEmpty(xml) || !xml.TrimStart().StartsWith("<", StringComparison.Ordinal))
                    continue;

                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                foreach (XmlElement element in document.SelectNodes(xpath))
                {
                    if (extensionTypes == null || extensionTypes.Contains(element.GetAttribute("ExtensionType")))
                        names.Add(element.GetAttribute("Name"));
                }
            }
            return names;
        }
    }
}
