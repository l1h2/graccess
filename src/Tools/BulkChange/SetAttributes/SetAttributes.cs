// SetAttributes: sets attribute values and lock states of templates and instances from a CSV file, such as a UDA's
// value, a description, engineering units or the lock of an I/O extension's settings. Each object is read back before
// it is saved and again after its check-in, and the objects derived from a changed template are checked.
// Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at the first failure.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // The rows for one object, in file order
    class ObjectGroup
    {
        public string Tagname;
        public IgObject Object;   // null when not found
        public string Reach = "";  // templates: the derived objects a change reaches
        public List<SetRow> Rows = new List<SetRow>();

        public bool IsTemplate
        {
            get { return Tagname.StartsWith("$", StringComparison.Ordinal); }
        }

        public bool HasChanges
        {
            get { return Rows.Exists(row => row.Action == SetRow.Change); }
        }
    }

    class SetAttributes
    {
        const string Usage =
            "Sets attribute values and lock states of templates and instances from a CSV file: for example a UDA's value,\r\n" +
            "a description, engineering units, or whether an I/O extension's settings are locked in a template. Without\r\n" +
            "-Apply this is a dry run that only reports what would change.\r\n" +
            "\r\n" +
            "Usage: SetAttributes.exe -f <file.csv> [-Apply]\r\n" +
            "\r\n" +
            "  -f <file.csv>   Input file with the columns object, attribute, set, to\r\n" +
            "  -Apply          Make the changes; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  object      Template (with its $) or instance\r\n" +
            "  attribute   Full attribute name, e.g. RESET, CH_S.InputSource, PV.Description, ANTI_RCY_TIME.REM.EngUnits\r\n" +
            "  set         value or lock\r\n" +
            "  to          value: the new value for the attribute's data type: true or false (Boolean), a whole number\r\n" +
            "              (Integer), a number with . as decimal separator (Float, Double), or text (String,\r\n" +
            "              InternationalizedString; empty for no text). lock: locked or unlocked.\r\n" +
            "\r\n" +
            "A value is written with the attribute's data type, which also replaces a stored value of another type (a UDA\r\n" +
            "whose data type was changed keeps its old value, e.g. Boolean false on an Integer). The values an object\r\n" +
            "stores itself are also read from the galaxy database, with your Windows login, because GRAccess can show them\r\n" +
            "converted to the new type. Locks are set in templates only. A row cannot be done when its object, or for a\r\n" +
            "template a derived object, is checked out, when the attribute is locked in a parent template, or when it is an\r\n" +
            "array or of another data type. Objects are changed in file order. After each template's check-in, its derived\r\n" +
            "templates and instances are checked for the locks and the values it locks (propagation.csv). A value on an\r\n" +
            "attribute the template does not lock does not reach derived objects that store their own value, and a lock\r\n" +
            "the template removes stays in derived templates (they take it over): give those objects rows of their own.\r\n" +
            "A value stored as written that GRAccess reads back in another type is reported as a NOTE: the object still\r\n" +
            "derives from an older package of its template (locking and unlocking the attribute there fixes it).";

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
            List<SetRow> rows = SetRow.ReadFile(inputPath, problems);
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
            using (SqlConnection database = GalaxyDatabase.Open(node, session.Galaxy.Name))
            {
                string galaxyName = session.Galaxy.Name;
                string runFolder = OutputPaths.CreateRunFolder(galaxyName, apply ? "_APPLY" : "_DRYRUN");
                File.Copy(inputPath, Path.Combine(runFolder, Path.GetFileName(inputPath)));

                List<ObjectGroup> groups = Plan(session, database, rows);
                PrintPlan(galaxyName, groups);

                int errors = rows.FindAll(row => row.Action == SetRow.Error).Count;
                int changes = rows.FindAll(row => row.Action == SetRow.Change).Count;
                if (!apply || errors > 0 || changes == 0)
                {
                    string planFile = Path.Combine(runFolder, "plan.csv");
                    WriteRows(planFile, rows);
                    ReleaseComObjects();
                    Console.WriteLine();
                    if (errors > 0)
                        Console.Error.WriteLine(errors + " row(s) cannot be done, so nothing was changed. Fix them and run again.");
                    else if (changes == 0)
                        Console.WriteLine("Nothing to change.");
                    else
                        Console.WriteLine("DRY RUN - nothing was changed. Run again with -Apply to make these " + changes + " change(s) on "
                            + groups.FindAll(g => g.HasChanges).Count + " object(s).");
                    Console.WriteLine("Plan: " + planFile);
                    return errors > 0 ? ExitCodes.Error : ExitCodes.Success;
                }

                if (!ConfirmGalaxy(galaxyName))
                {
                    Console.Error.WriteLine("The galaxy name did not match, so nothing was changed.");
                    return ExitCodes.Error;
                }

                string failedObject = null;
                int mismatches = 0;
                int shownOld = 0;
                int propagationFailures = 0;
                List<string[]> propagation = new List<string[]>();
                foreach (ObjectGroup group in groups)
                {
                    if (!group.HasChanges)
                        continue;
                    List<SetRow> changing = group.Rows.FindAll(r => r.Action == SetRow.Change);
                    if (failedObject != null)
                    {
                        foreach (SetRow row in changing)
                        {
                            row.Result = "Not applied";
                            row.Detail = "stopped after the failure on " + failedObject;
                        }
                        continue;
                    }

                    try
                    {
                        ApplyObject(session, group, changing, Path.GetFileName(inputPath));
                    }
                    catch (Exception ex)
                    {
                        failedObject = group.Tagname;
                        foreach (SetRow row in changing)
                        {
                            row.Result = "Failed";
                            row.Detail = Message(ex);
                        }
                        continue;
                    }
                    mismatches += Verify(session, database, group, changing, ref shownOld);
                    if (group.IsTemplate)
                        propagationFailures += CheckPropagation(session.Galaxy, group, changing, propagation);
                }

                string resultFile = Path.Combine(runFolder, "result.csv");
                WriteRows(resultFile, rows);
                if (propagation.Count > 0)
                {
                    using (CsvWriter csv = new CsvWriter(Path.Combine(runFolder, "propagation.csv")))
                    {
                        csv.WriteRow("template", "attribute", "set", "object", "kind", "now", "status");
                        foreach (string[] line in propagation)
                            csv.WriteRow(line);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Done " + rows.FindAll(r => r.Result == "Done").Count
                    + ", skipped " + rows.FindAll(r => r.Action == SetRow.Skip).Count
                    + ", failed " + rows.FindAll(r => r.Result == "Failed" || r.Result == "Not applied").Count
                    + "; verify mismatches: " + mismatches + "; propagation failures: " + propagationFailures + ".");
                if (shownOld > 0)
                    Console.WriteLine(shownOld + " value(s) are stored as written but GRAccess shows them in another type, because their objects "
                        + "still derive from an older package of their template (see the NOTE lines and result.csv).");
                Console.WriteLine("Details: " + runFolder);

                if (failedObject != null)
                    return ExitCodes.Error;
                return mismatches + propagationFailures + shownOld > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // Decides for every row whether it changes, is skipped or cannot be done. Changes nothing.
        static List<ObjectGroup> Plan(GalaxySession session, SqlConnection database, List<SetRow> rows)
        {
            List<ObjectGroup> groups = new List<ObjectGroup>();
            Dictionary<string, ObjectGroup> byTagname = new Dictionary<string, ObjectGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (SetRow row in rows)
            {
                ObjectGroup group;
                if (!byTagname.TryGetValue(row.Object, out group))
                {
                    group = new ObjectGroup();
                    group.Tagname = row.Object;
                    byTagname.Add(row.Object, group);
                    groups.Add(group);
                }
                group.Rows.Add(row);
            }

            int read = 0;
            foreach (ObjectGroup group in groups)
            {
                group.Object = session.FindObject(group.Tagname);
                string problem = null;
                if (group.Object == null)
                    problem = group.IsTemplate ? "template not found" : "instance not found (template names start with $)";
                else if (group.Object.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    problem = "checked out" + CheckedOutBy(group.Object) + "; check it in or undo the check-out first";
                if (problem != null)
                {
                    foreach (SetRow row in group.Rows)
                        SetError(row, problem);
                    continue;
                }

                IAttributes attributes = group.Object.Attributes;
                Dictionary<string, string> stored = StoredValues.Read(database, group.Tagname);
                foreach (SetRow row in group.Rows)
                    PlanRow(group, attributes, stored, row);

                // A template cannot be checked in while an object derived from it is checked out
                if (group.IsTemplate && group.HasChanges)
                {
                    int templates = 0, instances = 0, deployed = 0;
                    foreach (KeyValuePair<IgObject, bool> descendant in FindDescendants(session.Galaxy, group.Tagname))
                    {
                        IgObject obj = descendant.Key;
                        if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                        {
                            foreach (SetRow row in group.Rows.FindAll(r => r.Action == SetRow.Change))
                                SetError(row, obj.Tagname + " is checked out" + CheckedOutBy(obj) + "; the template cannot be checked in while an object derived from it is");
                            break;
                        }
                        if (descendant.Value)
                        {
                            templates++;
                        }
                        else
                        {
                            instances++;
                            if (((IInstance)obj).DeploymentStatus != EDeploymentStatus.notDeployed)
                                deployed++;
                        }
                    }
                    group.Reach = templates + " derived template(s) and " + instances + " instance(s), " + deployed + " of them deployed";
                }
                if (++read % 25 == 0)
                    ReleaseComObjects();
            }
            return groups;
        }

        static void PlanRow(ObjectGroup group, IAttributes attributes, Dictionary<string, string> stored, SetRow row)
        {
            IAttribute attribute = attributes[row.Attribute];
            if (attribute == null)
            {
                SetError(row, "attribute not found");
                return;
            }
            string note = SymbolNote(attributes, row.Attribute);
            MxPropertyLockedEnum locked = attribute.Locked;

            if (row.Set == SetRow.Lock)
            {
                row.Current = LockText(locked);
                row.Target = row.To;
                if (!group.IsTemplate)
                    SetError(row, "locks are set in templates; an instance has the lock its template gives it");
                else if (locked == MxPropertyLockedEnum.MxLockedInParent)
                    SetError(row, "locked in a parent template; change the lock there");
                else if (locked != MxPropertyLockedEnum.MxLockedInMe && locked != MxPropertyLockedEnum.MxUnLocked)
                    SetError(row, "has no lock that can be changed (" + locked + ")");
                else
                    Decide(row, locked == row.TargetLock, note);
                return;
            }

            row.Current = CurrentValue(attribute, stored, row.Attribute);
            if (locked == MxPropertyLockedEnum.MxLockedInParent)
            {
                SetError(row, "locked in a parent template; set it there");
                return;
            }
            if (attribute.UpperBoundDim1 >= 0)
            {
                SetError(row, "is an array; arrays are not supported");
                return;
            }
            string problem = Parse(attribute.DataType, row.To, out row.TargetValue);
            if (problem != null)
            {
                SetError(row, problem);
                return;
            }
            row.DataType = attribute.DataType;
            row.Target = Describe(row.DataType, row.TargetValue);
            row.LockedInObject = locked == MxPropertyLockedEnum.MxLockedInMe;
            Decide(row, row.Current == row.Target, note);
        }

        static void Decide(SetRow row, bool alreadySet, string note)
        {
            row.Action = alreadySet ? SetRow.Skip : SetRow.Change;
            List<string> details = new List<string>();
            if (alreadySet)
                details.Add("already set");
            if (note.Length > 0)
                details.Add(note);
            row.Detail = string.Join("; ", details.ToArray());
        }

        static void SetError(SetRow row, string problem)
        {
            row.Action = SetRow.Error;
            row.Detail = problem;
        }

        // A symbol shares the object's attribute namespace (see docs\GRAccess-Notes.md): a name that starts with a
        // symbol's name is one of that symbol's properties, even when a UDA has the same name as the symbol
        static string SymbolNote(IAttributes attributes, string name)
        {
            for (int dot = name.IndexOf('.'); dot > 0; dot = name.IndexOf('.', dot + 1))
            {
                string prefix = name.Substring(0, dot);
                if (attributes[prefix + "._VisualElementDefinition"] != null)
                    return "a property of the symbol " + prefix;
            }
            return "";
        }

        // The value in the file, for the attribute's data type. Returns null when it fits, otherwise the problem.
        static string Parse(MxDataType type, string text, out object value)
        {
            value = null;
            switch (type)
            {
                case MxDataType.MxBoolean:
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                        value = true;
                    else if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                        value = false;
                    else
                        return "a Boolean takes true or false, not '" + text + "'";
                    return null;
                case MxDataType.MxInteger:
                    int integer;
                    if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer))
                        return "an Integer takes a whole number, not '" + text + "'";
                    value = integer;
                    return null;
                case MxDataType.MxFloat:
                    float single;
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out single) || float.IsNaN(single) || float.IsInfinity(single))
                        return "a Float takes a number with . as decimal separator, not '" + text + "'";
                    value = single;
                    return null;
                case MxDataType.MxDouble:
                    double number;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                        return "a Double takes a number with . as decimal separator, not '" + text + "'";
                    value = number;
                    return null;
                case MxDataType.MxString:
                case MxDataType.MxInternationalizedString:
                    value = text;
                    return null;
                default:
                    return "is " + TypeName(type) + "; the tool writes Boolean, Integer, Float, Double, String and InternationalizedString values";
            }
        }

        static MxValue ToMxValue(MxDataType type, object target)
        {
            MxValue value = new MxValueClass();
            switch (type)
            {
                case MxDataType.MxBoolean:
                    value.PutBoolean((bool)target);
                    break;
                case MxDataType.MxInteger:
                    value.PutInteger((int)target);
                    break;
                case MxDataType.MxFloat:
                    value.PutFloat((float)target);
                    break;
                case MxDataType.MxDouble:
                    value.PutDouble((double)target);
                    break;
                case MxDataType.MxInternationalizedString:
                    value.PutInternationalString(1033, (string)target);  // PutString is rejected for these
                    break;
                default:
                    value.PutString((string)target);
                    break;
            }
            return value;
        }

        // The attribute's value for the plan. A value the object stores with another data type than the attribute's (left
        // by a data type change) is shown as stored, because GRAccess may show it converted; it then never equals a
        // target, so the row is written.
        static string CurrentValue(IAttribute attribute, Dictionary<string, string> stored, string name)
        {
            string shown = Describe(attribute.value);
            string hex;
            if (!stored.TryGetValue(name, out hex))
                return shown;
            MxDataType type = StoredValues.TypeOf(hex);
            if (type == attribute.DataType || type == MxDataType.MxNoData || !Enum.IsDefined(typeof(MxDataType), type))
                return shown;
            object value = StoredValues.ValueOf(hex);
            string text = value != null ? Describe(type, value) : TypeName(type) + " value";
            return text == shown ? text : text + " (stored; GRAccess shows " + shown + ")";
        }

        // The value with its own data type, which can differ from the attribute's (e.g. "Boolean false" on an Integer)
        static string Describe(MxValue value)
        {
            MxDataType type = value.GetDataType();
            switch (type)
            {
                case MxDataType.MxBoolean:
                    return Describe(type, value.GetBoolean());
                case MxDataType.MxInteger:
                    return Describe(type, value.GetInteger());
                case MxDataType.MxFloat:
                    return Describe(type, value.GetFloat());
                case MxDataType.MxDouble:
                    return Describe(type, value.GetDouble());
                case MxDataType.MxString:
                case MxDataType.MxInternationalizedString:
                case MxDataType.MxBigString:
                    return Describe(type, value.GetString());
                case MxDataType.MxNoData:
                    return "No Data";
                default:
                    return TypeName(type) + " " + value.GetString();
            }
        }

        static string Describe(MxDataType type, object value)
        {
            switch (type)
            {
                case MxDataType.MxBoolean:
                    return "Boolean " + ((bool)value ? "true" : "false");
                case MxDataType.MxInteger:
                    return "Integer " + ((int)value).ToString(CultureInfo.InvariantCulture);
                case MxDataType.MxFloat:
                    return "Float " + ((float)value).ToString("R", CultureInfo.InvariantCulture);
                case MxDataType.MxDouble:
                    return "Double " + ((double)value).ToString("R", CultureInfo.InvariantCulture);
                default:
                    return TypeName(type) + " \"" + value + "\"";
            }
        }

        static string TypeName(MxDataType type)
        {
            return type.ToString().Substring(2);  // MxInteger -> Integer
        }

        static string LockText(MxPropertyLockedEnum locked)
        {
            switch (locked)
            {
                case MxPropertyLockedEnum.MxLockedInMe:
                    return SetRow.Locked;
                case MxPropertyLockedEnum.MxUnLocked:
                    return SetRow.Unlocked;
                case MxPropertyLockedEnum.MxLockedInParent:
                    return "locked in parent";
                default:
                    return locked.ToString();
            }
        }

        // Null when the attribute has what the row asks for, otherwise what it has instead
        static string Problem(IAttribute attribute, SetRow row)
        {
            if (attribute == null)
                return "is missing";
            string now = row.Set == SetRow.Lock ? LockText(attribute.Locked) : Describe(attribute.value);
            return now == row.Target ? null : "is " + now + ", not " + row.Target;
        }

        static void PrintPlan(string galaxyName, List<ObjectGroup> groups)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (ObjectGroup group in groups)
            {
                foreach (SetRow row in group.Rows)
                {
                    string line = "  " + row.Action.ToUpperInvariant().PadRight(7) + row.FullName + (row.Set == SetRow.Lock ? " (lock)" : "");
                    if (row.Action == SetRow.Change)
                        line += ": " + row.Current + " -> " + row.Target;
                    if (row.Detail.Length > 0)
                        line += row.Action == SetRow.Error ? ": " + row.Detail : " (" + row.Detail + ")";
                    Console.WriteLine(line);
                }
                if (group.Reach.Length > 0)
                    Console.WriteLine("         " + group.Tagname + " reaches " + group.Reach);
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

        // Reads the object again, checks it out, makes the rows' changes, reads them back and checks it in. Any failure
        // undoes the check-out and is rethrown. Every row is written, even when the object seems to have the value by
        // now: after a template's check-in, GRAccess shows a derived object's own value converted to the attribute's
        // new data type although the stored value keeps its old type (see docs\GRAccess-Notes.md).
        static void ApplyObject(GalaxySession session, ObjectGroup group, List<SetRow> changing, string inputFileName)
        {
            Console.WriteLine();
            Console.WriteLine("Changing " + group.Tagname + "...");
            IgObject obj = session.FindObject(group.Tagname);
            if (obj == null)
                throw new GRAccessException(group.Tagname + " was not found any more.");
            if (obj.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                throw new GRAccessException(group.Tagname + " is checked out" + CheckedOutBy(obj) + ".");
            group.Object = obj;

            bool checkedOut = false;
            bool checkedIn = false;
            try
            {
                ObjectEdits.CheckOut(obj);
                checkedOut = true;
                changed = true;

                IAttributes attributes = obj.Attributes;
                foreach (SetRow row in changing)
                {
                    IAttribute attribute = attributes[row.Attribute];
                    if (attribute == null)
                        throw new GRAccessException(row.FullName + " does not exist.");
                    if (row.Set == SetRow.Lock)
                    {
                        attribute.SetLocked(row.TargetLock);
                        GRAccessException.ThrowIfFailed(attribute.CommandResult, (row.TargetLock == MxPropertyLockedEnum.MxUnLocked ? "Unlock " : "Lock ") + row.FullName);
                        Console.WriteLine("  " + row.Attribute + " lock: " + row.Current + " -> " + row.Target);
                    }
                    else
                    {
                        MxValue value = ToMxValue(row.DataType, row.TargetValue);
                        attribute.SetValue(value);
                        GRAccessException.ThrowIfFailed(attribute.CommandResult, "Set " + row.FullName);
                        Console.WriteLine("  " + row.Attribute + ": " + row.Current + " -> " + row.Target);
                    }
                }

                // Read everything back from a new attribute collection before saving
                IAttributes check = obj.Attributes;
                foreach (SetRow row in changing)
                {
                    string problem = Problem(check[row.Attribute], row);
                    if (problem != null)
                        throw new GRAccessException(row.FullName + " " + problem + " after the change, so nothing is saved.");
                    if (row.Set == SetRow.Value && group.IsTemplate)
                        row.LockedInObject = check[row.Attribute].Locked == MxPropertyLockedEnum.MxLockedInMe;
                }

                ObjectEdits.SaveAndCheckIn(obj, "SetAttributes: " + changing.Count + " change(s) from " + inputFileName);
                checkedIn = true;
                foreach (SetRow row in changing)
                    row.Result = "Changed";
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
                    if (ObjectEdits.UndoCheckOut(obj))
                        Console.Error.WriteLine("  The check-out was undone, so " + group.Tagname + " is unchanged.");
                    else
                        Console.Error.WriteLine("  WARNING: " + group.Tagname + " is still checked out. Undo the check-out in the IDE.");
                }
            }
        }

        // Reads the object again after its check-in, and its stored values from the galaxy database, and compares every
        // changed row. Returns the number that differ; shownOld counts the values stored as written that GRAccess shows
        // converted to another type.
        static int Verify(GalaxySession session, SqlConnection database, ObjectGroup group, List<SetRow> changed, ref int shownOld)
        {
            IgObject fresh = session.FindObject(group.Tagname);
            IAttributes attributes = fresh.Attributes;
            Dictionary<string, string> stored = StoredValues.Read(database, group.Tagname);
            int mismatches = 0;
            foreach (SetRow row in changed)
            {
                IAttribute attribute = attributes[row.Attribute];
                string problem = Problem(attribute, row);
                string hex = null;
                bool storedHere = row.Set == SetRow.Value && stored.TryGetValue(row.Attribute, out hex);
                if (storedHere && StoredValues.TypeOf(hex) != row.DataType)
                {
                    problem = "is stored as " + TypeName(StoredValues.TypeOf(hex)) + " in the galaxy database";
                }
                else if (problem != null && attribute != null && storedHere && StoredValues.ValueOf(hex) != null
                    && Describe(row.DataType, StoredValues.ValueOf(hex)) == row.Target)
                {
                    // Stored as written. GRAccess shows an object's own value converted to the type of the value in the
                    // template package the object derives from, which can be one from before the template's latest
                    // change (see docs\GRAccess-Notes.md)
                    row.Result = "Done";
                    row.Detail = "stored as " + row.Target + ", but GRAccess shows " + Describe(attribute.value)
                        + ": the object still derives from an older package of its template; a change the template locks (for "
                        + "example locking and unlocking the attribute there) moves it to the current one";
                    shownOld++;
                    Console.WriteLine("  NOTE: " + row.FullName + " is " + row.Detail);
                    continue;
                }
                if (problem == null)
                {
                    row.Result = "Done";
                    continue;
                }
                row.Result = "Mismatch";
                row.Detail = problem + " after the check-in";
                mismatches++;
                Console.Error.WriteLine("  VERIFY FAILED: " + row.FullName + " " + row.Detail);
            }
            if (!group.IsTemplate && ((IInstance)fresh).DeploymentStatus == EDeploymentStatus.deployedWithPendingChanges)
                Console.WriteLine("  " + group.Tagname + " is deployed and now has pending changes (this tool never deploys).");
            return mismatches;
        }

        // Checks every template and instance derived from a changed template for the rows that must reach them: a lock
        // (derived objects show it locked in the parent, or unlocked) and a value the template locks. Values the template
        // does not lock are not checked: a derived object that stores its own value keeps it, and GRAccess cannot tell
        // which ones do, since it shows an own value converted to the attribute's new data type (see
        // docs\GRAccess-Notes.md). Returns the number of derived objects that did not get a change. The objects are
        // queried again: ones read before the check-in still show what they had then.
        static int CheckPropagation(IGalaxy galaxy, ObjectGroup group, List<SetRow> changed, List<string[]> lines)
        {
            List<SetRow> done = changed.FindAll(r => r.Result == "Done");
            List<SetRow> reaching = done.FindAll(r => r.Set == SetRow.Lock || r.LockedInObject);
            int unlockedValues = done.Count - reaching.Count;
            if (unlockedValues > 0)
                Console.WriteLine("  " + unlockedValues + " value(s) set on attributes the template does not lock: derived objects that store their own value keep it.");
            List<KeyValuePair<IgObject, bool>> descendants = FindDescendants(galaxy, group.Tagname);
            if (reaching.Count == 0 || descendants.Count == 0)
                return 0;
            int templates = descendants.FindAll(d => d.Value).Count;
            Console.WriteLine("  Checking " + templates + " derived template(s) and " + (descendants.Count - templates) + " instance(s)...");

            int failures = 0;
            int pendingDeploy = 0;
            int read = 0;
            foreach (KeyValuePair<IgObject, bool> descendant in descendants)
            {
                IgObject obj = descendant.Key;
                IAttributes attributes = obj.Attributes;
                List<string> problems = new List<string>();
                foreach (SetRow row in reaching)
                {
                    IAttribute attribute = attributes[row.Attribute];
                    string now = "";
                    bool ok = false;
                    if (attribute != null && row.Set == SetRow.Lock)
                    {
                        MxPropertyLockedEnum locked = attribute.Locked;
                        now = LockText(locked);
                        ok = row.TargetLock == MxPropertyLockedEnum.MxUnLocked
                            ? locked == MxPropertyLockedEnum.MxUnLocked
                            : locked == MxPropertyLockedEnum.MxLockedInParent;
                    }
                    else if (attribute != null)
                    {
                        now = Describe(attribute.value);
                        ok = now == row.Target;
                    }
                    if (!ok)
                        problems.Add(row.Attribute + (row.Set == SetRow.Lock ? " lock" : "") + " is " + (attribute == null ? "missing" : now));
                    lines.Add(new[] { group.Tagname, row.Attribute, row.Set, obj.Tagname, descendant.Value ? "Template" : "Instance", now, ok ? "OK" : "Failed" });
                }
                if (problems.Count > 0)
                {
                    failures++;
                    Console.Error.WriteLine("    " + obj.Tagname + ": " + string.Join("; ", problems.ToArray()));
                }
                if (!descendant.Value && ((IInstance)obj).DeploymentStatus == EDeploymentStatus.deployedWithPendingChanges)
                    pendingDeploy++;
                if (++read % 25 == 0)
                    ReleaseComObjects();
            }

            if (failures == 0)
                Console.WriteLine("  All " + descendants.Count + " derived object(s) have the locks and locked values.");
            else
                Console.Error.WriteLine("  PROPAGATION FAILED for " + failures + " of " + descendants.Count + " derived object(s)."
                    + " A derived template that shows a lock the template removed has taken the lock over: unlock it there too.");
            if (pendingDeploy > 0)
                Console.WriteLine("  " + pendingDeploy + " deployed instance(s) now have pending changes and need to be redeployed (this tool never deploys).");
            return failures;
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

        static string CheckedOutBy(IgObject obj)
        {
            return string.IsNullOrEmpty(obj.checkedOutBy) ? "" : " by " + obj.checkedOutBy;
        }

        static string Message(Exception ex)
        {
            return ex is GRAccessException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        static void WriteRows(string path, List<SetRow> rows)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                csv.WriteRow("row", "object", "attribute", "set", "to", "action", "current", "target", "result", "detail");
                foreach (SetRow row in rows)
                    csv.WriteRow(row.Row, row.Object, row.Attribute, row.Set, row.To, row.Action, row.Current, row.Target, row.Result, row.Detail);
            }
        }

        // Releases the GRAccess objects no longer used (every read of Attributes builds the whole collection in
        // GRAccessApp.exe), but only until the first change: a garbage collection between writing a String value and
        // logging out leaves the process stuck when it exits (see docs\GRAccess-Notes.md)
        static bool changed;

        static void ReleaseComObjects()
        {
            if (changed)
                return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
