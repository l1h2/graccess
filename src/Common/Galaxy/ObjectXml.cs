using System;
using System.Collections.Generic;
using System.Xml;
using ArchestrA.GRAccess;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Reads the XML that objects keep in attributes such as UDAs, Extensions and CmdData (see docs\GRAccess-Notes.md).
    /// </summary>
    public static class ObjectXml
    {
        private static readonly string[] IoExtensionTypes = { "inputextension", "outputextension", "inputoutputextension" };

        /// <summary>Elements that match the XPath in the attribute's XML. Empty when the attribute is missing or holds no XML.</summary>
        public static List<XmlElement> SelectElements(IgObject obj, string attributeName, string xpath)
        {
            List<XmlElement> elements = new List<XmlElement>();
            IAttribute attribute = obj.Attributes[attributeName];
            if (attribute == null)
                return elements;

            string xml = attribute.value.GetString();
            if (string.IsNullOrEmpty(xml) || !xml.TrimStart().StartsWith("<", StringComparison.Ordinal))
                return elements;

            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            foreach (XmlElement element in document.SelectNodes(xpath))
                elements.Add(element);
            return elements;
        }

        /// <summary>
        /// The I/O extension type (inputextension, outputextension or inputoutputextension) of each attribute that has
        /// I/O, sorted by attribute name. Extensions inherited from templates are included when asked for.
        /// </summary>
        public static SortedDictionary<string, string> IoExtensions(IgObject obj, bool includeInherited)
        {
            SortedDictionary<string, string> extensions = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] xmlAttributes = includeInherited ? new[] { "Extensions", "_InheritedExtensions" } : new[] { "Extensions" };
            foreach (string xmlAttribute in xmlAttributes)
            {
                foreach (XmlElement element in SelectElements(obj, xmlAttribute, "/ExtensionInfo/AttributeExtension/Attribute"))
                {
                    string type = element.GetAttribute("ExtensionType").ToLowerInvariant();
                    if (Array.IndexOf(IoExtensionTypes, type) >= 0)
                        extensions[element.GetAttribute("Name")] = type;
                }
            }
            return extensions;
        }
    }
}
