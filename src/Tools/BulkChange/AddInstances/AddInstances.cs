// AddInstances: creates instances (with their contained objects) from templates listed in a CSV file, assigns them to an
// area, and gets every object onto the requested I/O scan group: from the area when it is on that scan group, otherwise
// with the IDE's I/O assignment call. What still is not on the scan group is listed for assignment in the IDE.
// Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at the first failure.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    class AddInstances
    {
        const string Usage =
            "Creates instances from templates listed in a CSV file, with their contained objects, assigns them to an area,\r\n" +
            "and assigns every object with I/O to the requested scan group. Without -Apply this is a dry run that only\r\n" +
            "reports what would be done.\r\n" +
            "\r\n" +
            "Usage: AddInstances.exe -f <file.csv> [-Apply]\r\n" +
            "\r\n" +
            "  -f <file.csv>   Input file with the columns template, name, area, io\r\n" +
            "  -Apply          Create the instances; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  template   Template to create the instance from; the leading $ is optional\r\n" +
            "  name       Tagname of the new instance (contained objects get generated names)\r\n" +
            "  area       Area to assign the instance to\r\n" +
            "  io         I/O device and scan group, e.g. BACLite_DDESuiteLink.NAE6_Normal\r\n" +
            "\r\n" +
            "I/O: after the area is assigned, every object is checked in the galaxy database. Objects that did not take the\r\n" +
            "scan group from the area (new objects with I/O usually start on Simulator.Fast) are assigned with the call the\r\n" +
            "IDE uses, which GRAccess does not document, and checked again. Objects still not on the scan group are listed\r\n" +
            "so they can be assigned in the IDE. Nothing is deployed.\r\n" +
            "Exit code 3 when some objects need their I/O assigned in the IDE.";

        static readonly string[] Options = { "f", "Apply" };

        const string FromArea = "area";
        const string FromAssignCall = "assign call";

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string inputPath = Path.GetFullPath(args.Require("f"));
            bool apply = args.Has("Apply");
            if (!File.Exists(inputPath))
                throw new UsageException("Input file not found: " + inputPath);

            List<string> problems = new List<string>();
            List<InstanceRow> rows = InstanceRow.ReadFile(inputPath, problems);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("The input file has problems, so nothing was changed:");
                foreach (string problem in problems)
                    Console.Error.WriteLine("  " + problem);
                return ExitCodes.Error;
            }

            string node, galaxyName;
            ToolRunner.ResolveGalaxy(args, out node, out galaxyName);

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            using (SqlConnection connection = GalaxyDatabase.Open(node, session.Galaxy.Name))
            using (IoAssigner assigner = new IoAssigner(session.Galaxy))
            {
                galaxyName = session.Galaxy.Name;
                GalaxyIo io = new GalaxyIo(connection);
                string runFolder = OutputPaths.CreateRunFolder(galaxyName, apply ? "_APPLY" : "_DRYRUN");
                File.Copy(inputPath, Path.Combine(runFolder, Path.GetFileName(inputPath)));

                Dictionary<string, IgObject> objects = new Dictionary<string, IgObject>(StringComparer.OrdinalIgnoreCase);
                Plan(session, io, rows, objects);
                PrintPlan(galaxyName, rows);

                int errors = rows.FindAll(row => row.Action == InstanceRow.Error).Count;
                if (!apply || errors > 0)
                {
                    string planFile = Path.Combine(runFolder, "plan.csv");
                    WritePlan(planFile, rows);
                    Console.WriteLine();
                    if (errors > 0)
                        Console.Error.WriteLine(errors + " row(s) cannot be done, so nothing was changed. Fix them and run again.");
                    else
                        Console.WriteLine("DRY RUN - nothing was changed. Run again with -Apply to create these instances.");
                    Console.WriteLine("Plan: " + planFile);
                    return errors > 0 ? ExitCodes.Error : ExitCodes.Success;
                }

                if (!ConfirmGalaxy(galaxyName))
                {
                    Console.Error.WriteLine("The galaxy name did not match, so nothing was changed.");
                    return ExitCodes.Error;
                }

                string failedRow = null;
                foreach (InstanceRow row in rows)
                {
                    if (failedRow != null)
                    {
                        row.Result = "Not applied";
                        row.ResultDetail = "stopped after the failure on " + failedRow;
                        continue;
                    }
                    if (!ApplyRow(row, objects, io, assigner))
                        failedRow = row.Name;
                }

                WriteResults(Path.Combine(runFolder, "result.csv"), rows);
                WriteObjects(Path.Combine(runFolder, "objects.csv"), rows);
                return Summarize(rows, runFolder, failedRow != null);
            }
        }

        // Decides for every row whether it can be created. Changes nothing. Templates, areas and devices found are kept in
        // objects for ApplyRow.
        static void Plan(GalaxySession session, GalaxyIo io, List<InstanceRow> rows, Dictionary<string, IgObject> objects)
        {
            Dictionary<string, string> areaIo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceRow row in rows)
            {
                List<string> problems = new List<string>();

                IgObject template = Find(session, objects, row.Template);
                if (template == null)
                    problems.Add("template " + row.Template + " not found");
                else if (template.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    problems.Add("template " + row.Template + " is checked out" + By(template) + "; check it in first");

                IgObject existing = session.FindObject(row.Name);  // names cannot start with $, and only templates do
                if (existing != null)
                    problems.Add("an instance named " + existing.Tagname + " already exists (from " + existing.DerivedFrom
                        + (string.IsNullOrEmpty(existing.Area) ? "" : ", in " + existing.Area) + ")");

                IgObject area = Find(session, objects, row.Area);
                if (area == null)
                    problems.Add("area " + row.Area + " not found");
                else if (area.category != ECATEGORY.idxCategoryArea)
                    problems.Add(row.Area + " is not an area");
                else if (area.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    row.Notes.Add("area " + row.Area + " is checked out" + By(area));

                IgObject device = Find(session, objects, row.Device);
                if (device == null)
                {
                    problems.Add("I/O device " + row.Device + " not found");
                }
                else
                {
                    List<string> scanGroups = ScanGroups(device);
                    if (scanGroups == null)
                        problems.Add(row.Device + " is not an I/O device (it has no scan groups)");
                    else if (!scanGroups.Exists(name => string.Equals(name, row.ScanGroup, StringComparison.OrdinalIgnoreCase)))
                        problems.Add(row.Device + " has no scan group " + row.ScanGroup + " (it has " + string.Join(", ", scanGroups.ToArray()) + ")");
                    else if (device.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                        row.Notes.Add("I/O device " + row.Device + " is checked out" + By(device));
                }

                if (problems.Count == 0)
                {
                    string ofArea;
                    if (!areaIo.TryGetValue(row.Area, out ofArea))
                    {
                        ofArea = io.AreaIo(row.Area);
                        areaIo.Add(row.Area, ofArea);
                    }
                    if (ofArea == null)
                        row.Notes.Add("area " + row.Area + " has no I/O device, so " + row.Io + " is assigned with the IDE's call");
                    else if (!string.Equals(ofArea, row.Io, StringComparison.OrdinalIgnoreCase))
                        row.Notes.Add("area " + row.Area + " is on " + ofArea + ", so " + row.Io + " is assigned with the IDE's call");
                }

                if (problems.Count > 0)
                {
                    row.Action = InstanceRow.Error;
                    row.Detail = string.Join("; ", problems.ToArray());
                }
                else
                {
                    row.Action = InstanceRow.Add;
                }
            }
        }

        static void PrintPlan(string galaxyName, List<InstanceRow> rows)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (InstanceRow row in rows)
            {
                string line = "  " + row.Action.ToUpperInvariant().PadRight(7) + row.Name + " from " + row.Template + " in " + row.Area + ", io " + row.Io;
                if (row.Detail.Length > 0)
                    line += ": " + row.Detail;
                Console.WriteLine(line);
                foreach (string note in row.Notes)
                    Console.WriteLine("         note: " + note);
            }
        }

        static bool ConfirmGalaxy(string galaxyName)
        {
            Console.WriteLine();
            Console.Write("Type the galaxy name to create these instances in " + galaxyName + ": ");
            string answer = Console.In.ReadLine();
            if (Console.IsInputRedirected)
                Console.WriteLine();
            return answer != null && string.Equals(answer.Trim(), galaxyName, StringComparison.OrdinalIgnoreCase);
        }

        // Creates the instance, assigns the area, then gets its objects onto the scan group (see Usage). Returns false when
        // creating or assigning the area failed, which stops the run. I/O that could not be assigned does not stop it.
        // Nothing is undone: this tool never deletes.
        static bool ApplyRow(InstanceRow row, Dictionary<string, IgObject> objects, GalaxyIo io, IoAssigner assigner)
        {
            Console.WriteLine();
            Console.WriteLine("Creating " + row.Name + " from " + row.Template + "...");

            ITemplate template = (ITemplate)objects[row.Template];
            IInstance instance = template.CreateInstance(row.Name, true);
            ICommandResult created = ((IgObject)template).CommandResult;
            if (instance == null || created == null || !created.Successful)
                return Fail(row, "create failed: " + Describe(created));

            IgObject obj = (IgObject)instance;
            if (!string.Equals(obj.Tagname, row.Name, StringComparison.OrdinalIgnoreCase))
                return Fail(row, "created as " + obj.Tagname + " instead of " + row.Name);

            obj.Area = row.Area;
            ICommandResult assigned = obj.CommandResult;
            if (assigned == null || !assigned.Successful)
            {
                row.Objects.AddRange(Check(row, io));
                return Fail(row, "created, but assigning area " + row.Area + " failed: " + Describe(assigned));
            }

            List<ObjectState> states = Check(row, io);
            foreach (ObjectState state in states)
            {
                state.IoAfterArea = state.Io ?? "none";
                if (state.Status == ObjectState.Ok)
                    state.IoSource = FromArea;
            }

            List<ObjectState> wrongIo = states.FindAll(state => state.Status == ObjectState.AssignIoInIde);
            if (wrongIo.Count > 0)
            {
                string problem;
                try
                {
                    int deviceId = io.InstanceId(row.Device);
                    problem = deviceId == 0
                        ? "I/O device " + row.Device + " not found in the galaxy database"
                        : assigner.Assign(deviceId, row.ScanGroup, wrongIo.ConvertAll(state => state.Id).ToArray());
                }
                catch (Exception ex)
                {
                    problem = ex.GetType().Name + ": " + ex.Message;
                }

                List<ObjectState> after = Check(row, io);
                foreach (ObjectState state in after)
                {
                    ObjectState before = states.Find(s => s.Id == state.Id);
                    state.IoAfterArea = before != null ? before.IoAfterArea : "";
                    if (state.Status == ObjectState.Ok)
                        state.IoSource = before != null && before.Status == ObjectState.Ok ? FromArea : FromAssignCall;
                    else if (state.Status == ObjectState.AssignIoInIde)
                        state.Detail = problem.Length > 0 ? "the IDE's assign call failed: " + problem : "the IDE's assign call did not change it";
                }
                states = after;
            }
            row.Objects.AddRange(states);

            row.Result = "Created";
            int fromArea = states.FindAll(s => s.IoSource == FromArea).Count;
            int fromCall = states.FindAll(s => s.IoSource == FromAssignCall).Count;
            int needIo = states.FindAll(s => s.Status == ObjectState.AssignIoInIde).Count;
            int noArea = states.FindAll(s => s.Status == ObjectState.AreaNotSet).Count;
            row.ResultDetail = states.Count + " object(s); " + (fromArea + fromCall) + " on " + row.Io
                + " (" + fromArea + " from the area, " + fromCall + " by the assign call)"
                + (needIo > 0 ? "; " + needIo + " to assign in the IDE" : "")
                + (noArea > 0 ? "; " + noArea + " without the area" : "");

            foreach (ObjectState state in states)
            {
                string line = "  " + state.Tagname + " (" + state.HierarchicalName + "): area " + (state.Area ?? "none") + ", ";
                if (state.Status == ObjectState.Ok)
                    line += "on " + state.Io + (state.IoSource == FromArea ? " (from the area)"
                        : " (assigned with the IDE's call; after the area it was on " + state.IoAfterArea + ")");
                else if (state.Status == ObjectState.NoIo)
                    line += "no I/O";
                else if (state.Status == ObjectState.AssignIoInIde)
                    line += "on " + (state.Io ?? "no scan group") + " - assign it to " + row.Io + " in the IDE" + (state.Detail.Length > 0 ? " (" + state.Detail + ")" : "");
                else
                    line += state.Status + ", on " + (state.Io ?? "no scan group");
                Console.WriteLine(line);
            }
            return true;
        }

        // Reads back the instance and its contained objects and compares them with the row
        static List<ObjectState> Check(InstanceRow row, GalaxyIo io)
        {
            List<ObjectState> states = io.Objects(row.Name);
            foreach (ObjectState state in states)
            {
                if (!string.Equals(state.Area, row.Area, StringComparison.OrdinalIgnoreCase))
                    state.Status = ObjectState.AreaNotSet;
                else if (string.Equals(state.Io, row.Io, StringComparison.OrdinalIgnoreCase))
                    state.Status = ObjectState.Ok;
                else if (state.Io == null && state.AutoReferences == 0)
                    state.Status = ObjectState.NoIo;
                else
                    state.Status = ObjectState.AssignIoInIde;
            }
            return states;
        }

        static int Summarize(List<InstanceRow> rows, string runFolder, bool failed)
        {
            int created = 0, createdObjects = 0, fromCall = 0;
            List<string> needIo = new List<string>();
            List<string> noArea = new List<string>();
            foreach (InstanceRow row in rows)
            {
                createdObjects += row.Objects.Count;
                if (row.Result == "Created")
                    created++;
                foreach (ObjectState state in row.Objects)
                {
                    if (state.IoSource == FromAssignCall)
                        fromCall++;
                    if (state.Status == ObjectState.AssignIoInIde)
                        needIo.Add("  " + state.Tagname + " (" + state.HierarchicalName + "): on " + (state.Io ?? "no scan group") + ", should be " + row.Io);
                    if (state.Status == ObjectState.AreaNotSet)
                        noArea.Add("  " + state.Tagname + " (" + state.HierarchicalName + "): area " + (state.Area ?? "none") + ", should be " + row.Area);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Created " + created + " instance(s) (" + createdObjects + " object(s) with their contained objects), failed "
                + rows.FindAll(r => r.Result == "Failed").Count + ", not applied " + rows.FindAll(r => r.Result == "Not applied").Count + ".");
            if (fromCall > 0)
                Console.WriteLine(fromCall + " object(s) were assigned to their scan group with the IDE's call.");
            if (needIo.Count > 0)
            {
                Console.WriteLine(needIo.Count + " object(s) are not on their scan group; assign them in the IDE (IO Devices view):");
                foreach (string line in needIo)
                    Console.WriteLine(line);
            }
            if (noArea.Count > 0)
            {
                Console.WriteLine(noArea.Count + " object(s) did not get the area:");
                foreach (string line in noArea)
                    Console.WriteLine(line);
            }
            Console.WriteLine("Details: " + runFolder);

            if (failed)
                return ExitCodes.Error;
            return needIo.Count > 0 || noArea.Count > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
        }

        static bool Fail(InstanceRow row, string detail)
        {
            row.Result = "Failed";
            row.ResultDetail = detail;
            Console.Error.WriteLine("  FAILED: " + detail);
            return false;
        }

        // A template ($...) or instance by tagname, cached for the whole run
        static IgObject Find(GalaxySession session, Dictionary<string, IgObject> objects, string tagname)
        {
            IgObject obj;
            if (!objects.TryGetValue(tagname, out obj))
            {
                obj = session.FindObject(tagname);
                objects.Add(tagname, obj);
            }
            return obj;
        }

        // The device's scan group names, or null when the object has no scan groups
        static List<string> ScanGroups(IgObject device)
        {
            IAttribute list = device.Attributes["ScanGroupList"];
            if (list == null)
                return null;
            List<string> names = new List<string>();
            MxValue value = list.value;
            int size;
            value.GetDimensionSize(out size);
            for (int i = 1; i <= size; i++)  // array elements are 1-based
            {
                MxValue element = new MxValueClass();
                value.GetElement(i, element);
                names.Add(element.GetString());
            }
            return names;
        }

        static string By(IgObject obj)
        {
            return string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " by " + obj.checkedOutBy;
        }

        static string Describe(ICommandResult result)
        {
            if (result == null)
                return "GRAccess returned no result";
            string text = ((result.Text ?? "").Trim() + " " + (result.CustomMessage ?? "").Trim()).Trim();
            return text.Length > 0 ? text : result.ID.ToString();
        }

        static void WritePlan(string path, List<InstanceRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "template", "name", "area", "io", "action", "detail", "notes");
                foreach (InstanceRow row in rows)
                    csv.WriteRow(row.Row, row.Template, row.Name, row.Area, row.Io, row.Action, row.Detail, string.Join("; ", row.Notes.ToArray()));
            }
        }

        static void WriteResults(string path, List<InstanceRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "template", "name", "area", "io", "action", "result", "detail", "notes");
                foreach (InstanceRow row in rows)
                    csv.WriteRow(row.Row, row.Template, row.Name, row.Area, row.Io, row.Action, row.Result, row.ResultDetail, string.Join("; ", row.Notes.ToArray()));
            }
        }

        static void WriteObjects(string path, List<InstanceRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("instance", "object", "fullName", "area", "requestedIo", "io", "status", "ioFrom", "ioAfterArea", "detail");
                foreach (InstanceRow row in rows)
                {
                    foreach (ObjectState state in row.Objects)
                        csv.WriteRow(row.Name, state.Tagname, state.HierarchicalName, state.Area ?? "", row.Io, state.Io ?? "", state.Status, state.IoSource, state.IoAfterArea, state.Detail);
                }
            }
        }
    }
}
