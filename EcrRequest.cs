namespace Transactium.EcrService
{
    sealed class EcrRequest:IDisposable
    {
        public string Request { get; init; }
        public string Response { get; private set; }
        public DateTime RequestedTime { get; init; }
        public DateTime? RepliedTime { get; private set; }
        public TimeSpan WaitFor { get; init; }
        readonly SemaphoreSlim Semaphore = new(0, 1); //initialised to zero, maximum 1, awaiting a response
        readonly SemaphoreSlim Lock = new(1, 1);
        public bool Removed { get; private set; }
        public Exception Exception { get; private set; }

        public Task WaitReply(CancellationToken ct) => Semaphore.WaitAsync(ct);
        public void SignalReply() => Semaphore.Release();
        public Task WaitLock(CancellationToken ct) => Lock.WaitAsync(ct);
        public void ReleaseLock() => Lock.Release();

        public void Dispose()
        {
            Semaphore.Dispose();
            Lock.Dispose();
        }

        internal void SetResponse(string resp)
        {
            LockExec(() =>
            {
                if (RepliedTime.HasValue)
                    throw new InvalidOperationException("Already Replied");
                Response = resp;
                RepliedTime = DateTime.UtcNow;
            });
        }

        internal void SetException(Exception e)
        {
            LockExec(() =>
            {
                if (Exception != null)
                    throw new InvalidOperationException("Exception already set");
                Exception = e;
            });
        }

        // Internal function to lock and unlock properly
        void LockExec(Action act)
        {
            Lock.Wait();
            try { act(); }
            finally { Lock.Release(); }
        }
        internal void Remove()
        {
            LockExec(() => {
                if (Removed)
                    throw new InvalidOperationException("Removed already");
                Removed = true;
            });
        }
    }
}