using System;
using System.Collections.Generic;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One row of the input file and what happens to it
    class SetRow
    {
        // What the row sets
        public const string Value = "value";
        public const string Lock = "lock";

        // Actions
        public const string Change = "change";
        public const string Skip = "skip";
        public const string Error = "error";

        public const string Locked = "locked";
        public const string Unlocked = "unlocked";

        static readonly string[] Columns = { "object", "attribute", "set", "to" };

        public int Row;                  // line in the input file
        public string Object;
        public string Attribute;
        public string Set;               // Value or Lock
        public string To;                // as written in the file
        public string Action = "";
        public string Current = "";      // the value or lock state before the change
        public string Target = "";       // what the row asks for, in the same form as Current
        public string Result = "";
        public string Detail = "";

        // Worked out by the plan
        public MxDataType DataType;      // value rows: the attribute's data type, which the value is written with
        public object TargetValue;       // value rows: bool, int, float, double or string
        public MxPropertyLockedEnum TargetLock;  // lock rows
        public bool LockedInObject;      // value rows: the attribute is locked in this template, so derived objects get the value

        public string FullName
        {
            get { return Object + "." + Attribute; }
        }

        // Reads and validates the whole file. Every problem found is added to the list; the rows can only be used
        // when the list stays empty.
        public static List<SetRow> ReadFile(string path, List<string> problems)
        {
            List<SetRow> rows = new List<SetRow>();
            List<string[]> records = CsvReader.ReadFile(path);
            if (records.Count == 0)
            {
                problems.Add("The file is empty.");
                return rows;
            }

            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records[0].Length; i++)
            {
                string column = records[0][i].Trim().TrimStart('﻿');
                if (Array.FindIndex(Columns, known => string.Equals(known, column, StringComparison.OrdinalIgnoreCase)) < 0)
                    problems.Add("Unknown column '" + column + "'. The columns are: " + string.Join(", ", Columns) + ".");
                else if (columns.ContainsKey(column))
                    problems.Add("Column '" + column + "' appears more than once.");
                else
                    columns.Add(column, i);
            }
            foreach (string required in Columns)
            {
                if (!columns.ContainsKey(required))
                    problems.Add("Missing column '" + required + "'.");
            }
            if (problems.Count > 0)
                return rows;

            Dictionary<string, int> firstRowOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < records.Count; r++)
            {
                string[] record = records[r];
                if (Array.TrueForAll(record, field => field.Trim().Length == 0))
                    continue;

                SetRow row = new SetRow();
                row.Row = r + 1;
                string where = "Row " + row.Row + ": ";
                row.Object = Field(record, columns, "object");
                row.Attribute = Field(record, columns, "attribute");
                row.Set = Field(record, columns, "set").ToLowerInvariant();
                row.To = Field(record, columns, "to");

                if (row.Object.Length == 0)
                    problems.Add(where + "object is empty.");
                if (row.Attribute.Length == 0)
                    problems.Add(where + "attribute is empty.");
                if (row.Set == Lock)
                {
                    row.To = row.To.ToLowerInvariant();
                    if (row.To == Locked)
                        row.TargetLock = MxPropertyLockedEnum.MxLockedInMe;
                    else if (row.To == Unlocked)
                        row.TargetLock = MxPropertyLockedEnum.MxUnLocked;
                    else
                        problems.Add(where + "a lock row takes locked or unlocked in 'to', not '" + row.To + "'.");
                }
                else if (row.Set != Value)
                {
                    problems.Add(where + "set must be value or lock, not '" + row.Set + "'.");
                }

                string key = row.FullName + " (" + row.Set + ")";
                int firstRow;
                if (firstRowOf.TryGetValue(key, out firstRow))
                    problems.Add(where + key + " is already on row " + firstRow + ".");
                else if (row.Object.Length > 0 && row.Attribute.Length > 0)
                    firstRowOf.Add(key, row.Row);

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
}
