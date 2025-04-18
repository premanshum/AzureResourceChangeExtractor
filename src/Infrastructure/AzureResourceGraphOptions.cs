using System;
using System.Collections.Generic;
using System.Text;

namespace Admiral.Policies.Services
{
    public class AzureResourceGraphOptions
    {
        public string TenantId { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
    }
}