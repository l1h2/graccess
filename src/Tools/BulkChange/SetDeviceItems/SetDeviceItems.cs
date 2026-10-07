// SetDeviceItems: sets the device items (item name -> item reference) of device integration object scan groups from a
// CSV file, so that every scan group in the file holds exactly the file's items. Scan groups not in the file are left
// alone. Follows the checklist in src\Tools\BulkChange\README.md: dry run unless -Apply, typed confirmation, stop at
// the first failure, never deploy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One device item: the name objects use (Alias in the XML) and the address sent to the server (Name in the XML)
    class DeviceItem
    {
        public string Name;
        public string Reference;
        public int Row;  // input file row, 0 for items read from the galaxy

        public DeviceItem(string name, string reference, int row)
        {
            Name = name;
            Reference = reference;
            Row = row;
        }
    }

    // One line of the plan and the result: what happens to one item of one scan group
    class ItemChange
    {
        public const string Keep = "keep";
        public const string Recase = "recase";      // same item and reference, the name changes only in letter case
        public const string Change = "change";      // the reference changes (and the name's case, if it differs)
        public const string Add = "add";
        public const string Remove = "remove";

        public string Device;
        public string ScanGroup;
        public string Item;            // the name it will have (or has, for removals)
        public string Action;
        public string CurrentName = "";  // only when it differs from Item (case)
        public string Current = "";      // current reference
        public string Reference = "";    // new reference
        public int Row;
        public string Result = "";
        public string Detail = "";
    }

    // Everything the plan found for one scan group of one device
    class ScanGroupPlan
    {
        public string ScanGroup;
        public int ClearRow;  // input row that asks for no items at all
        public string CurrentXml = "";
        public List<DeviceItem> Current = new List<DeviceItem>();
        public List<DeviceItem> Target = new List<DeviceItem>();
        public List<ItemChange> Changes = new List<ItemChange>();

        public bool HasChanges
        {
            get { return Changes.Exists(c => c.Action != ItemChange.Keep); }
        }
    }

    class DevicePlan
    {
        public string Tagname;
        public IgObject Device;
        public string Problem;
        public List<ScanGroupPlan> ScanGroups = new List<ScanGroupPlan>();

        public bool HasChanges
        {
            get { return ScanGroups.Exists(s => s.HasChanges); }
        }
    }

    class SetDeviceItems
    {
        const string Usage =
            "Compares the device items (item name -> item reference) of device integration object scan groups with a CSV\r\n" +
            "file, and fills empty scan groups with the file's items. Without -Apply this is a dry run that reports, per scan\r\n" +
            "group, which items are the same, missing, different (reference or letter case) or not in the file - use it to\r\n" +
            "check a device against an agreed list. With -Apply it writes only scan groups that have no items yet: GRAccess\r\n" +
            "cannot replace the items of a scan group that has some (see docs\\GRAccess-Notes.md), so change those in the IDE.\r\n" +
            "Scan groups not in the file are left alone.\r\n" +
            "\r\n" +
            "Usage: SetDeviceItems.exe -f <file.csv> [-Apply]\r\n" +
            "\r\n" +
            "  -f <file.csv>   Input file with the columns device, scanGroup, item, reference\r\n" +
            "  -Apply          Make the changes; you are asked to type the galaxy name to confirm\r\n" +
            "\r\n" +
            "Columns:\r\n" +
            "  device      The device integration object (e.g. BACLite_DDESuiteLink)\r\n" +
            "  scanGroup   The scan group (topic), e.g. NAE6_Normal\r\n" +
            "  item        The item name objects use, e.g. ECCP_CH1.CHS_T\r\n" +
            "  reference   The item reference sent to the server, e.g. AI:3052894:PRESENT-VALUE\r\n" +
            "A scan group's only row may leave item and reference empty to say it has no items; that is accepted for a scan\r\n" +
            "group that is already empty (GRAccess cannot remove all of a scan group's items: do that in the IDE).\r\n" +
            "\r\n" +
            "Item names are compared without case, like all ArchestrA names. With -Apply the device is checked out once, each\r\n" +
            "empty scan group's AliasDatabase is written (the object updates ItemList), the result is read back before saving\r\n" +
            "(any difference undoes the check-out), and the device is checked in. The device is never deployed. Each write run\r\n" +
            "has so far left its process stuck while exiting (it keeps its exe folder locked until the server restarts).";

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
            List<DevicePlan> devices = ReadFile(inputPath, problems);
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

                Plan(session, devices);
                PrintPlan(galaxyName, devices);

                int errors = devices.FindAll(d => d.Problem != null).Count;
                bool changes = devices.Exists(d => d.Problem == null && d.HasChanges);
                if (!apply || errors > 0 || !changes)
                {
                    string planFile = Path.Combine(runFolder, "plan.csv");
                    WriteChanges(planFile, devices, false);
                    ReleaseComObjects();
                    Console.WriteLine();
                    if (errors > 0)
                        Console.Error.WriteLine(errors + " device(s) cannot be changed, so nothing was changed. Fix them and run again.");
                    else if (!changes)
                        Console.WriteLine("Nothing to change.");
                    else
                        Console.WriteLine("DRY RUN - nothing was changed. Run again with -Apply to make these changes.");
                    Console.WriteLine("Plan: " + planFile);
                    return errors > 0 ? ExitCodes.Error : ExitCodes.Success;
                }

                // GRAccess can only fill an empty scan group exactly (see the note in ApplyDevice), so -Apply refuses
                // to change a scan group that has items; replace those in the IDE (delete the items, import a file)
                List<string> populated = new List<string>();
                foreach (DevicePlan device in devices)
                {
                    foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                    {
                        if (scanGroup.HasChanges && scanGroup.Current.Count > 0)
                            populated.Add(device.Tagname + "." + scanGroup.ScanGroup + " (" + scanGroup.Current.Count + " items)");
                    }
                }
                if (populated.Count > 0)
                {
                    WriteChanges(Path.Combine(runFolder, "plan.csv"), devices, false);
                    ReleaseComObjects();
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Nothing was changed: these scan groups already have items, and GRAccess cannot replace them reliably:");
                    foreach (string name in populated)
                        Console.Error.WriteLine("  " + name);
                    Console.Error.WriteLine("Delete their items in the IDE and import the new ones there; run this tool without -Apply afterwards to check.");
                    return ExitCodes.Error;
                }

                if (!ConfirmGalaxy(galaxyName))
                {
                    Console.Error.WriteLine("The galaxy name did not match, so nothing was changed.");
                    return ExitCodes.Error;
                }

                string failedDevice = null;
                int mismatches = 0;
                foreach (DevicePlan device in devices)
                {
                    if (!device.HasChanges)
                        continue;
                    if (failedDevice != null)
                    {
                        SetResult(device, "Not applied", "stopped after the failure on " + failedDevice);
                        continue;
                    }
                    foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                    {
                        if (scanGroup.HasChanges)
                            File.WriteAllText(Path.Combine(runFolder, "previous_" + device.Tagname + "_" + scanGroup.ScanGroup + ".xml"),
                                scanGroup.CurrentXml, new UTF8Encoding(false));
                    }
                    try
                    {
                        ApplyDevice(device, Path.GetFileName(inputPath));
                    }
                    catch (Exception ex)
                    {
                        failedDevice = device.Tagname;
                        SetResult(device, "Failed", Message(ex));
                        continue;
                    }
                    mismatches += Verify(session, device);
                }

                string resultFile = Path.Combine(runFolder, "result.csv");
                WriteChanges(resultFile, devices, true);
                ReleaseComObjects();
                Console.WriteLine();
                if (failedDevice == null)
                    Console.WriteLine(mismatches == 0
                        ? "Every changed scan group now holds exactly the file's items."
                        : "VERIFY FAILED: " + mismatches + " item(s) differ from the file after the check-in (see result.csv).");
                Console.WriteLine("Details: " + runFolder);
                if (failedDevice != null)
                    return ExitCodes.Error;
                return mismatches > 0 ? ExitCodes.CompletedWithFindings : ExitCodes.Success;
            }
        }

        // Reads the input file into one plan per device and scan group, in file order
        static List<DevicePlan> ReadFile(string path, List<string> problems)
        {
            List<DevicePlan> devices = new List<DevicePlan>();
            List<string[]> records = CsvReader.ReadFile(path);
            if (records.Count == 0)
            {
                problems.Add("the file is empty");
                return devices;
            }

            string[] header = records[0];
            int device = Column(header, "device", problems), scanGroup = Column(header, "scanGroup", problems);
            int item = Column(header, "item", problems), reference = Column(header, "reference", problems);
            if (problems.Count > 0)
                return devices;

            Dictionary<string, DevicePlan> byDevice = new Dictionary<string, DevicePlan>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ScanGroupPlan> byScanGroup = new Dictionary<string, ScanGroupPlan>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < records.Count; i++)
            {
                string[] record = records[i];
                if (record.Length == 1 && record[0].Trim().Length == 0)
                    continue;
                int row = i + 1;
                string deviceName = Field(record, device), scanGroupName = Field(record, scanGroup);
                string itemName = Field(record, item), itemReference = Field(record, reference);
                bool clear = itemName.Length == 0 && itemReference.Length == 0;  // the scan group ends up without items
                if (deviceName.Length == 0 || scanGroupName.Length == 0 || (!clear && (itemName.Length == 0 || itemReference.Length == 0)))
                {
                    problems.Add("row " + row + ": device and scanGroup are required, and item and reference go together "
                        + "(leave both empty, in the scan group's only row, to remove all its items)");
                    continue;
                }
                string key = deviceName + "." + scanGroupName + "." + itemName;
                int first;
                if (seen.TryGetValue(key, out first))
                {
                    problems.Add("row " + row + ": " + key + " is also in row " + first + " (item names are compared without case)");
                    continue;
                }
                seen.Add(key, row);

                DevicePlan devicePlan;
                if (!byDevice.TryGetValue(deviceName, out devicePlan))
                {
                    devicePlan = new DevicePlan();
                    devicePlan.Tagname = deviceName;
                    byDevice.Add(deviceName, devicePlan);
                    devices.Add(devicePlan);
                }
                ScanGroupPlan scanGroupPlan;
                if (!byScanGroup.TryGetValue(deviceName + "." + scanGroupName, out scanGroupPlan))
                {
                    scanGroupPlan = new ScanGroupPlan();
                    scanGroupPlan.ScanGroup = scanGroupName;
                    byScanGroup.Add(deviceName + "." + scanGroupName, scanGroupPlan);
                    devicePlan.ScanGroups.Add(scanGroupPlan);
                }
                if (clear)
                    scanGroupPlan.ClearRow = row;
                else
                    scanGroupPlan.Target.Add(new DeviceItem(itemName, itemReference, row));
            }
            foreach (KeyValuePair<string, ScanGroupPlan> scanGroupPlan in byScanGroup)
            {
                if (scanGroupPlan.Value.ClearRow > 0 && scanGroupPlan.Value.Target.Count > 0)
                    problems.Add("row " + scanGroupPlan.Value.ClearRow + ": the row without item and reference (remove all items) "
                        + "must be the only row for " + scanGroupPlan.Key);
            }
            return devices;
        }

        static int Column(string[] header, string name, List<string> problems)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i].Trim().TrimStart('﻿'), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            problems.Add("the header has no column " + name);
            return -1;
        }

        static string Field(string[] record, int index)
        {
            return index < record.Length ? record[index].Trim() : "";
        }

        // Works out every change. Changes nothing.
        static void Plan(GalaxySession session, List<DevicePlan> devices)
        {
            foreach (DevicePlan device in devices)
            {
                device.Device = session.FindObject(device.Tagname);
                if (device.Device == null)
                    device.Problem = "device not found";
                else if (device.Device.Attributes["ScanGroupList"] == null)
                    device.Problem = "not a device integration object (it has no scan groups)";
                else if (device.Device.CheckoutStatus != ECheckoutStatus.notCheckedOut)
                    device.Problem = "checked out" + (string.IsNullOrEmpty(device.Device.checkedOutBy) ? "" : " by " + device.Device.checkedOutBy) + "; check it in or undo the check-out first";
                if (device.Problem != null)
                    continue;

                foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                {
                    IAttribute aliasDatabase = device.Device.Attributes[scanGroup.ScanGroup + ".AliasDatabase"];
                    if (aliasDatabase == null)
                    {
                        device.Problem = "has no scan group " + scanGroup.ScanGroup + " (no " + scanGroup.ScanGroup + ".AliasDatabase)";
                        break;
                    }
                    scanGroup.CurrentXml = aliasDatabase.value.GetString() ?? "";
                    scanGroup.Current = ParseItems(scanGroup.CurrentXml);
                    if (scanGroup.ClearRow > 0 && scanGroup.Current.Count > 0)
                    {
                        device.Problem = "row " + scanGroup.ClearRow + " asks to remove all " + scanGroup.Current.Count + " item(s) of " + scanGroup.ScanGroup
                            + ", which GRAccess cannot do (an empty ItemList is ignored); remove them in the IDE";
                        break;
                    }
                    PlanScanGroup(device.Tagname, scanGroup);
                }
            }
        }

        static void PlanScanGroup(string deviceName, ScanGroupPlan scanGroup)
        {
            Dictionary<string, DeviceItem> current = new Dictionary<string, DeviceItem>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceItem item in scanGroup.Current)
            {
                if (!current.ContainsKey(item.Name))
                    current.Add(item.Name, item);
            }
            HashSet<string> targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceItem target in scanGroup.Target)
            {
                targetNames.Add(target.Name);
                ItemChange change = NewChange(deviceName, scanGroup.ScanGroup, target.Name, target.Row);
                change.Reference = target.Reference;
                DeviceItem existing;
                if (!current.TryGetValue(target.Name, out existing))
                {
                    change.Action = ItemChange.Add;
                }
                else
                {
                    change.Current = existing.Reference;
                    if (existing.Name != target.Name)
                        change.CurrentName = existing.Name;
                    if (existing.Reference != target.Reference)
                        change.Action = ItemChange.Change;
                    else
                        change.Action = existing.Name == target.Name ? ItemChange.Keep : ItemChange.Recase;
                }
                scanGroup.Changes.Add(change);
            }
            foreach (DeviceItem item in scanGroup.Current)
            {
                if (targetNames.Contains(item.Name))
                    continue;
                ItemChange change = NewChange(deviceName, scanGroup.ScanGroup, item.Name, 0);
                change.Action = ItemChange.Remove;
                change.Current = item.Reference;
                scanGroup.Changes.Add(change);
            }
        }

        static ItemChange NewChange(string device, string scanGroup, string item, int row)
        {
            ItemChange change = new ItemChange();
            change.Device = device;
            change.ScanGroup = scanGroup;
            change.Item = item;
            change.Row = row;
            return change;
        }

        // The items in ScanGroup.AliasDatabase: <ItemsList><Item Name="AV:3002575:PRESENT-VALUE" Alias="LSC3_CH3.X"/></ItemsList>,
        // where Alias is the item name and Name the reference. An item without an alias is used by its reference.
        static List<DeviceItem> ParseItems(string xml)
        {
            List<DeviceItem> items = new List<DeviceItem>();
            if (xml.Trim().Length == 0)
                return items;
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            foreach (XmlElement element in document.SelectNodes("/ItemsList/Item"))
            {
                string reference = element.GetAttribute("Name");
                string name = element.GetAttribute("Alias");
                items.Add(new DeviceItem(name.Length > 0 ? name : reference, reference, 0));
            }
            return items;
        }

        // The items as the IDE writes them: sorted by name without case (lower-case ordinal), no whitespace
        static string ItemsXml(List<DeviceItem> items)
        {
            StringBuilder xml = new StringBuilder("<ItemsList>");
            foreach (DeviceItem item in Sorted(items))
                xml.Append("<Item Name=\"").Append(SecurityElement.Escape(item.Reference)).Append("\" Alias=\"").Append(SecurityElement.Escape(item.Name)).Append("\"/>");
            return xml.Append("</ItemsList>").ToString();
        }

        static List<DeviceItem> Sorted(List<DeviceItem> items)
        {
            List<DeviceItem> sorted = new List<DeviceItem>(items);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));
            return sorted;
        }

        static void PrintPlan(string galaxyName, List<DevicePlan> devices)
        {
            Console.WriteLine("Plan for " + galaxyName + ":");
            foreach (DevicePlan device in devices)
            {
                if (device.Problem != null)
                {
                    Console.WriteLine("  ERROR  " + device.Tagname + ": " + device.Problem);
                    continue;
                }
                foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                {
                    Dictionary<string, int> counts = new Dictionary<string, int>();
                    foreach (ItemChange change in scanGroup.Changes)
                    {
                        int n;
                        counts.TryGetValue(change.Action, out n);
                        counts[change.Action] = n + 1;
                    }
                    List<string> parts = new List<string>();
                    foreach (string action in new[] { ItemChange.Keep, ItemChange.Add, ItemChange.Change, ItemChange.Recase, ItemChange.Remove })
                    {
                        int n;
                        if (counts.TryGetValue(action, out n))
                            parts.Add(action + " " + n);
                    }
                    Console.WriteLine("  " + (scanGroup.HasChanges ? "CHANGE " : "SAME   ") + device.Tagname + "." + scanGroup.ScanGroup + ": "
                        + scanGroup.Current.Count + " item(s) now, " + scanGroup.Target.Count + " after (" + string.Join(", ", parts.ToArray()) + ")");
                }
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

        // Checks out the device, writes every changed scan group and checks it in. Any failure undoes the check-out and
        // is rethrown.
        static void ApplyDevice(DevicePlan device, string inputFileName)
        {
            IgObject obj = device.Device;
            Console.WriteLine();
            Console.WriteLine("Changing " + device.Tagname + "...");
            bool checkedOut = false;
            bool checkedIn = false;
            try
            {
                ObjectEdits.CheckOut(obj);
                checkedOut = true;
                foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                {
                    if (!scanGroup.HasChanges)
                        continue;
                    string name = device.Tagname + "." + scanGroup.ScanGroup;
                    // The scan group keeps its items in step between AliasDatabase (names and references) and ItemList
                    // (names). Setting AliasDatabase on an empty scan group gives exactly its items and updates ItemList.
                    // On a scan group with items it only adds and updates, never removes; a written ItemList is applied
                    // on Save, rebuilding the items with every reference "<undefined>"; an empty ItemList value is
                    // ignored (all seen on TESTGALAXY, 6 Oct 2026). Execute therefore only lets empty scan groups through.
                    if (scanGroup.Current.Count > 0)
                        throw new GRAccessException(name + " has items; GRAccess cannot replace them reliably.");
                    SetString(obj, scanGroup.ScanGroup + ".AliasDatabase", ItemsXml(scanGroup.Target));
                    string problem = InSessionProblem(obj, scanGroup);
                    if (problem != null)
                        throw new GRAccessException(name + " did not take the items as expected (" + problem + "); nothing is saved.");
                    Console.WriteLine("  " + name + ": " + scanGroup.Target.Count + " item(s), AliasDatabase and ItemList as expected");
                }
                ObjectEdits.SaveAndCheckIn(obj, "SetDeviceItems: " + inputFileName);
                checkedIn = true;
                Console.WriteLine("  Checked in " + device.Tagname);
                IInstance instance = obj as IInstance;
                if (instance != null && instance.DeploymentStatus != EDeploymentStatus.notDeployed)
                    Console.WriteLine("  " + device.Tagname + " is deployed: redeploy it for the new items to take effect (this tool never deploys).");
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
                        Console.Error.WriteLine("  The check-out was undone, so " + device.Tagname + " is unchanged.");
                    else
                        Console.Error.WriteLine("  WARNING: " + device.Tagname + " is still checked out. Undo the check-out in the IDE.");
                }
            }
        }

        static void SetString(IgObject obj, string attributeName, string text)
        {
            IAttribute attribute = obj.Attributes[attributeName];
            MxValue value = new MxValueClass();
            value.PutString(text);
            attribute.SetValue(value);
            GRAccessException.ThrowIfFailed(attribute.CommandResult, "Set " + obj.Tagname + "." + attributeName);
            Marshal.ReleaseComObject(value);
        }

        static List<string> ReadItemList(IgObject obj, string scanGroup)
        {
            List<string> names = new List<string>();
            IAttribute attribute = obj.Attributes[scanGroup + ".ItemList"];
            if (attribute == null)
                return names;
            MxValue value = attribute.value;
            int size = 0;
            try
            {
                value.GetDimensionSize(out size);
            }
            catch (Exception)
            {
                return names;  // no data (an empty list)
            }
            for (int i = 1; i <= size; i++)  // array elements are 1-based
            {
                MxValue element = new MxValueClass();
                value.GetElement(i, element);
                names.Add(element.GetString());
                Marshal.ReleaseComObject(element);  // thousands of these otherwise stall the process at exit
            }
            Marshal.ReleaseComObject(value);
            return names;
        }

        // Reads the checked-out object back: AliasDatabase must hold exactly the target items (names with the file's
        // spelling, references) and ItemList exactly their names. Null when it does.
        static string InSessionProblem(IgObject obj, ScanGroupPlan scanGroup)
        {
            List<DeviceItem> stored = ParseItems(obj.Attributes[scanGroup.ScanGroup + ".AliasDatabase"].value.GetString() ?? "");
            if (stored.Count != scanGroup.Target.Count)
                return "AliasDatabase has " + stored.Count + " item(s), expected " + scanGroup.Target.Count;
            Dictionary<string, string> expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DeviceItem item in scanGroup.Target)
                expected[item.Name] = item.Reference;
            foreach (DeviceItem item in stored)
            {
                string reference;
                if (!expected.TryGetValue(item.Name, out reference))
                    return "unexpected item " + item.Name;
                if (reference != item.Reference)
                    return item.Name + " has reference " + item.Reference + ", expected " + reference;
            }
            if (!SameNames(ReadItemList(obj, scanGroup.ScanGroup), scanGroup.Target))
                return "ItemList does not list exactly the item names";
            return null;
        }

        // The same names with the same spelling, in any order
        static bool SameNames(List<string> names, List<DeviceItem> items)
        {
            if (names.Count != items.Count)
                return false;
            HashSet<string> expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceItem item in items)
                expected.Add(item.Name);
            foreach (string name in names)
            {
                if (!expected.Remove(name))
                    return false;
            }
            return expected.Count == 0;
        }

        // Reads the device again and compares every changed scan group with the file. Returns the number of items that differ.
        static int Verify(GalaxySession session, DevicePlan device)
        {
            IgObject fresh = session.FindObject(device.Tagname);
            int mismatches = 0;
            foreach (ScanGroupPlan scanGroup in device.ScanGroups)
            {
                if (!scanGroup.HasChanges)
                    continue;
                Dictionary<string, DeviceItem> now = new Dictionary<string, DeviceItem>(StringComparer.OrdinalIgnoreCase);
                List<DeviceItem> stored = ParseItems(fresh.Attributes[scanGroup.ScanGroup + ".AliasDatabase"].value.GetString() ?? "");
                foreach (DeviceItem item in stored)
                    now[item.Name] = item;
                bool itemListOk = fresh.Attributes[scanGroup.ScanGroup + ".ItemList"] == null || SameNames(ReadItemList(fresh, scanGroup.ScanGroup), scanGroup.Target);

                foreach (ItemChange change in scanGroup.Changes)
                {
                    DeviceItem item;
                    bool found = now.TryGetValue(change.Item, out item);
                    if (change.Action == ItemChange.Remove)
                        Mark(change, !found, found ? "still there" : "", ref mismatches);
                    else
                        Mark(change, found && item.Name == change.Item && item.Reference == change.Reference,
                            !found ? "missing" : item.Name != change.Item ? "named " + item.Name : item.Reference != change.Reference ? "reference " + item.Reference : "",
                            ref mismatches);
                }
                if (stored.Count != scanGroup.Target.Count)
                    Console.Error.WriteLine("  " + device.Tagname + "." + scanGroup.ScanGroup + " has " + stored.Count + " item(s), expected " + scanGroup.Target.Count);
                if (!itemListOk)
                {
                    mismatches++;
                    Console.Error.WriteLine("  " + device.Tagname + "." + scanGroup.ScanGroup + ".ItemList does not list exactly the item names");
                }
            }
            return mismatches;
        }

        static void Mark(ItemChange change, bool ok, string problem, ref int mismatches)
        {
            if (ok)
            {
                change.Result = "Done";
            }
            else
            {
                change.Result = "Mismatch";
                change.Detail = problem;
                mismatches++;
            }
        }

        static void SetResult(DevicePlan device, string result, string detail)
        {
            foreach (ScanGroupPlan scanGroup in device.ScanGroups)
            {
                foreach (ItemChange change in scanGroup.Changes)
                {
                    if (change.Action == ItemChange.Keep)
                        continue;
                    change.Result = result;
                    change.Detail = detail;
                }
            }
        }

        static void WriteChanges(string path, List<DevicePlan> devices, bool withResult)
        {
            using (CsvWriter csv = new CsvWriter(path))
            {
                if (withResult)
                    csv.WriteRow("row", "device", "scanGroup", "item", "action", "currentName", "current", "reference", "result", "detail");
                else
                    csv.WriteRow("row", "device", "scanGroup", "item", "action", "currentName", "current", "reference", "detail");
                foreach (DevicePlan device in devices)
                {
                    if (device.Problem != null)
                    {
                        csv.WriteRow("", device.Tagname, "", "", "error", "", "", "", device.Problem);
                        continue;
                    }
                    foreach (ScanGroupPlan scanGroup in device.ScanGroups)
                    {
                        foreach (ItemChange c in scanGroup.Changes)
                        {
                            object row = c.Row > 0 ? (object)c.Row : "";
                            if (withResult)
                                csv.WriteRow(row, c.Device, c.ScanGroup, c.Item, c.Action, c.CurrentName, c.Current, c.Reference, c.Action == ItemChange.Keep ? "Unchanged" : c.Result, c.Detail);
                            else
                                csv.WriteRow(row, c.Device, c.ScanGroup, c.Item, c.Action, c.CurrentName, c.Current, c.Reference, c.Detail);
                        }
                    }
                }
            }
        }

        static string Message(Exception ex)
        {
            return ex is GRAccessException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        // Releases the GRAccess objects still held, on the STA thread and before the session logs out; left to the
        // finalizer at exit, they kept two test runs from ending (TESTGALAXY, 6 Oct 2026)
        static void ReleaseComObjects()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
