using System.Collections.Concurrent;

namespace Transactium.EcrService
{
    sealed class EcrRequests:IDisposable
    {
        readonly ConcurrentQueue<EcrRequest> requests=new();
        readonly SemaphoreSlim newRequest = new(0);//no maximum, initialized 0, represents count of pending requests

        public void Dispose()
        {
            newRequest.Dispose();
            foreach (var r in requests)
            {
                r.Dispose();
            }
            requests.Clear();
        }
        public void AddRequest(EcrRequest req)
        {
            requests.Enqueue(req);
            newRequest.Release();
        }

        public async Task<EcrRequest> WaitRequest(CancellationToken ct)
        {
            while (true)
            {
                await newRequest.WaitAsync(ct);
                if (requests.TryDequeue(out var req))
                {
                    await req.WaitLock(CancellationToken.None);
                    if (req.Removed)
                        req.Dispose();
                    else
                    {
                        req.ReleaseLock();
                        return req;
                    }
                }
            }
        }

        public static async Task RemoveRequest(EcrRequest req)
        {
            await req.WaitLock(CancellationToken.None);
            req.Removed = true;
            req.ReleaseLock();
            if (req.Exception != null)
                req.Dispose();
        }
    }
}