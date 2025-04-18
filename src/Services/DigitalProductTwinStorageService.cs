
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using colonel.Policies;
using colonel.Rest.Models;
using colonel.Shared.DDD;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace colonel.Policies.Services
{
    public class DigitalProductTwinStorageService
    {
        public DigitalProductTwinStorageService(IOptions<Options> options, ILogger<DigitalProductTwinStorageService> logger)
        {
            _options = options;
            _logger = logger;
        }

        private BlobContainerClient _containerClient;
        private QueueServiceClient _queueClient;
        private readonly IOptions<Options> _options;
        private readonly ILogger<DigitalProductTwinStorageService> _logger;
        private const string ContainerName = "product-twins";

        public async Task<JObject> GetDigitalProductTwinAsync(string productCode, string versionId = null)
        {
            var twins = await GetContainerReferenceAsync();
            var client = twins.GetBlobClient(GetBlobName(productCode));

            if (await client.ExistsAsync())
            {
                if (string.IsNullOrEmpty(versionId))
                {
                    var blob = await client.DownloadAsync();
                    return GetDigitalProductTwin(blob.Value);
                }
                else
                {
                    var rawVersion = versionId.Base64Decode();

                    var versions = await twins.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version, GetBlobName(productCode)).GetAllAsync();
                    var version = versions.FirstOrDefault(v => v.VersionId == rawVersion);
                    if (version != null)
                    {
                        client = client.WithVersion(rawVersion);

                        var blob = await client.DownloadAsync();
                        return GetDigitalProductTwin(blob.Value);
                    }
                }
            }
            return null;
        }

        public async Task<IList<ProductDigitalTwinMetadata>> ListDigitalProductTwinsAsync()
        {
            var blobinfoList = new List<ProductDigitalTwinMetadata>();

            var twins = await GetContainerReferenceAsync();
            var pagedBlobs = await twins.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None).GetAllAsync();
            blobinfoList.AddRange(pagedBlobs.Select(p => new ProductDigitalTwinMetadata
            {
                ProductCode = p.Name.Replace(".json", ""),
                CreatedOn = p.Properties.CreatedOn?.UtcDateTime,
                VersionId = p.VersionId.Base64Encode(),
                Size = p.Properties.ContentLength
            }));

            return blobinfoList.OrderBy(info => info.ProductCode).ToList();
        }
        public async Task<IList<ProductDigitalTwinMetadata>> ListDigitalProductTwinVersionsAsync(string productCode)
        {
            var blobinfoList = new List<ProductDigitalTwinMetadata>();

            var twins = await GetContainerReferenceAsync();
            var pagedBlobs = await twins.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version, GetBlobName(productCode)).GetAllAsync();
            blobinfoList.AddRange(pagedBlobs.Select(p => new ProductDigitalTwinMetadata
            {
                ProductCode = p.Name.Replace(".json", ""),
                CreatedOn = p.Properties.CreatedOn?.UtcDateTime,
                VersionId = p.VersionId.Base64Encode(),
                Size = p.Properties.ContentLength
            }));

            return blobinfoList.OrderByDescending(info => info.CreatedOn ?? DateTimeOffset.UtcNow).ToList();
        }

        public delegate bool ModifyTwinDelegate(JObject twin, IDictionary<string, string> metadata);
        public async Task UpdateDigitalProductTwinAsync(string productCode, ModifyTwinDelegate modifier)
        {
            var twins = await GetContainerReferenceAsync();

            var client = twins.GetBlobClient(GetBlobName(productCode));

            var hasChanges = false;

            string updatedVersionId = null;

            if (await client.ExistsAsync())
            {
                var success = true;
                for (int i = 0; i < 6; i++)
                {
                    var blob = await client.DownloadAsync();
                    var etag = blob.Value.Details.ETag;
                    var currentTwin = GetDigitalProductTwin(blob.Value);

                    var metadata = new Dictionary<string, string>();
                    hasChanges = modifier(currentTwin, metadata);
                    if (hasChanges)
                    {
                        try
                        {
                            updatedVersionId = await UploadJObjectAsync(currentTwin, metadata, client, new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = etag } });
                            break;
                        }
                        catch (RequestFailedException ex)
                        {
                            if (ex.Status == (int)HttpStatusCode.PreconditionFailed)
                            {
                                _logger.LogDebug($"Precondition failed for uploading new version of blob. Let's retry [{i}/6]]");
                                success = false;
                            }
                            else
                                throw;
                        }
                    }
                    else
                    {
                        //No changes, break
                        break;
                    }
                }
                if (!success)
                {
                    throw new DomainException($"Could not update Digital Product Twin ({productCode}). Tried 6 times :(");
                }

            }
            else
            {
                var metadata = new Dictionary<string, string>();
                var currentTwin = new JObject(new JProperty("product", new JObject(new JProperty("code", productCode))));
                hasChanges = modifier(currentTwin, metadata);
                if (hasChanges)
                {
                    updatedVersionId = await UploadJObjectAsync(currentTwin, metadata, client);
                }
            }

            if (hasChanges)
            {
                //Notify of change
                var queueclient = GetQueueReference();
                var queue = queueclient.GetQueueClient("product-twin-changes");
                await queue.CreateIfNotExistsAsync();
                await queue.SendMessageAsync(JsonConvert.SerializeObject(new { ProductCode = productCode, VersionId = updatedVersionId }).Base64Encode());
            }
        }


        private async Task<BlobContainerClient> GetContainerReferenceAsync()
        {
            if (_containerClient == null)
            {
                _containerClient = new BlobContainerClient(_options.Value.StorageConnectionString, ContainerName);

                await _containerClient.CreateIfNotExistsAsync();
            }
            return _containerClient;
        }
        private QueueServiceClient GetQueueReference()
        {
            if (_queueClient == null)
            {
                _queueClient = new QueueServiceClient(_options.Value.StorageConnectionString);
            }
            return _queueClient;
        }

        private string GetBlobName(string productCode) => $"{productCode}.json";
        private JObject GetDigitalProductTwin(BlobDownloadInfo blobInfo)
        {
            using var sr = new StreamReader(blobInfo.Content);
            using var jsonTextReader = new JsonTextReader(sr);
            return JObject.Load(jsonTextReader);
        }
        private async Task<string> UploadJObjectAsync(JObject jobject, IDictionary<string, string> metadata, BlobClient blobClient, BlobUploadOptions blobUploadOptions = null)
        {
            using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jobject.ToString())))
            {
                var options = blobUploadOptions ?? new BlobUploadOptions { };
                if (metadata == null)
                {
                    options.Metadata ??= new Dictionary<string, string>();
                    foreach (var item in metadata)
                    {
                        if (options.Metadata.ContainsKey(item.Key))
                            options.Metadata[item.Key] = item.Value;
                        else
                            options.Metadata.Add(item);
                    }
                }

                var response = await blobClient.UploadAsync(memoryStream, options);
                return response.Value.VersionId.Base64Encode();
            }
        }



        public class Options
        {
            public string StorageConnectionString { get; set; }
        }
    }
}