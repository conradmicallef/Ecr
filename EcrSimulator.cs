// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Transactium.EcrService
{
    public sealed class EcrSimulator:IAsyncDisposable
    {
        readonly CancellationTokenSource cts = new();
        readonly TcpListener listener=new(IPAddress.Loopback, 2000);
        private readonly ILogger<EcrSimulator> logger;
        readonly Task executeTask;
        public EcrSimulator(ILogger<EcrSimulator> logger)
        {
            this.logger = logger;
            executeTask = Task.Run(Execute);
        }
        async Task Execute()
        {
            try
            {
#if KEEPALIVES
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                listener.Server.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                listener.Server.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.KeepAlive, true);
#endif
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
                            var req = await Receive(client,cts.Token);
                            logger.LogInformation("SIMU RX {packet}", req);
                            await Send(client, req, cts.Token);
                            logger.LogInformation("SIMU TX {packet}", req);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "In Simulator");
                    }
                }
            }catch(Exception e)
            {
                logger.LogError(e,"In Simulator startup");
            }
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

        private static async Task Send(TcpClient tcpClient, string v, CancellationToken ct)
        {
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


        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            listener.Stop();
            cts.Cancel();
            await executeTask;
            cts.Dispose();
        }
    }
}