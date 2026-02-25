using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Azure;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace DocumentOperations
{
    /// <summary>
    /// Admin function to create or recreate the search index, data source, and indexer
    /// on Azure AI Search "aisearch-2024" pointing to ADLS "aids4alaskastate/alaskadocuments".
    /// Call POST /api/capturefilecontent to provision resources.
    /// </summary>
    public static class CaptureFileContent
    {
        private const string IndexName = "alaska-documents";
        private const string DataSourceName = "alaska-adls-datasource";
        private const string IndexerName = "alaska-documents-indexer";

        [FunctionName("capturefilecontent")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "capturefilecontent")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("capturefilecontent function triggered.");

            string searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT") ?? "https://aisearch-2024.search.windows.net";
            string adlsAccountName = Environment.GetEnvironmentVariable("ADLS_ACCOUNT_NAME") ?? "aids4alaskastate";
            string containerName = Environment.GetEnvironmentVariable("ADLS_PARENT_CONTAINER") ?? "alaskadocuments";
            string uamiClientId = Environment.GetEnvironmentVariable("ADLS_UAMI_CLIENT_ID") ?? "71da2648-2dd7-423f-8436-e719faf7975c";

            try
            {
                // Authenticate with Managed Identity
                var credentialOptions = new DefaultAzureCredentialOptions();
                if (!string.IsNullOrEmpty(uamiClientId))
                {
                    credentialOptions.ManagedIdentityClientId = uamiClientId;
                }
                var credential = new DefaultAzureCredential(credentialOptions);

                var indexClient = new SearchIndexClient(new Uri(searchEndpoint), credential);
                var indexerClient = new SearchIndexerClient(new Uri(searchEndpoint), credential);

                // ── Step 1: Create or update the search index ──
                log.LogInformation("Creating/updating index '{index}'...", IndexName);

                var index = new SearchIndex(IndexName)
                {
                    Fields =
                    {
                        // Key field - base64 encoded storage path
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },

                        // Document content extracted by the indexer
                        new SearchableField("content") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },

                        // Standard blob metadata fields
                        new SimpleField("metadata_storage_path", SearchFieldDataType.String) { IsFilterable = true },
                        new SearchableField("metadata_storage_name") { IsFilterable = true, IsSortable = true },
                        new SimpleField("metadata_storage_size", SearchFieldDataType.Int64) { IsFilterable = true, IsSortable = true },
                        new SimpleField("metadata_storage_last_modified", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                        new SimpleField("metadata_storage_content_type", SearchFieldDataType.String) { IsFilterable = true },

                        // Custom metadata fields (set by UploadFiles function)
                        new SearchableField("projectId") { IsFilterable = true, IsFacetable = true },
                        new SearchableField("categoryId") { IsFilterable = true, IsFacetable = true },
                        new SearchableField("notes") { IsFilterable = false }
                    }
                };

                await indexClient.CreateOrUpdateIndexAsync(index);
                log.LogInformation("Index '{index}' created/updated successfully.", IndexName);

                // ── Step 2: Create or update the data source connection ──
                log.LogInformation("Creating/updating data source '{ds}'...", DataSourceName);

                // Use Resource ID for managed identity connection to ADLS Gen2
                string resourceId = $"/subscriptions/74beb7e5-9547-4a02-a2c2-68d4b3804ebf/resourceGroups/Datahub/providers/Microsoft.Storage/storageAccounts/{adlsAccountName}";

                // Connection string uses ResourceId for managed identity auth
                // Format: ResourceId=/subscriptions/.../storageAccounts/accountName;
                var dataSource = new SearchIndexerDataSourceConnection(
                    name: DataSourceName,
                    type: SearchIndexerDataSourceType.AzureBlob,
                    connectionString: $"ResourceId=/subscriptions/74beb7e5-9547-4a02-a2c2-68d4b3804ebf/resourceGroups/Datahub/providers/Microsoft.Storage/storageAccounts/{adlsAccountName};",
                    container: new SearchIndexerDataContainer(containerName)
                );

                await indexerClient.CreateOrUpdateDataSourceConnectionAsync(dataSource);
                log.LogInformation("Data source '{ds}' created/updated successfully.", DataSourceName);

                // ── Step 3: Create or update the indexer ──
                log.LogInformation("Creating/updating indexer '{indexer}'...", IndexerName);

                var indexer = new SearchIndexer(IndexerName, DataSourceName, IndexName)
                {
                    // Map base64-encoded storage path to the key field
                    Parameters = new IndexingParameters()
                    {
                        IndexingParametersConfiguration = new IndexingParametersConfiguration()
                        {
                            ParsingMode = BlobIndexerParsingMode.Default,
                            DataToExtract = BlobIndexerDataToExtract.ContentAndMetadata
                        }
                    },
                    // Schedule: run every hour
                    Schedule = new IndexingSchedule(TimeSpan.FromHours(1))
                };

                // Map storage path to the key field (base64 encode)
                indexer.FieldMappings.Add(new FieldMapping("metadata_storage_path")
                {
                    TargetFieldName = "id",
                    MappingFunction = new FieldMappingFunction("base64Encode")
                });

                // Map custom blob metadata to index fields
                indexer.FieldMappings.Add(new FieldMapping("metadata_storage_path") { TargetFieldName = "metadata_storage_path" });
                indexer.FieldMappings.Add(new FieldMapping("metadata_storage_name") { TargetFieldName = "metadata_storage_name" });

                await indexerClient.CreateOrUpdateIndexerAsync(indexer);
                log.LogInformation("Indexer '{indexer}' created/updated successfully.", IndexerName);

                // ── Step 4: Run the indexer immediately ──
                log.LogInformation("Running indexer '{indexer}'...", IndexerName);
                await indexerClient.RunIndexerAsync(IndexerName);

                return new OkObjectResult(new
                {
                    message = "Search index, data source, and indexer created/updated successfully.",
                    index = IndexName,
                    dataSource = DataSourceName,
                    indexer = IndexerName,
                    status = "Indexer started. Documents will be indexed shortly."
                });
            }
            catch (RequestFailedException ex)
            {
                log.LogError(ex, "Azure AI Search operation failed.");
                return new ObjectResult(new { error = ex.ErrorCode, message = ex.Message })
                {
                    StatusCode = ex.Status
                };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unexpected error during index setup.");
                return new ObjectResult(new { error = ex.GetType().Name, message = ex.Message })
                {
                    StatusCode = 500
                };
            }
        }
    }
}
