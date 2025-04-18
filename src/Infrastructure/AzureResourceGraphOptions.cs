using System;
using System.Collections.Generic;
using System.Text;

namespace colonel.Policies.Services
{
    public class AzureResourceGraphOptions
    {
        public string TenantId { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
    }
}