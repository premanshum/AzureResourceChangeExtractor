using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using colonel.Core.Tasks;
using colonel.Policies.Services;
using colonel.Rest;
using colonel.Shared;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.Management.ResourceGraph;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: FunctionsStartup(typeof(colonel.Policies.Startup))]
namespace colonel.Policies
{
    public class Startup : FunctionsStartup
    {

        public override void Configure(IFunctionsHostBuilder builder)
        {

            var configuration = new ConfigurationBuilder()
                .AddcolonelConfiguration("Policies", new colonelConfigurationOptions
                {
                    ConfigValueRewrites = new[] {
                        new KeyValuePair<string, string>("Policies:StorageAccountConnectionString", "AzureWebJobsStorage"),
                        new KeyValuePair<string, string>("Policies:DigitalTwin:StorageAccountConnectionString", "AzureWebJobsPolicies")
                    }
                })
                .Build();

            builder.Services.Replace(ServiceDescriptor.Singleton(typeof(IConfiguration), configuration));

            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<FunctionsHealthChecks>();
            builder.Services.AddSingleton<BundleStorageAccountService>();
            builder.Services.AddSingleton<DigitalProductTwinStorageService>();
            builder.Services.AddSingleton<CheckMetadataStorageService>();
            builder.Services.AddSingleton<DecisionLogStorageService>();
            builder.Services.AddSingleton<DecisionLogViewModelService>();
            
            builder.Services.AddSingleton<GraphAuthenticationProvider>();
            builder.Services.AddSingleton(sp => new Microsoft.Graph.GraphServiceClient(sp.GetService<GraphAuthenticationProvider>()));

            builder.Services.AddTransient<OpaHttpClientHandler>();
            builder.Services.AddSingleton<OpaHttpClientFactory>();
            builder.Services.AddHttpClient(OpaHttpClientFactory.NamedHttpClientName, client =>
            {
                if (Debugger.IsAttached)
                    client.BaseAddress = new Uri("http://localhost:8181");
                else
                    client.BaseAddress = new Uri($"https://apsazew{configuration.GetValue<string>("EnvironmentCode")}-colonel-policies-opa.azurewebsites.net");
            }).AddHttpMessageHandler<OpaHttpClientHandler>();


            builder.Services.AddSingleton<ActivityLogQueryHelper>();
            builder.Services.AddSingleton<AzureResourceGraphHelper>();
            builder.Services.AddTransient<LogAnalyticsQueryHttpClientHandler>();
            builder.Services.AddSingleton<LogAnalyticsQueryHttpClientFactory>();
            builder.Services.AddHttpClient(LogAnalyticsQueryHttpClientFactory.NamedHttpClientName, client =>
            {
                string url = $"https://api.loganalytics.io/v1/workspaces/{configuration.GetValue<string>("AzureAuditLogAnalyticsWorkspaceId")}/";
                client.BaseAddress = new Uri(url);
            }).AddHttpMessageHandler<LogAnalyticsQueryHttpClientHandler>();


            builder.Services.AddcolonelHostingComponents(configuration);

            builder.Services.AddTransient<IAuditLogService, AuditLogService>();
            builder.Services.AddTransientcolonelClient();

            builder.Services.ConfigureAsyncOperationFramework<Startup>(configuration);

            builder.Services.Configure<BundleStorageAccountService.Options>(options => options.StorageConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies"));
            builder.Services.Configure<DigitalProductTwinStorageService.Options>(options => options.StorageConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies"));
            builder.Services.Configure<CheckMetadataStorageService.Options>(options => options.StorageConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies"));
            builder.Services.Configure<DecisionLogStorageService.Options>(options => options.StorageConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies"));
            builder.Services.Configure<DecisionLogViewModelService.Options>(options => options.StorageConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies"));

            builder.Services.Configure<AzureResourceGraphOptions>(x =>
            {
                configuration.GetSection("AzureAdAutomation").Bind(x);
                configuration.GetSection("AzureAd").Bind(x);
            });
        }
        [Microsoft.Azure.WebJobs.FunctionName(nameof(AsyncOperationEntity))]
        public static Task Run([EntityTrigger] IDurableEntityContext ctx) => ctx.DispatchAsync<AsyncOperationEntity>();

    }
}