﻿using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Transactium.EcrService
{
    /// <summary>
    /// Class handing an ECR connection. This should be only instantiated from the factory and never directly
    /// Consuming it: you call WaitForState function or IO function
    /// Task handles connectivity, reconnections, and monitoring for activity
    /// Task queues commands if sent in parallel, and awaits response from terminal before sending
    /// Task generates logs of request and responses
    /// </summary>
    public sealed class EcrConnection : EcrConnectionBase,IAsyncDisposable
    {
        private readonly IPEndPoint ep;
        private readonly CancellationToken ct;
        private readonly ILogger<EcrConnection> logger;
        private readonly Task executeTask;
        public enum State { Disconnected, Connected, Ready, Waiting}
        State state;
        readonly EcrRequests requests = new();
        internal EcrConnection(IPEndPoint ep,ILogger<EcrConnection> logger, CancellationToken ct)
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
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(30000,ct);
                }
            }
            logger.LogInformation("Stopped ECR {ep}", ep.ToString());

        }
        // Log version and perform ping
        private async Task ExecuteConnectedState(TcpClient tcpClient)
        {
            var versResp = await Exchange(tcpClient, "VERS", TimeSpan.FromSeconds(1));
            if (!versResp.StartsWith("VERS"))
                throw new EcrException("Unexpected response");
            while (!ct.IsCancellationRequested)
            {
                var pingResp = await Exchange(tcpClient, "PING", TimeSpan.FromSeconds(1));
                if (!pingResp.StartsWith("PING"))
                    throw new EcrException("Unexpected response");
                state = State.Ready;
                LogState();
                await ExecuteReadyState(tcpClient);
            }
        }
        // loop while not idle for a minute waiting for new requests
        private async Task ExecuteReadyState(TcpClient tcpClient)
        {
            while (!ct.IsCancellationRequested)
            {
                using CancellationTokenSource ctWaitRequest = CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    var request = await requests.WaitRequest(ctWaitRequest.Token);
                    // absolute timeout of 5 minutes to wait for reply and after this will trigger a connection reset
                    using CancellationTokenSource ctWaitResponse=CancelAfter(TimeSpan.FromMinutes(5));
                    // timeout indicated by caller for the response
                    using CancellationTokenSource ctWaitForResponse = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        state = State.Waiting;
                        ctWaitForResponse.CancelAfter(request.WaitFor);
                        var resp = await Exchange(tcpClient, request.Request, ctWaitForResponse.Token);
                        await request.WaitLock(CancellationToken.None);
                        request.Response = resp;
                        request.RepliedTime = DateTime.UtcNow;
                        request.SignalReply();
                        request.ReleaseLock();
                        state = State.Ready;
                    }
                    catch (Exception e)
                    {
                        await request.WaitLock(CancellationToken.None);
                        //flag response status
                        request.Exception = e;
                        request.SignalReply();
                        if (request.Removed)
                            request.Dispose();
                        else
                            request.ReleaseLock();
                        //see if its required to continue waiting for response
                        if (e is OperationCanceledException 
                            && ctWaitForResponse.IsCancellationRequested 
                            && !ctWaitResponse.IsCancellationRequested)
                        {
                            var resp=await Receive(tcpClient, ctWaitResponse.Token);
                            logger.LogError("Response received late - consider increasing timeout {ep} - {resp}", ep.ToString(), resp);
                            continue;
                        }
                        if (e is OperationCanceledException)
                            throw new EcrException("Timeout", e);
                        throw;
                    }
                }
                catch (OperationCanceledException)
                    when (!ct.IsCancellationRequested && ctWaitRequest.IsCancellationRequested)
                {
                    //eat exception to exit state to rerun ping
                    return;
                }
            }
        }
        /// <summary>
        /// perform io function with terminal
        /// </summary>
        /// <param name="tcpClient"></param>
        /// <param name="v"></param>
        /// <param name="ts"></param>
        /// <returns></returns>
        private Task<string> Exchange(TcpClient tcpClient, string v, TimeSpan ts)
        {
            using var ct = CancelAfter(ts);
            return Exchange(tcpClient,v, ct.Token);
        }
        /// <summary>
        /// perform io function with terminal
        /// </summary>
        /// <param name="tcpClient"></param>
        /// <param name="v"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <exception cref="EcrException"></exception>

        private async Task<string> Exchange(TcpClient tcpClient, string v, CancellationToken ct)
        {
            if (state ==State.Disconnected)
                throw new EcrException("Invalid State");
            // try to empty buffer before sending new request
            while (true)
            {
                try
                {
                    using var ctExtra = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ctExtra.CancelAfter(100);
                    var extra = await Receive(tcpClient, ctExtra.Token);
                    logger.LogError("Unexpected reply from {ep} - {extra}", ep.ToString(), extra);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            logger.LogInformation("TX {ep} {req}", ep.ToString(), v);
            await Send(tcpClient, v, ct);
            var resp=await Receive(tcpClient,ct);
            logger.LogInformation("RX {ep} {resp}", ep.ToString(), resp);
            return resp;
        }

        public async ValueTask DisposeAsync()
        {
            if (!ct.IsCancellationRequested)
                throw new EcrException("Disposing without cancellation request");
            await executeTask;
            executeTask.Dispose();
            requests.Dispose();
        }

        public async Task<string> IO(string request,TimeSpan waitFor)
        {
            if (string.IsNullOrEmpty(request)) throw new ArgumentNullException(nameof(request));
            if (waitFor.TotalSeconds<1) throw new ArgumentOutOfRangeException(nameof(waitFor));
            if (state < State.Connected)
            {
                logger.LogWarning("ECR not connected - Exchange failed for {request}", request);
                throw new EcrException("ECR not connected");
            }
            EcrRequest req = new() { Request = request, RequestedTime = DateTime.UtcNow, WaitFor = waitFor };
            requests.AddRequest(req);
            try
            {
                using var ctWait = CancelAfter(waitFor);
                await req.WaitReply(ctWait.Token);
                await req.WaitLock(CancellationToken.None);
                req.Dispose();
                if (!req.RepliedTime.HasValue)
                    throw req.Exception??new EcrException("Request not replied");
                return req.Response;
            }
            catch(Exception e)
            {
                logger.LogWarning(e, "Exchange failed for {request}",request);
                await EcrRequests.RemoveRequest(req);
                if (e is OperationCanceledException)
                    throw new EcrException("Timeout", e);
                throw;
            }
        }

        public async Task WaitForState(State waitForState, TimeSpan timeSpan)
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
                    logger.LogWarning("State {waitForState} not Reached - stuck in {state}", waitForState,state);
                    throw; 
                }
            }
        }
        public State GetState() => state;
    }
}