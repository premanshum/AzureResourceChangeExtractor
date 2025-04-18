
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Admiral.Policies.Services;
using Admiral.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Admiral.Policies
{
    public class EvaluatePoliciesFunctions
    {
        private readonly IAdmiralUserContext _admiralUserContext;
        private readonly OpaHttpClientFactory _opaHttpClientFactory;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;

        public EvaluatePoliciesFunctions(IAdmiralUserContext admiralUserContext, OpaHttpClientFactory opaHttpClientFactory, DigitalProductTwinStorageService digitalProductTwinStorageService)
        {
            _admiralUserContext = admiralUserContext;
            _opaHttpClientFactory = opaHttpClientFactory;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
        }

        [FunctionName("EvaluatePolicies")]
        public async Task EvaluatePolicies([QueueTrigger("product-twin-changes", Connection = "Policies")] string json,
            [Blob("incoming-decision-logs", Connection = "Policies")] CloudBlobContainer decisionLogBlobs,
            ILogger log)
        {

            var inputMessage = JObject.Parse(json);
            var productCode = inputMessage.Value<string>("ProductCode");
            var versionId = inputMessage.Value<string>("VersionId");

            var twin = await _digitalProductTwinStorageService.GetDigitalProductTwinAsync(productCode, versionId);

            var body = new JObject(new JProperty("input", twin));

            var bundlepath = "digitaltwin/full_cap_check";

            var opaClient = _opaHttpClientFactory.GetHttpClient();
            var response = await opaClient.PostAsync($"v1/data/{bundlepath}?provenance=true&metrics=true", new StringContent(body.ToString()));

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var decisionJson = JObject.Parse(responseBody);
                var decisionId = Guid.NewGuid().ToString("N");
                var cloudBlockBlob = decisionLogBlobs.GetBlockBlobReference($"{decisionId}.json");
                await cloudBlockBlob.UploadTextAsync(decisionJson.ToString());

                log.LogInformation("Processed twin change {0}.{1} as decision {2}. Metrics: {3}. Provenance: {4}",
                    productCode, versionId, decisionId,
                    decisionJson.Value<JObject>("metrics").ToString(Formatting.None),
                    decisionJson.Value<JObject>("provenance").ToString(Formatting.None));
            }
            else
            {
                var responseBody = await response.Content?.ReadAsStringAsync();
                log.LogDebug("Body: {0}", responseBody);
                log.LogError("Failed to process twin change {0}.{1}", productCode, versionId);

            }

        }

        [FunctionName("EvaluatePoliciesManual")]
        public async Task<IActionResult> EvaluatePoliciesManual([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "policies/evaluate")] HttpRequest req,
            [Queue("product-twin-changes", Connection = "Policies")] IAsyncCollector<string> messages, ILogger log)
        {
            _admiralUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var body = await req.ReadAsStringAsync();

            var productCodes = new string[] { };
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    productCodes = JArray.Parse(body).Where(i => i.Type == JTokenType.String).Select(i => i.ToString()).ToArray();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not parse body");
                    return new BadRequestObjectResult(new { message = "Expected array in body" });
                }
            }

            var allTwins = await _digitalProductTwinStorageService.ListDigitalProductTwinsAsync();

            if (productCodes.Length > 0)
                allTwins = allTwins.Where(t => productCodes.Contains(t.ProductCode)).ToArray();

            await Task.WhenAll(allTwins.Select(twin => messages.AddAsync(JsonConvert.SerializeObject(new { twin.ProductCode, twin.VersionId }))));
            return new OkJsonResult(allTwins);
        }

        [FunctionName(nameof(EvaluateAllPolicies))]
        public async Task EvaluateAllPolicies([QueueTrigger("evaulate-all-products", Connection = "Policies")] string json,
            [Queue("product-twin-changes", Connection = "Policies")] IAsyncCollector<string> messages, ILogger log)
        {
            var allTwins = await _digitalProductTwinStorageService.ListDigitalProductTwinsAsync();

            await Task.WhenAll(allTwins.Select(twin => messages.AddAsync(JsonConvert.SerializeObject(new { twin.ProductCode, twin.VersionId }))));
        }
    }
}
