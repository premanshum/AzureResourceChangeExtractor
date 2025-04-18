
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Admiral.Core.Tasks;
using Admiral.Policies.Properties;
using Admiral.Policies.Services;
using Admiral.Rest;
using Admiral.Rest.Models;
using Admiral.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Admiral.Policies
{
    public class AzureResourceGroupChangeCaptureFunctions
    {
        private readonly IAdmiralClient _admiralClient;
        private readonly ActivityLogQueryHelper _logAuditQueryHelper;
        private readonly AzureResourceGraphHelper _azureResourceGraphHelper;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;
        private readonly IConfiguration _configuration;
        private readonly IAdmiralUserContext _admiralUserContext;

        public AzureResourceGroupChangeCaptureFunctions(IAdmiralClient admiralClient, ActivityLogQueryHelper logAuditQueryHelper,
            AzureResourceGraphHelper azureResourceGraphHelper, DigitalProductTwinStorageService digitalProductTwinStorageService,
            IConfiguration configuration, IAdmiralUserContext admiralUserContext)
        {
            _admiralClient = admiralClient;
            _logAuditQueryHelper = logAuditQueryHelper;
            _azureResourceGraphHelper = azureResourceGraphHelper;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
            _configuration = configuration;
            _admiralUserContext = admiralUserContext;
        }

        [FunctionName(nameof(AzureResourceGroupChangeCaptureTimerTrigger_Test))]
        public async Task<IActionResult> AzureResourceGroupChangeCaptureTimerTrigger_Test(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "policies/sourcetriggers/azureresourcegroups")] HttpRequest req,
            ILogger log, [DurableClient] IDurableClient starter)
        {
            _admiralUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var response = await starter.StartNewAsAsyncOperation(_admiralUserContext, nameof(AzureResourceGroupChangeCapture), (object)"20m", new[] { new EntityReference("policies", "change_capture") }, null);
            return response.Result;
        }

        [FunctionName(nameof(AzureResourceGroupChangeCaptureTimerTrigger))]
        public async Task AzureResourceGroupChangeCaptureTimerTrigger([TimerTrigger("0 */5 * * * *")] TimerInfo timer, ILogger log, [DurableClient] IDurableClient starter)
        {
            if (System.Diagnostics.Debugger.IsAttached) return;
            if (_configuration.GetSection("Functions").GetSection(nameof(AzureResourceGroupChangeCaptureTimerTrigger)).GetValue<bool>("Disabled"))
            {
                log.LogInformation($"Skipped execution of {nameof(AzureResourceGroupChangeCaptureTimerTrigger)} as it is disabled");
                return;
            }

            var response = await starter.StartNewAsAsyncOperation(_admiralUserContext, nameof(AzureResourceGroupChangeCapture), (object)"6m", new[] { new EntityReference("policies", "change_capture") }, null);
            log.LogInformation("Started new AzureResourceGroupChangeCapture with id {0}", response.OrchestratorInstanceId);
        }

        [AsyncOperationInfo("Policies", "Azure Resource Group change analysis",
            new[] { "Detect changed Resource groups", "Map to Products", "Update Digital Product Twins" })]
        [FunctionName(nameof(AzureResourceGroupChangeCapture))]
        public async Task AzureResourceGroupChangeCapture([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Running);
            var lookBack = context.GetInput<object>();

            //Get changed RGs
            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Running);
            var changedResourceGroups = await context.CallActivityAsync<ChangedResourceGroups[]>(nameof(GetChangedResourceGroups), lookBack);
            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Completed);
            log.LogInformation("Got {0} changed Resource Groups", changedResourceGroups.Length);

            if (changedResourceGroups.Length == 0)
            {
                context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Skipped);
                context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Skipped);
                context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, "No modified Resource Groups found");
                return;
            }

            //Get detect affected products
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running);
            var affectedProducts = await context.CallActivityAsync<AffectedProduct[]>(nameof(GetAffectedProducts), changedResourceGroups);
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Completed);
            log.LogInformation("Got {0} affected Products: {1}", affectedProducts.Length, String.Join(", ", affectedProducts.Select(p => p.ProductCode)));

            if (affectedProducts.Length == 0)
            {
                context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Skipped);
                context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, "No affected Admiral Products found in list for Resource Groups");
                return;
            }

            //Update Twins
            context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Running, 0, affectedProducts.Length);
            for (var i = 0; i < affectedProducts.Length; i++)
            {
                var product = affectedProducts[i];
                context.AddEntityReferencesToAsyncOperation(new EntityReference("product", product.ProductCode));
                await context.CallActivityAsync(nameof(UpdateProductTwinWithAzureResources), product);
                if (i % 9 == 0)
                    context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Running, i, affectedProducts.Length);
            }
            context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Completed, affectedProducts.Length, affectedProducts.Length);

            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"Digital Product Twins updated for {affectedProducts.Length} Admiral Products");
        }

        [FunctionName(nameof(AzureResourceGroupDailyTimerTrigger))]
        public async Task AzureResourceGroupDailyTimerTrigger([TimerTrigger("0 30 3 * * *")] TimerInfo timer, ILogger log, [DurableClient] IDurableClient starter)
        {
            if (System.Diagnostics.Debugger.IsAttached) return;
            if (_configuration.GetSection("Functions").GetSection(nameof(AzureResourceGroupDailyTimerTrigger)).GetValue<bool>("Disabled"))
            {
                log.LogInformation($"Skipped execution of {nameof(AzureResourceGroupDailyTimerTrigger)} as it is disabled");
                return;
            }

            var response = await starter.StartNewAsAsyncOperation(_admiralUserContext, nameof(AzureResourceGroupTriggerAll), new[] { new EntityReference("policies", "change_capture") }, null);
            log.LogInformation("Started new AzureResourceGroupTriggerAll with id {0}", response.Result);
        }

        [AsyncOperationInfo("Policies", "Azure Resource Group manual analysis",
            new[] { "Get all valid products", "Update Digital Product Twins" })]
        [FunctionName(nameof(AzureResourceGroupTriggerAll))]
        public async Task AzureResourceGroupTriggerAll([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Running);

            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Running);
            var allProductCodes = await context.CallActivityAsync<string[]>("GetAllProductCodes", null);
            log.LogInformation("Got {0} products", allProductCodes.Length);
            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Completed);

            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running, 0, allProductCodes.Length);
            for (var i = 0; i < allProductCodes.Length; i++)
            {
                var productCode = allProductCodes[i];
                await context.CallActivityAsync(nameof(UpdateProductTwinWithAzureResources), new AffectedProduct { ProductCode = productCode });
                context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running, i, allProductCodes.Length);
            }
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Completed, allProductCodes.Length, allProductCodes.Length);
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"Digital Product Twins updated for {allProductCodes.Length} Admiral Products");
        }





        [FunctionName(nameof(GetAllProductCodes))]
        public async Task<string[]> GetAllProductCodes([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var allProducts = await _admiralClient.GetProductsAsync();

            return allProducts.Select(p => p.Code).ToArray();
        }


        [FunctionName(nameof(GetChangedResourceGroups))]
        public async Task<ChangedResourceGroups[]> GetChangedResourceGroups([ActivityTrigger] IDurableActivityContext context)
        {
            var lookBack = (string)context.GetInput<object>();
            var query = String.Format(Resources.GetRecentResourceChangesQuery, lookBack);

            var changes = await _logAuditQueryHelper.QueryLogs(query);

            var result = (from change in changes.AsEnumerable()
                          group change
                          by new { SubscriptionId = change.Field<string>("SubscriptionId"), ResourceGroup = change.Field<string>("ResourceGroup") }
                          into grp
                          select new ChangedResourceGroups
                          {
                              SubscriptionId = grp.Key.SubscriptionId,
                              ResourceGroup = grp.Key.ResourceGroup,
                              Callers = grp.Select(c => new Caller { Type = c.Field<string>("PrincipalType"), Id = c.Field<string>("Caller") }).ToArray()
                          }).ToArray();

            return result;
        }
        [FunctionName(nameof(GetAffectedProducts))]
        public async Task<AffectedProduct[]> GetAffectedProducts([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var changedResourceGroups = context.GetInput<ChangedResourceGroups[]>();

            log.LogInformation("Checking the following Resource Groups for matching products in Admiral: {0}",
                String.Join(", ", changedResourceGroups.Select(rg => $"{rg.SubscriptionId}/{rg.ResourceGroup}")));

            ///1. Go through all RG and try to find it in Admiral
            ///2. If not there; skip, otherwise group by product Code to get unique products
            ///3. Return a nice strucutre
            var changedProducts = (await Task.WhenAll(changedResourceGroups.Select(async rg =>
            {
                var id = $"subscriptions/{rg.SubscriptionId.ToLower()}/resourceGroups/{rg.ResourceGroup.ToLowerInvariant()}";
                var product = await _admiralClient.GetAzureResourceGroupByIdWithHttpMessagesAsync(id);
                switch (product.Response.StatusCode)
                {
                    case System.Net.HttpStatusCode.OK:
                        log.LogInformation("Matched Resource Group to Product: {0}/{1} => {2}", rg.SubscriptionId, rg.ResourceGroup, product.Body.Code);
                        return (product.Body.Code, rg.Callers, id);
                    case System.Net.HttpStatusCode.NotFound:
                        log.LogInformation("No match found for Resource Group : {0}/{1}", rg.SubscriptionId, rg.ResourceGroup);
                        return (null, null, null);
                    default:
                        log.LogError("Could not get product verification for resource group: {0}/{1}", rg.SubscriptionId, rg.ResourceGroup);
                        return (null, null, null);
                }
            })))
            .Where(p => p.Code != null)
            .GroupBy(p => p.Code)
            .Select(p =>
                new AffectedProduct
                {
                    ProductCode = p.Key,
                    Callers = p.SelectMany(p => p.Callers)
                               .GroupBy(c => c.Type + c.Id)
                               .Select(c => c.First()).ToArray(),
                    ChangedResourceGroups = p.Select(p => p.id).Distinct().ToArray()
                }).ToArray();

            log.LogInformation("Got {0} Products: {1}", changedProducts.Length, String.Join(", ", changedProducts.Select(p => p.ProductCode)));
            return changedProducts;
        }



        [FunctionName(nameof(UpdateProductTwinWithAzureResources))]
        public async Task UpdateProductTwinWithAzureResources([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var product = context.GetInput<AffectedProduct>();
            try
            {
                var rgsInAdmiral = await _admiralClient.GetAzureResourceGroupsForProductAsync(product.ProductCode);
                var environmentsInAdmiral = await _admiralClient.GetProductEnvironmentAsync(product.ProductCode);

                var resourcesInAzure = (await Task.WhenAll(from rg in rgsInAdmiral
                                                           group rg by rg.SubscriptionId into subs
                                                           let query = $"resources | where resourceGroup in~ ({String.Join(',', subs.Select(rg => $"'{rg.Name}'").Distinct())}) | order by subscriptionId asc, resourceGroup asc"
                                                           select _azureResourceGraphHelper.QueryAsync(query, new[] { subs.Key })
                                        ))
                                        .SelectMany(r => r).ToArray();


                ApplyAdmiralMetadataToResources(resourcesInAzure, rgsInAdmiral, environmentsInAdmiral);

                await _digitalProductTwinStorageService.UpdateDigitalProductTwinAsync(product.ProductCode, (twin, metadata) =>
                {

                    var hasChanges = false;

                    if (!twin.ContainsKey("azure_resources")) twin.Add("azure_resources", new JArray());
                    JArray resourcesInTwin = twin.Value<JArray>("azure_resources");

                    var resourcesInAzureArray = new JArray(resourcesInAzure);

                    if (JToken.DeepEquals(resourcesInAzureArray, resourcesInTwin))
                    {
                        log.LogInformation("No changes in Digital Twin (Azure Resources) for product: {0}", product.ProductCode);
                    }
                    else
                    {
                        log.LogInformation("Changes found in Digital Twin (Azure Resources) for product: {0}", product.ProductCode);
                        resourcesInTwin.Replace(resourcesInAzureArray);
                        hasChanges = true;
                    }

                    if (!twin.ContainsKey("azure_deployment_principals")) twin.Add("azure_deployment_principals", new JArray());
                    JArray azureDeploymentPrincipals = twin.Value<JArray>("azure_deployment_principals");
                    hasChanges = hasChanges || ApplyDeploymentPrincipals(azureDeploymentPrincipals, resourcesInAzure, product);

                    return hasChanges;
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not update AzureResources for product", product.ProductCode);
            }

        }


        private void ApplyAdmiralMetadataToResources(JObject[] resourcesInAzure, IList<AzureResourceGroup> rgsInAdmiral, ProductEnvironment environmentsInAdmiral)
        {
            foreach (var resource in resourcesInAzure)
            {
                var rgInAzure = rgsInAdmiral.FirstOrDefault(rg => rg.SubscriptionId.Equals(resource.Value<string>("subscriptionId"), StringComparison.InvariantCultureIgnoreCase) &&
                                                                  rg.Name.Equals(resource.Value<string>("resourceGroup"), StringComparison.InvariantCultureIgnoreCase));

                if (rgInAzure == null) continue;
                resource.Add("owningTeam", rgInAzure.Owner);

                var env = environmentsInAdmiral.Environments.FirstOrDefault(e => e.Code == rgInAzure.EnvironmentCode);

                if (env == null) continue;

                resource.Add("environment", new JObject {
                    {"displayName",  env.Name },
                    {"code",  env.Code },
                    {"type",  env.Type.Name},
                    {"level",  env.Type.Level.Name}
                });

            }
        }
        private bool ApplyDeploymentPrincipals(JArray azureDeploymentPrincipals, JObject[] resourcesInAzure, AffectedProduct product)
        {
            var deployedTo = new JArray((from rg in resourcesInAzure
                                         let id = $"subscriptions/{rg.Value<string>("subscriptionId").ToLowerInvariant()}/resourceGroups/{rg.Value<string>("resourceGroup").ToLowerInvariant()}"
                                         where product.ChangedResourceGroups.Contains(id)
                                         let env = rg.Value<JObject>("environment")
                                         group env by env.Value<string>("code") into byCode
                                         select byCode.First()
                                         ).ToArray());

            if (product.Callers != null && product.Callers.Any())
            {
                var deploymentPrincipals = new JArray((from caller in product.Callers
                                                       orderby caller.Type, caller.Id
                                                       select new JObject(
                                                           new JProperty("id", caller.Id),
                                                           new JProperty("type", caller.Type),
                                                           new JProperty("deployedTo", deployedTo)
                                                           )
                                                       ).ToArray());

                if (JToken.DeepEquals(azureDeploymentPrincipals, deploymentPrincipals))
                {
                    return false;
                }
                else
                {
                    azureDeploymentPrincipals.Replace(deploymentPrincipals);
                    return true;
                }
            }
            return false;
        }

        public class ChangedResourceGroups
        {
            public string SubscriptionId { get; set; }
            public string ResourceGroup { get; set; }
            public Caller[] Callers { get; set; }
        }
        public class AffectedProduct
        {
            public string ProductCode { get; set; }
            public Caller[] Callers { get; set; }
            public string[] ChangedResourceGroups { get; internal set; }
        }

        public class Caller
        {
            public string Type { get; set; }
            public string Id { get; set; }
        }

        public class ModifyingPrincipal : Caller
        {
            public DateTime LastDeployment { get; set; }
            public JArray DeployedTo { get; set; }
        }
    }
}
