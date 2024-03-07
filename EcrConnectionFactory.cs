// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;

namespace Transactium.EcrService
{
    /// <summary>
    /// This class should be a singleton
    /// It guarantees one connection per pos based on the connectionstring
    /// </summary>
    public sealed class EcrConnectionFactory:IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private readonly ConcurrentDictionary<string, EcrConnection> connections = new();
        private readonly ILogger<EcrConnectionFactory> logger;

        public EcrConnectionFactory(ILogger<EcrConnectionFactory> logger)
        {
            this.logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            logger.LogInformation("Closing ECR Connections");
            cts.Cancel();
            foreach (var c in connections.ToList())
                await c.Value.DisposeAsync();
            cts.Dispose();
            connections.Clear();
        }

        public EcrConnection GetOrCreate(string connectionString)
        {
            var host = IPAddress.Parse(connectionString.Split(':')[0]);
            var port = short.Parse(connectionString.Split(":")[1]);
            var ep = new IPEndPoint(host, port);
            //Rebuild connectionstring after parsing to ensure no formatting disparity
            connectionString = $"{host}:{port}";
            return connections.GetOrAdd(connectionString, (s) =>
            {
                logger.LogInformation("Creating New Connection {connectionString}", connectionString);
                return new(ep, logger, cts.Token);
            });
        }
    }
}