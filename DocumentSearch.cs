using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Newtonsoft.Json;

namespace DocumentOperations
{
    /// <summary>
    /// Search the Azure AI Search index.
    /// GET  /api/documentsearch?q=...&project=...&category=...&pageSize=20&skip=0
    /// POST /api/documentsearch  { "query": "..." }
    /// </summary>
    public static class DocumentSearch
    {
        [FunctionName("documentsearch")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiOperation(
            operationId: "documentsearch",
            tags: new[] { "Search" },
            Summary = "Search documents",
            Description = "Searches documents in Azure AI Search index by query string or body.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiRequestBody(
            contentType: "application/json",
            bodyType: typeof(object),
            Required = false,
            Description = "Request body with query parameter.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.OK,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Success response.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.BadRequest,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Bad request response.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.InternalServerError,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Internal server error response.")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "documentsearch")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("documentsearch function triggered.");

            // Get search query from query string or body
            string query = req.Query["q"];

            if (string.IsNullOrEmpty(query) && req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(req.Body);
                var bodyJson = await reader.ReadToEndAsync();
                dynamic body = JsonConvert.DeserializeObject(bodyJson);
                query = body?.query;
            }

            if (string.IsNullOrEmpty(query))
            {
                return new BadRequestObjectResult("Please provide a search query via 'q' query parameter or 'query' in the request body.");
            }

            // Read configuration
            string searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT") ?? "https://aisearch-2024.search.windows.net";
            string indexName = Environment.GetEnvironmentVariable("SEARCH_INDEX_NAME") ?? "alaska-documents";
            string uamiClientId = Environment.GetEnvironmentVariable("ADLS_UAMI_CLIENT_ID") ?? "71da2648-2dd7-423f-8436-e719faf7975c";

            // Optional filters from query string
            string projectFilter = req.Query["project"];
            string categoryFilter = req.Query["category"];
            int pageSize = int.TryParse(req.Query["pageSize"], out int ps) ? ps : 20;
            int skip = int.TryParse(req.Query["skip"], out int sk) ? sk : 0;

            try
            {
                // Authenticate with Managed Identity
                var credentialOptions = new DefaultAzureCredentialOptions();
                if (!string.IsNullOrEmpty(uamiClientId))
                {
                    credentialOptions.ManagedIdentityClientId = uamiClientId;
                }
                var credential = new DefaultAzureCredential(credentialOptions);

                var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, credential);

                // Build search options
                var searchOptions = new SearchOptions
                {
                    Size = pageSize,
                    Skip = skip,
                    IncludeTotalCount = true,
                    QueryType = SearchQueryType.Simple
                };

                // Add fields to return
                searchOptions.Select.Add("metadata_storage_path");
                searchOptions.Select.Add("metadata_storage_name");
                searchOptions.Select.Add("projectId");
                searchOptions.Select.Add("categoryId");
                searchOptions.Select.Add("notes");
                searchOptions.Select.Add("content");

                // Add highlighting on content
                searchOptions.HighlightFields.Add("content");

                // Build OData filter if project or category specified
                var filters = new List<string>();
                if (!string.IsNullOrEmpty(projectFilter))
                {
                    filters.Add($"projectId eq '{projectFilter}'");
                }
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    filters.Add($"categoryId eq '{categoryFilter}'");
                }
                if (filters.Count > 0)
                {
                    searchOptions.Filter = string.Join(" and ", filters);
                }

                log.LogInformation("Searching index '{index}' for query: '{query}', filter: '{filter}'",
                    indexName, query, searchOptions.Filter ?? "(none)");

                // Execute search
                SearchResults<SearchDocument> results = await searchClient.SearchAsync<SearchDocument>(query, searchOptions);

                // Build response
                var documents = new List<object>();
                await foreach (SearchResult<SearchDocument> result in results.GetResultsAsync())
                {
                    var doc = new Dictionary<string, object>
                    {
                        { "score", result.Score },
                        { "path", result.Document.GetString("metadata_storage_path") },
                        { "fileName", result.Document.GetString("metadata_storage_name") },
                        { "projectId", TryGetString(result.Document, "projectId") },
                        { "categoryId", TryGetString(result.Document, "categoryId") },
                        { "notes", TryGetString(result.Document, "notes") }
                    };

                    // Add highlights if available
                    if (result.Highlights != null && result.Highlights.ContainsKey("content"))
                    {
                        doc["highlights"] = result.Highlights["content"];
                    }

                    documents.Add(doc);
                }

                var response = new
                {
                    query = query,
                    totalCount = results.TotalCount,
                    count = documents.Count,
                    results = documents
                };

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Search operation failed.");
                return new ObjectResult(new { error = ex.GetType().Name, message = ex.Message })
                {
                    StatusCode = 500
                };
            }
        }

        private static string TryGetString(SearchDocument doc, string key)
        {
            try
            {
                return doc.ContainsKey(key) ? doc.GetString(key) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
