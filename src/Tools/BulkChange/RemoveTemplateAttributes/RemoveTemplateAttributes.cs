// RemoveTemplateAttributes: removes user-defined attributes (UDAs), with their extensions, from templates listed in a
// CSV file, then checks that no derived template or instance still has them.
// Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at the first failure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // The rows for one template, and what the plan found about the objects derived from it
    class RemovalGroup
    {
        public string Tagname;
        public IgObject Template;  // null when not found
        public List<RemovalRow> Rows = new List<RemovalRow>();
        public List<KeyValuePair<IgObject, bool>> Descendants = new List<KeyValuePair<IgObject, bool>>();  // true = template

        public bool HasChanges
        {
            get { return Rows.Exists(row => row.Action == RemovalRow.Remove); }
        }
    }

    class RemoveTemplateAttributes
    {
        const string Usage =
            "Removes user-defined attributes (UDAs) and their extensions from templates listed in a CSV file, then checks\r\n" +
            "that no derived template or instance still has them. Without -Apply this is a dry run that only reports what\r\n" +
            "would change.\r\n" +
            "\r\n" +
            "Usage: RemoveTemplateAttributes.exe -f <file.csv> [-Apply]\r\n" +
            "\r\n" +
            "  -f <file.csv>   Input file with the columns template, name\r\n" +
            "  -Apply          Make the changes; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  template   Template that defines the attribute; the leading $ is optional\r\n" +
            "  name       Attribute name\r\n" +
            "\r\n" +
            "A row cannot be done when the attribute is inherited (remove it in the template that defines it), when a\r\n" +
            "derived object added its own extension to it, when a derived object is checked out, or when a graphic, script\r\n" +
            "or other object still references it (the galaxy database's resolved references are checked).";

        static readonly string[] Options = { "f", "Apply" };

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
            List<RemovalRow> rows = RemovalRow.ReadFile(inputPath, problems);
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

                List<RemovalGroup> groups = Plan(session, node, galaxyName, rows);
                PrintPlan(galaxyName, rows);

                int errors = rows.FindAll(row => row.Action == RemovalRow.Error).Count;
                int changes = rows.FindAll(row => row.Action == RemovalRow.Remove).Count;
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
                foreach (RemovalGroup group in groups)
                {
                    if (!group.HasChanges)
                        continue;
                    List<RemovalRow> changing = group.Rows.FindAll(r => r.Action == RemovalRow.Remove);
                    if (failedTemplate != null)
                    {
                        foreach (RemovalRow row in changing)
                        {
                            row.Result = "Not applied";
                            row.Detail = "stopped after the failure on " + failedTemplate;
                        }
                        continue;
                    }

                    try
                    {
                        ApplyTemplate(group, changing, Path.GetFileName(inputPath));
                    }
                    catch (Exception ex)
                    {
                        failedTemplate = group.Tagname;
                        foreach (RemovalRow row in changing)
                        {
                            row.Result = "Failed";
                            row.Detail = Message(ex);
                        }
                        continue;
                    }
                    propagationFailures += CheckPropagation(session.Galaxy, group, changing, propagation);
                }

                string resultFile = Path.Combine(runFolder, "result.csv");
                WriteRows(resultFile, rows);
                if (propagation.Count > 0)
                {
                    using (CsvWriter csv = new CsvWriter(Path.Combine(runFolder, "propagation.csv")))
                    {
                        csv.WriteRow("template", "object", "kind", "status", "problems");
                        foreach (string[] line in propagation)
                            csv.WriteRow(line);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Removed " + rows.FindAll(r => r.Result == "Removed").Count
                    + ", skipped " + rows.FindAll(r => r.Action == RemovalRow.Skip).Count
                    + ", failed " + rows.FindAll(r => r.Result == "Failed" || r.Result == "Not applied").Count
                    + "; propagation failures: " + propagationFailures + ".");
                Console.WriteLine("Details: " + runFolder);

                if (failedTemplate != null)
                    return ExitCodes.Error;
                return propagationFailures > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // Decides for every row whether it removes, is skipped or cannot be done. Changes nothing.
        static List<RemovalGroup> Plan(GalaxySession session, string node, string galaxyName, List<RemovalRow> rows)
        {
            List<RemovalGroup> groups = new List<RemovalGroup>();
            Dictionary<string, RemovalGroup> byTagname = new Dictionary<string, RemovalGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (RemovalRow row in rows)
            {
                RemovalGroup group;
                if (!byTagname.TryGetValue(row.Template, out group))
                {
                    group = new RemovalGroup();
                    group.Tagname = row.Template;
                    byTagname.Add(row.Template, group);
                    groups.Add(group);
                }
                group.Rows.Add(row);
            }

            foreach (RemovalGroup group in groups)
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

                Dictionary<string, string> udaOwners = UdaOwners(group.Template);
                Dictionary<string, List<string>> ownExtensions = ExtensionsOf(group.Template, false);
                foreach (RemovalRow row in group.Rows)
                {
                    string owner;
                    bool isUda = udaOwners.TryGetValue(row.Name, out owner);
                    if (group.Template.Attributes[row.Name] == null)
                    {
                        row.Action = RemovalRow.Skip;
                        row.Detail = "not found (already removed?)";
                    }
                    else if (isUda && owner.Length == 0)
                    {
                        row.Action = RemovalRow.Remove;
                        List<string> extensions;
                        if (ownExtensions.TryGetValue(row.Name, out extensions))
                            row.Extensions = extensions;
                        row.Previous = DescribeCurrent(group.Template, row.Name, row.Extensions);
                    }
                    else
                    {
                        row.Action = RemovalRow.Error;
                        row.Detail = isUda
                            ? "inherited from " + owner + "; remove it in that template"
                            : "exists but is not a user-defined attribute of this template";
                    }
                }

                List<RemovalRow> removing = group.Rows.FindAll(r => r.Action == RemovalRow.Remove);
                if (removing.Count == 0)
                    continue;

                group.Descendants = FindDescendants(session.Galaxy, group.Tagname);
                int read = 0;
                foreach (KeyValuePair<IgObject, bool> descendant in group.Descendants)
                {
                    IgObject obj = descendant.Key;
                    if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    {
                        SetError(removing, obj.Tagname + " is checked out" + CheckedOutBy(obj) + "; the template cannot be checked in while an object derived from it is");
                        break;
                    }
                    // An extension a derived object added itself would be left pointing at a missing attribute
                    Dictionary<string, List<string>> added = ExtensionsOf(obj, false);
                    foreach (RemovalRow row in removing)
                    {
                        List<string> extensions;
                        if (added.TryGetValue(row.Name, out extensions))
                        {
                            row.Action = RemovalRow.Error;
                            row.Detail = obj.Tagname + " has its own " + string.Join("/", extensions.ToArray()) + " on " + row.Name + "; remove it there first";
                        }
                    }
                    if (++read % 25 == 0)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }

                // Anything that still reads or writes the attribute would lose its reference
                List<string> family = new List<string> { group.Template.Tagname };
                foreach (KeyValuePair<IgObject, bool> descendant in group.Descendants)
                    family.Add(descendant.Key.Tagname);
                foreach (RemovalRow row in group.Rows.FindAll(r => r.Action == RemovalRow.Remove))
                {
                    List<string> references = AttributeReferences.Find(node, galaxyName, row.Name, family);
                    row.References = references.Count;
                    if (references.Count > 0)
                    {
                        row.Action = RemovalRow.Error;
                        row.Detail = "still referenced: " + string.Join("; ", references.ToArray());
                    }
                }
            }
            return groups;
        }

        static void SetError(List<RemovalRow> rows, string problem)
        {
            foreach (RemovalRow row in rows)
            {
                row.Action = RemovalRow.Error;
                row.Detail = problem;
            }
        }

        static string CheckedOutBy(IgObject obj)
        {
            return string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " by " + obj.checkedOutBy;
        }

        // The attribute's current definition: data type, extensions, Boolean labels, engineering units and description
        static string DescribeCurrent(IgObject template, string name, List<string> extensions)
        {
            List<string> parts = new List<string> { template.Attributes[name].DataType.ToString().Substring(2) };
            if (extensions.Count > 0)
                parts.Add("extensions " + string.Join("/", extensions.ToArray()));

            IAttribute offMessage = template.Attributes[name + ".OffMsg"];
            IAttribute onMessage = template.Attributes[name + ".OnMsg"];
            if (offMessage != null && onMessage != null)
                parts.Add("labels " + offMessage.value.GetString() + "/" + onMessage.value.GetString());

            IAttribute engUnits = template.Attributes[name + ".EngUnits"];
            if (engUnits != null)
                parts.Add("units " + engUnits.value.GetString());

            IAttribute description = template.Attributes[name + ".Description"];
            if (description != null)
                parts.Add('"' + description.value.GetString() + '"');
            return string.Join(", ", parts.ToArray());
        }

        static void PrintPlan(string galaxyName, List<RemovalRow> rows)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (RemovalRow row in rows)
            {
                string line = "  " + row.Action.ToUpperInvariant().PadRight(7) + row.Template + "." + row.Name;
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

        // Checks out the template, removes the rows' extensions and UDAs and checks it in. Any failure undoes the
        // check-out and is rethrown.
        static void ApplyTemplate(RemovalGroup group, List<RemovalRow> changing, string inputFileName)
        {
            IgObject template = group.Template;
            ITemplate editable = (ITemplate)template;

            Console.WriteLine();
            Console.WriteLine("Changing " + group.Tagname + "...");
            bool checkedOut = false;
            bool checkedIn = false;
            try
            {
                ObjectEdits.CheckOut(template);
                checkedOut = true;

                foreach (RemovalRow row in changing)
                {
                    Console.WriteLine("  Removing " + row.Name + (row.Extensions.Count > 0 ? " and its " + string.Join("/", row.Extensions.ToArray()) : ""));
                    string fullName = group.Tagname + "." + row.Name;
                    foreach (string extension in row.Extensions)
                    {
                        editable.DeleteExtensionPrimitive(extension, row.Name);
                        GRAccessException.ThrowIfFailed(template.CommandResult, "Remove " + extension + " from " + fullName);
                    }
                    editable.DeleteUDA(row.Name);
                    GRAccessException.ThrowIfFailed(template.CommandResult, "Delete " + fullName);
                    if (template.Attributes[row.Name] != null)
                        throw new GRAccessException("Delete " + fullName + " reported success, but the attribute is still there.");
                }

                ObjectEdits.SaveAndCheckIn(template, "RemoveTemplateAttributes: " + changing.Count + " attribute(s) from " + inputFileName);
                checkedIn = true;
                foreach (RemovalRow row in changing)
                    row.Result = "Removed";
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

        // Every derived template and instance must have lost the attributes. Returns the number of objects that did not.
        // The objects are queried again: the ones read during the plan still show the attributes they had then.
        static int CheckPropagation(IGalaxy galaxy, RemovalGroup group, List<RemovalRow> changing, List<string[]> lines)
        {
            group.Descendants = FindDescendants(galaxy, group.Tagname);
            int templates = group.Descendants.FindAll(d => d.Value).Count;
            Console.WriteLine("  Checking propagation to " + templates + " derived template(s) and " + (group.Descendants.Count - templates) + " instance(s)...");
            int failures = 0;
            int pendingDeploy = 0;
            int read = 0;
            foreach (KeyValuePair<IgObject, bool> descendant in group.Descendants)
            {
                IgObject obj = descendant.Key;
                List<string> problems = new List<string>();
                foreach (RemovalRow row in changing)
                {
                    if (obj.Attributes[row.Name] != null)
                        problems.Add(row.Name + " is still there");
                }
                if (problems.Count > 0)
                {
                    failures++;
                    Console.Error.WriteLine("    " + obj.Tagname + ": " + string.Join("; ", problems.ToArray()));
                }
                if (!descendant.Value && ((IInstance)obj).DeploymentStatus == EDeploymentStatus.deployedWithPendingChanges)
                    pendingDeploy++;
                lines.Add(new[] { group.Tagname, obj.Tagname, descendant.Value ? "Template" : "Instance", problems.Count == 0 ? "OK" : "Failed", string.Join("; ", problems.ToArray()) });
                if (++read % 25 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            if (failures == 0)
                Console.WriteLine("  None of the " + group.Descendants.Count + " derived object(s) has the removed attribute(s) any more.");
            else
                Console.Error.WriteLine("  PROPAGATION FAILED for " + failures + " of " + group.Descendants.Count + " derived object(s).");
            if (pendingDeploy > 0)
                Console.WriteLine("  " + pendingDeploy + " deployed instance(s) now have pending changes and need to be redeployed (this tool never deploys).");
            return failures;
        }

        static string Message(Exception ex)
        {
            return ex is GRAccessException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        // UDA names of the object: "" for UDAs defined in it, the parent's tagname for inherited ones
        static Dictionary<string, string> UdaOwners(IgObject obj)
        {
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string xmlAttribute in new[] { "UDAs", "_InheritedUDAs" })
            {
                foreach (XmlElement element in ObjectXml.SelectElements(obj, xmlAttribute, "/UDAInfo/Attribute"))
                    owners[element.GetAttribute("Name")] = element.GetAttribute("InheritedFromTagName");
            }
            return owners;
        }

        // Every extension type (input, alarm, history, ...) on each attribute, as listed in the object's Extensions XML
        static Dictionary<string, List<string>> ExtensionsOf(IgObject obj, bool includeInherited)
        {
            Dictionary<string, List<string>> extensions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string[] xmlAttributes = includeInherited ? new[] { "Extensions", "_InheritedExtensions" } : new[] { "Extensions" };
            foreach (string xmlAttribute in xmlAttributes)
            {
                foreach (XmlElement element in ObjectXml.SelectElements(obj, xmlAttribute, "/ExtensionInfo/AttributeExtension/Attribute"))
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

        static void WriteRows(string path, List<RemovalRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "template", "name", "action", "previous", "references", "result", "detail");
                foreach (RemovalRow row in rows)
                    csv.WriteRow(row.Row, row.Template, row.Name, row.Action, row.Previous, row.References, row.Result, row.Detail);
            }
        }
    }
}
