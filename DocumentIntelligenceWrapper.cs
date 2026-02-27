using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Azure;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Newtonsoft.Json;

namespace DocumentOperations
{
    /// <summary>
    /// Called by UploadFiles after a successful upload.
    /// Reads the file from ADLS, extracts content via Azure Document Intelligence,
    /// and pushes the document into the Azure AI Search index.
    /// POST /api/calldocumentoperations
    /// Body: { "filePath", "notes" }
    /// </summary>
    public static class AzAISearchDocumentWrapper
    {
        [FunctionName("AzAISearchDocumentOperations")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiOperation(
            operationId: "AzAISearchDocumentOperations",
            tags: new[] { "Document" },
            Summary = "Process and index document",
            Description = "Reads file from ADLS, extracts content, and indexes in Azure AI Search.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiRequestBody(
            contentType: "application/json",
            bodyType: typeof(object),
            Required = true,
            Description = "Request body with filePath and notes.")]
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "AISearchDocumentOperations")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("AzAISearchDocumentOperations function triggered.");

            // Parse the request body
            string filePath, notes;

            try
            {
                using var reader = new StreamReader(req.Body);
                var bodyJson = await reader.ReadToEndAsync();
                dynamic body = JsonConvert.DeserializeObject(bodyJson);

                filePath = (string)body?.filePath;
                notes    = (string)body?.notes ?? "";
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to parse request body.");
                return new BadRequestObjectResult(new { error = "InvalidRequest", message = ex.Message });
            }

            if (string.IsNullOrEmpty(filePath))
            {
                return new BadRequestObjectResult(new { error = "MissingField", message = "filePath is required." });
            }

            // Derive fileName and projectId from the filePath URL
            // Expected format: https://{account}.dfs.core.windows.net/{container}/{projectId}/{fileName}
            string fileName, projectId, categoryId;
            try
            {
                var uri = new Uri(filePath);
                var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/');
                // segments[0] = container, segments[1] = projectId, segments[2+] = fileName
                fileName   = pathSegments.Length > 2 ? pathSegments[pathSegments.Length - 1] : Path.GetFileName(filePath);
                projectId  = pathSegments.Length > 2 ? pathSegments[1] : "";
                categoryId = ""; // Not passed; may be enriched later from blob metadata
            }
            catch
            {
                fileName   = Path.GetFileName(filePath);
                projectId  = "";
                categoryId = "";
            }

            log.LogInformation("Received file path: {path}", filePath);

            // Read configuration
            string accountName   = Environment.GetEnvironmentVariable("ADLS_ACCOUNT_NAME")    ?? "aids4alaskastate";
            string containerName = Environment.GetEnvironmentVariable("ADLS_PARENT_CONTAINER") ?? "alaskadocuments";
            string uamiClientId  = Environment.GetEnvironmentVariable("ADLS_UAMI_CLIENT_ID")  ?? "71da2648-2dd7-423f-8436-e719faf7975c";
            string diEndpoint    = Environment.GetEnvironmentVariable("DOC_INTELLIGENCE_ENDPOINT")
                                   ?? "https://alaska-document-intelligence.cognitiveservices.azure.com/";
            string searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT")      ?? "https://aisearch-2024.search.windows.net";
            string indexName      = Environment.GetEnvironmentVariable("SEARCH_INDEX_NAME")     ?? "alaska-documents";

            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrEmpty(uamiClientId))
            {
                credentialOptions.ManagedIdentityClientId = uamiClientId;
            }
            var credential = new DefaultAzureCredential(credentialOptions);

            // ── Step 1: Read file from ADLS ──
            // Parse the storage path to extract the relative path for ADLS access
            // filePath format: https://{account}.dfs.core.windows.net/{container}/{projectId}/{fileName}
            string adlsRelativePath;
            try
            {
                var uri = new Uri(filePath);
                // Path segments: /, container, projectId, fileName
                var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
                adlsRelativePath = segments.Length > 1 ? segments[1] : filePath;
            }
            catch
            {
                // If filePath is not a full URI, treat it as a relative path
                adlsRelativePath = filePath;
            }

            byte[] fileBytes;
            try
            {
                var serviceUri = new Uri($"https://{accountName}.dfs.core.windows.net");
                var serviceClient = new DataLakeServiceClient(serviceUri, credential);
                var filesystemClient = serviceClient.GetFileSystemClient(containerName);
                var fileClient = filesystemClient.GetFileClient(adlsRelativePath);

                log.LogInformation("Reading file from ADLS: {path}", adlsRelativePath);

                using var downloadStream = new MemoryStream();
                var download = await fileClient.ReadAsync();
                await download.Value.Content.CopyToAsync(downloadStream);
                fileBytes = downloadStream.ToArray();

                log.LogInformation("Read {size} bytes from ADLS for {file}", fileBytes.Length, fileName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to read file from ADLS: {path}", adlsRelativePath);
                return new ObjectResult(new { error = "StorageReadFailed", message = ex.Message }) { StatusCode = 500 };
            }

            // ── Step 2: Extract content using Document Intelligence ──
            string extractedContent = "";
            try
            {
                var diClient = new DocumentAnalysisClient(new Uri(diEndpoint), credential);

                log.LogInformation("Calling Document Intelligence for: {file}", fileName);

                using var docStream = new MemoryStream(fileBytes);
                AnalyzeDocumentOperation operation = await diClient.AnalyzeDocumentAsync(
                    WaitUntil.Completed, "prebuilt-read", docStream);

                AnalyzeResult analyzeResult = operation.Value;
                extractedContent = analyzeResult.Content ?? "";

                log.LogInformation("Document Intelligence extracted {chars} characters from {file}",
                    extractedContent.Length, fileName);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Document Intelligence extraction failed for {file}. Content will be empty in search index.", fileName);
                // Non-fatal: continue indexing with empty content
            }

            // ── Step 3: Push document into Azure AI Search index ──
            try
            {
                var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, credential);

                string storagePath = filePath;
                string docId = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(storagePath))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');

                var searchDoc = new SearchDocument(new Dictionary<string, object>
                {
                    { "id", docId },
                    { "content", extractedContent },
                    { "metadata_storage_path", storagePath },
                    { "metadata_storage_name", fileName },
                    { "metadata_storage_size", (long)fileBytes.Length },
                    { "metadata_storage_content_type", GetContentType(fileName) },
                    { "metadata_storage_last_modified", DateTimeOffset.UtcNow },
                    { "projectId", projectId ?? "" },
                    { "categoryId", categoryId ?? "" },
                    { "notes", notes ?? "" }
                });

                IndexDocumentsResult indexResult = await searchClient.MergeOrUploadDocumentsAsync(
                    new[] { searchDoc });

                bool succeeded = indexResult.Results[0].Succeeded;
                log.LogInformation("Document indexed in search: {file}, succeeded={ok}", fileName, succeeded);

                return new OkObjectResult(new
                {
                    message = "Document processed and indexed successfully.",
                    fileName = fileName,
                    extractedCharacters = extractedContent.Length,
                    indexed = succeeded
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to index document in search for {file}.", fileName);
                return new ObjectResult(new { error = "IndexingFailed", message = ex.Message }) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// Infer content type from file extension.
        /// </summary>
        private static string GetContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".tif" or ".tiff" => "image/tiff",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
        }
    }
}
