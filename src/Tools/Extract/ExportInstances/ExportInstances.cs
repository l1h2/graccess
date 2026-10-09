// ExportInstances: exports an area, all of its sub-areas and every instance in them to one package file, the same
// selection the IDE exports when every object under the area is selected by hand. Read-only.

using System;
using System.Collections.Generic;
using System.IO;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.Extract
{
    class ExportInstances
    {
        const string Usage =
            "Exports an area with all of its sub-areas and every instance in them, contained objects included, to one\r\n" +
            "package file (<area>.aaPKG), as the IDE does when all of those objects are selected and exported. Also\r\n" +
            "writes objects.csv, the list of what is in the package. Templates and Graphic Toolbox symbols are not\r\n" +
            "included: export those from their toolsets in the IDE.\r\n" +
            "\r\n" +
            "Usage: ExportInstances.exe -a <area> [-csv]\r\n" +
            "\r\n" +
            "  -a <area>   Area tagname\r\n" +
            "  -csv        Also write the same objects as a Galaxy Dump CSV file (<area>.csv)";

        static readonly string[] Options = { "a", "csv" };

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string areaName = args.Require("a");
            bool dump = args.Has("csv");

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                IGalaxy galaxy = session.Galaxy;
                IgObject area = GalaxyInstances.TryFind(galaxy, areaName);
                if (area == null)
                    throw new GRAccessException("Area '" + areaName + "' not found in " + galaxy.Name + ".");
                if (area.category != ECATEGORY.idxCategoryArea)
                    throw new GRAccessException("'" + areaName + "' is not an area.");

                List<IgObject> objects = new List<IgObject> { area };
                objects.AddRange(GalaxyInstances.FindInArea(galaxy, area.Tagname));
                int subAreas = objects.FindAll(o => o.category == ECATEGORY.idxCategoryArea).Count - 1;
                Console.WriteLine("Found " + objects.Count + " object(s): area " + area.Tagname + ", " + subAreas + " sub-area(s) and "
                    + (objects.Count - subAreas - 1) + " other instance(s).");

                string runFolder = OutputPaths.CreateRunFolder(galaxy.Name);
                List<string> checkedOut = WriteList(Path.Combine(runFolder, "objects.csv"), objects);

                IgObjects selection = galaxy.CreategObjectCollection();
                GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Create an object collection");
                foreach (IgObject obj in objects)
                    selection.Add(obj);
                if (selection.count != objects.Count)
                    throw new GRAccessException("The export collection holds " + selection.count + " object(s) instead of " + objects.Count + ".");

                string package = Path.Combine(runFolder, area.Tagname + ".aaPKG");
                Export(selection, EExportType.exportAsPDF, package);
                string csvFile = null;
                if (dump)
                {
                    csvFile = Path.Combine(runFolder, area.Tagname + ".csv");
                    Export(selection, EExportType.exportAsCSV, csvFile);
                }

                Console.WriteLine("Exported " + objects.Count + " object(s) to:");
                Console.WriteLine(package + " (" + new FileInfo(package).Length.ToString("N0") + " bytes)");
                if (csvFile != null)
                    Console.WriteLine(csvFile);
                Console.WriteLine("List of the objects: " + Path.Combine(runFolder, "objects.csv"));
                if (checkedOut.Count > 0)
                {
                    Console.WriteLine(checkedOut.Count + " object(s) are checked out, so the package may not hold their latest changes: "
                        + string.Join(", ", checkedOut.ToArray()));
                    return ExitCodes.CompletedWithFindings;
                }
                return ExitCodes.Success;
            }
        }

        // ExportObjects reports through the collection's CommandResults, one result per problem
        static void Export(IgObjects selection, EExportType type, string file)
        {
            string step = "Export to " + Path.GetFileName(file);
            Console.WriteLine(step + "...");
            selection.ExportObjects(type, file);
            ICommandResults results = selection.CommandResults;
            if (results != null && !results.CompletelySuccessful)
            {
                List<string> problems = new List<string>();
                foreach (ICommandResult result in results)
                {
                    if (result.Successful)
                        continue;
                    string detail = (result.Text ?? "").Trim();
                    string customMessage = (result.CustomMessage ?? "").Trim();
                    problems.Add(customMessage.Length > 0 ? detail + " - " + customMessage : detail);
                }
                throw new GRAccessException(step + " failed: " + string.Join("; ", problems.ToArray()));
            }
            if (!File.Exists(file))
                throw new GRAccessException(step + " reported success, but the file was not written.");
        }

        // objects.csv: one row per exported object. Returns the checked-out ones.
        static List<string> WriteList(string file, List<IgObject> objects)
        {
            List<string> checkedOut = new List<string>();
            using (CsvWriter csv = new CsvWriter(file))
            {
                csv.WriteRow("object", "tagname", "template", "area", "container", "category", "checkedOutBy");
                foreach (IgObject obj in objects)
                {
                    string by = "";
                    if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    {
                        by = string.IsNullOrEmpty(obj.checkedOutBy) ? "(checked out)" : obj.checkedOutBy;
                        checkedOut.Add(obj.HierarchicalName);
                    }
                    csv.WriteRow(obj.HierarchicalName, obj.Tagname, obj.DerivedFrom, obj.Area, obj.Container,
                        obj.category.ToString().Replace("idxCategory", ""), by);
                }
            }
            return checkedOut;
        }
    }
}
