using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ArchestrA.GRAccess;
using GRAccessTools.Common;

namespace GRAccessTools.BulkChange
{
    // One row of the input file, validated, plus what the tool decided to do with it and what happened.
    class AttributeRow
    {
        public const string Add = "Add";
        public const string Update = "Update";
        public const string Skip = "Skip";
        public const string Error = "Error";

        static readonly string[] RequiredColumns = { "template", "name", "dataType" };
        static readonly string[] OptionalColumns = { "Description", "IO", "label" };

        static readonly Dictionary<string, MxDataType> DataTypes = new Dictionary<string, MxDataType>(StringComparer.OrdinalIgnoreCase)
        {
            { "Boolean", MxDataType.MxBoolean },
            { "Integer", MxDataType.MxInteger },
            { "Float", MxDataType.MxFloat },
            { "Double", MxDataType.MxDouble },
            { "String", MxDataType.MxString },
            { "Time", MxDataType.MxTime },
            { "ElapsedTime", MxDataType.MxElapsedTime },
            { "InternationalizedString", MxDataType.MxInternationalizedString }
        };

        static readonly Dictionary<string, string> IoExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "I", "inputextension" },
            { "O", "outputextension" },
            { "IO", "inputoutputextension" }
        };

        static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]+$");

        public int Row;                 // record number in the file; the header is row 1
        public string Template;         // tagname, always starting with $
        public string Name;
        public string Description;      // "" when not given
        public string Io;               // as written: "", I, O or IO
        public string IoExtensionType;  // null when the attribute has no I/O
        public MxDataType DataType;
        public string DataTypeName;     // Boolean, Integer, ...; null when invalid
        public string Label;            // as written
        public string OffMessage;       // Boolean labels; null when not given
        public string OnMessage;
        public string EngUnits;         // engineering units of numeric attributes; null when not given

        public string Action = "";      // Add, Update, Skip or Error
        public string Previous = "";    // for Update: the attribute's definition before the change
        public string Result = "";      // after -Apply: Added, Updated, Failed or Not applied
        public string Detail = "";

        public bool IsChange
        {
            get { return Action == Add || Action == Update; }
        }

        // inputextension -> I, outputextension -> O, inputoutputextension -> IO; null for other extension types
        public static string IoLetters(string extensionType)
        {
            foreach (KeyValuePair<string, string> io in IoExtensions)
            {
                if (string.Equals(io.Value, extensionType, StringComparison.OrdinalIgnoreCase))
                    return io.Key;
            }
            return null;
        }

        // e.g. Boolean, IO=O, labels Fail/Pass, "Pump running"
        public string Summary()
        {
            List<string> parts = new List<string> { DataTypeName };
            if (Io.Length > 0)
                parts.Add("IO=" + Io.ToUpperInvariant());
            if (OffMessage != null)
                parts.Add("labels " + OffMessage + "/" + OnMessage);
            if (EngUnits != null)
                parts.Add("units " + EngUnits);
            if (Description.Length > 0)
                parts.Add('"' + Description + '"');
            return string.Join(", ", parts.ToArray());
        }

        // Reads and validates the whole file. Every problem found is added to the list; the rows can only be used
        // when the list stays empty.
        public static List<AttributeRow> ReadFile(string path, List<string> problems)
        {
            List<AttributeRow> rows = new List<AttributeRow>();
            List<string[]> records = CsvReader.ReadFile(path);
            if (records.Count == 0)
            {
                problems.Add("The file is empty.");
                return rows;
            }

            List<string> knownColumns = new List<string>(RequiredColumns);
            knownColumns.AddRange(OptionalColumns);
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records[0].Length; i++)
            {
                string column = records[0][i].Trim();
                if (!knownColumns.Exists(known => string.Equals(known, column, StringComparison.OrdinalIgnoreCase)))
                    problems.Add("Unknown column '" + column + "'. The columns are: " + string.Join(", ", knownColumns.ToArray()) + ".");
                else if (columns.ContainsKey(column))
                    problems.Add("Column '" + column + "' appears more than once.");
                else
                    columns.Add(column, i);
            }
            foreach (string required in RequiredColumns)
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

                AttributeRow row = new AttributeRow();
                row.Row = r + 1;
                string where = "Row " + row.Row + ": ";

                string template = Field(record, columns, "template");
                row.Template = template.StartsWith("$", StringComparison.Ordinal) ? template : "$" + template;
                row.Name = Field(record, columns, "name");
                row.Description = Field(record, columns, "Description");
                row.Io = Field(record, columns, "IO");
                row.Label = Field(record, columns, "label");
                string dataType = Field(record, columns, "dataType");

                if (template.Length == 0)
                    problems.Add(where + "template is empty.");

                if (row.Name.Length == 0)
                    problems.Add(where + "name is empty.");
                else if (!ValidName.IsMatch(row.Name))
                    problems.Add(where + "name '" + row.Name + "' may only contain letters, digits and _.");

                string typeName = dataType.StartsWith("Mx", StringComparison.OrdinalIgnoreCase) ? dataType.Substring(2) : dataType;
                MxDataType type;
                if (dataType.EndsWith("]", StringComparison.Ordinal))
                {
                    problems.Add(where + "arrays are not supported (dataType '" + dataType + "').");
                }
                else if (!DataTypes.TryGetValue(typeName, out type))
                {
                    problems.Add(where + "dataType '" + dataType + "' must be one of " + string.Join(", ", new List<string>(DataTypes.Keys).ToArray()) + ".");
                }
                else
                {
                    row.DataType = type;
                    row.DataTypeName = type.ToString().Substring(2);
                }

                string extensionType = null;
                if (row.Io.Length > 0 && !IoExtensions.TryGetValue(row.Io, out extensionType))
                    problems.Add(where + "IO '" + row.Io + "' must be I, O, IO or empty.");
                row.IoExtensionType = extensionType;

                if (row.Label.Length > 0 && row.DataTypeName != null)
                {
                    if (row.DataType == MxDataType.MxBoolean)
                    {
                        string[] labels = row.Label.Split('/');
                        if (labels.Length != 2 || labels[0].Trim().Length == 0 || labels[1].Trim().Length == 0)
                        {
                            problems.Add(where + "label '" + row.Label + "' of a Boolean must be Off/On, e.g. Fail/Pass.");
                        }
                        else
                        {
                            row.OffMessage = labels[0].Trim();
                            row.OnMessage = labels[1].Trim();
                        }
                    }
                    else if (row.DataType == MxDataType.MxInteger || row.DataType == MxDataType.MxFloat || row.DataType == MxDataType.MxDouble)
                    {
                        row.EngUnits = row.Label;
                    }
                    else
                    {
                        problems.Add(where + "label only applies to Boolean (Off/On labels) and Integer, Float or Double (engineering units).");
                    }
                }

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
