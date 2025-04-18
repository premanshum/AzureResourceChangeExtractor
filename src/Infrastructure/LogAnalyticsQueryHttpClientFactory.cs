
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Options;

namespace Admiral.Policies.Services
{
    public class LogAnalyticsQueryHttpClientFactory
    {
        internal const string NamedHttpClientName = "GovernanceLogAnalyticsQueryHttpClient";

        private readonly IHttpClientFactory _httpClientFactory;

        public LogAnalyticsQueryHttpClientFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public HttpClient GetHttpClient()
        {
            return _httpClientFactory.CreateClient(NamedHttpClientName);
        }
    }
}

