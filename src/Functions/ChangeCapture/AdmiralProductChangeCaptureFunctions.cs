using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using colonel.Core.Tasks;
using colonel.Policies.Services;
using colonel.Rest;
using colonel.Rest.Models;
using colonel.Shared;
using colonel.Shared.DDD;
using Azure.Identity;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using eventgridclient;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace colonel.Policies
{
    public class colonelProductChangeCaptureFunctions
    {
        private readonly IcolonelClient _colonelClient;
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;
        private readonly IConfiguration _configuration;
        private readonly IOptions<AzureResourceGraphOptions> _options;

        public colonelProductChangeCaptureFunctions(IcolonelClient colonelClient, IcolonelUserContext colonelUserContext,
            DigitalProductTwinStorageService digitalProductTwinStorageService,
            IConfiguration configuration, IOptions<AzureResourceGraphOptions> options)
        {
            _colonelClient = colonelClient;
            _colonelUserContext = colonelUserContext;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
            _configuration = configuration;
            _options = options;
        }

        [FunctionName(nameof(colonelProductChangeCaptureProductChanged))]
        public async Task<IActionResult> colonelProductChangeCaptureProductChanged([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/colonel")] HttpRequest req, ILogger log,
            [DurableClient] IDurableClient starter)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.EventGrid);
            req.Headers.TryGetValue("Request-Id", out var traceparent);

            string requestContent = await req.ReadAsStringAsync();

            var events = JsonConvert.DeserializeObject<BaseDomainEvent<ProductChangedEventPayload>[]>(requestContent);

            var productCodes = (from e in events
                                where e?.EventType == DomainEventType.ProductCreated ||
                                      e?.EventType == DomainEventType.ProductDecomissioned ||
                                      e?.EventType == DomainEventType.ProductUpdated
                                where e?.Data?.Code != null
                                select e.Data.Code).Distinct().ToArray();

            var response = await starter.StartNewAsAsyncOperation(_colonelUserContext, nameof(colonelProductChangeCapture), productCodes, new[] { new EntityReference("policies", "change_capture") }, null);
            log.LogInformation("Started new task ({0}, {1}) as the following products were changed: {2}", response.OrchestratorInstanceId, response.AsyncOperationId, String.Join(",", productCodes));

            return new OkResult();
        }
        public class ProductChangedEventPayload
        {
            public string Code { get; set; }
        }

        [FunctionName(nameof(colonelProductChangeCaptureTimerTrigger_Test))]
        public async Task<IActionResult> colonelProductChangeCaptureTimerTrigger_Test(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "policies/sourcetriggers/colonelproducts")] HttpRequest req,
            ILogger log, [DurableClient] IDurableClient starter)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var body = await req.ReadAsStringAsync();
            var products = String.IsNullOrEmpty(body) ? new string[] { } : JArray.Parse(body).Select(j => j.ToString()).ToArray();

            var response = await starter.StartNewAsAsyncOperation(_colonelUserContext, nameof(colonelProductChangeCapture), products, new[] { new EntityReference("policies", "change_capture") }, null);
            return response.Result;
        }

        [AsyncOperationInfo("Policies", "colonel Product Metadata Update",
            new[] { "Get all valid Products", "Update Digital Product Twins" })]
        [FunctionName(nameof(colonelProductChangeCapture))]
        public async Task colonelProductChangeCapture([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Running);

            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Running);
            var allDigitalTwins = await context.CallActivityAsync<ProductDigitalTwinMetadata[]>(nameof(CommonActivities.GetAllDigitalTwinMetadata), null);
            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Completed);


            var productFilter = context.GetInput<string[]>();
            if (productFilter != null && productFilter.Length > 0)
                allDigitalTwins = allDigitalTwins.Where(p => productFilter.Contains(p.ProductCode)).ToArray();

            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running);
            var changeCount = 0;
            for (int i = 0; i < allDigitalTwins.Length; i++)
            {
                var twin = allDigitalTwins[i];
                if (i % 9 == 0)
                    context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running, i, allDigitalTwins.Length);

                var hasChanged = await context.CallActivityAsync<bool>(nameof(UpdateProductTwinWithcolonelMetadata), twin.ProductCode);
                if (hasChanged) changeCount++;
            }
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Completed, allDigitalTwins.Length, allDigitalTwins.Length);
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"Updated {changeCount} Digital Product Twins out of {allDigitalTwins.Length}");
        }

        [FunctionName(nameof(UpdateProductTwinWithcolonelMetadata))]
        public async Task<bool> UpdateProductTwinWithcolonelMetadata([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var productCode = context.GetInput<string>();
            var hasChanges = false;
            try
            {
                var product = await _colonelClient.GetProductAsync(productCode);

                var newProductObject = ConvertProductToJObject(product);

                await _digitalProductTwinStorageService.UpdateDigitalProductTwinAsync(productCode, (twin, metadata) =>
                {

                    if (!twin.ContainsKey("product")) twin.Add("product", new JObject());
                    JObject productInTwin = twin.Value<JObject>("product");


                    if (JToken.DeepEquals(newProductObject, productInTwin))
                    {
                        log.LogInformation("No changes in Digital Twin (colonel Product Metadata) for product: {0}", productCode);
                    }
                    else
                    {
                        log.LogInformation("Changes found in Digital Twin (colonel Product Metadata) for product: {0}", productCode);
                        productInTwin.Replace(newProductObject);
                        hasChanges = true;
                    }

                    return hasChanges;
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not update colonel Product Metadata for product", productCode);
            }
            return hasChanges;
        }

        private static JObject ConvertProductToJObject(Product product)
            => new JObject(
                new JProperty("code", product.Code),
                new JProperty("applicationId", product.ApplicationId),
                new JProperty("bizAssessmentCompleted", product.BizAssessmentCompleted),
                new JProperty("category", product.Category),
                new JProperty("createdDate", product.CreatedDate),
                new JProperty("isReadOnly", product.IsReadOnly),
                new JProperty("name", product.Name),
                new JProperty("snowApplicationName", product.SnowApplicationName),
                new JProperty("state", product.State),
                new JProperty("udm", product.Udm)
                );
    }

}
