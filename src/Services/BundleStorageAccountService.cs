
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

namespace colonel.Policies.Services
{
    public class BundleStorageAccountService
    {
        public BundleStorageAccountService(IOptions<Options> options, ILogger<BundleStorageAccountService> logger)
        {
            _options = options;
            _logger = logger;
        }

        private CloudBlobContainer _bundles;
        private readonly IOptions<Options> _options;
        private readonly ILogger<BundleStorageAccountService> _logger;
        private const string ContainerName = "bundles";

        private async Task<CloudBlobContainer> GetBundleReferenceAsync()
        {
            if (_bundles == null)
            {
                var client = CloudStorageAccount.Parse(_options.Value.StorageConnectionString);

                var blobClient = client.CreateCloudBlobClient();

                _bundles = blobClient.GetContainerReference(ContainerName);
                await _bundles.CreateIfNotExistsAsync();
            }
            return _bundles;
        }

        public async Task<byte[]> DownloadBundleAsync(string bundle)
        {
            var docs = await GetBundleReferenceAsync();
            using var ms = new MemoryStream();
            await docs.GetBlockBlobReference($"{bundle.ToLowerInvariant()}.tar.gz").DownloadToStreamAsync(ms);

            return ms.ToArray();
        }

       


        public class Options
        {
            public string StorageConnectionString { get; set; }
        }
    }
}