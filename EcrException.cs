namespace Transactium.EcrService
{
    public class EcrException : Exception
    {
        public EcrException(string message) : base(message)
        {
        }

        public EcrException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}