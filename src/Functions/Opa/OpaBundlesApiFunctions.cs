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
using Admiral.Shared.DDD;
using Admiral.Shared;
using System.Linq;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Newtonsoft.Json.Linq;
using System.Globalization;
using Admiral.Policies.Services;

namespace Admiral.Policies
{
    public class OpaBundlesApiFunctions
    {
        private readonly IAdmiralUserContext _admiralUserContext;
        private readonly BundleStorageAccountService _storageAccountService;

        public OpaBundlesApiFunctions(IAdmiralUserContext admiralUserContext, BundleStorageAccountService storageAccountService)
        {
            _admiralUserContext = admiralUserContext;
            _storageAccountService = storageAccountService;
        }

        [FunctionName("GetOpaBundle")]
        public async Task<IActionResult> GetDiscoveryBundle(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "opa/bundles/{name}")] HttpRequest req, string name, ILogger log)
        {
            log.LogInformation($"Getting {name} bundle");
            var content = await _storageAccountService.DownloadBundleAsync(name);

            return new FileContentResult(content, new Microsoft.Net.Http.Headers.MediaTypeHeaderValue("application/x-gzip"));

            //_admiralUserContext.AsAuthorized().EnsureInRole(AppRoles.All, ProductRoles.All);
        }
    }
}