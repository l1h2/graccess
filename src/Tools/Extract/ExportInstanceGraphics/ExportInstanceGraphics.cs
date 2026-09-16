// ExportInstanceGraphics: writes, for one instance or for every instance in an area and its sub-areas, the saved graphics
// that embed one of the instance's own symbols (its linked graphics) to a CSV file. It reads the galaxy database, so it
// does not log in to the galaxy.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    class ExportInstanceGraphics
    {
        const string Usage =
            "Writes the graphics each instance is placed on to instance-graphics.csv: instance, and graphics, the saved\r\n" +
            "graphics that embed one of the instance's own symbols, e.g. L2_ECCP; L3_ECCP_Chillers.\r\n" +
            "\r\n" +
            "Usage: ExportInstanceGraphics.exe -i <instance>\r\n" +
            "       ExportInstanceGraphics.exe -a <area>\r\n" +
            "\r\n" +
            "  -i <instance>   Instance tagname or full name, e.g. -i ECCP_CH1_FlowMeter_CHWS\r\n" +
            "  -a <area>       Area tagname; every instance in it and in all of its sub-areas gets its own row\r\n" +
            "\r\n" +
            "graphics is empty when no graphic embeds one of the instance's symbols, and says \"" + NoLinkedGraphics + "\"\r\n" +
            "when the instance has no symbols of its own (e.g. a set point control).\r\n" +
            "Only symbols embedded in saved graphics count, in their checked-in or checked-out version; tag references in\r\n" +
            "animations and scripts do not. The galaxy database is read with your Windows login.";

        static readonly string[] Options = { "i", "a" };

        const string NoLinkedGraphics = "No linked graphics";

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

            GalaxyGraphics graphics;
            using (SqlConnection connection = GalaxyDatabase.Open(node, galaxyName))
            {
                try
                {
                    graphics = GalaxyGraphics.Load(connection);
                }
                catch (SqlException ex)
                {
                    throw new GRAccessException("Could not read the graphics from the " + galaxyName + " database on " + node + ": " + ex.Message);
                }
            }

            List<GalaxyObject> instances = instanceName != null
                ? new List<GalaxyObject> { FindInstance(graphics, instanceName, galaxyName) }
                : InstancesInArea(graphics, areaName, galaxyName);

            Dictionary<int, SortedSet<string>> placements = Placements(graphics);
            HashSet<int> withSymbols = ObjectsWithSymbols(graphics);

            int placed = 0, notPlaced = 0, noSymbols = 0;
            string file = Path.Combine(OutputPaths.CreateRunFolder(galaxyName), "instance-graphics.csv");
            using (CsvWriter csv = new CsvWriter(file))
            {
                csv.WriteRow("instance", "graphics");
                foreach (GalaxyObject instance in instances)
                {
                    string list;
                    SortedSet<string> names;
                    if (!withSymbols.Contains(instance.Id))
                    {
                        list = NoLinkedGraphics;
                        noSymbols++;
                    }
                    else if (placements.TryGetValue(instance.Id, out names))
                    {
                        list = string.Join("; ", new List<string>(names).ToArray());
                        placed++;
                    }
                    else
                    {
                        list = "";
                        notPlaced++;
                    }
                    csv.WriteRow(instance.HierarchicalName, list);
                }
            }

            Console.WriteLine("Exported the graphics of " + instances.Count + " instance(s) to:");
            Console.WriteLine(file);
            Console.WriteLine(placed + " are on graphics, " + notPlaced + " are on none, and " + noSymbols + " have no linked graphics.");
            return ExitCodes.Success;
        }

        static GalaxyObject FindInstance(GalaxyGraphics graphics, string name, string galaxyName)
        {
            GalaxyObject obj = graphics.FindAutomationObject(name);
            if (obj == null)
                throw new GRAccessException("Instance '" + name + "' not found in " + galaxyName + ".");
            if (obj.IsTemplate)
                throw new UsageException("'" + obj.Tagname + "' is a template. Give the name of an instance.");
            return obj;
        }

        // Every instance in the area and in all of its sub-areas, sorted by full name. Objects contained in another object
        // belong to the same area (or to the container itself when the container is an area).
        static List<GalaxyObject> InstancesInArea(GalaxyGraphics graphics, string areaName, string galaxyName)
        {
            GalaxyObject area = graphics.FindAutomationObject(areaName);
            if (area == null || area.IsTemplate)
                throw new GRAccessException("Area '" + areaName + "' not found in " + galaxyName + ".");
            if (area.Category != GalaxyObject.AreaCategory)
                throw new GRAccessException("'" + areaName + "' is not an area.");

            Dictionary<int, List<GalaxyObject>> byArea = new Dictionary<int, List<GalaxyObject>>();
            foreach (GalaxyObject obj in graphics.Objects.Values)
            {
                if (obj.Namespace != GalaxyObject.AutomationNamespace || obj.IsTemplate || obj.Area == 0)
                    continue;
                List<GalaxyObject> members;
                if (!byArea.TryGetValue(obj.Area, out members))
                {
                    members = new List<GalaxyObject>();
                    byArea.Add(obj.Area, members);
                }
                members.Add(obj);
            }

            SortedDictionary<string, GalaxyObject> found = new SortedDictionary<string, GalaxyObject>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> visitedAreas = new HashSet<int> { area.Id };
            Queue<int> areas = new Queue<int>();
            areas.Enqueue(area.Id);
            while (areas.Count > 0)
            {
                List<GalaxyObject> members;
                if (!byArea.TryGetValue(areas.Dequeue(), out members))
                    continue;
                foreach (GalaxyObject member in members)
                {
                    found[member.HierarchicalName] = member;
                    if (member.Category == GalaxyObject.AreaCategory && visitedAreas.Add(member.Id))
                        areas.Enqueue(member.Id);
                }
            }
            return new List<GalaxyObject>(found.Values);
        }

        // Object id -> names of the graphics that embed one of its symbols in their current (checked-in or checked-out)
        // version. The object's own symbols do not count.
        static Dictionary<int, SortedSet<string>> Placements(GalaxyGraphics graphics)
        {
            Dictionary<int, SortedSet<string>> placements = new Dictionary<int, SortedSet<string>>();
            foreach (EmbeddedSymbol embed in graphics.Embeds)
            {
                if (!graphics.IsCurrent(embed.ReferrerObjectId, embed.ReferrerPackageId))
                    continue;
                foreach (string boundKey in new[] { embed.CheckedInBoundKey, embed.CheckedOutBoundKey })
                {
                    GraphicVersion symbol;
                    if (boundKey == null || !graphics.Versions.TryGetValue(boundKey, out symbol) || symbol.ObjectId == embed.ReferrerObjectId)
                        continue;
                    SortedSet<string> names;
                    if (!placements.TryGetValue(symbol.ObjectId, out names))
                    {
                        names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        placements.Add(symbol.ObjectId, names);
                    }
                    names.Add(GraphicName(graphics, embed));
                }
            }
            return placements;
        }

        // A Graphic Toolbox or OMI element by its name, a symbol of another object as Object.Symbol, a ViewApp by its name
        static string GraphicName(GalaxyGraphics graphics, EmbeddedSymbol embed)
        {
            GraphicVersion version;
            string owner = graphics.TagnameOf(embed.ReferrerObjectId);
            if (!graphics.Versions.TryGetValue(embed.ReferrerKey, out version) || version.PrimitiveName.Length == 0)
                return owner;
            return owner + "." + version.PrimitiveName;
        }

        // Objects that have symbols of their own (defined in them or inherited) in their current version
        static HashSet<int> ObjectsWithSymbols(GalaxyGraphics graphics)
        {
            HashSet<int> objects = new HashSet<int>();
            foreach (GraphicVersion version in graphics.Versions.Values)
            {
                if (graphics.IsCurrent(version.ObjectId, version.PackageId))
                    objects.Add(version.ObjectId);
            }
            return objects;
        }
    }
}
