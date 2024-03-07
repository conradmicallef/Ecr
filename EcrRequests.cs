// See https://aka.ms/new-console-template for more information
using System.Collections.Concurrent;

namespace Transactium.EcrService
{
    sealed class EcrRequests:IDisposable
    {
        readonly ConcurrentQueue<EcrRequest> requests=new();
        readonly SemaphoreSlim newRequest = new(0,9999);

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
                    if (req.Removed)
                        req.Dispose();
                    else
                        return req;
                }
            }
        }

        public static void RemoveRequest(EcrRequest req)
        {
            req.Removed = true;
        }
    }
}