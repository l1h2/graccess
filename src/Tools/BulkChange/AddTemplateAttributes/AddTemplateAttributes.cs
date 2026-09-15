// AddTemplateAttributes: adds user-defined attributes (UDAs) to templates from a CSV file (or updates them with -o),
// then checks that every derived template and instance received the change.
// Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at the first failure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // The rows for one template
    class TemplateGroup
    {
        public string Tagname;
        public IgObject Template;  // null when not found
        public List<AttributeRow> Rows = new List<AttributeRow>();

        public bool HasChanges
        {
            get { return Rows.Exists(row => row.IsChange); }
        }
    }

    class AddTemplateAttributes
    {
        const string Usage =
            "Adds user-defined attributes (UDAs) to templates from a CSV file, then checks that every derived template\r\n" +
            "and instance received them. Without -Apply this is a dry run that only reports what would change.\r\n" +
            "\r\n" +
            "Usage: AddTemplateAttributes.exe -i <file.csv> [-o] [-Apply]\r\n" +
            "\r\n" +
            "  -i <file.csv>   Input file with the columns template, name, Description, IO, dataType, label\r\n" +
            "  -o              Update attributes that already exist (without -o those rows are skipped)\r\n" +
            "  -Apply          Make the changes; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  template      Template name; the leading $ is optional\r\n" +
            "  name          Attribute name\r\n" +
            "  Description   Attribute description (optional)\r\n" +
            "  IO            I (input), O (output), IO (input/output), or empty for no I/O\r\n" +
            "  dataType      Boolean, Integer, Float, Double, String, Time, ElapsedTime or InternationalizedString\r\n" +
            "  label         Boolean: Off/On labels, e.g. Fail/Pass. Integer, Float, Double: engineering units, e.g. GPM";

        static readonly string[] Options = { "i", "o", "Apply" };

        [STAThread]
        static int Main(string[] args)
        {
            return ToolRunner.Run(args, Usage, Options, Execute);
        }

        static int Execute(ToolArgs args)
        {
            string inputPath = Path.GetFullPath(args.Require("i"));
            bool overwrite = args.Has("o");
            bool apply = args.Has("Apply");
            if (!File.Exists(inputPath))
                throw new UsageException("Input file not found: " + inputPath);

            List<string> problems = new List<string>();
            List<AttributeRow> rows = AttributeRow.ReadFile(inputPath, problems);
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("The input file has problems, so nothing was changed:");
                foreach (string problem in problems)
                    Console.Error.WriteLine("  " + problem);
                return ExitCodes.Error;
            }

            using (GalaxySession session = ToolRunner.OpenGalaxy(args))
            {
                string galaxyName = session.Galaxy.Name;
                string runFolder = OutputPaths.CreateRunFolder(galaxyName, apply ? "_APPLY" : "_DRYRUN");
                File.Copy(inputPath, Path.Combine(runFolder, Path.GetFileName(inputPath)));

                List<TemplateGroup> groups = Plan(session, rows, overwrite);
                PrintPlan(galaxyName, rows);

                int errors = rows.FindAll(row => row.Action == AttributeRow.Error).Count;
                int changes = rows.FindAll(row => row.IsChange).Count;
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
                List<PropagationResult> propagation = new List<PropagationResult>();
                foreach (TemplateGroup group in groups)
                {
                    if (!group.HasChanges)
                        continue;

                    if (failedTemplate != null)
                    {
                        foreach (AttributeRow row in group.Rows.FindAll(r => r.IsChange))
                        {
                            row.Result = "Not applied";
                            row.Detail = "stopped after the failure on " + failedTemplate;
                        }
                        continue;
                    }

                    try
                    {
                        ApplyTemplate(group, Path.GetFileName(inputPath));
                    }
                    catch (Exception ex)
                    {
                        failedTemplate = group.Tagname;
                        string message = Message(ex);
                        if (message.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0)
                            PropagationCheck.ReportBlockedCheckIn(session.Galaxy, group.Tagname);
                        foreach (AttributeRow row in group.Rows.FindAll(r => r.IsChange))
                        {
                            row.Result = "Failed";
                            row.Detail = message;
                        }
                        continue;
                    }

                    propagation.AddRange(PropagationCheck.Run(session.Galaxy, group));
                }

                string resultFile = Path.Combine(runFolder, "result.csv");
                WriteRows(resultFile, rows);
                if (propagation.Count > 0)
                    PropagationCheck.WriteFile(Path.Combine(runFolder, "propagation.csv"), propagation);

                int propagationFailures = propagation.FindAll(p => p.Problems.Count > 0).Count;
                Console.WriteLine();
                Console.WriteLine("Added " + rows.FindAll(r => r.Result == "Added").Count
                    + ", updated " + rows.FindAll(r => r.Result == "Updated").Count
                    + ", skipped " + rows.FindAll(r => r.Action == AttributeRow.Skip).Count
                    + ", failed " + rows.FindAll(r => r.Result == "Failed" || r.Result == "Not applied").Count
                    + "; propagation failures: " + propagationFailures + ".");
                Console.WriteLine("Details: " + runFolder);

                if (failedTemplate != null)
                    return ExitCodes.Error;
                return propagationFailures > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // Decides for every row whether it adds, updates, is skipped or cannot be done. Changes nothing.
        static List<TemplateGroup> Plan(GalaxySession session, List<AttributeRow> rows, bool overwrite)
        {
            List<TemplateGroup> groups = new List<TemplateGroup>();
            Dictionary<string, TemplateGroup> byTagname = new Dictionary<string, TemplateGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (AttributeRow row in rows)
            {
                TemplateGroup group;
                if (!byTagname.TryGetValue(row.Template, out group))
                {
                    group = new TemplateGroup();
                    group.Tagname = row.Template;
                    byTagname.Add(row.Template, group);
                    groups.Add(group);
                }
                group.Rows.Add(row);
            }

            foreach (TemplateGroup group in groups)
            {
                group.Template = session.FindObject(group.Tagname);
                string problem = null;
                if (group.Template == null)
                    problem = "template not found";
                else if (group.Template.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    problem = "the template is checked out" + (string.IsNullOrEmpty(group.Template.checkedOutBy) ? "" : " by " + group.Template.checkedOutBy) + "; check it in or undo the check-out first";

                if (problem != null)
                {
                    foreach (AttributeRow row in group.Rows)
                    {
                        row.Action = AttributeRow.Error;
                        row.Detail = problem;
                    }
                    continue;
                }

                Dictionary<string, string> udaOwners = UdaOwners(group.Template);
                Dictionary<string, string> ioExtensions = null;
                foreach (AttributeRow row in group.Rows)
                {
                    string owner;
                    bool isUda = udaOwners.TryGetValue(row.Name, out owner);
                    string conflict;
                    if (group.Template.Attributes[row.Name] == null)
                    {
                        row.Action = AttributeRow.Add;
                    }
                    else if (!overwrite)
                    {
                        row.Action = AttributeRow.Skip;
                        row.Detail = "already exists (use -o to update it)";
                    }
                    else if (isUda && owner.Length == 0 && (conflict = SeparateUdaConflict(udaOwners, row)) != null)
                    {
                        row.Action = AttributeRow.Error;
                        row.Detail = conflict;
                    }
                    else if (isUda && owner.Length == 0)
                    {
                        row.Action = AttributeRow.Update;
                        if (ioExtensions == null)
                            ioExtensions = IoExtensionsOf(group.Template);
                        row.Previous = DescribeCurrent(group.Template, row.Name, ioExtensions);
                    }
                    else
                    {
                        row.Action = AttributeRow.Error;
                        row.Detail = isUda
                            ? "inherited from " + owner + "; update it in that template"
                            : "exists but is not a user-defined attribute of this template";
                    }
                }
            }
            return groups;
        }

        // A UDA literally named X.Description or X.EngUnits would clash with the description or engineering units
        // the tool sets on X
        static string SeparateUdaConflict(Dictionary<string, string> udaOwners, AttributeRow row)
        {
            if (row.Description.Length > 0 && udaOwners.ContainsKey(row.Name + ".Description"))
                return row.Name + ".Description is a separate UDA; delete it in the IDE before setting the description with this tool";
            if (row.EngUnits != null && udaOwners.ContainsKey(row.Name + ".EngUnits"))
                return row.Name + ".EngUnits is a separate UDA; delete it in the IDE before setting the engineering units with this tool";
            return null;
        }

        // The attribute's current definition, in the same style as AttributeRow.Summary, with the current texts
        static string DescribeCurrent(IgObject template, string name, Dictionary<string, string> ioExtensions)
        {
            List<string> parts = new List<string> { template.Attributes[name].DataType.ToString().Substring(2) };
            string extension;
            if (ioExtensions.TryGetValue(name, out extension))
                parts.Add("IO=" + AttributeRow.IoLetters(extension));

            IAttribute offMessage = template.Attributes[name + ".OffMsg"];
            IAttribute onMessage = template.Attributes[name + ".OnMsg"];
            if (offMessage != null && onMessage != null)
                parts.Add("labels " + offMessage.value.GetString() + "/" + onMessage.value.GetString());

            IAttribute engUnits = template.Attributes[name + ".EngUnits"];
            if (engUnits != null)
                parts.Add("units " + engUnits.value.GetString());

            IAttribute description = template.Attributes[name + ".Description"];
            if (description != null)
                parts.Add("description '" + description.value.GetString() + "'");
            return string.Join(", ", parts.ToArray());
        }

        static void PrintPlan(string galaxyName, List<AttributeRow> rows)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (AttributeRow row in rows)
            {
                string line = "  " + row.Action.ToUpperInvariant().PadRight(7) + row.Template + "." + row.Name;
                if (row.IsChange)
                    line += " (" + row.Summary() + ")";
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

        // Checks out the template, applies its rows and checks it in. Any failure undoes the check-out and is rethrown.
        static void ApplyTemplate(TemplateGroup group, string inputFileName)
        {
            IgObject template = group.Template;
            ITemplate editable = (ITemplate)template;
            List<AttributeRow> changes = group.Rows.FindAll(row => row.IsChange);

            Console.WriteLine();
            Console.WriteLine("Changing " + group.Tagname + "...");
            bool checkedOut = false;
            bool checkedIn = false;
            try
            {
                ObjectEdits.CheckOut(template);
                checkedOut = true;

                Dictionary<string, string> ioExtensions = IoExtensionsOf(template);
                foreach (AttributeRow row in changes)
                {
                    Console.WriteLine("  " + (row.Action == AttributeRow.Add ? "Adding " : "Updating ") + row.Name + " (" + row.Summary() + ")");
                    ApplyRow(template, editable, row, ioExtensions);
                }

                ObjectEdits.SaveAndCheckIn(template, "AddTemplateAttributes: " + changes.Count + " attribute(s) from " + inputFileName);
                checkedIn = true;
                foreach (AttributeRow row in changes)
                    row.Result = row.Action == AttributeRow.Add ? "Added" : "Updated";
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

        static string Message(Exception ex)
        {
            return ex is GRAccessException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        static void ApplyRow(IgObject template, ITemplate editable, AttributeRow row, Dictionary<string, string> ioExtensions)
        {
            string fullName = template.Tagname + "." + row.Name;
            MxAttributeCategory category = row.DataType == MxDataType.MxInternationalizedString
                ? MxAttributeCategory.MxCategoryWriteable_C_Lockable
                : MxAttributeCategory.MxCategoryWriteable_USC_Lockable;

            if (row.Action == AttributeRow.Add)
            {
                editable.AddUDA(row.Name, row.DataType, category, MxSecurityClassification.MxSecurityOperate, false, 0);
                GRAccessException.ThrowIfFailed(template.CommandResult, "Add " + fullName);
            }
            else
            {
                editable.UpdateUDA(row.Name, row.DataType, category, MxSecurityClassification.MxSecurityOperate, false, 0);
                GRAccessException.ThrowIfFailed(template.CommandResult, "Update " + fullName);
            }

            string currentIo;
            ioExtensions.TryGetValue(row.Name, out currentIo);
            if (!string.Equals(currentIo, row.IoExtensionType, StringComparison.OrdinalIgnoreCase))
            {
                if (currentIo != null)
                {
                    editable.DeleteExtensionPrimitive(currentIo, row.Name);
                    GRAccessException.ThrowIfFailed(template.CommandResult, "Remove " + currentIo + " from " + fullName);
                }
                if (row.IoExtensionType != null)
                {
                    editable.AddExtensionPrimitive(row.IoExtensionType, row.Name, false);
                    GRAccessException.ThrowIfFailed(template.CommandResult, "Add " + row.IoExtensionType + " to " + fullName);
                }
            }

            if (row.IoExtensionType != null)
                LockIoSettings(template, row.Name);

            if (row.Description.Length > 0)
                SetPropertyAndLock(template, row.Name, ".Description", row.Description, (attribute, text) => attribute.Description = text);

            if (row.OffMessage != null)
            {
                // _CmdAdd switches Boolean labels on the same way the IDE does, creating .OnMsg, .OffMsg and .Msg
                if (template.Attributes[row.Name + ".OnMsg"] == null)
                    SetValue(template, "_CmdAdd", "<CmdData><BooleanLabel><Attribute Name=\"" + row.Name + "\"/></BooleanLabel></CmdData>", "Add Boolean labels to " + fullName);
                SetAndLock(template, row.Name + ".OffMsg", row.OffMessage);
                SetAndLock(template, row.Name + ".OnMsg", row.OnMessage);
            }

            if (row.EngUnits != null)
                SetPropertyAndLock(template, row.Name, ".EngUnits", row.EngUnits, (attribute, text) => attribute.EngUnits = text);
        }

        // Locks the attribute's I/O settings (such as the Write to reference) the way locking the I/O block in the IDE
        // does. Only the settings that exist for this extension and data type are there to lock.
        static void LockIoSettings(IgObject template, string attributeName)
        {
            string[] settings = { "InputSource", "OutputDest", "OutputEveryScan", "InvertValue", "Deadband", "DiffOutputDest" };
            foreach (string setting in settings)
            {
                IAttribute attribute = template.Attributes[attributeName + "." + setting];
                if (attribute == null || attribute.Locked != MxPropertyLockedEnum.MxUnLocked)
                    continue;
                attribute.SetLocked(MxPropertyLockedEnum.MxLockedInMe);
                GRAccessException.ThrowIfFailed(attribute.CommandResult, "Lock " + template.Tagname + "." + attributeName + "." + setting);
            }
        }

        // Sets the description or engineering units through the attribute's Description or EngUnits property. GRAccess
        // then creates X.Description or X.EngUnits the same way the IDE does (not as a separate UDA); it is then locked.
        static void SetPropertyAndLock(IgObject template, string attributeName, string suffix, string text, Action<IAttribute, string> set)
        {
            string fullName = template.Tagname + "." + attributeName + suffix;
            IAttribute attribute = template.Attributes[attributeName];
            set(attribute, text);
            GRAccessException.ThrowIfFailed(attribute.CommandResult, "Set " + fullName);

            IAttribute property = template.Attributes[attributeName + suffix];
            if (property == null)
                throw new GRAccessException("Set " + fullName + " failed: GRAccess did not create it.");
            property.SetLocked(MxPropertyLockedEnum.MxLockedInMe);
            GRAccessException.ThrowIfFailed(property.CommandResult, "Lock " + fullName);
        }

        static void SetAndLock(IgObject template, string name, string text)
        {
            string fullName = template.Tagname + "." + name;
            SetValue(template, name, text, "Set " + fullName);
            IAttribute attribute = template.Attributes[name];
            attribute.SetLocked(MxPropertyLockedEnum.MxLockedInMe);
            GRAccessException.ThrowIfFailed(attribute.CommandResult, "Lock " + fullName);
        }

        static void SetValue(IgObject template, string name, string text, string step)
        {
            IAttribute attribute = template.Attributes[name];
            if (attribute == null)
                throw new GRAccessException(step + " failed: " + template.Tagname + "." + name + " does not exist.");

            MxValue value = new MxValueClass();
            value.PutString(text);
            attribute.SetValue(value);
            GRAccessException.ThrowIfFailed(attribute.CommandResult, step);
        }

        // UDA names of the template: "" for UDAs defined in it, the parent's tagname for inherited ones
        static Dictionary<string, string> UdaOwners(IgObject template)
        {
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string xmlAttribute in new[] { "UDAs", "_InheritedUDAs" })
            {
                foreach (XmlElement element in SelectElements(template, xmlAttribute, "/UDAInfo/Attribute"))
                    owners[element.GetAttribute("Name")] = element.GetAttribute("InheritedFromTagName");
            }
            return owners;
        }

        // I/O extension type of each attribute that has one in this template
        static Dictionary<string, string> IoExtensionsOf(IgObject template)
        {
            Dictionary<string, string> extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement element in SelectElements(template, "Extensions", "/ExtensionInfo/AttributeExtension/Attribute"))
            {
                string type = element.GetAttribute("ExtensionType");
                if (AttributeRow.IsIoExtension(type))
                    extensions[element.GetAttribute("Name")] = type;
            }
            return extensions;
        }

        // Elements of the XML that templates keep in attributes such as UDAs and Extensions
        static List<XmlElement> SelectElements(IgObject obj, string xmlAttributeName, string xpath)
        {
            List<XmlElement> elements = new List<XmlElement>();
            IAttribute attribute = obj.Attributes[xmlAttributeName];
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

        static void WriteRows(string path, List<AttributeRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "template", "name", "dataType", "IO", "label", "Description", "action", "previous", "result", "detail");
                foreach (AttributeRow row in rows)
                    csv.WriteRow(row.Row, row.Template, row.Name, row.DataTypeName, row.Io, row.Label, row.Description, row.Action, row.Previous, row.Result, row.Detail);
            }
        }
    }
}
