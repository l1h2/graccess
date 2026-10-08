using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One row of the input file and what happens to it
    class RenameRow
    {
        public const string Rename = "rename";
        public const string Skip = "skip";
        public const string Error = "error";

        static readonly string[] Columns = { "template", "name", "newName" };
        // The same rule as AddTemplateAttributes for new names. Existing names are left to the galaxy: some EMGALAXY
        // UDAs are all digits or contain a dot (e.g. ANTI_RCY_TIME.REM).
        static readonly Regex ValidNewName = new Regex(@"^[A-Za-z0-9_]+$");

        public int Row;                  // line in the input file
        public string Template;          // always with the leading $
        public string Name;
        public string NewName;
        public string Action = "";
        public string Previous = "";     // the attribute's definition before the rename
        public List<string> Extensions = new List<string>();  // extension types on the attribute in the template
        public List<string> References = new List<string>();  // graphics, scripts and objects that use the old name
        public string Result = "";
        public string Detail = "";

        // Reads and validates the whole file. Every problem found is added to the list; the rows can only be used
        // when the list stays empty.
        public static List<RenameRow> ReadFile(string path, List<string> problems)
        {
            List<RenameRow> rows = new List<RenameRow>();
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

            Dictionary<string, int> rowOfName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> rowOfNewName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < records.Count; r++)
            {
                string[] record = records[r];
                if (Array.TrueForAll(record, field => field.Trim().Length == 0))
                    continue;

                RenameRow row = new RenameRow();
                row.Row = r + 1;
                string where = "Row " + row.Row + ": ";
                string template = Field(record, columns, "template");
                row.Template = template.StartsWith("$", StringComparison.Ordinal) ? template : "$" + template;
                row.Name = Field(record, columns, "name");
                row.NewName = Field(record, columns, "newName");

                if (template.Length == 0)
                    problems.Add(where + "template is empty.");
                if (row.Name.Length == 0)
                    problems.Add(where + "name is empty.");
                if (row.NewName.Length == 0)
                    problems.Add(where + "newName is empty.");
                else if (!ValidNewName.IsMatch(row.NewName))
                    problems.Add(where + "newName '" + row.NewName + "' may only contain letters, digits and _.");
                bool sameName = row.Name.Length > 0 && string.Equals(row.Name, row.NewName, StringComparison.OrdinalIgnoreCase);
                if (sameName)
                    problems.Add(where + "the new name only differs in letter case (or not at all), and attribute names ignore case.");

                // Each attribute is renamed once, each new name is used once, and a new name is never another row's
                // old name: the renames must not depend on the order they are done in
                string nameKey = row.Template + "." + row.Name;
                string newKey = row.Template + "." + row.NewName;
                int other;
                if (rowOfName.TryGetValue(nameKey, out other))
                    problems.Add(where + nameKey + " is already renamed on row " + other + ".");
                if (rowOfNewName.TryGetValue(newKey, out other))
                    problems.Add(where + "row " + other + " already renames an attribute to " + newKey + ".");
                if (row.Name.Length > 0 && !rowOfName.ContainsKey(nameKey))
                    rowOfName.Add(nameKey, row.Row);
                if (row.NewName.Length > 0 && !rowOfNewName.ContainsKey(newKey))
                    rowOfNewName.Add(newKey, row.Row);

                rows.Add(row);
            }

            foreach (RenameRow row in rows)
            {
                int other;
                if (rowOfName.TryGetValue(row.Template + "." + row.NewName, out other) && other != row.Row)
                    problems.Add("Row " + row.Row + ": the new name " + row.NewName + " is the old name on row " + other + "; rename in two runs instead.");
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
