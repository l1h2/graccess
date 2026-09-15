using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Writes a CSV file as UTF-8 with BOM (so Excel shows non-ASCII text correctly), quoting values
    /// that contain commas, quotes or line breaks. Booleans are written as true/false and null as empty.
    /// </summary>
    public sealed class CsvWriter : IDisposable
    {
        private static readonly char[] CharsNeedingQuotes = { ',', '"', '\r', '\n' };

        private readonly StreamWriter writer;

        public CsvWriter(string path)
        {
            writer = new StreamWriter(path, false, new UTF8Encoding(true));
        }

        public void WriteRow(params object[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    writer.Write(',');
                writer.Write(Format(values[i]));
            }
            writer.Write("\r\n");
        }

        public void Dispose()
        {
            writer.Dispose();
        }

        private static string Format(object value)
        {
            if (value == null)
                return "";

            string text = value is bool
                ? ((bool)value ? "true" : "false")
                : Convert.ToString(value, CultureInfo.InvariantCulture);

            if (text.IndexOfAny(CharsNeedingQuotes) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
