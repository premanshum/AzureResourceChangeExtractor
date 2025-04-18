

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
    public class CheckMetadataFunctions
    {
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly CheckMetadataStorageService _checkMetadataStorageService;

        public CheckMetadataFunctions(IcolonelUserContext colonelUserContext, CheckMetadataStorageService checkMetadataStorageService)
        {
            _colonelUserContext = colonelUserContext;
            _checkMetadataStorageService = checkMetadataStorageService;
        }

        [FunctionName("ListGlobalChecksMetadata")]
        public async Task<IActionResult> ListGlobalChecksMetadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "policies/checks")] HttpRequest req,
            ILogger log)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var metadata = await _checkMetadataStorageService.ListGlobalChecksMetadata();

            return new OkObjectResult(metadata);
        }
        [FunctionName("GetGlobalCheckMetadata")]
        public async Task<IActionResult> GetGlobalCheckMetadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "policies/checks/{code}")] HttpRequest req,
            ILogger log, string code)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var metadata = await _checkMetadataStorageService.ListGlobalChecksMetadata();
            var check = metadata.FirstOrDefault(c => c.Key == code);

            if (check != null)
                return new OkJsonResult(check);
            else
                return new NotFoundResult();
        }
        [FunctionName("GetGlobalCheckDocs")]
        public async Task<IActionResult> GetGlobalCheckDocs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "policies/checks/{code}/docs")] HttpRequest req,
            ILogger log, string code)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.All);
            var docs = await _checkMetadataStorageService.GetCheckDocumentation(code);
            if (docs != null)
                return new FileContentResult(docs, "text/markdown");
            else
                return new NotFoundResult();
        }

    }
}