// FindInstanceGraphics: lists the graphics that include an instance or the objects it contains (embedded symbols, tag
// references in animations and scripts, and references written in graphic definitions), and the ViewApps that show them.
// It reads the galaxy database, so it does not log in to the galaxy.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using GRAccessTools.Common;

namespace GRAccessTools.Evaluate
{
    class FindInstanceGraphics
    {
        const string Usage =
            "Lists the graphics that include an instance or the objects it contains, and the ViewApps that show them:\r\n" +
            "Graphic Toolbox symbols, OMI layouts, symbols of other templates and instances, and ViewApps that embed\r\n" +
            "one of its symbols, reference one of its attributes, or have a reference to it (Name.Attribute) written in\r\n" +
            "their definition, e.g. in a script.\r\n" +
            "\r\n" +
            "Usage: FindInstanceGraphics.exe -i <instance>\r\n" +
            "\r\n" +
            "  -i <instance>   Instance tagname or full name, e.g. -i ECCP_CH1_FlowMeter_CHWS\r\n" +
            "\r\n" +
            "Both the checked-in and the checked-out versions of graphics are searched. Not found: edits not yet saved\r\n" +
            "in the IDE, tag references typed into InTouch windows, and names put together at runtime (for example\r\n" +
            "OMI asset navigation or scripts that build a reference from parts).\r\n" +
            "The galaxy database is read with your Windows login.\r\n" +
            "\r\n" +
            "Exit code 3 when graphics include the instance, 0 when none do.";

        static readonly string[] Options = { "i" };

        const int MaxReasons = 10;
        const int MaxListed = 5;

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string name = args.Require("i");
            string node, galaxyName;
            ToolRunner.ResolveGalaxy(args, out node, out galaxyName);

            Stopwatch stopwatch = Stopwatch.StartNew();
            using (SqlConnection connection = GalaxyDatabase.Open(node, galaxyName))
            {
                GalaxyGraphics graphics;
                GraphicSearch search;
                try
                {
                    Console.WriteLine("Reading the graphics of " + galaxyName + "...");
                    graphics = GalaxyGraphics.Load(connection);
                    search = new GraphicSearch(graphics, FindInstance(graphics, name, galaxyName));
                    search.FindEmbeddedSymbols();
                    search.FindTagReferences(connection);
                    search.FindCrossReferences(connection);
                    search.FindNamesInDefinitions(connection);
                }
                catch (SqlException ex)
                {
                    throw new GRAccessException("Could not read the graphics from the " + galaxyName + " database on " + node + ": " + ex.Message);
                }

                GalaxyObject instance = search.Targets[0];
                Console.WriteLine();
                PrintInstance(graphics, search);

                List<List<GraphicHit>> entries = Entries(search);
                Console.WriteLine();
                if (entries.Count == 0)
                    Console.WriteLine("No graphic includes " + instance.Tagname + ".");
                else
                    Console.WriteLine(search.Hits.Count + " graphic(s) include " + instance.Tagname + ":");
                foreach (List<GraphicHit> entry in entries)
                {
                    Console.WriteLine();
                    PrintEntry(graphics, search, entry);
                }

                Console.WriteLine();
                Console.WriteLine("Searched " + graphics.Embeds.Count + " embedded symbols and " + search.DefinitionsSearched
                    + " graphic definitions in " + (stopwatch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s.");
                return entries.Count > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // The instance by tagname or hierarchical name, without case
        static GalaxyObject FindInstance(GalaxyGraphics graphics, string name, string galaxyName)
        {
            GalaxyObject obj = graphics.FindAutomationObject(name);
            if (obj == null)
                throw new GRAccessException("Instance '" + name + "' not found in " + galaxyName + ".");
            if (obj.IsTemplate)
                throw new UsageException("'" + obj.Tagname + "' is a template. Give the name of an instance.");
            return obj;
        }

        static void PrintInstance(GalaxyGraphics graphics, GraphicSearch search)
        {
            GalaxyObject instance = search.Targets[0];
            string line = instance.Tagname;
            if (!string.Equals(instance.HierarchicalName, instance.Tagname, StringComparison.OrdinalIgnoreCase))
                line += " (" + instance.HierarchicalName + ")";
            line += ": instance of " + graphics.TagnameOf(instance.DerivedFrom);
            if (instance.Area != 0)
                line += " in area " + graphics.TagnameOf(instance.Area);
            if (instance.CheckedOutPackage != 0)
                line += ", checked out by " + instance.CheckedOutBy;
            Console.WriteLine(line);

            if (search.Targets.Count > 1)
            {
                List<string> contained = new List<string>();
                for (int i = 1; i < search.Targets.Count; i++)
                    contained.Add(search.Targets[i].HierarchicalName);
                Console.WriteLine("Also searched the " + contained.Count + " object(s) it contains: " + ListOf(contained, MaxListed));
            }
        }

        // One entry per graphic, except that copies of one inherited symbol in several objects share an entry, and so do
        // several symbols of the same template or instance
        static List<List<GraphicHit>> Entries(GraphicSearch search)
        {
            Dictionary<string, List<GraphicHit>> byDefinition = new Dictionary<string, List<GraphicHit>>();
            foreach (GraphicHit hit in search.Hits.Values)
                GroupOf(byDefinition, hit.DefinitionGraphicId).Add(hit);

            List<List<GraphicHit>> entries = new List<List<GraphicHit>>();
            Dictionary<string, List<GraphicHit>> byOwner = new Dictionary<string, List<GraphicHit>>();
            foreach (List<GraphicHit> group in byDefinition.Values)
            {
                GraphicHit hit = group[0];
                if (group.Count == 1 && IsOwnedSymbol(search, hit))
                    GroupOf(byOwner, hit.ObjectId.ToString()).Add(hit);
                else
                    entries.Add(group);
            }
            entries.AddRange(byOwner.Values);

            entries.Sort((a, b) => string.Compare(EntryName(search, a), EntryName(search, b), StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        static List<GraphicHit> GroupOf(Dictionary<string, List<GraphicHit>> groups, string key)
        {
            List<GraphicHit> group;
            if (!groups.TryGetValue(key, out group))
            {
                group = new List<GraphicHit>();
                groups.Add(key, group);
            }
            return group;
        }

        static bool IsOwnedSymbol(GraphicSearch search, GraphicHit hit)
        {
            return !hit.GraphicId.StartsWith("obj|", StringComparison.Ordinal)
                && search.OwnerOf(hit.GraphicId).Namespace == GalaxyObject.AutomationNamespace;
        }

        // Several symbols of one object, rather than copies of one symbol in several objects
        static bool IsSymbolsOfOneObject(List<GraphicHit> entry)
        {
            return entry.Count > 1 && entry.TrueForAll(hit => hit.ObjectId == entry[0].ObjectId);
        }

        static string EntryName(GraphicSearch search, List<GraphicHit> entry)
        {
            if (entry.Count == 1)
                return search.NameOf(entry[0].GraphicId);
            return IsSymbolsOfOneObject(entry) ? search.OwnerOf(entry[0].GraphicId).Tagname : search.NameOf(entry[0].DefinitionGraphicId);
        }

        static void PrintEntry(GalaxyGraphics graphics, GraphicSearch search, List<GraphicHit> entry)
        {
            SortedSet<string> reasons = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            SortedSet<string> namesInDefinition = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);  // of graphics found only by name
            List<string> graphicIds = new List<string>();
            bool inCheckedIn = false, inCheckedOut = false;
            foreach (GraphicHit hit in entry)
            {
                reasons.UnionWith(hit.Reasons);
                if (hit.Reasons.Count == 0)
                {
                    foreach (string name in hit.NamesInDefinition)
                        namesInDefinition.Add((entry.Count > 1 ? search.NameOf(hit.GraphicId) + " has" : "has") + " a reference to " + name);
                }
                graphicIds.Add(hit.GraphicId);
                inCheckedIn |= hit.InCheckedIn;
                inCheckedOut |= hit.InCheckedOut;
            }

            GalaxyObject owner = search.OwnerOf(entry[0].GraphicId);
            if (entry.Count == 1)
            {
                Console.WriteLine(search.NameOf(entry[0].GraphicId) + " (" + search.KindOf(entry[0].GraphicId) + VersionNote(owner, inCheckedIn, inCheckedOut) + ")");
            }
            else if (IsSymbolsOfOneObject(entry))
            {
                List<string> symbols = graphicIds.ConvertAll(id => search.NameOf(id).Substring(owner.Tagname.Length + 1));
                symbols.Sort(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine(owner.Tagname + " (" + entry.Count + " symbols of " + (owner.IsTemplate ? "template" : "instance")
                    + VersionNote(owner, inCheckedIn, inCheckedOut) + "): " + ListOf(symbols, MaxListed));
            }
            else
            {
                List<string> owners = entry.ConvertAll(hit => graphics.TagnameOf(hit.ObjectId));
                owners.Sort(StringComparer.OrdinalIgnoreCase);
                string definition = entry[0].DefinitionGraphicId;
                Console.WriteLine(search.NameOf(definition) + " (" + search.KindOf(definition) + "), in " + entry.Count
                    + " objects that inherit it: " + ListOf(owners, MaxListed));
            }

            int printed = 0;
            foreach (string reason in reasons)
            {
                if (printed++ == MaxReasons)
                {
                    Console.WriteLine("  ... and " + (reasons.Count - MaxReasons) + " more reference(s)");
                    break;
                }
                Console.WriteLine("  " + reason);
            }
            foreach (string name in namesInDefinition)
                Console.WriteLine("  " + name + ".<attribute> written in its definition (for example in a script or a custom property value)");

            // The ViewApps that show it; only when there are none, the top graphics it ends up in (e.g. an unused layout)
            List<ShownIn> shownIn = search.WhereShown(graphicIds);
            if (shownIn.Exists(shown => shown.IsViewApp))
                shownIn.RemoveAll(shown => !shown.IsViewApp);
            shownIn.Sort((a, b) => string.Compare(search.NameOf(a.GraphicId), search.NameOf(b.GraphicId), StringComparison.OrdinalIgnoreCase));
            foreach (ShownIn shown in shownIn)
            {
                List<string> through = shown.Through.ConvertAll(id => search.NameOf(id));
                if (entry.Count > 1 && shown.From != shown.GraphicId && !shown.From.StartsWith("obj|", StringComparison.Ordinal))
                    through.Insert(0, search.NameOf(shown.From));
                string line = "  shown in " + search.NameOf(shown.GraphicId) + " (" + search.KindOf(shown.GraphicId) + ")";
                if (through.Count > 0)
                    line += " through " + string.Join(" > ", through.ToArray());
                if (!shown.IsViewApp)
                    line += ", which is not embedded in a ViewApp or another graphic (it can still be opened by name, e.g. from navigation)";
                Console.WriteLine(line);
            }
            if (shownIn.Count == 0 && !entry[0].GraphicId.StartsWith("obj|", StringComparison.Ordinal))
                Console.WriteLine("  not embedded in any other graphic, layout or ViewApp (it can still be opened by name, e.g. from navigation or a script)");
        }

        // Whether the graphic is checked out, and whether the reference is only in one of its versions
        static string VersionNote(GalaxyObject owner, bool inCheckedIn, bool inCheckedOut)
        {
            if (owner.CheckedOutPackage == 0)
                return "";
            if (inCheckedIn && inCheckedOut)
                return ", checked out by " + owner.CheckedOutBy;
            if (inCheckedOut)
                return ", only in the version checked out by " + owner.CheckedOutBy + ", not checked in yet";
            return ", only in the checked-in version: the version checked out by " + owner.CheckedOutBy + " no longer has it";
        }

        static string ListOf(List<string> names, int max)
        {
            if (names.Count <= max)
                return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, max).ToArray()) + " and " + (names.Count - max) + " more";
        }
    }
}
