namespace GRAccessTools.Common
{
    /// <summary>Process exit codes shared by all tools, so scripts can react to the outcome.</summary>
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int Error = 1;
        public const int Usage = 2;

        /// <summary>The tool finished but found problems: Evaluate findings, or objects a BulkChange tool could not change.</summary>
        public const int CompletedWithFindings = 3;
    }
}
