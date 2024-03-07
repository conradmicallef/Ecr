using System.Net.Sockets;

namespace Transactium.EcrService
{
    /// <summary>
    /// Base class containing protocol send and receive
    /// </summary>
    public abstract class EcrConnectionBase
    {

        static internal async Task<string> Receive(TcpClient tcpClient, CancellationToken ct)
        {
            var blen = new byte[2];
            if (2 != await tcpClient.Client.ReceiveAsync(blen, ct))
                throw new EcrException("Length header not received");
            int len = blen[0] * 256 + blen[1];
            if (len > 10240)
                throw new EcrException("Invalid Length");
            var pl = new byte[len];
            if (len != await tcpClient.Client.ReceiveAsync(pl, ct))
                throw new EcrException("Payload not Received");
            var ret = System.Text.Encoding.UTF8.GetString(pl);
            return ret;
        }

        internal static async Task Send(TcpClient tcpClient, string v, CancellationToken ct)
        {
            await tcpClient.Client.SendAsync(MakePacket(v), ct);
        }

        internal static ArraySegment<byte> MakePacket(string v)
        {
            var a = System.Text.Encoding.UTF8.GetBytes(v);
            int len = a.Length;
            byte[] ret = new byte[len + 2];
            Array.Copy(a, 0, ret, 2, len);
            ret[0] = (byte)(len / 256);
            ret[1] = (byte)(len % 256);
            return ret;
        }

    }
}