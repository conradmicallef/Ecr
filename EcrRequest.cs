namespace Transactium.EcrService
{
    sealed class EcrRequest:IDisposable
    {
        public string Request;
        public string Response;
        public DateTime RequestedTime;
        public DateTime? RepliedTime;
        public TimeSpan WaitFor;
        readonly SemaphoreSlim Semaphore = new(0, 1); //initialised to zero, maximum 1, awaiting a response
        readonly SemaphoreSlim Lock = new(1, 1);
        public bool Removed;
        public Exception Exception;

        public Task WaitReply(CancellationToken ct) => Semaphore.WaitAsync(ct);
        public void SignalReply() => Semaphore.Release();
        public Task WaitLock(CancellationToken ct) => Lock.WaitAsync(ct);
        public void ReleaseLock() => Lock.Release();

        public void Dispose()
        {
            Semaphore.Dispose();
            Lock.Dispose();
        }

    }
}