
using System;
using System.Net.Http;

namespace colonel.Policies.Services
{
    public class OpaHttpClientFactory
    {
        internal const string NamedHttpClientName = "OpaHttpClient";

        private readonly IHttpClientFactory _httpClientFactory;

        public OpaHttpClientFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public HttpClient GetHttpClient()
        {
            var client = _httpClientFactory.CreateClient(NamedHttpClientName);
            client.Timeout = TimeSpan.FromMinutes(10);
            return client;
        }
    }
}




