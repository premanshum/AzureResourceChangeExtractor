




using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using colonel.Policies.Services;
using colonel.Rest.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace colonel.Policies
{
    public class CommonActivities
    {
        private readonly DigitalProductTwinStorageService _digitalProductTwinStorageService;

        public CommonActivities(DigitalProductTwinStorageService digitalProductTwinStorageService)
        {
            _digitalProductTwinStorageService = digitalProductTwinStorageService;
        }
        [FunctionName(nameof(GetAllDigitalTwinMetadata))]
        public async Task<ProductDigitalTwinMetadata[]> GetAllDigitalTwinMetadata([ActivityTrigger] IDurableActivityContext context)
        {
            var allTwins = await _digitalProductTwinStorageService.ListDigitalProductTwinsAsync();
            return allTwins.ToArray();
        }
    }
}
