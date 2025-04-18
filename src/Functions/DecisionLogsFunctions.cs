using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using colonel.Shared.DDD;
using colonel.Shared;
using System.Linq;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Newtonsoft.Json.Linq;
using System.Globalization;
using colonel.Policies.Services;
using System.IO.Compression;
using Microsoft.Azure.Storage.Blob;
using colonel.Policies.Models;

namespace colonel.Policies
{
    public class DecisionLogsFunctions
    {
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly DecisionLogStorageService _decisionLogStorageService;
        private readonly DecisionLogViewModelService _decisionLogViewModelService;

        public DecisionLogsFunctions(IcolonelUserContext colonelUserContext, DecisionLogStorageService decisionLogStorageService, DecisionLogViewModelService decisionLogViewModelService)
        {
            _colonelUserContext = colonelUserContext;
            _decisionLogStorageService = decisionLogStorageService;
            _decisionLogViewModelService = decisionLogViewModelService;
        }

        [FunctionName("ProcessIncomingDecisionLogsFunction")]
        public async Task ProcessIncomingDecisionLogsFunction(
            [BlobTrigger("incoming-decision-logs/{name}", Connection = "Policies")] CloudBlockBlob decisionLogBlob,
            [Blob("opa-decision-log-store/{name}", FileAccess.ReadWrite, Connection = "Policies")] CloudBlockBlob opaDecisionLogStoreBlob,
            ILogger log, string name)
        {
            var json = await decisionLogBlob.DownloadTextAsync();
            var decisionLog = JsonConvert.DeserializeObject<OpaDecisionLogModel>(json);

            decisionLog.DecisionId = name.Replace(".json", "");
            decisionLog.Timestamp = DateTime.UtcNow;

            if (decisionLog.Value != null)
            {
                await Task.WhenAll(
                    //Store RAW document in blob
                    opaDecisionLogStoreBlob.UploadTextAsync(json),
                    //Update DecisionLog + VievModels
                    _decisionLogStorageService.UpdateAsync(decisionLog));
            }
            else
            {
                log.LogWarning("Missing 'Result' in OPA Decision Log");
            }

            await decisionLogBlob.DeleteAsync();
        }

        [FunctionName("ListDecisionLogsForProduct")]
        public async Task<IActionResult> ListDecisionLogsForProduct(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productCode}/policies/decisionlogs")] HttpRequest req,
            ILogger log, string productCode)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var daysback = req.Query.ContainsKey("daysback") ? int.TryParse(req.Query["daysback"].First(), out var i) ? i : 30 : 30;

            var decisionLogs = await _decisionLogViewModelService.GetHistoryAsync(productCode, daysback);
            return new OkObjectResult(decisionLogs);
        }
        [FunctionName("GetLatestDecisionLogForProduct")]
        public async Task<IActionResult> GetLatestDecisionLogForProduct(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productCode}/policies/decisionlogs/latest")] HttpRequest req,
            ILogger log, string productCode)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var decisionLogs = await _decisionLogViewModelService.GetLatestAsync(productCode);
            if (decisionLogs != null && decisionLogs.Count > 0)
                return new OkObjectResult(decisionLogs.FirstOrDefault());
            return new NotFoundResult();
        }
        [FunctionName("ListLatestDecisionLogs")]
        public async Task<IActionResult> ListLatestDecisionLogs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "policies/decisionlogs")] HttpRequest req, ILogger log)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);

            var decisionLogs = await _decisionLogViewModelService.GetLatestAsync();
            return new OkObjectResult(decisionLogs);
        }

        [FunctionName("GetDecisionLogDetailsForProduct")]
        public async Task<IActionResult> GetDecisionLogDetailsForProduct(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productCode}/policies/decisionlogs/details/{versionId}")] HttpRequest req,
            ILogger log, string productCode, string versionId)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var decisionLogs = await _decisionLogStorageService.GetDecisionLogByVersionAsync(productCode, versionId);
            if (decisionLogs != null)
                return new OkObjectResult(decisionLogs);
            return new NotFoundResult();
        }
    }
}