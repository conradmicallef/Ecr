// See https://aka.ms/new-console-template for more information
namespace Transactium.EcrService
{
    sealed class EcrRequest:IDisposable
    {
        public string Request;
        public string Response;
        public DateTime RequestedTime;
        public DateTime? RepliedTime;
        public TimeSpan WaitFor;
        public SemaphoreSlim Semaphore = new(0, 1);
        public bool Removed;
        public Exception Exception;

        public void Dispose()
        {
            Semaphore.Dispose();
        }
    }
}