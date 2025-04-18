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

namespace colonel.Policies
{
    public class OpaBundlesApiFunctions
    {
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly BundleStorageAccountService _storageAccountService;

        public OpaBundlesApiFunctions(IcolonelUserContext colonelUserContext, BundleStorageAccountService storageAccountService)
        {
            _colonelUserContext = colonelUserContext;
            _storageAccountService = storageAccountService;
        }

        [FunctionName("GetOpaBundle")]
        public async Task<IActionResult> GetDiscoveryBundle(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "opa/bundles/{name}")] HttpRequest req, string name, ILogger log)
        {
            log.LogInformation($"Getting {name} bundle");
            var content = await _storageAccountService.DownloadBundleAsync(name);

            return new FileContentResult(content, new Microsoft.Net.Http.Headers.MediaTypeHeaderValue("application/x-gzip"));

            //_colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.All, ProductRoles.All);
        }
    }
}