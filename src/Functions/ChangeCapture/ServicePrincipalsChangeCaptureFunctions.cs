
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using colonel.Core.Tasks;
using colonel.Policies.Services;
using colonel.Rest;
using colonel.Rest.Models;
using colonel.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json.Linq;

namespace colonel.Policies
{
    public class ServicePrincipalsChangeCaptureFunctions
    {
        private readonly IcolonelClient _colonelClient;
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly GraphServiceClient _graphServiceClient;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;
        private readonly IConfiguration _configuration;

        public ServicePrincipalsChangeCaptureFunctions(IcolonelClient colonelClient, IcolonelUserContext colonelUserContext,
            GraphServiceClient graphServiceClient, DigitalProductTwinStorageService digitalProductTwinStorageService,
            IConfiguration configuration)
        {
            _colonelClient = colonelClient;
            _colonelUserContext = colonelUserContext;
            _graphServiceClient = graphServiceClient;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
            _configuration = configuration;
        }

        [FunctionName(nameof(ServicePrincipalsChangeCaptureTimerTrigger_Test))]
        public async Task<IActionResult> ServicePrincipalsChangeCaptureTimerTrigger_Test(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "policies/sourcetriggers/aadserviceprincipals")] HttpRequest req,
            ILogger log, [DurableClient] IDurableClient starter)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var body = await req.ReadAsStringAsync();
            var products = String.IsNullOrEmpty(body) ? new string[] { } : JArray.Parse(body).Select(j => j.ToString()).ToArray();

            var response = await starter.StartNewAsAsyncOperation(_colonelUserContext, nameof(ServicePrincipalGroupChangeCapture), products, new[] { new EntityReference("policies", "change_capture") }, null);
            return response.Result;
        }

        [FunctionName(nameof(ServicePrincipalsChangeCaptureTimerTrigger))]
        public async Task ServicePrincipalsChangeCaptureTimerTrigger([TimerTrigger("0 0 4 * * *")] TimerInfo timer, ILogger log, [DurableClient] IDurableClient starter)
        {
            if (System.Diagnostics.Debugger.IsAttached) return;
            if (_configuration.GetSection("Functions").GetSection(nameof(ServicePrincipalsChangeCaptureTimerTrigger)).GetValue<bool>("Disabled"))
            {
                log.LogInformation($"Skipped execution of {nameof(ServicePrincipalsChangeCaptureTimerTrigger)} as it is disabled");
                return;
            }

            var response = await starter.StartNewAsAsyncOperation(_colonelUserContext, nameof(ServicePrincipalGroupChangeCapture), null, new[] { new EntityReference("policies", "change_capture") }, null);
            log.LogInformation("Started new AzureResourceGroupChangeCapture with id {0}", response.OrchestratorInstanceId);
        }


        [AsyncOperationInfo("Policies", "Azure AD Service Principals analysis",
            new[] { "Get all valid Products", "Update Digital Product Twins" })]
        [FunctionName(nameof(ServicePrincipalGroupChangeCapture))]
        public async Task ServicePrincipalGroupChangeCapture([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Running);
            var productFilter = context.GetInput<string[]>();

            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Running);
            var allProducts = await context.CallActivityAsync<string[]>(nameof(GetAllProductCodesSP), productFilter);
            log.LogInformation("Got {0} Products to check", allProducts.Length);
            context.SetAsyncOperationStepStatus(0, AsyncOperationStepStatusEnum.Completed);

            if (allProducts.Length == 0)
            {
                context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Skipped);
                context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"No Products to check");
                return;
            }

            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running);
            var changeCount = 0;
            for (int i = 0; i < allProducts.Length; i++)
            {
                var product = allProducts[i];
                if (i % 9 == 0)
                    context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Running, i, allProducts.Length);

                var hasChanged = await context.CallActivityAsync<bool>(nameof(UpdateProductTwinWithServicePrincipals), product);
                if (hasChanged) changeCount++;
            }
            context.SetAsyncOperationStepStatus(1, AsyncOperationStepStatusEnum.Completed, allProducts.Length, allProducts.Length);
            context.SetAsyncOperationStatus(AsyncOperationStatusEnum.Completed, $"Updated {changeCount} Digital Product Twins out of {allProducts.Length}");
        }

        [FunctionName(nameof(GetAllProductCodesSP))]
        public async Task<string[]> GetAllProductCodesSP([ActivityTrigger] string[] productFilter)
        {
            var allProducts = await _colonelClient.GetProductsAsync();

            var products = allProducts.Select(p => p.Code);
            if (productFilter?.Length > 0)
                products = products.Intersect(productFilter);

            return products.ToArray();
        }

        [FunctionName(nameof(UpdateProductTwinWithServicePrincipals))]
        public async Task<bool> UpdateProductTwinWithServicePrincipals([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var productCode = context.GetInput<string>();
            var hasChanges = false;
            try
            {
                var userCache = new ConcurrentDictionary<string, Task<JObject>>();

                var servicePrincipalsIncolonel = await _colonelClient.GetAzureADServicePrincipalsForProductAsync(productCode);
                var environmentsIncolonel = await _colonelClient.GetProductEnvironmentAsync(productCode);


                var spObjects = await Task.WhenAll(from sp in servicePrincipalsIncolonel
                                                   select BuildSPObjectAsync(sp, environmentsIncolonel, userCache, log));


                await _digitalProductTwinStorageService.UpdateDigitalProductTwinAsync(productCode, (twin, metadata) =>
                {
                    //Remove incorrectly spelled node
                    if (twin.ContainsKey("service_principles")) twin.Remove("service_principles");
                    if (!twin.ContainsKey("service_principals")) twin.Add("service_principles", new JArray());
                    JArray spsInTwin = twin.Value<JArray>("service_principles");

                    var spsInAzureADArray = new JArray(spObjects);

                    if (JToken.DeepEquals(spsInAzureADArray, spsInTwin))
                    {
                        log.LogInformation("No changes in Digital Twin (Azure AD Service Principals) for product: {0}", productCode);
                    }
                    else
                    {
                        log.LogInformation("Changes found in Digital Twin (Azure AD Service Principals) for product: {0}", productCode);
                        spsInTwin.Replace(spsInAzureADArray);
                        hasChanges = true;
                    }

                    return hasChanges;
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not update Azure AD Service Principals for product", productCode);
            }
            return hasChanges;
        }

        private async Task<JObject> BuildSPObjectAsync(AzureADServicePrincipalListReponse sp, ProductEnvironment environmentsIncolonel,
            ConcurrentDictionary<string, Task<JObject>> userCache, ILogger log)
        {

            var data = new JObject(new JProperty("objectId", sp.ServicePrincipalObjectId));
            ApplycolonelMetadataToSp(data, sp, environmentsIncolonel);
            try
            {
                var aadSp = await _graphServiceClient.ServicePrincipals[sp.ServicePrincipalObjectId].Request()
                    .Select("accountEnabled,appId,displayName,appDisplayName,keyCredentials,passwordCredentials,replyUrls,servicePrincipalType").GetAsync();
                if (aadSp == null)
                {
                    data.Add("status", "NotFoundInAzureAD");
                }
                else
                {
                    data.Add("status", aadSp.AccountEnabled == true ? "Active" : "Inactive");
                    data.Add("appId", aadSp.AppId);
                    data.Add("displayName", aadSp.DisplayName);
                    data.Add("appDisplayName", aadSp.AppDisplayName);
                    data.Add("keyCredentials", new JArray(aadSp.KeyCredentials?
                        .Select(cred => new JObject(
                            new JProperty("id", cred.KeyId),
                            new JProperty("type", cred.Type),
                            new JProperty("displayName", cred.DisplayName),
                            new JProperty("startDateTime", cred.StartDateTime),
                            new JProperty("endDateTime", cred.EndDateTime),
                            new JProperty("usage", cred.Usage)
                        ))));
                    data.Add("passwordCredentials", new JArray(aadSp.PasswordCredentials?
                        .Select(cred => new JObject(
                            new JProperty("id", cred.KeyId),
                            new JProperty("displayName", cred.DisplayName),
                            new JProperty("startDateTime", cred.StartDateTime),
                            new JProperty("endDateTime", cred.EndDateTime)
                        ))));
                    data.Add("replyUrls", new JArray(aadSp.ReplyUrls));
                    data.Add("servicePrincipalType", aadSp.ServicePrincipalType);

                    var jOwners = new JArray();
                    data.Add("owners", jOwners);
                    var owners = await _graphServiceClient.ServicePrincipals[sp.ServicePrincipalObjectId].Owners.Request().GetAsync();

                    foreach (var owner in owners)
                    {
                        jOwners.Add(await userCache.GetOrAdd(owner.Id, async id =>
                        {
                            switch (owner)
                            {
                                case Microsoft.Graph.User user:
                                    user = await _graphServiceClient.Users[user.Id].Request().Select("id,displayName,accountEnabled,employeeId,externalUserState,userPrincipalName,userType").GetAsync();
                                    return new JObject(
                                       new JProperty("id", user.Id),
                                       new JProperty("type", owner.ODataType),
                                       new JProperty("displayName", user.DisplayName),
                                       new JProperty("accountEnabled", user.AccountEnabled),
                                       new JProperty("employeeId", user.EmployeeId),
                                       new JProperty("externalUserState", user.ExternalUserState),
                                       new JProperty("userPrincipalName", user.UserPrincipalName),
                                       new JProperty("userType", user.UserType)
                                       );
                                case ServicePrincipal servicePrincipal:
                                    servicePrincipal = await _graphServiceClient.ServicePrincipals[servicePrincipal.Id].Request().Select("id,displayName,accountEnabled,servicePrincipalType").GetAsync();
                                    return new JObject(
                                       new JProperty("id", servicePrincipal.Id),
                                       new JProperty("type", owner.ODataType),
                                       new JProperty("displayName", servicePrincipal.DisplayName),
                                       new JProperty("accountEnabled", servicePrincipal.AccountEnabled),
                                       new JProperty("servicePrincipalType", servicePrincipal.ServicePrincipalType)
                                       );
                                default:
                                    return new JObject(
                                       new JProperty("id", owner.Id),
                                       new JProperty("type", owner.ODataType)
                                       );
                            }
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                data.Add("status", "NotFoundInAzureAD");
                log.LogInformation(ex, "NotFoundInAzureAD {0}", sp.ServicePrincipalObjectId);
            }
            return data;
        }

        private void ApplycolonelMetadataToSp(JObject spObject, AzureADServicePrincipalListReponse sp, ProductEnvironment environmentsIncolonel)
        {
            spObject.Add("owningTeam", sp.Owner);

            var env = environmentsIncolonel.Environments.FirstOrDefault(e => e.Code == sp.EnvironmentCode);

            if (env == null) return;

            spObject.Add("environment", new JObject {
                    {"displayName",  env.Name },
                    {"code",  env.Code },
                    {"type",  env.Type.Name},
                    {"level",  env.Type.Level.Name}
                });
        }
    }
}
