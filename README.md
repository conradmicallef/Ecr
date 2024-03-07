This is a template for communicating with the ECR

Do not make any changes to the ECR classes as this will most likely introduce BUGS

To use setup singleton for EcrConnectionFactory

```
    services.AddSingleton<EcrConnectionFactory>();
```

When you need ECR connection, instantiate it
```
    var cf = sp.GetRequiredService<EcrConnectionFactory>();
    var ecr = cf.GetOrCreate("127.0.0.1:2000"); // Change IP address to IP address of terminal
```

You can check for state using
```
    await ecr.WaitForState(EcrConnection.State.Ready, TimeSpan.FromSeconds(30));
```

or poll the state using
```
    ecr.GetState()
```

send commands as necessary using the IO function

```
    var resp=await ecr.IO("PING", TimeSpan.FromSeconds(2));
```

Make sure to setup appropriate timeouts for functions
Make sure to enable logging to see activity, warnings, and errors

Do Validate responses to make sure responses received are for the command requested

EcrConnecion will handle:
Background job to setup connectivity with POS
Background job to check connectivity with POS
Regular PING to check device is alive (every 1 minute of inactivity)
Serialisation of commands
Awaiting of responses
