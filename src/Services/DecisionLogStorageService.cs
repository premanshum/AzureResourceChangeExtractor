
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Admiral.Policies.Models;
using Admiral.Rest.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Admiral.Policies.Services
{
    public class DecisionLogStorageService
    {
        private static JsonSerializerSettings _jsonSerializerSettings;
        private BlobContainerClient _blobClient;
        private readonly IOptions<Options> _options;
        private readonly ILogger<DecisionLogStorageService> _logger;
        private readonly DecisionLogViewModelService _viewModelService;
        private readonly CheckMetadataStorageService _checkMetadataStorageService;
        private const string ContainerName = "decision-log-store";

        public DecisionLogStorageService(IOptions<Options> options, ILogger<DecisionLogStorageService> logger,
            DecisionLogViewModelService viewModelService, CheckMetadataStorageService checkMetadataStorageService)
        {
            _options = options;
            _logger = logger;
            _viewModelService = viewModelService;
            _checkMetadataStorageService = checkMetadataStorageService;
            if (_jsonSerializerSettings == null)
            {
                _jsonSerializerSettings = new JsonSerializerSettings
                {
                    Formatting = Formatting.None,
                    NullValueHandling = NullValueHandling.Ignore
                };
                _jsonSerializerSettings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
            }
        }

        private async Task<BlobContainerClient> GetBlobContainerClientAsync()
        {
            if (_blobClient == null)
            {
                _blobClient = new BlobContainerClient(_options.Value.StorageConnectionString, ContainerName);

                await _blobClient.CreateIfNotExistsAsync();
            }
            return _blobClient;
        }
        public async Task UpdateAsync(OpaDecisionLogModel opaDecisionLog)
        {

            var productCode = opaDecisionLog.Value.Metadata.ProductCode;

            var currentDecisionLog = await GetCurrentDecisionLogOrDefaultAsync(productCode);
            var newDecisionLog = MergeWithCurrentDecisionLog(currentDecisionLog, opaDecisionLog);

            await ApplySeverityToChecksAsync(newDecisionLog.Checks, productCode);
            var newVersionId = await UpdateDecisionLogAsync(productCode, newDecisionLog);


            await _viewModelService.UpdateViewModelsAsync(newDecisionLog, newVersionId, opaDecisionLog.Timestamp);

        }

        private PolicyDecisionLogDetails MergeWithCurrentDecisionLog(PolicyDecisionLogDetails currentDecisionLog, OpaDecisionLogModel opaDecisionLog)
        {
            var newLog = new PolicyDecisionLogDetails
            {
                ProductCode = currentDecisionLog.ProductCode,
                OpaDecisionLogId = opaDecisionLog.DecisionId,
                Changes = new List<PolicyDecisionLogDetailsChangesItem>(),
                Checks = new List<PolicyDecisionLogDetailsChecksItem>()
            };

            foreach (var check in opaDecisionLog.Value.Checks)
            {
                //Add checks from OPA to new Log
                var affected = GetAffected(check.Value.Affected);
                var newState = affected == null ? PolicyDecisionLogCheckState.Inconclusive :
                               affected.Count == 0 ? PolicyDecisionLogCheckState.Passed :
                               PolicyDecisionLogCheckState.Failed;

                newLog.Checks.Add(new PolicyDecisionLogDetailsChecksItem
                {
                    Key = check.Key,
                    Total = check.Value.Total,
                    Affected = affected,
                    State = newState,
                    Error = affected == null ? "Affected-property could not be parsed. Expected either Object, Array[name], String or null." : null
                });

                var newResources = affected == null ? new List<string>() : affected.Select(a => a.Key).ToList();

                //Find changes from existing
                var existingCheck = currentDecisionLog.Checks.FirstOrDefault(c => c.Key == check.Key);
                if (existingCheck == null)
                {
                    //New Check added
                    newLog.Changes.Add(new PolicyDecisionLogDetailsChangesItem
                    {
                        CheckKey = check.Key,
                        Type = PolicyDecisionLogChangeType.NewCheck,
                        ResourcesAdded = newResources,
                        CurrentCheckState = newState
                    });
                }
                else
                {
                    //Existing check, check if anything was added or removed
                    var existingResources = existingCheck.Affected.Select(a => a.Key).ToList();

                    var addedDiff = newResources.Except(existingResources);
                    var removedDiff = existingResources.Except(newResources);
                    if (addedDiff.Any() || removedDiff.Any())
                    {
                        newLog.Changes.Add(new PolicyDecisionLogDetailsChangesItem
                        {
                            CheckKey = check.Key,
                            Type = PolicyDecisionLogChangeType.ResourceChanges,
                            ResourcesAdded = addedDiff.ToList(),
                            ResourcesRemoved = removedDiff.ToList(),
                            CurrentCheckState = newState,
                            PreviousCheckState = existingCheck.State
                        });
                    }
                    else
                    {
                        //No changes
                    }
                }
            }

            //Add checks that has been removed since last time
            foreach (var item in currentDecisionLog.Checks)
            {
                if (!newLog.Checks.Any(c => c.Key == item.Key))
                {
                    newLog.Changes.Add(new PolicyDecisionLogDetailsChangesItem
                    {
                        CheckKey = item.Key,
                        Type = PolicyDecisionLogChangeType.CheckRemoved,
                        PreviousCheckState = item.State
                    });
                }
            }

            return newLog;
        }

        private async Task ApplySeverityToChecksAsync(IList<PolicyDecisionLogDetailsChecksItem> checks, string productCode)
        {

            var metadataList = await _checkMetadataStorageService.ListProductAdjustedChecksMetadata(productCode);

            foreach (var check in checks)
            {
                var metadata = metadataList.FirstOrDefault(m => m.Key == check.Key);
                if (metadata == null)
                {
                    _logger.LogWarning("Could not find metadata for check: {0}. Unable to determine Severity etc.", check.Key);
                    continue;
                }

                check.Severity = metadata.Severity;
            }
        }

        private IDictionary<string, object> GetAffected(JToken affected)
        {
            var dic = new Dictionary<string, object>();

            switch (affected.Type)
            {
                case JTokenType.Object:
                    //add each property as an item
                    foreach (var p in (JObject)affected)
                        dic.Add(p.Key, p.Value);
                    break;
                case JTokenType.Array:
                    //add each element as an item
                    var arr = (JArray)affected;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var item = arr[i];
                        switch (item.Type)
                        {
                            case JTokenType.None:
                                break;
                            case JTokenType.Object:
                                {
                                    var data = (JObject)item.DeepClone();
                                    data.Remove("name");
                                    var name = item.Value<string>("name");
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        _logger.LogWarning("Affected object did not have a 'name' property: {0}", item.ToString());
                                    }
                                    else if (dic.ContainsKey(name))
                                    {
                                        _logger.LogWarning("Affected object already have an entry with that name : {0}", name);
                                    }
                                    else
                                        dic.Add(name, data);
                                }
                                break;
                            case JTokenType.Integer:
                            case JTokenType.Float:
                            case JTokenType.String:
                            case JTokenType.Date:
                            case JTokenType.Guid:
                            case JTokenType.Uri:
                                {
                                    var name = item.ToString();
                                    if (dic.ContainsKey(name))
                                    {
                                        _logger.LogWarning("Affected object already have an entry with that name : {0}", name);
                                    }
                                    else
                                        dic.Add(name, item);
                                }
                                break;
                            default:
                                dic.Add(i.ToString(), item);
                                break;
                        }
                    }
                    break;
                case JTokenType.String:
                    //Add 1 item
                    dic.Add(affected.ToString(), affected);
                    break;
                case JTokenType.Null:
                    //Add 0 (zero) items
                    break;
                default:
                    return null;
            }
            return dic;
        }

        public async Task<PolicyDecisionLogDetails> GetCurrentDecisionLogOrDefaultAsync(string productCode)
        {
            var client = await GetBlobContainerClientAsync();

            var blockRef = client.GetBlobClient($"{productCode}.json");
            if (!await blockRef.ExistsAsync())
                return new PolicyDecisionLogDetails
                {
                    ProductCode = productCode,
                    Changes = new List<PolicyDecisionLogDetailsChangesItem>(),
                    Checks = new List<PolicyDecisionLogDetailsChecksItem>()
                };
            else
            {
                var blob = await blockRef.DownloadAsync();
                using var sr = new StreamReader(blob.Value.Content);
                var log = JsonConvert.DeserializeObject<PolicyDecisionLogDetails>(sr.ReadToEnd(), _jsonSerializerSettings);
                log.Changes ??= new List<PolicyDecisionLogDetailsChangesItem>();
                log.Checks ??= new List<PolicyDecisionLogDetailsChecksItem>();
                return log;
            }
        }
        public async Task<PolicyDecisionLogDetails> GetDecisionLogByVersionAsync(string productCode, string versionId)
        {
            var client = await GetBlobContainerClientAsync();

            var blockRef = client.GetBlobClient($"{productCode}.json").WithVersion(versionId.Base64Decode());
            var blob = await blockRef.DownloadAsync();
            if (blob.GetRawResponse().Status == 404)
                return null;
            else
            {
                using var sr = new StreamReader(blob.Value.Content);
                return JsonConvert.DeserializeObject<PolicyDecisionLogDetails>(sr.ReadToEnd(), _jsonSerializerSettings);
            }
        }

        private async Task<string> UpdateDecisionLogAsync(string productCode, PolicyDecisionLogDetails decisionLog)
        {
            var client = await GetBlobContainerClientAsync();

            var blobClient = client.GetBlobClient($"{productCode}.json");
            using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(decisionLog, _jsonSerializerSettings))))
            {
                var response = await blobClient.UploadAsync(memoryStream, overwrite: true);
                return response.Value.VersionId.Base64Encode();
            }
        }



        public class Options
        {
            public string StorageConnectionString { get; set; }
        }
    }
}

