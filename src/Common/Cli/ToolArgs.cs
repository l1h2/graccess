using System;
using System.Collections.Generic;

namespace GRAccessTools.Common
{
    /// <summary>
    /// Parses PowerShell-style named options: "-Name value", or "-Switch" with no value.
    /// Names are case-insensitive, "--Name" also works, and unknown options are rejected so typos
    /// fail instead of silently using a default. A value cannot start with '-'.
    /// </summary>
    public class ToolArgs
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ToolArgs(string[] args, IEnumerable<string> allowedOptions)
        {
            HashSet<string> allowed = new HashSet<string>(allowedOptions, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Length; i++)
            {
                if (!IsOptionName(args[i]))
                    throw new UsageException("Unexpected argument '" + args[i] + "'. Options are written as -Name value.");

                string name = args[i].TrimStart('-');
                if (!allowed.Contains(name))
                    throw new UsageException("Unknown option '" + args[i] + "'.");
                if (values.ContainsKey(name))
                    throw new UsageException("Option -" + name + " was given more than once.");

                // The next token is the value, unless it is another option (then this one is a switch)
                string value = null;
                if (i + 1 < args.Length && !IsOptionName(args[i + 1]))
                {
                    value = args[i + 1];
                    i++;
                }
                values.Add(name, value);
            }
        }

        /// <summary>True if the option was given, with or without a value.</summary>
        public bool Has(string name)
        {
            return values.ContainsKey(name);
        }

        /// <summary>The option's value, or defaultValue if the option was not given.</summary>
        public string Get(string name, string defaultValue)
        {
            string value;
            if (!values.TryGetValue(name, out value))
                return defaultValue;
            if (value == null)
                throw new UsageException("Option -" + name + " needs a value.");
            return value;
        }

        public string Require(string name)
        {
            string value = Get(name, null);
            if (value == null)
                throw new UsageException("Missing required option -" + name + ".");
            return value;
        }

        private static bool IsOptionName(string token)
        {
            return token.Length > 1 && token[0] == '-';
        }
    }
}
