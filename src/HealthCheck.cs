using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using colonel.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using colonel.Policies.Services;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace colonel.Policies
{
    public class HealthCheck
    {
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly FunctionsHealthChecks _functionsHealthChecks;
        private readonly OpaHttpClientFactory _opaHttpClientFactory;
        private readonly BlobServiceClient _functionsBlob;
        private readonly BlobServiceClient _dataBlob;
        private readonly QueueServiceClient _functionsQueue;
        private readonly QueueServiceClient _dataQueue;

        public HealthCheck(IcolonelUserContext colonelUserContext, FunctionsHealthChecks functionsHealthChecks, OpaHttpClientFactory opaHttpClientFactory,
            IConfiguration configuration)
        {
            _colonelUserContext = colonelUserContext;
            _functionsHealthChecks = functionsHealthChecks;
            _opaHttpClientFactory = opaHttpClientFactory;

            var functionsConnectionString = configuration.GetValue<string>("AzureWebJobsStorage");
            var dataConnectionString = configuration.GetValue<string>("AzureWebJobsPolicies");
            _functionsBlob = new BlobServiceClient(functionsConnectionString);
            _dataBlob = new BlobServiceClient(functionsConnectionString);
            _functionsQueue = new QueueServiceClient(functionsConnectionString);
            _dataQueue = new QueueServiceClient(functionsConnectionString);

        }
        [FunctionName("health")]
        public async Task<IActionResult> Healthcheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req, ILogger log)
        {
            _colonelUserContext.AsAuthorized();

            var healthChecks = await _functionsHealthChecks.PerformHealthCheck(("OpenPolicyAgent", async () =>
            {
                var client = _opaHttpClientFactory.GetHttpClient();
                var mainHealth = await client.GetAsync("/health");
                var bundlesHealth = await client.GetAsync("/health?bundles");
                var pluginsHealth = await client.GetAsync("/health?plugins");

                return new FunctionsHealthCheckResult
                {
                    Status = mainHealth.IsSuccessStatusCode && bundlesHealth.IsSuccessStatusCode && pluginsHealth.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Data = new Dictionary<string, JToken> {
                    { "opa-main", await mainHealth.Content.ReadAsStringAsync() },
                    { "opa-bundles", await bundlesHealth.Content.ReadAsStringAsync() },
                    { "opa-plugins", await pluginsHealth.Content.ReadAsStringAsync() }
                }
                };
            }
            ),
            ("Storage", async () =>
            {
                var functionsBlobOk = await _functionsBlob.GetAccountInfoAsync();
                var dataBlobOk = await _functionsBlob.GetAccountInfoAsync();
                string[] functionsQueueOk = null;
                try
                {
                    functionsQueueOk = (await _functionsQueue.GetQueuesAsync().GetAllAsync()).Select(q => q.Name).ToArray();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error performing health check for FunctionsQueue");
                }

                string[] dataQueueOk = null;
                try
                {
                    dataQueueOk = (await _dataQueue.GetQueuesAsync().GetAllAsync()).Select(q => q.Name).ToArray();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Error performing health check for DataQueue");
                }

                return new FunctionsHealthCheckResult
                {
                    Status = functionsBlobOk.GetRawResponse().Status == 200 &&
                             dataBlobOk.GetRawResponse().Status == 200 &&
                             functionsQueueOk != null &&
                             dataQueueOk != null ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Data = new Dictionary<string, JToken> {
                        { "functions-blob", JObject.FromObject(functionsBlobOk.Value) },
                        { "functions-queue", new JArray(functionsQueueOk) },
                        { "data-blob", JObject.FromObject(dataBlobOk.Value) },
                        { "data-queue", new JArray(dataQueueOk) },
                    }
                };
            }
            ));

            return healthChecks.Status != HealthStatus.Unhealthy ? new OkObjectResult(healthChecks.ToJson()) : new ObjectResult(healthChecks.ToJson()) { StatusCode = 503 };
        }


    }
}
