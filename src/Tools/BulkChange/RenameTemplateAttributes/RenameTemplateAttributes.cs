// RenameTemplateAttributes: renames user-defined attributes (UDAs) of templates from a CSV file, with their extensions,
// then checks that every derived template and instance has the new name and kept what it had under the old one.
// Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at the first failure.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // The rows for one template, the objects derived from it, and what every one of them had before the rename
    class RenameGroup
    {
        public string Tagname;
        public IgObject Template;  // null when not found
        public List<RenameRow> Rows = new List<RenameRow>();
        public List<KeyValuePair<IgObject, bool>> Descendants = new List<KeyValuePair<IgObject, bool>>();  // true = template
        public HashSet<string> Family = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // the template and its descendants
        // tagname -> old attribute name -> property (value, data type, lock, description, I/O references, ...) -> text
        public Dictionary<string, Dictionary<string, SortedDictionary<string, string>>> Before =
            new Dictionary<string, Dictionary<string, SortedDictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        public bool HasChanges
        {
            get { return Rows.Exists(row => row.Action == RenameRow.Rename); }
        }
    }

    class RenameTemplateAttributes
    {
        const string Usage =
            "Renames user-defined attributes (UDAs) of templates from a CSV file, then checks that every derived template\r\n" +
            "and instance has the new name with the data type, category, security, extensions, value, lock, description,\r\n" +
            "units, labels and I/O references it had under the old one. Without -Apply this is a dry run that only reports\r\n" +
            "what would change.\r\n" +
            "\r\n" +
            "Usage: RenameTemplateAttributes.exe -f <file.csv> [-r] [-Apply]\r\n" +
            "\r\n" +
            "  -f <file.csv>   Input file with the columns template, name, newName\r\n" +
            "  -r              Rename even when graphics, scripts or other objects reference the attribute: they keep the\r\n" +
            "                  old name and stop working until they are changed (plan.csv lists them)\r\n" +
            "  -Apply          Make the changes; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  template   Template that defines the attribute; the leading $ is optional\r\n" +
            "  name       Current attribute name\r\n" +
            "  newName    New attribute name (letters, digits and _)\r\n" +
            "\r\n" +
            "A row cannot be done when the attribute is inherited (rename it in the template that defines it), when the\r\n" +
            "template or an object derived from it already uses the new name (for an attribute, symbol, script or other\r\n" +
            "extension), when a derived object added its own extension to the attribute, when a derived object is checked\r\n" +
            "out, or, without -r, when the galaxy database shows a graphic, script or other object that references the\r\n" +
            "attribute. The galaxy only records the references of instances, so a template without instances whose own\r\n" +
            "scripts or symbols use the name is not seen. An I/O reference left at ---Auto--- follows the new name, so the\r\n" +
            "device item an object reads becomes <object>.<newName>: change the I/O device's items to match.";

        static readonly string[] Options = { "f", "r", "Apply" };

        // What is compared before and after, besides the attribute's own value, data type, category, security and lock
        static readonly string[] Properties = { ".Description", ".EngUnits", ".OffMsg", ".OnMsg", ".InputSource", ".OutputDest", ".DiffOutputDest" };

        const string PrimitiveSql =
            "SELECT g.tag_name, pd.primitive_name FROM primitive_instance pi " +
            "JOIN gobject g ON g.gobject_id = pi.gobject_id AND pi.package_id IN (g.checked_in_package_id, g.checked_out_package_id) " +
            "JOIN primitive_definition pd ON pd.primitive_definition_id = pi.primitive_definition_id " +
            "WHERE pi.primitive_name = @name";

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string inputPath = Path.GetFullPath(args.Require("f"));
            bool allowReferences = args.Has("r");
            bool apply = args.Has("Apply");
            if (!File.Exists(inputPath))
                throw new UsageException("Input file not found: " + inputPath);

            List<string> problems = new List<string>();
            List<RenameRow> rows = RenameRow.ReadFile(inputPath, problems);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("The input file has problems, so nothing was changed:");
                foreach (string problem in problems)
                    Console.Error.WriteLine("  " + problem);
                return ExitCodes.Error;
            }

            string node, galaxyArg;
            ToolRunner.ResolveGalaxy(args, out node, out galaxyArg);
            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                string galaxyName = session.Galaxy.Name;
                string runFolder = OutputPaths.CreateRunFolder(galaxyName, apply ? "_APPLY" : "_DRYRUN");
                File.Copy(inputPath, Path.Combine(runFolder, Path.GetFileName(inputPath)));

                List<RenameGroup> groups = Plan(session, node, galaxyName, rows, allowReferences);
                PrintPlan(galaxyName, rows);

                int errors = rows.FindAll(row => row.Action == RenameRow.Error).Count;
                int changes = rows.FindAll(row => row.Action == RenameRow.Rename).Count;
                if (!apply || errors > 0 || changes == 0)
                {
                    string planFile = Path.Combine(runFolder, "plan.csv");
                    WriteRows(planFile, rows);
                    Console.WriteLine();
                    if (errors > 0)
                        Console.Error.WriteLine(errors + " row(s) cannot be done, so nothing was changed. Fix them and run again.");
                    else if (changes == 0)
                        Console.WriteLine("Nothing to change.");
                    else
                        Console.WriteLine("DRY RUN - nothing was changed. Run again with -Apply to make these changes.");
                    Console.WriteLine("Plan: " + planFile);
                    return errors > 0 ? ExitCodes.Error : ExitCodes.Success;
                }

                if (!ConfirmGalaxy(galaxyName))
                {
                    Console.Error.WriteLine("The galaxy name did not match, so nothing was changed.");
                    return ExitCodes.Error;
                }

                string failedTemplate = null;
                List<string[]> propagation = new List<string[]>();
                int propagationFailures = 0;
                try
                {
                    foreach (RenameGroup group in groups)
                    {
                        if (!group.HasChanges)
                            continue;
                        List<RenameRow> changing = group.Rows.FindAll(r => r.Action == RenameRow.Rename);
                        if (failedTemplate != null)
                        {
                            foreach (RenameRow row in changing)
                            {
                                row.Result = "Not applied";
                                row.Detail = "stopped after the failure on " + failedTemplate;
                            }
                            continue;
                        }

                        try
                        {
                            ApplyTemplate(session, group, changing, Path.GetFileName(inputPath));
                        }
                        catch (Exception ex)
                        {
                            failedTemplate = group.Tagname;
                            foreach (RenameRow row in changing)
                            {
                                row.Result = "Failed";
                                row.Detail = Message(ex);
                            }
                            continue;
                        }

                        // The rename is checked in; a failure of the check itself stops the run but keeps what was done
                        try
                        {
                            propagationFailures += CheckPropagation(session, group, changing, propagation);
                        }
                        catch (Exception ex)
                        {
                            failedTemplate = group.Tagname;
                            propagationFailures++;
                            Console.Error.WriteLine("  CHECK FAILED: " + Message(ex));
                            foreach (RenameRow row in changing)
                                row.Detail = (row.Detail.Length > 0 ? row.Detail + "; " : "") + "renamed and checked in, but the check afterwards failed: " + Message(ex);
                            propagation.Add(new[] { group.Tagname, group.Tagname, "Template (changed)", "Not checked", Message(ex), "" });
                        }
                    }
                }
                finally
                {
                    WriteRows(Path.Combine(runFolder, "result.csv"), rows);
                    if (propagation.Count > 0)
                    {
                        using (CsvWriter csv = new CsvWriter(Path.Combine(runFolder, "propagation.csv")))
                        {
                            csv.WriteRow("template", "object", "kind", "status", "problems", "notes");
                            foreach (string[] line in propagation)
                                csv.WriteRow(line);
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Renamed " + rows.FindAll(r => r.Result == "Renamed").Count
                    + ", skipped " + rows.FindAll(r => r.Action == RenameRow.Skip).Count
                    + ", failed " + rows.FindAll(r => r.Result == "Failed" || r.Result == "Not applied").Count
                    + "; propagation failures: " + propagationFailures + ".");
                int stale = rows.FindAll(r => r.Result == "Renamed" && r.References.Count > 0).Count;
                if (stale > 0)
                    Console.WriteLine(stale + " renamed attribute(s) are still referenced by their old name; result.csv lists where.");
                Console.WriteLine("Details: " + runFolder);

                if (failedTemplate != null)
                    return ExitCodes.Error;
                return propagationFailures > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // Decides for every row whether it renames, is skipped or cannot be done, and records what every object has
        // before the rename. Changes nothing.
        static List<RenameGroup> Plan(GalaxySession session, string node, string galaxyName, List<RenameRow> rows, bool allowReferences)
        {
            List<RenameGroup> groups = new List<RenameGroup>();
            Dictionary<string, RenameGroup> byTagname = new Dictionary<string, RenameGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (RenameRow row in rows)
            {
                RenameGroup group;
                if (!byTagname.TryGetValue(row.Template, out group))
                {
                    group = new RenameGroup();
                    group.Tagname = row.Template;
                    byTagname.Add(row.Template, group);
                    groups.Add(group);
                }
                group.Rows.Add(row);
            }

            foreach (RenameGroup group in groups)
            {
                group.Template = session.FindObject(group.Tagname);
                string problem = null;
                if (group.Template == null)
                    problem = "template not found";
                else if (group.Template.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    problem = "the template is checked out" + CheckedOutBy(group.Template) + "; check it in or undo the check-out first";
                if (problem != null)
                {
                    SetError(group.Rows, problem);
                    continue;
                }

                IAttributes templateAttributes = group.Template.Attributes;
                Dictionary<string, string> udaOwners = UdaOwners(templateAttributes);
                Dictionary<string, List<string>> ownExtensions = ExtensionsOf(templateAttributes, false);
                foreach (RenameRow row in group.Rows)
                {
                    string owner, newOwner;
                    bool isUda = udaOwners.TryGetValue(row.Name, out owner);
                    bool hasOld = templateAttributes[row.Name] != null;
                    bool newIsOwnUda = udaOwners.TryGetValue(row.NewName, out newOwner) && newOwner.Length == 0;
                    bool hasNew = templateAttributes[row.NewName] != null || udaOwners.ContainsKey(row.NewName);
                    if (!hasOld && newIsOwnUda)
                    {
                        row.Action = RenameRow.Skip;
                        row.Detail = "already renamed: the template defines " + row.NewName + " and has no " + row.Name;
                    }
                    else if (!hasOld)
                        Fail(row, row.Name + " not found");
                    else if (!isUda || owner.Length > 0)
                        Fail(row, isUda ? "inherited from " + owner + "; rename it in that template" : "exists but is not a user-defined attribute of this template");
                    else if (hasNew)
                        Fail(row, "the template already has an attribute named " + row.NewName);
                    else
                    {
                        row.Action = RenameRow.Rename;
                        List<string> extensions;
                        if (ownExtensions.TryGetValue(row.Name, out extensions))
                            row.Extensions = extensions;
                        row.Previous = DescribeCurrent(templateAttributes, row.Name, row.Extensions);
                    }
                }

                List<RenameRow> renaming = group.Rows.FindAll(r => r.Action == RenameRow.Rename);
                if (renaming.Count == 0)
                    continue;

                Snapshot(group, group.Template.Tagname, templateAttributes, renaming);
                group.Family.Add(group.Template.Tagname);
                group.Descendants = FindDescendants(session.Galaxy, group.Tagname);
                int read = 0;
                foreach (KeyValuePair<IgObject, bool> descendant in group.Descendants)
                {
                    IgObject obj = descendant.Key;
                    string tagname = obj.Tagname;
                    group.Family.Add(tagname);
                    if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    {
                        foreach (RenameRow row in renaming)
                            Fail(row, tagname + " is checked out" + CheckedOutBy(obj) + "; the template cannot be checked in while an object derived from it is");
                        continue;
                    }
                    // An extension or UDA a derived object added itself is not renamed with the template's attribute
                    IAttributes attributes = obj.Attributes;
                    Dictionary<string, List<string>> added = ExtensionsOf(attributes, false);
                    Dictionary<string, string> owners = UdaOwners(attributes);
                    foreach (RenameRow row in renaming)
                    {
                        List<string> extensions;
                        string owner;
                        if (added.TryGetValue(row.Name, out extensions))
                            Fail(row, tagname + " has its own " + string.Join("/", extensions.ToArray()) + " on " + row.Name + "; remove it there first");
                        else if (attributes[row.NewName] != null || (owners.TryGetValue(row.NewName, out owner) && owner.Length == 0))
                            Fail(row, tagname + " already has an attribute named " + row.NewName);
                    }
                    Snapshot(group, tagname, attributes, renaming);

                    // GRAccess objects stay alive in GRAccessApp.exe until this process collects garbage; nothing has
                    // been changed yet, so collecting here is safe (see docs\GRAccess-Notes.md)
                    if (++read % 25 == 0)
                        ReleaseComObjects();
                }

                // A symbol, script or other extension of the family that already carries the new name
                List<string> familyNames = new List<string>(group.Family);
                foreach (RenameRow row in group.Rows.FindAll(r => r.Action == RenameRow.Rename))
                {
                    List<string> primitives = PrimitivesNamed(node, galaxyName, row.NewName, group.Family);
                    if (primitives.Count > 0)
                        Fail(row, row.NewName + " is already the name of: " + string.Join("; ", primitives.ToArray()));
                }

                // Anything that reads or writes the attribute by name keeps the old name
                foreach (RenameRow row in group.Rows.FindAll(r => r.Action == RenameRow.Rename))
                {
                    row.References = AttributeReferences.Find(node, galaxyName, row.Name, familyNames);
                    if (row.References.Count == 0)
                        continue;
                    if (allowReferences)
                        row.Detail = "keeps the old name (fix after the rename): " + string.Join("; ", row.References.ToArray());
                    else
                        Fail(row, "still referenced (run with -r to rename anyway and fix these afterwards): " + string.Join("; ", row.References.ToArray()));
                }
            }

            // Two templates in one derivation line cannot take the same new name: the second rename would meet the first
            for (int i = 0; i < groups.Count; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    RenameGroup a = groups[i];
                    RenameGroup b = groups[j];
                    if (!a.Family.Contains(b.Tagname) && !b.Family.Contains(a.Tagname))
                        continue;
                    foreach (RenameRow x in a.Rows.FindAll(r => r.Action == RenameRow.Rename))
                    {
                        foreach (RenameRow y in b.Rows.FindAll(r => r.Action == RenameRow.Rename))
                        {
                            if (!string.Equals(x.NewName, y.NewName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            Fail(x, "row " + y.Row + " gives " + y.NewName + " to an attribute of " + b.Tagname + ", in the same derivation line");
                            Fail(y, "row " + x.Row + " gives " + x.NewName + " to an attribute of " + a.Tagname + ", in the same derivation line");
                        }
                    }
                }
            }
            return groups;
        }

        // Marks the row as impossible, keeping the first reason found
        static void Fail(RenameRow row, string reason)
        {
            if (row.Action == RenameRow.Error)
                return;
            row.Action = RenameRow.Error;
            row.Detail = reason;
        }

        static void SetError(List<RenameRow> rows, string problem)
        {
            foreach (RenameRow row in rows)
                Fail(row, problem);
        }

        // Primitives (symbols, scripts, extensions) named <name> in the template or a derived object, as "<tagname>: <kind>"
        static List<string> PrimitivesNamed(string node, string galaxyName, string name, HashSet<string> family)
        {
            List<string> found = new List<string>();
            using (SqlConnection connection = GalaxyDatabase.Open(node, galaxyName))
            using (SqlCommand command = GalaxyDatabase.Command(connection, PrimitiveSql, 120))
            {
                command.Parameters.AddWithValue("@name", name);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string text = reader.GetString(0) + ": " + reader.GetString(1);
                        if (family.Contains(reader.GetString(0)) && !found.Contains(text))
                            found.Add(text);
                    }
                }
            }
            return found;
        }

        // Records what the object has under each old name: value, data type, category, security, lock, extensions and
        // the properties above
        static void Snapshot(RenameGroup group, string tagname, IAttributes attributes, List<RenameRow> rows)
        {
            Dictionary<string, SortedDictionary<string, string>> byName = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> extensions = ExtensionsOf(attributes, true);
            foreach (RenameRow row in rows)
                byName[row.Name] = Read(attributes, extensions, row.Name);
            group.Before[tagname] = byName;
        }

        static SortedDictionary<string, string> Read(IAttributes attributes, Dictionary<string, List<string>> extensions, string name)
        {
            SortedDictionary<string, string> state = new SortedDictionary<string, string>(StringComparer.Ordinal);
            IAttribute attribute = attributes[name];
            if (attribute == null)
                return state;
            state["dataType"] = TypeName(attribute.DataType);
            state["category"] = attribute.AttributeCategory.ToString();
            state["security"] = attribute.SecurityClassification.ToString();
            state["value"] = Describe(attribute.value);
            state["lock"] = attribute.Locked.ToString();
            List<string> types;
            List<string> sorted = extensions.TryGetValue(name, out types) ? new List<string>(types) : new List<string>();
            sorted.Sort(StringComparer.Ordinal);
            state["extensions"] = string.Join("/", sorted.ToArray());
            foreach (string property in Properties)
            {
                IAttribute part = attributes[name + property];
                if (part != null)
                    state[property] = Describe(part.value) + " (" + part.Locked + ")";
            }
            return state;
        }

        static string CheckedOutBy(IgObject obj)
        {
            return string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " by " + obj.checkedOutBy;
        }

        // The attribute's current definition: data type, extensions, Boolean labels, engineering units and description
        static string DescribeCurrent(IAttributes attributes, string name, List<string> extensions)
        {
            List<string> parts = new List<string> { TypeName(attributes[name].DataType) };
            if (extensions.Count > 0)
                parts.Add("extensions " + string.Join("/", extensions.ToArray()));

            IAttribute offMessage = attributes[name + ".OffMsg"];
            IAttribute onMessage = attributes[name + ".OnMsg"];
            if (offMessage != null && onMessage != null)
                parts.Add("labels " + offMessage.value.GetString() + "/" + onMessage.value.GetString());

            IAttribute engUnits = attributes[name + ".EngUnits"];
            if (engUnits != null)
                parts.Add("units " + engUnits.value.GetString());

            IAttribute description = attributes[name + ".Description"];
            if (description != null)
                parts.Add('"' + description.value.GetString() + '"');
            return string.Join(", ", parts.ToArray());
        }

        static void PrintPlan(string galaxyName, List<RenameRow> rows)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (RenameRow row in rows)
            {
                string line = "  " + row.Action.ToUpperInvariant().PadRight(7) + row.Template + "." + row.Name + " -> " + row.NewName;
                if (row.Previous.Length > 0)
                    line += " - currently: " + row.Previous;
                if (row.Detail.Length > 0)
                    line += ": " + row.Detail;
                Console.WriteLine(line);
            }
        }

        static bool ConfirmGalaxy(string galaxyName)
        {
            Console.WriteLine();
            Console.Write("Type the galaxy name to apply these changes to " + galaxyName + ": ");
            string answer = Console.In.ReadLine();
            if (Console.IsInputRedirected)
                Console.WriteLine();
            return answer != null && string.Equals(answer.Trim(), galaxyName, StringComparison.OrdinalIgnoreCase);
        }

        // Checks out the template, renames the rows' UDAs and checks it in. RenameUDA renames the UDA's extensions with
        // it; if they did not follow, the rename is not saved. Any failure undoes the check-out and is rethrown.
        static void ApplyTemplate(GalaxySession session, RenameGroup group, List<RenameRow> changing, string inputFileName)
        {
            // Read the template again: an earlier group's check-in leaves the objects read during the plan out of date
            IgObject template = session.FindObject(group.Tagname);
            if (template == null)
                throw new GRAccessException(group.Tagname + " was not found.");
            if (template.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                throw new GRAccessException(group.Tagname + " is checked out" + CheckedOutBy(template) + " now.");
            ITemplate editable = (ITemplate)template;

            Console.WriteLine();
            Console.WriteLine("Changing " + group.Tagname + "...");
            bool checkedOut = false;
            bool checkedIn = false;
            try
            {
                ObjectEdits.CheckOut(template);
                checkedOut = true;

                foreach (RenameRow row in changing)
                {
                    Console.WriteLine("  Renaming " + row.Name + " to " + row.NewName + (row.Extensions.Count > 0 ? " with its " + string.Join("/", row.Extensions.ToArray()) : ""));
                    string fullName = group.Tagname + "." + row.Name;
                    editable.RenameUDA(row.Name, row.NewName);
                    GRAccessException.ThrowIfFailed(template.CommandResult, "Rename " + fullName + " to " + row.NewName);
                    IAttributes attributes = template.Attributes;
                    if (attributes[row.NewName] == null || attributes[row.Name] != null)
                        throw new GRAccessException("Rename " + fullName + " reported success, but " + row.NewName + " is not there or " + row.Name + " still is.");

                    Dictionary<string, List<string>> extensions = ExtensionsOf(attributes, false);
                    List<string> moved;
                    if (!extensions.TryGetValue(row.NewName, out moved))
                        moved = new List<string>();
                    if (extensions.ContainsKey(row.Name) || !SameItems(moved, row.Extensions))
                        throw new GRAccessException("After renaming " + fullName + " its extensions are " + Show(extensions, row.Name) + " under the old name and "
                            + Show(extensions, row.NewName) + " under the new one; expected " + string.Join("/", row.Extensions.ToArray()) + " under the new one.");
                }

                ObjectEdits.SaveAndCheckIn(template, "RenameTemplateAttributes: " + changing.Count + " attribute(s) from " + inputFileName);
                checkedIn = true;
                foreach (RenameRow row in changing)
                    row.Result = "Renamed";
                Console.WriteLine("  Checked in " + group.Tagname);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  FAILED: " + Message(ex));
                throw;
            }
            finally
            {
                if (checkedOut && !checkedIn)
                {
                    if (ObjectEdits.UndoCheckOut(template))
                        Console.Error.WriteLine("  The check-out was undone, so " + group.Tagname + " is unchanged.");
                    else
                        Console.Error.WriteLine("  WARNING: " + group.Tagname + " is still checked out. Undo the check-out in the IDE.");
                }
            }
        }

        static bool SameItems(List<string> a, List<string> b)
        {
            List<string> x = new List<string>(a);
            List<string> y = new List<string>(b);
            x.Sort(StringComparer.OrdinalIgnoreCase);
            y.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("/", x.ToArray()).Equals(string.Join("/", y.ToArray()), StringComparison.OrdinalIgnoreCase);
        }

        static string Show(Dictionary<string, List<string>> extensions, string name)
        {
            List<string> types;
            return extensions.TryGetValue(name, out types) && types.Count > 0 ? string.Join("/", types.ToArray()) : "none";
        }

        // The template and every derived template and instance must have the new name, not the old one, with what they
        // had under the old name. Returns the number of objects that do not. The objects are queried again: the ones
        // read during the plan still show what they had then.
        static int CheckPropagation(GalaxySession session, RenameGroup group, List<RenameRow> changing, List<string[]> lines)
        {
            List<KeyValuePair<IgObject, bool>> objects = new List<KeyValuePair<IgObject, bool>>();
            IgObject changed = session.FindObject(group.Tagname);
            if (changed == null)
                throw new GRAccessException(group.Tagname + " was not found after the check-in.");
            objects.Add(new KeyValuePair<IgObject, bool>(changed, true));
            objects.AddRange(FindDescendants(session.Galaxy, group.Tagname));

            int templates = objects.FindAll(d => d.Value).Count - 1;
            Console.WriteLine("  Checking the template, " + templates + " derived template(s) and " + (objects.Count - templates - 1) + " instance(s)...");
            int failures = 0;
            int pendingDeploy = 0;
            foreach (KeyValuePair<IgObject, bool> entry in objects)
            {
                IgObject obj = entry.Key;
                string tagname = obj.Tagname;
                List<string> problems = new List<string>();
                List<string> notes = new List<string>();
                Dictionary<string, SortedDictionary<string, string>> before;
                if (!group.Before.TryGetValue(tagname, out before))
                    problems.Add("not seen during the plan (created since?), so there is nothing to compare with");
                else
                {
                    IAttributes attributes = obj.Attributes;
                    Dictionary<string, List<string>> extensions = ExtensionsOf(attributes, true);
                    foreach (RenameRow row in changing)
                    {
                        if (attributes[row.Name] != null)
                            problems.Add(row.Name + " is still there");
                        if (attributes[row.NewName] == null)
                        {
                            problems.Add(row.NewName + " is missing");
                            continue;
                        }
                        SortedDictionary<string, string> was = before[row.Name];
                        SortedDictionary<string, string> now = Read(attributes, extensions, row.NewName);
                        foreach (string key in Union(was.Keys, now.Keys))
                        {
                            string a, b;
                            was.TryGetValue(key, out a);
                            now.TryGetValue(key, out b);
                            if (a == b)
                                continue;
                            string text = row.NewName + (key.StartsWith(".", StringComparison.Ordinal) ? key : " " + key) + " is " + (b ?? "missing") + ", was " + (a ?? "missing") + " as " + row.Name;
                            // GRAccess shows an object's value in the type of the template package it derives from, and the
                            // rename moves objects to the new package (docs\GRAccess-Notes.md): the same number is a note
                            if (key == "value" && SameNumber(a, b))
                                notes.Add(text + " (same value shown in another type)");
                            else
                                problems.Add(text);
                        }
                    }
                }

                bool isChangedTemplate = string.Equals(tagname, group.Tagname, StringComparison.OrdinalIgnoreCase);
                string kind = isChangedTemplate ? "Template (changed)" : entry.Value ? "Template" : "Instance";
                if (problems.Count > 0)
                {
                    failures++;
                    Console.Error.WriteLine("    " + tagname + ": " + string.Join("; ", problems.ToArray()));
                }
                foreach (string note in notes)
                    Console.WriteLine("    NOTE " + tagname + ": " + note);
                if (!entry.Value && ((IInstance)obj).DeploymentStatus == EDeploymentStatus.deployedWithPendingChanges)
                    pendingDeploy++;
                lines.Add(new[] { group.Tagname, tagname, kind, problems.Count == 0 ? "OK" : "Failed", string.Join("; ", problems.ToArray()), string.Join("; ", notes.ToArray()) });
            }
            if (failures == 0)
                Console.WriteLine("  All " + objects.Count + " object(s) have the new name(s), with what they had before.");
            else
                Console.Error.WriteLine("  PROPAGATION FAILED for " + failures + " of " + objects.Count + " object(s).");
            if (pendingDeploy > 0)
                Console.WriteLine("  " + pendingDeploy + " deployed instance(s) now have pending changes and need to be redeployed (this tool never deploys).");
            return failures;
        }

        static List<string> Union(IEnumerable<string> a, IEnumerable<string> b)
        {
            List<string> keys = new List<string>(a);
            foreach (string key in b)
            {
                if (!keys.Contains(key))
                    keys.Add(key);
            }
            return keys;
        }

        // Whether two described values ("Boolean true", "Integer 1", "Float 1", ...) hold the same number
        static bool SameNumber(string a, string b)
        {
            double x, y;
            return a != null && b != null && TryNumber(a, out x) && TryNumber(b, out y) && x == y;
        }

        static bool TryNumber(string described, out double number)
        {
            number = 0;
            string[] parts = described.Split(' ');
            if (parts.Length != 2)
                return false;
            if (parts[0] == "Boolean")
            {
                number = parts[1] == "true" ? 1 : 0;
                return parts[1] == "true" || parts[1] == "false";
            }
            if (parts[0] != "Integer" && parts[0] != "Float" && parts[0] != "Double")
                return false;
            return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        static string Message(Exception ex)
        {
            return ex is GRAccessException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        // The value with its own data type, e.g. "Integer 1" or "String \"text\""
        static string Describe(MxValue value)
        {
            MxDataType type = value.GetDataType();
            switch (type)
            {
                case MxDataType.MxBoolean:
                    return "Boolean " + (value.GetBoolean() ? "true" : "false");
                case MxDataType.MxInteger:
                    return "Integer " + value.GetInteger().ToString(CultureInfo.InvariantCulture);
                case MxDataType.MxFloat:
                    return "Float " + value.GetFloat().ToString("R", CultureInfo.InvariantCulture);
                case MxDataType.MxDouble:
                    return "Double " + value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                case MxDataType.MxNoData:
                    return "No Data";
                default:
                    return TypeName(type) + " \"" + value.GetString() + "\"";
            }
        }

        static string TypeName(MxDataType type)
        {
            return type.ToString().Substring(2);  // MxInteger -> Integer
        }

        // Elements matching the XPath in the XML an attribute holds (UDAs, Extensions, ...). Reading obj.Attributes builds
        // the whole collection in GRAccessApp.exe, so the callers read it once per object and pass it in.
        static List<XmlElement> SelectElements(IAttributes attributes, string attributeName, string xpath)
        {
            List<XmlElement> elements = new List<XmlElement>();
            IAttribute attribute = attributes[attributeName];
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

        // UDA names of the object: "" for UDAs defined in it, the parent's tagname for inherited ones
        static Dictionary<string, string> UdaOwners(IAttributes attributes)
        {
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string xmlAttribute in new[] { "UDAs", "_InheritedUDAs" })
            {
                foreach (XmlElement element in SelectElements(attributes, xmlAttribute, "/UDAInfo/Attribute"))
                    owners[element.GetAttribute("Name")] = element.GetAttribute("InheritedFromTagName");
            }
            return owners;
        }

        // Every extension type (input, alarm, history, ...) on each attribute, as listed in the object's Extensions XML
        static Dictionary<string, List<string>> ExtensionsOf(IAttributes attributes, bool includeInherited)
        {
            Dictionary<string, List<string>> extensions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string[] xmlAttributes = includeInherited ? new[] { "Extensions", "_InheritedExtensions" } : new[] { "Extensions" };
            foreach (string xmlAttribute in xmlAttributes)
            {
                foreach (XmlElement element in SelectElements(attributes, xmlAttribute, "/ExtensionInfo/AttributeExtension/Attribute"))
                {
                    string name = element.GetAttribute("Name");
                    List<string> types;
                    if (!extensions.TryGetValue(name, out types))
                    {
                        types = new List<string>();
                        extensions.Add(name, types);
                    }
                    string type = element.GetAttribute("ExtensionType").ToLowerInvariant();
                    if (!types.Contains(type))
                        types.Add(type);
                }
            }
            return extensions;
        }

        // derivedOrInstantiatedFrom only returns direct children, so walk down the derivation tree.
        // The bool is true for templates and false for instances.
        static List<KeyValuePair<IgObject, bool>> FindDescendants(IGalaxy galaxy, string tagname)
        {
            List<KeyValuePair<IgObject, bool>> found = new List<KeyValuePair<IgObject, bool>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> parents = new Queue<string>();
            parents.Enqueue(tagname);
            while (parents.Count > 0)
            {
                string parent = parents.Dequeue();
                foreach (bool isTemplate in new[] { true, false })
                {
                    EgObjectIsTemplateOrInstance kind = isTemplate ? EgObjectIsTemplateOrInstance.gObjectIsTemplate : EgObjectIsTemplateOrInstance.gObjectIsInstance;
                    IgObjects children = galaxy.QueryObjects(kind, EConditionType.derivedOrInstantiatedFrom, parent, EMatch.MatchCondition);
                    GRAccessException.ThrowIfFailed(galaxy.CommandResult, "Find objects derived from " + parent);
                    foreach (IgObject child in children)
                    {
                        if (!seen.Add(child.Tagname))
                            continue;
                        found.Add(new KeyValuePair<IgObject, bool>(child, isTemplate));
                        if (isTemplate)
                            parents.Enqueue(child.Tagname);
                    }
                }
            }
            return found;
        }

        // Releases the .NET wrappers of GRAccess objects read so far. Only called while planning, before any change: a
        // forced collection after a change can hang the process at exit (docs\GRAccess-Notes.md).
        static void ReleaseComObjects()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        static void WriteRows(string path, List<RenameRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "template", "name", "newName", "action", "previous", "references", "result", "detail");
                foreach (RenameRow row in rows)
                    csv.WriteRow(row.Row, row.Template, row.Name, row.NewName, row.Action, row.Previous, string.Join("; ", row.References.ToArray()), row.Result, row.Detail);
            }
        }
    }
}
