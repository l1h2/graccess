using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Reads CSV files: comma separated, UTF-8 with or without BOM, with quoted fields that may contain commas,
    /// quotes ("") and line breaks.
    /// </summary>
    public static class CsvReader
    {
        /// <summary>All records in the file, including the header, as arrays of fields.</summary>
        public static List<string[]> ReadFile(string path)
        {
            return Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }

        public static List<string[]> Parse(string text)
        {
            List<string[]> records = new List<string[]>();
            List<string> fields = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"')
                        field.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"')
                        field.Append(text[++i]);
                    else
                        quoted = false;
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    fields.Add(field.ToString());
                    field.Length = 0;
                    records.Add(fields.ToArray());
                    fields.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                records.Add(fields.ToArray());
            }
            return records;
        }
    }
}
