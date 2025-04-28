using EchoBot;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using DotNetEnv;

// Load .env file
DotNetEnv.Env.Load();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Echo Bot Service";
    })
    .ConfigureServices((hostContext, services) =>
    {
        LoggerProviderOptions.RegisterProviderOptions<
            EventLogSettings, EventLogLoggerProvider>(services);

        services.AddSingleton<IBotHost, BotHost>();

        // Bind AppSettings from configuration section
        services.Configure<AppSettings>(hostContext.Configuration.GetSection("AppSettings"));

        services.AddHostedService<EchoBotWorker>();
    })
    .Build();

await host.RunAsync();
