
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

namespace colonel.Policies
{
    public class OpaDecisionLogsFunctions
    {
        public OpaDecisionLogsFunctions()
        {
        }

        [FunctionName("PostDecisionLogsFunctions")]
        public async Task<IActionResult> PostDecisionLogs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "opa/logs")] HttpRequest req, 
            [Blob("incoming-decision-logs", Connection = "Policies")] CloudBlobContainer decisionLogBlobs,
            ILogger log)
        {

            string decompressedReqBody = string.Empty;
            using (GZipStream decompressionStream = new GZipStream(req.Body, CompressionMode.Decompress))
            {
                using (StreamReader sr = new StreamReader(decompressionStream))
                {
                    decompressedReqBody = sr.ReadToEnd();
                }
            }
            
            var json = JArray.Parse(decompressedReqBody);


            await decisionLogBlobs.CreateIfNotExistsAsync();

            var tasks = new List<Task>();
            foreach (JObject decisionJson in json)
            {
                var path = decisionJson.Value<string>("path")?.ToLowerInvariant();
                var decisionId = decisionJson.Value<string>("decision_id");
                switch (path)
                {
                    case "ACME/full_cap_check":
                        //Store content in blob (can be above 64KB)
                        var cloudBlockBlob = decisionLogBlobs.GetBlockBlobReference($"{decisionId}.json");
                        tasks.Add(cloudBlockBlob.UploadTextAsync(decisionJson.ToString()));
                        break;
                    default:
                        log.LogInformation($"Unknown path in DecisionLog. Skipping. Path: {path}");
                        break;
                }
            }

            await Task.WhenAll(tasks);

            return new OkResult();
        }
    }
}
