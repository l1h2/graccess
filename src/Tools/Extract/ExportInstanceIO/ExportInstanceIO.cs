// ExportInstanceIO: writes the I/O references of one instance, or of every instance in an area and its sub-areas, to a
// CSV file, with auto-assigned I/O resolved to the full path the system uses. With -u only the unmapped references are
// written: those whose item is not in the ItemList of their device's scan group.

using System;
using System.Collections.Generic;
using System.IO;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    class ExportInstanceIO
    {
        const string Usage =
            "Writes the I/O references of an instance, or of every instance in an area and its sub-areas, to\r\n" +
            "instance-io.csv: instance, template, attribute, and the full I/O path the system uses.\r\n" +
            "\r\n" +
            "Usage: ExportInstanceIO.exe -i <instance> [-u]\r\n" +
            "       ExportInstanceIO.exe -a <area> [-u]\r\n" +
            "\r\n" +
            "  -i <instance>   Instance tagname or full name, e.g. -i LSC3_PumpVFDControl_CHWR.SetPointControl\r\n" +
            "  -a <area>       Area tagname; the instances in all of its sub-areas are included\r\n" +
            "  -u              Only unmapped references, written to instance-io-unmapped.csv: those whose item is not\r\n" +
            "                  in the ItemList of their device's scan group, or whose object has no I/O device assigned\r\n" +
            "\r\n" +
            "Auto-assigned I/O (---Auto---) is resolved from the galaxy's I/O device assignments, which are read from\r\n" +
            "the galaxy database with your Windows login.";

        static readonly string[] Options = { "i", "a", "u" };

        const string AutoReference = "---Auto---";

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string instanceName = args.Get("i", null);
            string areaName = args.Get("a", null);
            bool unmappedOnly = args.Has("u");
            if ((instanceName == null) == (areaName == null))
                throw new UsageException("Give either -i <instance> or -a <area>.");

            string node, galaxyName;
            ToolRunner.ResolveGalaxy(args, out node, out galaxyName);

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                IGalaxy galaxy = session.Galaxy;
                List<IgObject> instances = instanceName != null
                    ? new List<IgObject> { FindInstance(galaxy, instanceName) }
                    : FindInstancesInArea(galaxy, areaName);

                IoAssignments assignments = IoAssignments.Load(node, galaxy.Name);
                Console.WriteLine("Reading the I/O of " + instances.Count + " instance(s)...");

                Dictionary<string, string> templateNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string[]> rows = new List<string[]>();
                int unassigned = 0;
                foreach (IgObject instance in instances)
                    rows.AddRange(ReadIo(session, instance, assignments, templateNames, ref unassigned));

                int allRows = rows.Count;
                if (unmappedOnly)
                    rows = UnmappedOnly(session, rows);

                string file = Path.Combine(OutputPaths.CreateRunFolder(galaxy.Name), unmappedOnly ? "instance-io-unmapped.csv" : "instance-io.csv");
                using (CsvWriter csv = new CsvWriter(file))
                {
                    csv.WriteRow("instance", "template", "attribute", "path");
                    foreach (string[] row in rows)
                        csv.WriteRow(row);
                }

                if (unmappedOnly)
                    Console.WriteLine("Exported " + rows.Count + " unmapped of " + allRows + " I/O reference(s) of " + instances.Count + " instance(s) to:");
                else
                    Console.WriteLine("Exported " + rows.Count + " I/O reference(s) of " + instances.Count + " instance(s) to:");
                Console.WriteLine(file);
                if (unassigned > 0)
                    Console.WriteLine("Note: " + unassigned + " reference(s) are " + AutoReference + " but their object has no I/O device assigned, so their path starts with <IODevice>.");
            }
            return ExitCodes.Success;
        }

        // Keeps the references whose item is missing from their scan group's ItemList, and those whose object has no I/O
        // device assigned. References that do not point to a device scan group with an ItemList cannot be checked.
        static List<string[]> UnmappedOnly(GalaxySession session, List<string[]> rows)
        {
            ItemLists itemLists = new ItemLists(session);
            List<string[]> unmapped = new List<string[]>();
            int listed = 0, notChecked = 0;
            foreach (string[] row in rows)
            {
                ItemStatus status = itemLists.Check(row[3]);
                if (status == ItemStatus.Listed)
                    listed++;
                else if (status == ItemStatus.NotChecked)
                    notChecked++;
                else
                    unmapped.Add(row);
            }

            Console.WriteLine("Checked " + rows.Count + " I/O reference(s) against their scan group's ItemList: " + listed + " listed, " + unmapped.Count + " unmapped.");
            if (notChecked > 0)
                Console.WriteLine("Note: " + notChecked + " reference(s) do not point to a device scan group with an ItemList (for example Me.<attribute>), so they were not checked.");
            return unmapped;
        }

        static IgObject FindInstance(IGalaxy galaxy, string name)
        {
            IgObject instance = TryFindInstance(galaxy, name);
            if (instance == null)
                throw new GRAccessException("Instance '" + name + "' not found in " + galaxy.Name + ".");
            return instance;
        }

        // Accepts the tagname or the full hierarchical name (Container.ContainedName). Returns null when not found.
        static IgObject TryFindInstance(IGalaxy galaxy, string name)
        {
            string[] names = { name };
            IgObjects byTagname = galaxy.QueryObjectsByName(EgObjectIsTemplateOrInstance.gObjectIsInstance, ref names);
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find instance " + name);
            if (byTagname.count > 0)
                return byTagname[1];

            // hierarchicalNameLike uses SQL LIKE, where _ matches any character, so only the exact name counts
            IgObjects byHierarchicalName = galaxy.QueryObjects(EgObjectIsTemplateOrInstance.gObjectIsInstance, EConditionType.hierarchicalNameLike, name, EMatch.MatchCondition);
            GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find instance " + name);
            foreach (IgObject candidate in byHierarchicalName)
            {
                if (string.Equals(candidate.HierarchicalName, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        // Every instance in the area and in all of its sub-areas, sorted by full name. belongsToArea returns the objects
        // directly in an area, contained objects included; the sub-areas are the area objects among them.
        static List<IgObject> FindInstancesInArea(IGalaxy galaxy, string areaName)
        {
            IgObject area = TryFindInstance(galaxy, areaName);
            if (area == null)
                throw new GRAccessException("Area '" + areaName + "' not found in " + galaxy.Name + ".");
            if (area.category != ECATEGORY.idxCategoryArea)
                throw new GRAccessException("'" + areaName + "' is not an area.");

            SortedDictionary<string, IgObject> found = new SortedDictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visitedAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { area.Tagname };
            Queue<string> areas = new Queue<string>();
            areas.Enqueue(area.Tagname);
            while (areas.Count > 0)
            {
                string current = areas.Dequeue();
                IgObjects members = galaxy.QueryObjects(EgObjectIsTemplateOrInstance.gObjectIsInstance, EConditionType.belongsToArea, current, EMatch.MatchCondition);
                GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find the objects in area " + current);
                foreach (IgObject member in members)
                {
                    if (!string.Equals(member.Area, current, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string hierarchicalName = member.HierarchicalName;
                    if (!found.ContainsKey(hierarchicalName))
                        found.Add(hierarchicalName, member);
                    if (member.category == ECATEGORY.idxCategoryArea && visitedAreas.Add(member.Tagname))
                        areas.Enqueue(member.Tagname);
                }
            }
            return new List<IgObject>(found.Values);
        }

        // One row per I/O reference: inputs read X.InputSource and outputs write X.OutputDest. An input/output attribute
        // gets a second row only when it is set to write to a different reference than it reads from.
        static List<string[]> ReadIo(GalaxySession session, IgObject instance, IoAssignments assignments, Dictionary<string, string> templateNames, ref int unassigned)
        {
            List<string[]> rows = new List<string[]>();
            SortedDictionary<string, string> ioExtensions = ObjectXml.IoExtensions(instance, true);
            if (ioExtensions.Count == 0)
                return rows;

            string tagname = instance.Tagname;
            string hierarchicalName = instance.HierarchicalName;
            string template = TemplateName(session, instance.DerivedFrom, templateNames);
            foreach (KeyValuePair<string, string> io in ioExtensions)
            {
                string attribute = io.Key;
                bool reads = io.Value != "outputextension";
                bool writes = io.Value == "outputextension"
                    || (io.Value == "inputoutputextension" && ValueOf(instance, attribute + ".DiffOutputDest") == "true");

                string readPath = null;
                if (reads)
                {
                    readPath = ResolvePath(instance, tagname, hierarchicalName, attribute, "InputSource", 'I', assignments, ref unassigned);
                    rows.Add(new[] { hierarchicalName, template, attribute, readPath });
                }
                if (writes)
                {
                    string writePath = ResolvePath(instance, tagname, hierarchicalName, attribute, "OutputDest", 'O', assignments, ref unassigned);
                    if (writePath != readPath)
                        rows.Add(new[] { hierarchicalName, template, attribute, writePath });
                }
            }
            return rows;
        }

        // The reference the system uses: the value itself when it was set explicitly, or for ---Auto--- the assigned
        // I/O device and scan group followed by the item name from the naming rule
        static string ResolvePath(IgObject instance, string tagname, string hierarchicalName, string attribute, string setting, char ioType, IoAssignments assignments, ref int unassigned)
        {
            string reference = ValueOf(instance, attribute + "." + setting);
            if (!string.Equals(reference, AutoReference, StringComparison.OrdinalIgnoreCase))
                return reference;

            string path;
            if (!assignments.TryResolve(tagname, hierarchicalName, attribute, ioType, out path))
                unassigned++;
            return path;
        }

        // The template's full name, e.g. $PumpVFDControl.SetPointControl for a contained template
        static string TemplateName(GalaxySession session, string derivedFrom, Dictionary<string, string> cache)
        {
            string name;
            if (!cache.TryGetValue(derivedFrom, out name))
            {
                IgObject template = session.FindObject(derivedFrom);
                name = template == null ? derivedFrom : template.HierarchicalName;
                cache.Add(derivedFrom, name);
            }
            return name;
        }

        static string ValueOf(IgObject obj, string attributeName)
        {
            IAttribute attribute = obj.Attributes[attributeName];
            return attribute == null ? "" : attribute.value.GetString();
        }
    }
}
