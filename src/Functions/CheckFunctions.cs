using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Admiral.Policies.Services;
using Admiral.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Admiral.Policies
{
    public class CheckFunctions
    {
        private readonly IAdmiralUserContext _admiralUserContext;
        private readonly CheckMetadataStorageService _checkMetadataStorageService;

        public CheckFunctions(IAdmiralUserContext admiralUserContext, CheckMetadataStorageService checkMetadataStorageService)
        {
            _admiralUserContext = admiralUserContext;
            _checkMetadataStorageService = checkMetadataStorageService;
        }

        [FunctionName("UpdateCheckMetadata")]
        public async Task UpdateCheckMetadata([QueueTrigger("metadata-changes", Connection = "Policies")] string json, ILogger log,
            [Queue("evaulate-all-products", Connection = "Policies")] IAsyncCollector<string> triggerMessages)
        {
            var inputMessage = JObject.Parse(json);
            var metadataVersion = inputMessage.Value<string>("version");

            log.LogInformation("Processing new version of Check Metadata. Version: {0}", metadataVersion);

            await _checkMetadataStorageService.UpdateCheckMetadataFromMdFilesAsync();

            await triggerMessages.AddAsync(JsonConvert.SerializeObject(new { metadataVersion = metadataVersion }));
        }
    }
}
