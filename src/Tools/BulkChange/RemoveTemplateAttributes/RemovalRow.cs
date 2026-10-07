using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One row of the input file and what happens to it
    class RemovalRow
    {
        public const string Remove = "remove";
        public const string Skip = "skip";
        public const string Error = "error";

        static readonly string[] Columns = { "template", "name" };
        static readonly Regex ValidName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        public int Row;                  // line in the input file
        public string Template;          // always with the leading $
        public string Name;
        public string Action = "";
        public string Previous = "";     // the attribute's definition before the removal
        public List<string> Extensions = new List<string>();  // extension types on the attribute in the template
        public int References;           // references found in the galaxy database
        public string Result = "";
        public string Detail = "";

        // Reads and validates the whole file. Every problem found is added to the list; the rows can only be used
        // when the list stays empty.
        public static List<RemovalRow> ReadFile(string path, List<string> problems)
        {
            List<RemovalRow> rows = new List<RemovalRow>();
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

                RemovalRow row = new RemovalRow();
                row.Row = r + 1;
                string where = "Row " + row.Row + ": ";
                string template = Field(record, columns, "template");
                row.Template = template.StartsWith("$", StringComparison.Ordinal) ? template : "$" + template;
                row.Name = Field(record, columns, "name");

                if (template.Length == 0)
                    problems.Add(where + "template is empty.");
                if (row.Name.Length == 0)
                    problems.Add(where + "name is empty.");
                else if (!ValidName.IsMatch(row.Name))
                    problems.Add(where + "name '" + row.Name + "' may only contain letters, digits and _.");

                string key = row.Template + "." + row.Name;
                int firstRow;
                if (firstRowOf.TryGetValue(key, out firstRow))
                    problems.Add(where + key + " is already on row " + firstRow + ".");
                else if (row.Name.Length > 0)
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
