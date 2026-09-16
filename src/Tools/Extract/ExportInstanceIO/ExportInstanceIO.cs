// ExportInstanceIO: writes the I/O references of one instance, or of every instance in an area and its sub-areas, to a
// CSV file, with auto-assigned I/O resolved to the full path the system uses and the item reference each path sends to
// the server (the address in the topic). An empty reference marks I/O that reaches no address, e.g. an unmapped item.

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
            "instance-io.csv: instance, template, attribute, the full I/O path the system uses (path), and the\r\n" +
            "item reference that path sends to the server (reference).\r\n" +
            "\r\n" +
            "Usage: ExportInstanceIO.exe -i <instance>\r\n" +
            "       ExportInstanceIO.exe -a <area>\r\n" +
            "\r\n" +
            "  -i <instance>   Instance tagname or full name, e.g. -i LSC3_PumpVFDControl_CHWR.SetPointControl\r\n" +
            "  -a <area>       Area tagname; the instances in all of its sub-areas are included\r\n" +
            "\r\n" +
            "The reference is the item reference the item maps to in its scan group's device items. When the scan\r\n" +
            "group has no device items at all, the item is addressed directly and is itself the reference.\r\n" +
            "The reference is empty when the item is not one of its scan group's device items (unmapped), when the\r\n" +
            "path has no item or names a scan group the device does not have, when the object has no I/O device\r\n" +
            "assigned, when no I/O reference is set (---), or when the path does not point to a device scan group\r\n" +
            "(e.g. Me.PV). The console lists how many rows have an empty reference for each of these reasons.\r\n" +
            "Auto-assigned I/O (---Auto---) is resolved from the galaxy's I/O device assignments, which are read from\r\n" +
            "the galaxy database with your Windows login.";

        static readonly string[] Options = { "i", "a" };

        const string AutoReference = "---Auto---";

        // Instances read between releases of the GRAccess objects that are no longer used (see ReleaseGRAccessObjects)
        const int ReleaseInterval = 25;

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string instanceName = args.Get("i", null);
            string areaName = args.Get("a", null);
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
                for (int i = 0; i < instances.Count; i++)
                {
                    rows.AddRange(ReadIo(session, instances[i], assignments, templateNames));
                    instances[i] = null;
                    if ((i + 1) % ReleaseInterval == 0)
                        ReleaseGRAccessObjects();
                }
                ReleaseGRAccessObjects();

                DeviceItems deviceItems = new DeviceItems(session);
                int found = 0, direct = 0, notFound = 0, noItem = 0, noScanGroup = 0, unassigned = 0, notSet = 0, notDevice = 0;
                string file = Path.Combine(OutputPaths.CreateRunFolder(galaxy.Name), "instance-io.csv");
                using (CsvWriter csv = new CsvWriter(file))
                {
                    csv.WriteRow("instance", "template", "attribute", "path", "reference");
                    foreach (string[] row in rows)
                    {
                        string reference;
                        switch (deviceItems.Find(row[3], out reference))
                        {
                            case ItemStatus.Found: found++; break;
                            case ItemStatus.Direct: direct++; break;
                            case ItemStatus.NotFound: notFound++; break;
                            case ItemStatus.NoItem: noItem++; break;
                            case ItemStatus.NoScanGroup: noScanGroup++; break;
                            case ItemStatus.Unassigned: unassigned++; break;
                            case ItemStatus.NotSet: notSet++; break;
                            default: notDevice++; break;
                        }
                        csv.WriteRow(row[0], row[1], row[2], row[3], reference ?? "");
                    }
                }

                Console.WriteLine("Exported " + rows.Count + " I/O reference(s) of " + instances.Count + " instance(s) to:");
                Console.WriteLine(file);
                if (rows.Count == 0)
                    return ExitCodes.Success;

                Console.WriteLine((found + direct) + " have an item reference" + (found + direct == 0 ? "." : ":"));
                if (found > 0)
                    Console.WriteLine("  " + found + " mapped by their scan group's device items");
                if (direct > 0)
                    Console.WriteLine("  " + direct + " addressed directly: their scan group has no device items, so the item is the reference");

                int empty = notFound + noItem + noScanGroup + unassigned + notSet + notDevice;
                Console.WriteLine(empty + " have an empty reference" + (empty == 0 ? "." : ":"));
                if (notFound > 0)
                    Console.WriteLine("  " + notFound + " unmapped: the item is not one of its scan group's device items");
                if (noItem > 0)
                    Console.WriteLine("  " + noItem + " whose path ends at the scan group, without an item");
                if (noScanGroup > 0)
                    Console.WriteLine("  " + noScanGroup + " whose device has no scan group by that name");
                if (unassigned > 0)
                    Console.WriteLine("  " + unassigned + " " + AutoReference + " whose object has no I/O device assigned, so their path starts with <IODevice>");
                if (notSet > 0)
                    Console.WriteLine("  " + notSet + " with no I/O reference set (---)");
                if (notDevice > 0)
                    Console.WriteLine("  " + notDevice + " that do not point to a device scan group, e.g. Me.PV or an attribute of the device itself");
            }
            return ExitCodes.Success;
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
        static List<string[]> ReadIo(GalaxySession session, IgObject instance, IoAssignments assignments, Dictionary<string, string> templateNames)
        {
            List<string[]> rows = new List<string[]>();
            SortedDictionary<string, string> ioExtensions = ObjectXml.IoExtensions(instance, true);
            if (ioExtensions.Count == 0)
                return rows;

            // Each read of instance.Attributes builds the whole attribute collection in GRAccessApp.exe, so read it once for
            // all the I/O settings below
            IAttributes attributes = instance.Attributes;
            string tagname = instance.Tagname;
            string hierarchicalName = instance.HierarchicalName;
            string template = TemplateName(session, instance.DerivedFrom, templateNames);
            foreach (KeyValuePair<string, string> io in ioExtensions)
            {
                string attribute = io.Key;
                bool reads = io.Value != "outputextension";
                bool writes = io.Value == "outputextension"
                    || (io.Value == "inputoutputextension" && ValueOf(attributes, attribute + ".DiffOutputDest") == "true");

                string readPath = null;
                if (reads)
                {
                    readPath = ResolvePath(attributes, tagname, hierarchicalName, attribute, "InputSource", 'I', assignments);
                    rows.Add(new[] { hierarchicalName, template, attribute, readPath });
                }
                if (writes)
                {
                    string writePath = ResolvePath(attributes, tagname, hierarchicalName, attribute, "OutputDest", 'O', assignments);
                    if (writePath != readPath)
                        rows.Add(new[] { hierarchicalName, template, attribute, writePath });
                }
            }
            return rows;
        }

        // The reference the system uses: the value itself when it was set explicitly, or for ---Auto--- the assigned
        // I/O device and scan group followed by the item name from the naming rule (<IODevice> when no device is assigned)
        static string ResolvePath(IAttributes attributes, string tagname, string hierarchicalName, string attribute, string setting, char ioType, IoAssignments assignments)
        {
            string reference = ValueOf(attributes, attribute + "." + setting);
            if (!string.Equals(reference, AutoReference, StringComparison.OrdinalIgnoreCase))
                return reference;

            return assignments.Resolve(tagname, hierarchicalName, attribute, ioType);
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

        static string ValueOf(IAttributes attributes, string attributeName)
        {
            IAttribute attribute = attributes[attributeName];
            return attribute == null ? "" : attribute.value.GetString();
        }

        // GRAccess objects stay alive in GRAccessApp.exe, a 32-bit process, until .NET releases their wrappers, which only
        // happens when this process collects garbage. Reading a large area without releasing them made GRAccessApp.exe
        // run out of memory and stop (1118 instances of the MU area on EMGALAXY).
        static void ReleaseGRAccessObjects()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
