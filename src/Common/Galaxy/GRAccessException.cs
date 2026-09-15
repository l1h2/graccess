using System;
using ArchestrA.GRAccess;

namespace GRAccessTools.Common
{
    /// <summary>
    /// A GRAccess operation failed. GRAccess does not throw when a call fails; it sets CommandResult
    /// instead, so call ThrowIfFailed after every GRAccess call.
    /// </summary>
    public class GRAccessException : Exception
    {
        public GRAccessException(string message) : base(message)
        {
        }

        public static void ThrowIfFailed(ICommandResult result, string step)
        {
            if (result == null)
                throw new GRAccessException(step + " failed: GRAccess returned no command result.");
            if (result.Successful)
                return;

            string detail = result.Text;
            if (!string.IsNullOrEmpty(result.CustomMessage))
                detail += " - " + result.CustomMessage;
            throw new GRAccessException(step + " failed: " + detail);
        }
    }
}
