// ExportTemplateAttributes: writes every attribute of a template to a CSV file.
// Templates are collected by FindTemplates first and exported one by one, so other ways of choosing
// templates (such as a whole toolset) only need to add to that list.

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
            "Writes every attribute of a template to attributes.csv: toolset path, template, attribute name,\r\n" +
            "description, data type, whether it has I/O enabled, and whether it is a user-defined attribute (UDA).\r\n" +
            "\r\n" +
            "Usage: ExportTemplateAttributes.exe -t <template> [-Galaxy <name>]\r\n" +
            "\r\n" +
            "  -t <template>   Template name. The leading $ is optional: -t Pump is the same as -t '$Pump'";

        static readonly string[] Options = { "t" };

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
            string templateName = args.Require("t");

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                string galaxyName = session.Galaxy.Name;
                List<IgObject> templates = FindTemplates(session, templateName);
                if (templates.Count == 0)
                {
                    Console.Error.WriteLine("Template '" + TemplateTagname(templateName) + "' not found in " + galaxyName + ".");
                    return ExitCodes.Error;
                }

                string file = Path.Combine(OutputPaths.CreateRunFolder(galaxyName), "attributes.csv");
                int rows = 0;
                using (CsvWriter csv = new CsvWriter(file))
                {
                    csv.WriteRow(Columns);
                    foreach (IgObject template in templates)
                        rows += WriteAttributes(csv, template);
                }

                Console.WriteLine("Exported " + rows + " attributes of " + templates.Count + " template(s) to:");
                Console.WriteLine(file);
            }
            return ExitCodes.Success;
        }

        static List<IgObject> FindTemplates(GalaxySession session, string templateName)
        {
            List<IgObject> templates = new List<IgObject>();
            IgObject template = session.FindObject(TemplateTagname(templateName));
            if (template != null)
                templates.Add(template);
            return templates;
        }

        static string TemplateTagname(string name)
        {
            return name.StartsWith("$", StringComparison.Ordinal) ? name : "$" + name;
        }

        // One row per attribute, sorted by name. Returns the number of rows written.
        static int WriteAttributes(CsvWriter csv, IgObject template)
        {
            string path = (((ITemplate)template).Toolset ?? "").Replace('$', '/');  // GRAccess separates toolset levels with $
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
