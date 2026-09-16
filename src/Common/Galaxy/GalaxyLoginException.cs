namespace GRAccessTools.Common
{
    /// <summary>
    /// The galaxy was found but rejected the login. Separate from other GRAccess failures (node unreachable,
    /// galaxy not found) so callers can add login-specific hints.
    /// </summary>
    public class GalaxyLoginException : GRAccessException
    {
        public GalaxyLoginException(string message) : base(message)
        {
        }
    }
}
