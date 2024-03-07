// See https://aka.ms/new-console-template for more information
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Transactium.EcrService;

ServiceCollection services= new();
services.AddLogging(c => c.AddConsole());
services.AddSingleton<EcrConnectionFactory>();
services.AddSingleton<EcrSimulator>();

{
    var sp = services.BuildServiceProvider();
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var cf = sp.GetRequiredService<EcrConnectionFactory>();
    var sim = sp.GetRequiredService<EcrSimulator>();
    var ecr = cf.GetOrCreate("127.0.0.1:2000");
    try
    {
        await ecr.WaitState(EcrConnection.State.Ready, TimeSpan.FromSeconds(30));
        while (true) { 
            await Task.Delay(10000);
            var resp=await ecr.Exchange("XXXX", TimeSpan.FromSeconds(1));
        }
    }
    catch(Exception ex)
    {
        logger.LogError(ex, "In program");
    }
    await sp.DisposeAsync();
}