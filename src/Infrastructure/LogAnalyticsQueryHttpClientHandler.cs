
using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Admiral.Policies.Services
{
    internal class LogAnalyticsQueryHttpClientHandler : DelegatingHandler
    {
        private readonly AuthenticationContext _authenticationContext;
        private readonly ClientCredential _clientCredential;

        public LogAnalyticsQueryHttpClientHandler(IOptions<AzureResourceGraphOptions> options)
        {
            _authenticationContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", options.Value.TenantId));
            _clientCredential = new ClientCredential(options.Value.ClientId, options.Value.ClientSecret);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _authenticationContext.AcquireTokenAsync("https://api.loganalytics.io", _clientCredential);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            return await base.SendAsync(request, cancellationToken);

        }

    }
}

