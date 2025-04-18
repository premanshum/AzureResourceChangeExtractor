using colonel.Shared.DDD;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace colonel.Policies.Services
{
    public class ActivityLogQueryHelper
    {
        private readonly LogAnalyticsQueryHttpClientFactory _httpClientFactory;

        public ActivityLogQueryHelper(LogAnalyticsQueryHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<DataTable> QueryLogs(string query)
        {

            var _httpClient = _httpClientFactory.GetHttpClient();

            var request = new JObject(new JProperty("query", query));

            var httpContent = new StringContent(request.ToString(), Encoding.UTF8);
            httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await _httpClient.PostAsync("query", httpContent);
            if (!response.IsSuccessStatusCode)
            {
                throw new DomainException("Failed to query Log analytics", response.StatusCode);
            }

            string rawData = await response.Content.ReadAsStringAsync();
            return ConvertToDataTable(rawData);
        }

        private static DataTable ConvertToDataTable(string rawData)
        {
            var dataTable = new DataTable();
            var root = JObject.Parse(rawData);
            if (root.TryGetValue("tables", out var tables))
            {
                var table = ((JArray)tables)[0] as JObject;
                foreach (var col in table["columns"] as JArray)
                {
                    dataTable.Columns.Add(col.Value<string>("name"), typeof(string));
                }

                foreach (JArray row in table["rows"] as JArray)
                {
                    var dataRow = dataTable.NewRow();
                    for (int i = 0; i < row.Count; i++)
                    {
                        dataRow[i] = row[i].ToString();
                    }
                    dataTable.Rows.Add(dataRow);
                }

            }
            dataTable.AcceptChanges();
            return dataTable;
        }
    }
}


