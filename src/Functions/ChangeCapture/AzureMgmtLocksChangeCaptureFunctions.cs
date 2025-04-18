

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Admiral.Core.Tasks;
using Admiral.Policies.Services;
using Admiral.Rest;
using Admiral.Rest.Models;
using Admiral.Shared;
using Azure.Identity;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Newtonsoft.Json.Linq;

namespace Admiral.Policies
{
    public class AzureMgmtLocksChangeCaptureFunctions
    {
        private readonly IAdmiralClient _admiralClient;
        private readonly IAdmiralUserContext _admiralUserContext;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;
        private readonly IConfiguration _configuration;
        private readonly IOptions<AzureResourceGraphOptions> _options;

        public AzureMgmtLocksChangeCaptureFunctions(IAdmiralClient admiralClient, IAdmiralUserContext admiralUserContext,
            DigitalProductTwinStorageService digitalProductTwinStorageService,
            IConfiguration configuration, IOptions<AzureResourceGraphOptions> options)
        {
            _admiralClient = admiralClient;
            _admiralUserContext = admiralUserContext;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
            _configuration = configuration;
            _options = options;
        }

        [FunctionName(nameof(AzureManagementLocksChangeCaptureTimerTrigger_Test))]
        public async Task<IActionResult> AzureManagementLocksChangeCaptureTimerTrigger_Test(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "policies/sourcetriggers/azuremgmtlocks")] HttpRequest req,
            ILogger log, [DurableClient] IDurableClient starter)
        {
            _admiralUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var body = await req.ReadAsStringAsync();
            var products = String.IsNullOrEmpty(body) ? new string[] { } : JArray.Parse(body).Select(j => j.ToString()).ToArray();

            var response = await starter.StartNewAsAsyncOperation(_admiralUserContext, nameof(AzureManagementLocksChangeCapture), products, new[] { new EntityReference("policies", "change_capture") }, null);
            return response.Result;
        }

        [FunctionName(nameof(AzureManagementLocksChangeCaptureTimerTrigger))]
        public async Task AzureManagementLocksChangeCaptureTimerTrigger([TimerTrigger("0 0 5,17 * * *")] TimerInfo timer, ILogger log, [DurableClient] IDurableClient starter)
        {
            if (System.Diagnostics.Debugger.IsAttached) return;
            if (_configuration.GetSection("Functions").GetSection(nameof(AzureManagementLocksChangeCaptureTimerTrigger)).GetValue<bool>("Disabled"))
            {
                log.LogInformation($"Skipped execution of {nameof(AzureManagementLocksChangeCaptureTimerTrigger)} as it is disabled");
                return;
            }

            var response = await starter.StartNewAsAsyncOperation(_admiralUserContext, nameof(AzureManagementLocksChangeCapture), null, new[] { new EntityReference("policies", "change_capture") }, null);
            log.LogInformation("Started new AzureManagementLocksChangeCapture with id {0}", response.OrchestratorInstanceId);
        }

        [AsyncOperationInfo("Policies", "Azure Management Locks analysis",
            new[] { "Get all valid Products", "Get all Azure Subscriptions", "Get all Management Locks", "Update Digital Product Twins" })]
        [FunctionName(nameof(AzureManagementLocksChangeCapture))]
        public async Task AzureManagementLocksChangeCapture([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Running);

            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Running);
            var allDigitalTwinsTask = context.CallActivityAsync<ProductDigitalTwinMetadata[]>(nameof(CommonActivities.GetAllDigitalTwinMetadata), null);
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running);
            var allSubscriptions = await context.CallActivityAsync<string[]>(nameof(GetAllSubscriptionIds), null);
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Completed);
            context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Running);
            var allLocksTask = context.CallActivityAsync<DigitalTwinMgmtLock[]>(nameof(GetAllManagementLocks), allSubscriptions);

            await Task.WhenAll(allDigitalTwinsTask, allLocksTask);
            context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Completed);

            ProductDigitalTwinMetadata[] allDigitalTwins = null;
            DigitalTwinMgmtLock[] allLocks = null;
            if (allDigitalTwinsTask.IsCompletedSuccessfully)
            {
                allDigitalTwins = allDigitalTwinsTask.Result;
                var productFilter = context.GetInput<string[]>();
                if (productFilter != null && productFilter.Length > 0)
                    allDigitalTwins = allDigitalTwins.Where(p => productFilter.Contains(p.ProductCode)).ToArray();

                context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Completed);
            }
            else
            {
                context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Failed);
                context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Failed, "Failed to get Digital Product Twins");
                throw new Exception($"{nameof(allDigitalTwinsTask)} failed", allDigitalTwinsTask.Exception);
            }

            if (allLocksTask.IsCompletedSuccessfully)
            {
                allLocks = allLocksTask.Result;
                context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Completed);
            }
            else
            {
                context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Failed);
                context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Failed, "Failed to get Azure Management Locks");
                throw new Exception($"{nameof(allLocksTask)} failed", allLocksTask.Exception);
            }

            context.SetAsyncOperationStepStatus(3, AsyncOperationStepStatusEnum.Running);
            var changeCount = 0;
            for (int i = 0; i < allDigitalTwins.Length; i++)
            {
                var twin = allDigitalTwins[i];
                if (i % 9 == 0)
                    context.SetAsyncOperationStepStatus(2, AsyncOperationStepStatusEnum.Running, i, allDigitalTwins.Length);

                var hasChanged = await context.CallActivityAsync<bool>(nameof(UpdateProductTwinWithManagementLocks), (twin.ProductCode, allLocks));
                if (hasChanged) changeCount++;
            }
            context.SetAsyncOperationStepStatus(3, AsyncOperationStepStatusEnum.Completed, allDigitalTwins.Length, allDigitalTwins.Length);
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"Updated {changeCount} Digital Product Twins out of {allDigitalTwins.Length}");
        }

        [FunctionName(nameof(GetAllSubscriptionIds))]
        public async Task<string[]> GetAllSubscriptionIds([ActivityTrigger] IDurableActivityContext context)
        {
            var list = await _admiralClient.GetSubscriptionsAsync();
            return list.Select(sub => sub.SubscriptionId).ToArray();
        }
        [FunctionName(nameof(GetAllManagementLocks))]
        public async Task<DigitalTwinMgmtLock[]> GetAllManagementLocks([ActivityTrigger] string[] allSubscriptions)
        {
            var allLocks = new List<ManagementLockObject>();
            foreach (var subscriptionId in allSubscriptions)
            {
                var client = new ResourcesManagementClient(subscriptionId, new ClientSecretCredential(_options.Value.TenantId, _options.Value.ClientId, _options.Value.ClientSecret));
                var locks = await client.ManagementLocks.ListAtSubscriptionLevelAsync().GetAllAsync();
                allLocks.AddRange(locks);
            }

            var enrichedLocks = (from l in allLocks
                                 let scopeId = l.Id.Substring(0, l.Id.Length - $"/providers/Microsoft.Authorization/locks/{l.Name}".Length)
                                 let parts = scopeId.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                 orderby scopeId
                                 select new DigitalTwinMgmtLock
                                 {
                                     lockId = l.Id,
                                     lockType = l.Level.ToString(),
                                     lockNote = l.Notes,
                                     scopeId = scopeId,
                                     //Below is code to chunck up the id into the individual parts
                                     //        PART 0        PART 1                               PART 2         PART 3                       PART 4    PART 5                  PART 6          PART 7                      
                                     //"id": "/subscriptions/89731d00-1ebf-435f-919e-45920d73b538/resourceGroups/rgpazewpmgisnamegen8c001    /providers/Microsoft.Storage      /storageAccounts/100b00aa3bslsigmpwezagts/providers/Microsoft.Authorization/locks/100b00aa3bslsigmpwezagts",
                                     //"id": "/subscriptions/ee10ebdd-0c1e-4bf2-80fa-216b6ec3840e/resourceGroups/rgpazeweumlitjtdev001       /providers/Microsoft.Storage      /storageAccounts/saccounteventhubjtdev"
                                     //"id": "/subscriptions/89731d00-1ebf-435f-919e-45920d73b538/resourceGroups/rgpazewpsoe001costmonitoring/providers/Microsoft.Authorization/locks          /rg_test_lock"
                                     scopeSubscriptionId = parts[1],
                                     scopeResourceGroup = parts.Length >= 4 ? parts[3] : null,
                                     scopeResource = parts.Length == 8 ? parts.Last() : null,
                                     scopeLevel = parts.Length == 2 ? "Subscription" : parts.Length == 4 ? "ResourceGroup" : "Resource"

                                 }).ToArray();

            return enrichedLocks;
        }

        [FunctionName(nameof(UpdateProductTwinWithManagementLocks))]
        public async Task<bool> UpdateProductTwinWithManagementLocks([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var (productCode, allLocks) = context.GetInput<(string, DigitalTwinMgmtLock[])>();
            var hasChanges = false;
            try
            {

                await _digitalProductTwinStorageService.UpdateDigitalProductTwinAsync(productCode, (twin, metadata) =>
                {
                    var newLocks = new List<DigitalTwinMgmtLock>();
                    if (twin.ContainsKey("azure_resources"))
                    {
                        foreach (var resource in (JArray)twin["azure_resources"])
                        {
                            var id = resource.Value<string>("id");
                            var subscriptionId = resource.Value<string>("subscriptionId");
                            var resourceGroup = resource.Value<string>("resourceGroup");

                            var locksInScope = GetLocksInScopeForResource(subscriptionId, resourceGroup, id, allLocks);
                            foreach (var scopedLock in locksInScope)
                            {
                                if (!newLocks.Contains(scopedLock)) newLocks.Add(scopedLock);

                                scopedLock.lockedResources.Add(id);
                            }
                        }
                    }

                    //Sort locks to make comparison better
                    newLocks = newLocks.OrderBy(l => l.lockId).ToList();
                    foreach (var l in newLocks)
                        l.lockedResources.Sort();

                    var newLocksArray = new JArray(newLocks.Select(JObject.FromObject));

                    if (!twin.ContainsKey("azure_management_locks")) twin.Add("azure_management_locks", new JArray());
                    JArray locksInTwin = twin.Value<JArray>("azure_management_locks");


                    if (JToken.DeepEquals(newLocksArray, locksInTwin))
                    {
                        log.LogInformation("No changes in Digital Twin (Azure Management Locks) for product: {0}", productCode);
                    }
                    else
                    {
                        log.LogInformation("Changes found in Digital Twin (Azure Management Locks) for product: {0}", productCode);
                        locksInTwin.Replace(newLocksArray);
                        hasChanges = true;
                    }

                    return hasChanges;
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not update Azure Management Locks for product", productCode);
            }
            return hasChanges;
        }

        private IEnumerable<DigitalTwinMgmtLock> GetLocksInScopeForResource(string subscriptionId, string resourceGroup, string id, DigitalTwinMgmtLock[] allLocks)
        {
            foreach (var l in allLocks)
            {
                switch (l.scopeLevel)
                {
                    case "Resource":
                        if (l.scopeId == id) yield return l;
                        break;
                    case "ResourceGroup":
                        if (l.scopeResourceGroup == resourceGroup && l.scopeSubscriptionId == subscriptionId) yield return l;
                        break;
                    case "Subscription":
                        if (l.scopeSubscriptionId == subscriptionId) yield return l;
                        break;
                }
            }
        }

        public class DigitalTwinMgmtLock
        {
            public string lockId { get; set; }
            public string lockType { get; set; }
            public string lockNote { get; set; }
            public string scopeLevel { get; set; } //Resource, ResourceGroup or Subscription
            public string scopeSubscriptionId { get; set; }
            public string scopeResourceGroup { get; set; }
            public string scopeResource { get; set; }
            public string scopeId { get; set; }
            public List<string> lockedResources { get; set; } = new List<string>();
        }

    }

}
