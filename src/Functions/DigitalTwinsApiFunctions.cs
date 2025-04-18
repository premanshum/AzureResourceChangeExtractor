using System;
using System.Net.Http;
using System.Threading.Tasks;
using colonel.Policies.Services;
using colonel.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace colonel.Policies
{
    public class DigitalTwinsApiFunctions
    {
        private readonly IcolonelUserContext _colonelUserContext;
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;

        public DigitalTwinsApiFunctions(IcolonelUserContext colonelUserContext, DigitalProductTwinStorageService digitalProductTwinStorageService)
        {
            _colonelUserContext = colonelUserContext;
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
        }

        [FunctionName("ListProductDigitalTwins")]
        public async Task<IActionResult> ListProductDigitalTwins(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productCode}/digitaltwins")] HttpRequest req,
            ILogger log, string productCode)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);
            var versions = await _digitalProductTwinStorageService.ListDigitalProductTwinVersionsAsync(productCode);
            return new OkObjectResult(versions);
        }
        [FunctionName("GetProductDigitalTwinByVersion")]
        public async Task<IActionResult> GetProductDigitalTwinByVersion(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productCode}/digitaltwins/{versionId}")] HttpRequest req,
            ILogger log, string productCode, string versionId)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(ProductRoles.All, AppRoles.Administrator);

            var versionOrLatest = GetVersionOrLatest(versionId);

            var json = await _digitalProductTwinStorageService.GetDigitalProductTwinAsync(productCode, versionOrLatest);
            return new OkObjectResult(json);
        }


        [FunctionName("ListAllProductDigitalTwins")]
        public async Task<IActionResult> ListAllProductDigitalTwins(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "digitaltwins")] HttpRequest req, ILogger log)
        {
            _colonelUserContext.AsAuthorized().EnsureInRole(AppRoles.Administrator);
            var versions = await _digitalProductTwinStorageService.ListDigitalProductTwinsAsync();
            return new OkObjectResult(versions);
        }
        private string GetVersionOrLatest(string versionId) => versionId == null || versionId.Equals("latest", StringComparison.InvariantCultureIgnoreCase) ? null : versionId;
    }
}
