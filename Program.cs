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
        sim.AddHandler(async req => {
            if (req.StartsWith("W010"))
            {
                await Task.Delay(10000);
                return (true, req + ",0");
            }
            return (false, req);
        });
        sim.AddHandler(async req => {
            if (req.StartsWith("W001"))
            {
                await Task.Delay(1000);
                return (true, req + ",0");
            }
            return (false, req);
        });
        await ecr.WaitForState(EcrConnection.State.Ready, TimeSpan.FromSeconds(30));
        while (true) { 
            try{
                var resp=await ecr.IO("W001", TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "In program");
            }
            await Task.Delay(1000);
            if (Console.KeyAvailable)
                break;
        }
    }
    catch(Exception ex)
    {
        logger.LogError(ex, "In program");
    }
    await sp.DisposeAsync();
}