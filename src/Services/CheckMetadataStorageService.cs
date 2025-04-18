
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using colonel.Policies.Models;
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
using YamlDotNet.Serialization;

namespace colonel.Policies.Services
{
    public class CheckMetadataStorageService
    {
        public CheckMetadataStorageService(IOptions<Options> options, ILogger<CheckMetadataStorageService> logger)
        {
            _options = options;
            _logger = logger;
        }

        private BlobContainerClient _documentationContainerClient;
        private BlobContainerClient _metadataContainerClient;
        private readonly IOptions<Options> _options;
        private readonly ILogger<CheckMetadataStorageService> _logger;
        private const string DocumentationContainerName = "documentation";
        private const string MetadataContainerName = "metadata";


        public async Task UpdateCheckMetadataFromMdFilesAsync()
        {
            var docsContainer = await GetDocumentationContainerReferenceAsync();

            if (!await docsContainer.ExistsAsync())
            {
                _logger.LogWarning("Documentation Container does not exists");
                return;
            }

            var checkMetadataTasks = new List<Task<PolicyCheckMetadata>>();

            var pagedBlobs = docsContainer.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None);
            var enumerator = pagedBlobs.GetAsyncEnumerator();
            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var blobClient = docsContainer.GetBlobClient(enumerator.Current.Name);

                    checkMetadataTasks.Add(ExtractMetadataFromBlob(blobClient));
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            var allCheckMetadata = await Task.WhenAll(checkMetadataTasks);
            allCheckMetadata = allCheckMetadata.OrderBy(i => i.Key).ToArray();

            var metadataContainer = await GetMetadataContainerReferenceAsync();
            var globalMetadataClient = metadataContainer.GetBlobClient("_global.json");
            await UploadGlobalMetadataAsync(allCheckMetadata, globalMetadataClient);
        }

        private static readonly Regex _metadataYamlRegEx = new Regex(@"^\s*---(\s*?)\n([\s\S]+?)\n---(\s*?)\n", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _titleRegEx = new Regex(@"^\#([^\#].*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private async Task<PolicyCheckMetadata> ExtractMetadataFromBlob(BlobClient blobClient)
        {
            var metadata = new PolicyCheckMetadata { Key = blobClient.Name.Replace(".md", "") };

            var response = await blobClient.DownloadAsync();
            var mdString = (new StreamReader(response.Value.Content)).ReadToEnd();

            var metadataYaml = _metadataYamlRegEx.Match(mdString);
            if (metadataYaml?.Success == true)
            {
                try
                {
                    var yaml = metadataYaml.Groups[2].Value;
                    var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
                    var yamlMetadata = deserializer.Deserialize<PolicyCheckMetadata>(yaml);
                    yamlMetadata.Key = metadata.Key;
                    metadata = yamlMetadata;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not extract metadata from check: {0}", metadata.Key);
                }
            }

            var title = _titleRegEx.Match(mdString);
            metadata.Title = title?.Success == true ? title.Groups[1].Value : metadata.Key;

            return metadata;
        }



        public Task<PolicyCheckMetadata[]> ListProductAdjustedChecksMetadata(string productCode) => ListGlobalChecksMetadata();
        public async Task<PolicyCheckMetadata[]> ListGlobalChecksMetadata()
        {
            var metadataContainer = await GetMetadataContainerReferenceAsync();
            var globalMetadataClient = metadataContainer.GetBlobClient("_global.json");
            var blob = await globalMetadataClient.DownloadAsync();
            using var sr = new StreamReader(blob.Value.Content);

            return JsonConvert.DeserializeObject<PolicyCheckMetadata[]>(sr.ReadToEnd());
        }
        public async Task<byte[]> GetCheckDocumentation(string check)
        {
            var docsContainer = await GetDocumentationContainerReferenceAsync();
            var docsClient = docsContainer.GetBlobClient($"{check}.md");
            if (await docsClient.ExistsAsync())
            {
                var blob = await docsClient.DownloadAsync();
                using (MemoryStream ms = new MemoryStream())
                {
                    blob.Value.Content.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            return null;
        }

        private async Task<BlobContainerClient> GetDocumentationContainerReferenceAsync()
        {
            if (_documentationContainerClient == null)
            {
                _documentationContainerClient = new BlobContainerClient(_options.Value.StorageConnectionString, DocumentationContainerName);

                await _documentationContainerClient.CreateIfNotExistsAsync();
            }
            return _documentationContainerClient;
        }
        private async Task<BlobContainerClient> GetMetadataContainerReferenceAsync()
        {
            if (_metadataContainerClient == null)
            {
                _metadataContainerClient = new BlobContainerClient(_options.Value.StorageConnectionString, MetadataContainerName);

                await _metadataContainerClient.CreateIfNotExistsAsync();
            }
            return _metadataContainerClient;
        }

        private async Task UploadGlobalMetadataAsync(PolicyCheckMetadata[] metadata, BlobClient blobClient)
        {
            using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(metadata))))
            {
                await blobClient.UploadAsync(memoryStream, overwrite: true);
            }
        }

        public class Options
        {
            public string StorageConnectionString { get; set; }
        }
    }
}
