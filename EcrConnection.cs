// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Transactium.EcrService
{
    public sealed class EcrConnection : IAsyncDisposable
    {
        private readonly IPEndPoint ep;
        private readonly CancellationToken ct;
        private readonly ILogger logger;
        private readonly Task executeTask;
        public enum State { Disconnected, Connected, Ready, Waiting}
        State state;
        readonly EcrRequests requests = new();
        public EcrConnection(IPEndPoint ep,ILogger logger, CancellationToken ct)
        {
            this.ep = ep;
            this.logger = logger;
            this.ct = ct;
            executeTask = Task.Run(Execute,ct);
        }
        // Utility function to create linked token
        CancellationTokenSource CancelAfter(TimeSpan ts)
        {
            var ct = CancellationTokenSource.CreateLinkedTokenSource(this.ct);
            ct.CancelAfter(ts);
            return ct;
        }
        // Utility function to log state
        void LogState()
        {
            logger.LogInformation("State {state} {ep}", state, ep.ToString());
        }
        // Main task - connect to ecr, validate state and await commands
        // also ping connection when idle for 1 minute
        async Task Execute()
        {
            logger.LogInformation("Started ECR {ep}",ep.ToString());
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using TcpClient tcpClient = new();
                    state = State.Disconnected;
                    logger.LogInformation("Connecting {ep}", ep.ToString());
                    using CancellationTokenSource ctConnect = CancelAfter(TimeSpan.FromSeconds(10));
                    await tcpClient.ConnectAsync(ep, ctConnect.Token);
                    state = State.Connected;
                    LogState();
                    await ExecuteConnectedState(tcpClient);
                }
                catch (Exception e){
                    logger.LogError(e, "Error in Connection {ep}", ep.ToString());
                    state = State.Disconnected;
                }
            }
        }

        private async Task ExecuteConnectedState(TcpClient tcpClient)
        {
            var versResp = await Exchange(tcpClient, "VERS", TimeSpan.FromSeconds(1));
            while (!ct.IsCancellationRequested)
            {
                var pingResp = await Exchange(tcpClient, "PING", TimeSpan.FromSeconds(1));
                state = State.Ready;
                LogState();
                await ExecuteReadyState(tcpClient);
            }
        }

        private async Task ExecuteReadyState(TcpClient tcpClient)
        {
            while (!ct.IsCancellationRequested)
            {
                using CancellationTokenSource ctWaitRequest = CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    var request = await requests.WaitRequest(ctWaitRequest.Token);
                    try
                    {
                        state = State.Waiting;
                        var resp = await Exchange(tcpClient, request.Request, request.WaitFor);
                        request.Response = resp;
                        request.RepliedTime = DateTime.UtcNow;
                        request.Semaphore.Release();
                        state = State.Ready;
                    }
                    catch (Exception e)
                    {
                        request.Semaphore.Release();
                        request.Exception = e;
                        if (request.Removed)
                            request.Dispose();
                        if (ctWaitRequest.IsCancellationRequested && !ct.IsCancellationRequested)
                            continue;
                        throw;
                    }
                }
                catch (OperationCanceledException)
                    when (!ct.IsCancellationRequested)
                {
                    break;//redo ping 1 minute without activity
                }
            }
        }

        private Task<string> Exchange(TcpClient tcpClient, string v, TimeSpan ts)
        {
            using var ct = CancelAfter(ts);
            return Exchange(tcpClient,v, ct.Token);
        }

        private async Task<string> Exchange(TcpClient tcpClient, string v, CancellationToken ct)
        {
            if (state ==State.Disconnected)
                throw new Exception("Invalid State");
            while (tcpClient.Available>0)
            {
                var extra = await Receive(tcpClient,ct);
                logger.LogWarning("Unexpected reply from {ep} - {extra}", ep.ToString(), extra);
            }
            logger.LogInformation("TX {ep} {req}", ep.ToString(), v);
            await Send(tcpClient, v, ct);
            var resp=await Receive(tcpClient,ct);
            logger.LogInformation("RX {ep} {req}", ep.ToString(), resp);
            return resp;
        }

        private static async Task<string> Receive(TcpClient tcpClient,CancellationToken ct)
        {
            var blen = new byte[2];
            if (2 != await tcpClient.Client.ReceiveAsync(blen,ct))
                throw new Exception("Length header not received");
            int len = blen[0] * 256 + blen[1];
            if (len > 10240)
                throw new Exception("Invalid Length");
            var pl = new byte[len];
            if (len != await tcpClient.Client.ReceiveAsync(pl,ct))
                throw new Exception("Payload not Received");
            var ret = System.Text.Encoding.UTF8.GetString(pl);
            return ret;
        }

        private async Task Send(TcpClient tcpClient, string v, CancellationToken ct)
        {
            logger.LogInformation("TX: {req}", v);
            await tcpClient.Client.SendAsync(MakePacket(v), ct);
        }

        private static ArraySegment<byte> MakePacket(string v)
        {
            var a = System.Text.Encoding.UTF8.GetBytes(v);
            int len = a.Length;
            byte[] ret = new byte[len + 2];
            Array.Copy(a, 0, ret, 2, len);
            ret[0] = (byte)(len / 256);
            ret[1] = (byte)(len % 256);
            return ret;
        }

        public async ValueTask DisposeAsync()
        {
            if (!ct.IsCancellationRequested)
                throw new Exception("Disposing without cancellation request");
            await executeTask;
            executeTask.Dispose();
            requests.Dispose();
        }

        public async Task<string> Exchange(string request,TimeSpan waitFor)
        {
            if (string.IsNullOrEmpty(request)) throw new ArgumentNullException(nameof(request));
            if (waitFor.TotalSeconds<1) throw new ArgumentOutOfRangeException(nameof(waitFor));
            if (state < State.Connected)
            {
                logger.LogWarning("ECR not connected - Exchange failed for {request}", request);
                throw new Exception("ECR not connected");
            }
            EcrRequest req = new() { Request = request, RequestedTime = DateTime.UtcNow, WaitFor = waitFor };
            requests.AddRequest(req);
            try
            {
                using var ctWait = CancelAfter(waitFor);
                await req.Semaphore.WaitAsync(ctWait.Token);
                req.Dispose();
                if (!req.RepliedTime.HasValue)
                    throw req.Exception??new Exception("Request not replied");
                return req.Response;
            }
            catch(Exception e)
            {
                logger.LogWarning(e, "Exchange failed for {request}",request);
                EcrRequests.RemoveRequest(req);
                throw;
            }
        }

        public async Task WaitState(State waitForState, TimeSpan timeSpan)
        {
            using var ctWait=CancelAfter(timeSpan);
            while (!ctWait.IsCancellationRequested)
            {
                if (state == waitForState)
                    return;
                try
                {
                    await Task.Delay(1000, ctWait.Token);
                }
                catch {
                    logger.LogWarning("State {state} not Reached", waitForState);
                    throw; 
                }
            }
        }
    }
}