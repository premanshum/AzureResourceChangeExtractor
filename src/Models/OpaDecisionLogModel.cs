using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace colonel.Policies.Models
{
    public class OpaDecisionLogModel
    {
        [JsonProperty("decision_id")]
        public string DecisionId { get; set; }
        [JsonProperty("result")]
        public Result Value { get; set; }
        public DateTime Timestamp { get; set; }


        public class Result
        {
            [JsonProperty("_metadata")]
            public Metadata Metadata { get; set; }

            public Dictionary<string, Check> Checks { get; set; }
        }

        public class Metadata
        {
            public string DigitalTwinVersion { get; set; }
            public string ProductCode { get; set; }
        }

        public class Check
        {
            public int Total { get; set; }
            public JToken Affected { get; set; }
        }
    }
}