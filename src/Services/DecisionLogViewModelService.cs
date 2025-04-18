
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using colonel.Rest.Models;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace colonel.Policies.Services
{
    public class DecisionLogViewModelService
    {
        private readonly ILogger<DecisionLogViewModelService> _logger;
        private readonly TableServiceClient _tableServiceClient;
        private const string HistoryTableName = "DecisionLogProductHistory";
        private const string LatestTableName = "DecisionLogProductLatest";
        private readonly TableClient _historyTableClient;
        private readonly TableClient _latestTableClient;
        public DecisionLogViewModelService(IOptions<Options> options, ILogger<DecisionLogViewModelService> logger)
        {
            _logger = logger;
            _tableServiceClient = new TableServiceClient(options.Value.StorageConnectionString);
            _historyTableClient = _tableServiceClient.GetTableClient(HistoryTableName);
            _latestTableClient = _tableServiceClient.GetTableClient(LatestTableName);
        }


        #region Update Model
        public async Task UpdateViewModelsAsync(PolicyDecisionLogDetails decisionLog, string versionId, DateTime createdOn)
        {
            await Task.WhenAll(_historyTableClient.CreateIfNotExistsAsync(), _latestTableClient.CreateIfNotExistsAsync());

            await Task.WhenAll(UpdateHistoryViewModelAsync(decisionLog, versionId, createdOn), UpdateLatestViewModelAsync(decisionLog, versionId, createdOn));
        }

        private async Task UpdateHistoryViewModelAsync(PolicyDecisionLogDetails decisionLog, string versionId, DateTime createdOn)
        {
            var historyPartitionKey = decisionLog.ProductCode;
            //var transaction = _historyTableClient.CreateTransactionalBatch(historyPartitionKey);
            var checkBatches = decisionLog.Checks.Batch(150).ToArray();
            for (int i = 0; i < checkBatches.Length; i++)
            {
                var entity = new TableEntity(historyPartitionKey, $"{DateTime.MaxValue.Ticks - createdOn.Ticks:d19}_{i:d3}")
                {
                    { "ProductCode", decisionLog.ProductCode},
                    { "DecisionLogVersionId", versionId},
                    { "Date", createdOn},
                };
                foreach (var check in checkBatches[i])
                {
                    entity.Add($"C_{check.Key}", BuildCheckDataField(check));
                }
                //transaction.UpdateEntity(entity, Azure.ETag.All, TableUpdateMode.Replace);
                await _historyTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            }

            //await transaction.SubmitBatchAsync();
        }
        private async Task UpdateLatestViewModelAsync(PolicyDecisionLogDetails decisionLog, string versionId, DateTime createdOn)
        {
            var latestPartitionKey = "LATEST";
            //var transaction = _latestTableClient.CreateTransactionalBatch(latestPartitionKey);
            var checkBatches = decisionLog.Checks.Batch(150).ToArray();
            for (int i = 0; i < checkBatches.Length; i++)
            {
                var entity = new TableEntity(latestPartitionKey, $"{decisionLog.ProductCode}_{i:d3}")
                {
                    { "ProductCode", decisionLog.ProductCode},
                    { "DecisionLogVersionId", versionId},
                    { "Date", createdOn},
                };
                foreach (var check in checkBatches[i])
                {
                    entity.Add($"C_{check.Key}", BuildCheckDataField(check));
                }
                //transaction.UpdateEntity(entity, Azure.ETag.All, TableUpdateMode.Replace);
                await _latestTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            }

            //await transaction.SubmitBatchAsync();
        }

        private string BuildCheckDataField(PolicyDecisionLogDetailsChecksItem check) =>
            $"{check.State}|{check.Total}|{(check.Affected != null ? check.Affected.Count.ToString() : "N/A")}|{check.Severity}";

        #endregion


        #region Queries


        public async Task<IList<PolicyDecisionLog>> GetLatestAsync(string productCode = null)
        {

            var query = productCode != null ? _latestTableClient.QueryAsync<TableEntity>($"PartitionKey eq 'LATEST' and RowKey ge '{productCode}_000' and RowKey le '{productCode}_999'")
                                            : _latestTableClient.QueryAsync<TableEntity>($"PartitionKey eq 'LATEST'");

            var results = await query.GetAllAsync();

            var output = (from entity in results
                          let pCode = entity["ProductCode"].ToString()
                          group entity by pCode into product
                          orderby product.Key
                          select new PolicyDecisionLog
                          {
                              ProductCode = product.Key,
                              ExecutedOn = ((DateTimeOffset)product.First()["Date"]).UtcDateTime,
                              DecisionLogVersionId = product.First()["DecisionLogVersionId"].ToString(),
                              Checks = product.SelectMany(p => p.Where(c => c.Key.StartsWith("C_")))
                                              .Select(c => DeserializeCheck(c.Key.Substring(2), c.Value.ToString())).ToList()
                          }).ToList();
            return output;
        }

        public async Task<IList<PolicyDecisionLog>> GetHistoryAsync(string productCode, int daysBack)
        {

            var query = _historyTableClient.QueryAsync<TableEntity>($"PartitionKey eq '{productCode}' and RowKey le '{DateTime.MaxValue.Ticks - DateTime.UtcNow.AddDays(-daysBack).Ticks}_999'");

            var results = await query.GetAllAsync();

            var output = (from entity in results
                          let executedOn = (DateTimeOffset)entity["Date"]
                          group entity by executedOn into product
                          orderby product.Key descending
                          select new PolicyDecisionLog
                          {
                              ProductCode = product.First()["ProductCode"].ToString(),
                              ExecutedOn = product.Key.UtcDateTime,
                              DecisionLogVersionId = product.First()["DecisionLogVersionId"].ToString(),
                              Checks = product.SelectMany(p => p.Where(c => c.Key.StartsWith("C_")))
                                              .Select(c => DeserializeCheck(c.Key.Substring(2), c.Value.ToString())).ToList()
                          }).ToList();
            return output;
        }

        private PolicyDecisionLogChecksItem DeserializeCheck(string key, string data)
        {
            var parts = data.Split('|', StringSplitOptions.None);
            return new PolicyDecisionLogChecksItem
            {
                Key = key,
                State = Enum.TryParse<PolicyDecisionLogCheckState>(parts[0], out var state) ? state : PolicyDecisionLogCheckState.Inconclusive,
                Total = int.Parse(parts[1]),
                Affected = parts[2].Equals("N/A", StringComparison.InvariantCultureIgnoreCase) ? (int?)null : int.Parse(parts[2]),
                Severity = parts.Length > 3 && Enum.TryParse<PolicyDecisionLogCheckSeverity>(parts[3], out var severity) ? severity : PolicyDecisionLogCheckSeverity.NotSet,
            };
        }

        #endregion

        public class Options
        {
            public string StorageConnectionString { get; set; }
        }
    }
}


