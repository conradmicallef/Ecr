using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Transactium.EcrService
{
    public class EcrSimulator : EcrConnectionBase, IAsyncDisposable
    {
        readonly CancellationTokenSource cts = new();
        readonly TcpListener listener = new(IPAddress.Loopback, 2000);
        private readonly ILogger<EcrSimulator> logger;
        readonly Task executeTask;
        readonly List<Func<string, Task<(bool, string)>>> handlers = [];
        readonly SemaphoreSlim handlerLock = new(1, 1);//MUTEX initial 1, max 1
        public EcrSimulator(ILogger<EcrSimulator> logger)
        {
            this.logger = logger;
            executeTask = Task.Run(Execute);
        }
        public void AddHandler(Func<string, Task<(bool, string)>> handler)
        {
            handlerLock.Wait();
            try
            {
                handlers.Add(handler);
            }
            finally { handlerLock.Release(); }
        }
        public void RemoveHandler(Func<string, Task<(bool, string)>> handler)
        {
            handlerLock.Wait();
            try
            {
                handlers.Remove(handler);
            }
            finally { handlerLock.Release(); }
        }
        async Task Execute()
        {
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
                listener.Start();
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        logger.LogInformation("SIMU: wait connection");
                        var client = await listener.AcceptTcpClientAsync(cts.Token);
                        logger.LogInformation("SIMU: connected");
                        while (!cts.IsCancellationRequested)
                        {
                            var req = await Receive(client, cts.Token);
                            logger.LogInformation("SIMU RX {Request}", req);
                            var resp = await Handle(req);
                            await Send(client, resp, cts.Token);
                            logger.LogInformation("SIMU TX {Response}", req);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "In Simulator");
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "In Simulator startup");
            }
        }

        private async Task<string> Handle(string req)
        {
            await handlerLock.WaitAsync();
            try
            {
                foreach (var h in handlers)
                {
                    (bool handled, string resp) = await h(req);
                    if (handled)
                        return resp;
                }
                return req;
            }
            finally { handlerLock.Release(); }
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            listener.Stop();
            cts.Cancel();
            await executeTask;
            executeTask.Dispose();
            cts.Dispose();
            handlerLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}