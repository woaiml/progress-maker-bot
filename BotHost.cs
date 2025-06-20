// ***********************************************************************
// Assembly         : EchoBot
// Author           : bcage29
// Created          : 10-27-2023
//
// Last Modified By : bcage29
// Last Modified On : 10-27-2023
// ***********************************************************************
// <copyright file="BotHost.cs" company="Microsoft">
//     Copyright  2023
// </copyright>
// <summary></summary>
// ***********************************************************************
using DotNetEnv.Configuration;
using EchoBot.Bot;
using EchoBot.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common.Telemetry;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using JWT;
using JWT.Algorithms;
using JWT.Serializers;

namespace EchoBot
{
    /// <summary>
    /// Bot Web Application
    /// </summary>
    public class BotHost : IBotHost
    {
        private readonly ILogger<BotHost> _logger;
        private readonly AppSettings _settings;
        private WebApplication? _app;

        /// <summary>
        /// Bot Host constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="settings"></param>
        public BotHost(ILogger<BotHost> logger, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        /// <summary>
        /// Starting the Bot and Web App
        /// </summary>
        /// <returns></returns>
        public async Task StartAsync()
        {
            _logger.LogInformation("Starting the Echo Bot");
            // Set up the bot web application
            var builder = WebApplication.CreateBuilder();

            // if (builder.Environment.IsDevelopment())
            // {
            // load the .env file environment variables
            builder.Configuration.AddDotNetEnv();
            // }

            // Add Environment Variables
            builder.Configuration.AddEnvironmentVariables(prefix: "AppSettings__");

            // TODO: Remove this after debugging
            foreach (var envVar in Environment.GetEnvironmentVariables().Keys)
            {
                Console.WriteLine($"{envVar}: {Environment.GetEnvironmentVariable(envVar.ToString())}");
            }

            // Add services to the container.
            builder.Services.AddControllers();

            // --- CORS configuration ---
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    policy.WithOrigins(
                        "http://localhost:3000",
                        "https://e698-115-96-27-193.ngrok-free.app"
                    )
                    .AllowAnyHeader()
                    .AllowAnyMethod();
                });
            });
            // --- End CORS configuration ---

            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var section = builder.Configuration.GetSection("AppSettings");
            var appSettings = section.Get<AppSettings>();

            builder.Services
                .AddOptions<AppSettings>()
                .BindConfiguration(nameof(AppSettings))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            builder.Services.AddSingleton<IGraphLogger, GraphLogger>(_ => new GraphLogger("EchoBotWorker", redirectToTrace: true));
            builder.Services.AddSingleton<IBotMediaLogger, BotMediaLogger>();
            builder.Logging.AddApplicationInsights();
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            builder.Logging.AddEventLog(config => config.SourceName = "Echo Bot Service");

            builder.Services.AddSingleton<IBotService, BotService>();

            // Bot Settings Setup
            var botInternalHostingProtocol = "https";
            if (appSettings.UseLocalDevSettings)
            {
                // if running locally with ngrok
                // the call signalling and notification will use the same internal and external ports
                // because you cannot receive requests on the same tunnel with different ports

                // calls come in over 443 (external) and route to the internally hosted port: BotCallingInternalPort
                botInternalHostingProtocol = "http";

                builder.Services.PostConfigure<AppSettings>(options =>
                {
                    options.BotInstanceExternalPort = appSettings.BotInstanceExternalPort;
                    options.BotInternalPort = appSettings.BotCallingInternalPort;

                });
            }
            else
            {
                //appSettings.MediaDnsName = appSettings.ServiceDnsName;
                builder.Services.PostConfigure<AppSettings>(options =>
                {
                    options.MediaDnsName = appSettings.ServiceDnsName;
                });
            }

            // localhost
            var baseDomain = "+";

            // http for local development
            // https for running on VM
            var callListeningUris = new HashSet<string>
            {
                $"{botInternalHostingProtocol}://{baseDomain}:{appSettings.BotCallingInternalPort}/",
                $"{botInternalHostingProtocol}://{baseDomain}:{appSettings.BotInternalPort}/"
            };

            builder.WebHost.UseUrls(callListeningUris.ToArray());

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ConfigureHttpsDefaults(listenOptions =>
                {
                    listenOptions.ServerCertificate = Utilities.GetCertificateFromStore(appSettings.CertificateThumbprint);
                });
            });

            _app = builder.Build();

            using (var scope = _app.Services.CreateScope())
            {
                var bot = scope.ServiceProvider.GetRequiredService<IBotService>();
                bot.Initialize();
            }

            // --- BEGIN: Notify Python server when bot is ready ---
            // try
            // {
            //     var pythonServerUrl = _settings.PythonServerUrl;
            //     var jwtSecret = _settings.WebSocketJwtSecret;
            //     var companyId = _settings.CompanyId;

            //     if (!string.IsNullOrWhiteSpace(pythonServerUrl) && !string.IsNullOrWhiteSpace(jwtSecret) && !string.IsNullOrWhiteSpace(companyId))
            //     {
            //         var payload = new Dictionary<string, object>
            //         {
            //             { "companyId", companyId },
            //             { "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            //         };
            //         var algorithm = new HMACSHA256Algorithm();
            //         var serializer = new JsonNetSerializer();
            //         var urlEncoder = new JwtBase64UrlEncoder();
            //         var encoder = new JwtEncoder(algorithm, serializer, urlEncoder);
            //         var token = encoder.Encode(payload, jwtSecret);

            //         using (var client = new HttpClient())
            //         {
            //             Console.WriteLine($"Generated JWT token: {token}");
            //             // Set the Authorization header properly with "Bearer"
            //             client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            //             var postUrl = pythonServerUrl.TrimEnd('/') + "/service/started";
            //             var response = await client.PostAsync(postUrl, null);
            //             // Log request headers
            //             foreach (var header in client.DefaultRequestHeaders)
            //             {
            //                 Console.WriteLine($"Header: {header.Key} = {string.Join(", ", header.Value)}");
            //             }
            //             if (response.IsSuccessStatusCode)
            //             {
            //                 Console.WriteLine($"Successfully called {postUrl} at startup.");
            //             }
            //             else
            //             {
            //                 Console.WriteLine($"Failed to call {postUrl} at startup. Status: {response.StatusCode}");
            //             }
            //         }
            //     }
            //     else
            //     {
            //         _logger.LogWarning("One or more required configuration values are missing in AppSettings.");
            //     }
            // }
            // catch (Exception ex)
            // {
            //     _logger.LogError(ex, "Error during startup notification: {Message}", ex.Message);
            // }
            // --- END: Notify Python server when bot is ready ---

            // Configure the HTTP request pipeline.
            if (_app.Environment.IsDevelopment())
            {
                // https://localhost:<port>/swagger
                _app.UseSwagger();
                _app.UseSwaggerUI();
            }

            // --- Use CORS before authorization and controllers ---
            _app.UseCors("AllowFrontend");
            // --- End Use CORS ---

            _app.UseAuthorization();

            _app.MapControllers();

            await _app.RunAsync();
        }

        /// <summary>
        /// Stop the bot web application
        /// </summary>
        /// <returns></returns>
        public async Task StopAsync()
        {
            if (_app != null) 
            {
                using (var scope = _app.Services.CreateScope())
                {
                    var bot = scope.ServiceProvider.GetRequiredService<IBotService>();
                    // terminate all calls and dispose of the call client
                    await bot.Shutdown();
                }

                // stop the bot web application
                await _app.StopAsync();
            }
        }
    }
}
