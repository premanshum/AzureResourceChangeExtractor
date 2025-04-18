

using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Graph;

namespace colonel.Policies.Services
{
    internal class GraphAuthenticationProvider : IAuthenticationProvider
    {
        private readonly AuthenticationContext _authenticationContext;
        private readonly ClientCredential _clientCredential;

        public GraphAuthenticationProvider(IOptions<AzureResourceGraphOptions> options)
        {
            _authenticationContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", options.Value.TenantId));
            _clientCredential = new ClientCredential(options.Value.ClientId, options.Value.ClientSecret);
        }

        public async Task AuthenticateRequestAsync(HttpRequestMessage request) 
        {
            var token = await _authenticationContext.AcquireTokenAsync("https://graph.microsoft.com", _clientCredential);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

    }
}


