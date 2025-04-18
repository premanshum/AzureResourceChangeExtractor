
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using colonel.Shared;

namespace colonel.Policies.Services
{
    internal class OpaHttpClientHandler : DelegatingHandler
    {
        private readonly ITokenHelper _tokenhelper;

        public OpaHttpClientHandler(ITokenHelper tokenhelper)
        {
            _tokenhelper = tokenhelper;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _tokenhelper.GetTokenForSharedClient(includeScheme: false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);

        }

    }
}