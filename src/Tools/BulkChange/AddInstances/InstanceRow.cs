using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One row of the input file, validated, plus what the tool decided to do with it and what happened.
    class InstanceRow
    {
        public const string Add = "Add";
        public const string Error = "Error";

        static readonly string[] Columns = { "template", "name", "area", "io" };
        static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]+$");
        static readonly string[] ReservedNames = { "Me", "MyContainer", "MyArea", "MyHost", "MyPlatform", "MyEngine", "System" };
        const int MaxNameLength = 32;

        public int Row;           // record number in the file; the header is row 1
        public string Template;   // tagname, always starting with $
        public string Name;
        public string Area;
        public string Io;         // Device.ScanGroup
        public string Device;
        public string ScanGroup;

        public string Action = "";  // Add or Error
        public string Detail = "";  // why a row cannot be done
        public readonly List<string> Notes = new List<string>();  // warnings that do not stop the row

        public string Result = "";  // after -Apply: Created, Failed or Not applied
        public string ResultDetail = "";
        public readonly List<ObjectState> Objects = new List<ObjectState>();  // the created instance and its contained objects

        // Reads and validates the whole file. Every problem found is added to the list; the rows can only be used when
        // the list stays empty.
        public static List<InstanceRow> ReadFile(string path, List<string> problems)
        {
            List<InstanceRow> rows = new List<InstanceRow>();
            List<string[]> records = CsvReader.ReadFile(path);
            if (records.Count == 0)
            {
                problems.Add("The file is empty.");
                return rows;
            }

            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records[0].Length; i++)
            {
                string column = records[0][i].Trim();
                if (Array.FindIndex(Columns, known => string.Equals(known, column, StringComparison.OrdinalIgnoreCase)) < 0)
                    problems.Add("Unknown column '" + column + "'. The columns are: " + string.Join(", ", Columns) + ".");
                else if (columns.ContainsKey(column))
                    problems.Add("Column '" + column + "' appears more than once.");
                else
                    columns.Add(column, i);
            }
            foreach (string column in Columns)
            {
                if (!columns.ContainsKey(column))
                    problems.Add("Missing column '" + column + "'.");
            }
            if (problems.Count > 0)
                return rows;

            Dictionary<string, int> firstRowOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < records.Count; r++)
            {
                string[] record = records[r];
                if (Array.TrueForAll(record, field => field.Trim().Length == 0))
                    continue;

                InstanceRow row = new InstanceRow();
                row.Row = r + 1;
                string where = "Row " + row.Row + ": ";

                string template = Field(record, columns, "template");
                row.Template = template.Length == 0 || template.StartsWith("$", StringComparison.Ordinal) ? template : "$" + template;
                row.Name = Field(record, columns, "name");
                row.Area = Field(record, columns, "area");
                row.Io = Field(record, columns, "io");

                if (template.Length == 0)
                    problems.Add(where + "template is empty.");

                if (row.Name.Length == 0)
                    problems.Add(where + "name is empty.");
                else if (!ValidName.IsMatch(row.Name))
                    problems.Add(where + "name '" + row.Name + "' may only contain letters, digits and _.");
                else if (row.Name.Length > MaxNameLength)
                    problems.Add(where + "name '" + row.Name + "' is longer than " + MaxNameLength + " characters.");
                else if (Array.FindIndex(ReservedNames, reserved => string.Equals(reserved, row.Name, StringComparison.OrdinalIgnoreCase)) >= 0)
                    problems.Add(where + "name '" + row.Name + "' is reserved.");

                if (row.Area.Length == 0)
                    problems.Add(where + "area is empty.");
                else if (row.Area.StartsWith("$", StringComparison.Ordinal))
                    problems.Add(where + "area '" + row.Area + "' is a template; give an area instance.");

                string[] io = row.Io.Split('.');
                if (io.Length != 2 || io[0].Trim().Length == 0 || io[1].Trim().Length == 0)
                {
                    problems.Add(where + "io '" + row.Io + "' must be Device.ScanGroup, e.g. BACLite_DDESuiteLink.NAE6_Normal.");
                }
                else if (io[0].Trim().StartsWith("$", StringComparison.Ordinal))
                {
                    problems.Add(where + "io device '" + io[0].Trim() + "' is a template; give an I/O device instance.");
                }
                else
                {
                    row.Device = io[0].Trim();
                    row.ScanGroup = io[1].Trim();
                    row.Io = row.Device + "." + row.ScanGroup;
                }

                int firstRow;
                if (row.Name.Length > 0 && firstRowOf.TryGetValue(row.Name, out firstRow))
                    problems.Add(where + "name " + row.Name + " is already on row " + firstRow + ".");
                else if (row.Name.Length > 0)
                    firstRowOf.Add(row.Name, row.Row);

                rows.Add(row);
            }

            if (rows.Count == 0 && problems.Count == 0)
                problems.Add("The file has no rows.");
            return rows;
        }

        static string Field(string[] record, Dictionary<string, int> columns, string column)
        {
            int index;
            if (!columns.TryGetValue(column, out index) || index >= record.Length)
                return "";
            return record[index].Trim();
        }
    }

    // A created object as the galaxy database shows it after the row was applied
    class ObjectState
    {
        public const string Ok = "OK";
        public const string NoIo = "No I/O";
        public const string AssignIoInIde = "Assign I/O in the IDE";
        public const string AreaNotSet = "Area not set";

        public int Id;
        public string Tagname;
        public string HierarchicalName;
        public string Area;       // null when none
        public string Io;         // Device.ScanGroup, or null when the object is not assigned to a scan group
        public int AutoReferences;  // I/O references set to ---Auto---, which take their path from the scan group
        public string Status = "";
        public string IoSource = "";  // how it got the requested scan group: "area", "assign call", or "" when it did not
        public string IoAfterArea = "";  // the scan group it was on right after the area was assigned ("none" when none)
        public string Detail = "";    // why the I/O could not be assigned
    }
}
