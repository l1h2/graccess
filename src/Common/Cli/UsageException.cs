using System;

namespace GRAccessTools.Common
{
    /// <summary>The tool was called with invalid arguments. ToolRunner prints the message and the usage text.</summary>
    public class UsageException : Exception
    {
        public UsageException(string message) : base(message)
        {
        }
    }
}
