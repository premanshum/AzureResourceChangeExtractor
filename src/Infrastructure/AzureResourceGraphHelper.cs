using Admiral.Shared.DDD;
using Microsoft.Azure.Management.ResourceGraph;
using Microsoft.Azure.Management.ResourceGraph.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Admiral.Policies.Services
{
    public class AzureResourceGraphHelper
    {
        private readonly ClientCredential _clientCredential;
        private readonly AuthenticationContext _authenticationContext;
        public AzureResourceGraphHelper(IOptions<AzureResourceGraphOptions> options)
        {
            _authenticationContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", options.Value.TenantId));
            _clientCredential = new ClientCredential(options.Value.ClientId, options.Value.ClientSecret);
        }

        public async Task<IList<JObject>> QueryAsync(string query, IList<string> subscriptions)
        {
            var token = await _authenticationContext.AcquireTokenAsync("https://management.core.windows.net/", _clientCredential);
            var credentials = new TokenCredentials(token.AccessToken);
            var client = new ResourceGraphClient(credentials);

            var rows = new List<JObject>();

            string skipToken = null;
            do
            {
                var response = await client.ResourcesAsync(new QueryRequest
                {
                    Options = new QueryRequestOptions
                    {
                        ResultFormat = ResultFormat.ObjectArray,
                        Top = 1000,
                        SkipToken = skipToken
                    },
                    Subscriptions = subscriptions,
                    Query = query
                });
                rows.AddRange(((JArray)response.Data).Cast<JObject>());
                skipToken = response.SkipToken;
            }
            while (skipToken != null);

            return rows;
        }
    }
}