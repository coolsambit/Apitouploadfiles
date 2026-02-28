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
    /// Example request for DocumentIntelligenceOperations
    /// </summary>
    public class DocumentIntelligenceRequest
    {
        public string fullpathofthefile { get; set; } = "https://aids4alaskastate.dfs.core.windows.net/alaskadocuments/projectA/report.pdf";
        /// <summary>
        /// Required: Secret for Document Intelligence API
        /// </summary>
        public string documentIntelligenceSecret { get; set; }
    }
    /// <summary>
    /// Called by UploadFiles after a successful upload.
    /// Reads the file from ADLS, extracts content via Azure Document Intelligence,
    /// and pushes the document into the Azure AI Search index.
    /// POST /api/calldocumentoperations
    /// Body: { "filePath", "notes" }
    /// </summary>
    public static class DocumentIntelligenceWrapper
    {
        /// <summary>
        /// Reads a file from ADLS and validates its size (max 30 MB).
        /// </summary>
        private static async Task<byte[]> ReadAndValidateFileAsync(string filePath, Azure.Core.TokenCredential credential, ILogger log)
        {
            string accountName = null;
            string containerName = null;
            string adlsRelativePath = null;
            try
            {
                var uri = new Uri(filePath);
                // Example: https://{account}.dfs.core.windows.net/{container}/{projectId}/{fileName}
                accountName = uri.Host.Split('.')[0];
                var segments = uri.AbsolutePath.TrimStart('/').Split('/');
                containerName = segments.Length > 0 ? segments[0] : null;
                // adlsRelativePath: everything after container name
                adlsRelativePath = segments.Length > 1 ? string.Join('/', segments, 1, segments.Length - 1) : "";
                // URL-decode the relative path to handle spaces and special characters
                adlsRelativePath = Uri.UnescapeDataString(adlsRelativePath);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to parse filePath for account/container/relativePath: {filePath}", filePath);
                throw new InvalidOperationException("Invalid filePath format.", ex);
            }
            try
            {
                var serviceUri = new Uri($"https://{accountName}.dfs.core.windows.net");
                var serviceClient = new DataLakeServiceClient(serviceUri, credential);
                var filesystemClient = serviceClient.GetFileSystemClient(containerName);
                var fileClient = filesystemClient.GetFileClient(adlsRelativePath);
                log.LogInformation($"ADLS read details: accountName={accountName}, containerName={containerName}, adlsRelativePath={adlsRelativePath}");
                log.LogInformation("Reading file from ADLS: {path}", adlsRelativePath);
                using var downloadStream = new MemoryStream();
                var download = await fileClient.ReadAsync();
                await download.Value.Content.CopyToAsync(downloadStream);
                var fileBytes = downloadStream.ToArray();
                log.LogInformation("Read {size} bytes from ADLS for {file}", fileBytes.Length, Path.GetFileName(filePath));
                if (fileBytes.Length > 30 * 1024 * 1024)
                {
                    throw new InvalidOperationException($"File size {fileBytes.Length} bytes exceeds 30 MB limit.");
                }
                return fileBytes;
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to read or validate file from ADLS. Details: accountName={accountName}, containerName={containerName}, adlsRelativePath={adlsRelativePath}, Exception={ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Extracts content from a file using Document Intelligence.
        /// </summary>
        private static async Task<string> ExtractContentAsync(
            byte[] fileBytes,
            string diEndpoint,
            bool useManagedIdentity,
            Azure.Core.TokenCredential managedIdentityCredential,
            Azure.AzureKeyCredential apiKeyCredential,
            ILogger log,
            string fileName)
        {
            // Check if file is DOCX or DOC and convert to PDF if needed
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            byte[] fileBytesForExtraction = fileBytes;
            if (ext == ".docx" || ext == ".doc")
            {
                log.LogInformation($"Converting {fileName} to PDF for better extraction...");
                fileBytesForExtraction = ConvertWordToPdf(fileBytes, fileName, log);
            }
            DocumentAnalysisClient diClient = useManagedIdentity
                ? new DocumentAnalysisClient(new Uri(diEndpoint), managedIdentityCredential)
                : new DocumentAnalysisClient(new Uri(diEndpoint), apiKeyCredential);
            using var fileStream = new MemoryStream(fileBytesForExtraction);
            var operation = await diClient.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-read", fileStream);
            AnalyzeResult analyzeResult = operation.Value;
            string extractedContent = analyzeResult.Content ?? "";
            log.LogInformation($"Document Intelligence extracted {extractedContent.Length} characters from {fileName}");
            return extractedContent;
        }

        /// <summary>
        /// Converts DOCX/DOC bytes to PDF bytes. Placeholder for actual implementation.
        /// </summary>
        private static byte[] ConvertWordToPdf(byte[] wordBytes, string fileName, ILogger log)
        {
            // TODO: Implement using a library like GemBox.Document, Aspose.Words, or LibreOffice CLI
            // For now, log and return original bytes (no conversion)
            log.LogWarning($"[Placeholder] DOCX/DOC to PDF conversion not implemented. Returning original bytes for {fileName}.");
            return wordBytes;
        }

        /// <summary>
        /// Writes extracted content to ADLS output path.
        /// </summary>
        private static async Task WriteOutputToAdlsAsync(
            string outputContent,
            string outputPath,
            Azure.Core.TokenCredential credential,
            ILogger log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputContent))
                {
                    log.LogWarning($"No extracted content to write to ADLS for {outputPath}. Skipping write.");
                    throw new InvalidOperationException("No extracted content to write to ADLS.");
                }
                var accountName = Environment.GetEnvironmentVariable("ADLS_ACCOUNT_NAME") ?? "aids4alaskastate";
                var serviceUri = new Uri($"https://{accountName}.dfs.core.windows.net");
                var containerName = Environment.GetEnvironmentVariable("ADLS_PARENT_CONTAINER") ?? "alaskadocuments";
                var serviceClient = new DataLakeServiceClient(serviceUri, credential);
                var filesystemClient = serviceClient.GetFileSystemClient(containerName);
                var fileClient = filesystemClient.GetFileClient(outputPath);
                using var outputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(outputContent));
                await fileClient.CreateAsync();
                await fileClient.AppendAsync(outputStream, offset: 0);
                await fileClient.FlushAsync(position: outputStream.Length);
                log.LogInformation($"Extracted content written to ADLS: {outputPath}");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Could not write output to ADLS for {outputPath}. Reason: {ex.Message}");
                throw new InvalidOperationException($"Could not write output to ADLS for {outputPath}. Reason: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts content from a chunk using Document Intelligence and stores chunk in ADLS.
        /// </summary>
        
        [FunctionName("DocumentIntelligenceOperations")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiOperation(
            operationId: "DocumentIntelligenceOperations",
            tags: new[] { "Document" },
            Summary = "Process and index document",
            Description = "Reads file from ADLS, extracts content, and creates chunks for Azure AI Search. Returns 202 Accepted if processing is in progress, 200 OK if successful, and error codes if failed.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiRequestBody(
            contentType: "application/json",
            bodyType: typeof(DocumentIntelligenceRequest),
            Required = true,
            Description = "Request body with fullpathofthefile and documentIntelligenceSecret.",
            Example = typeof(DocumentIntelligenceRequest))]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.OK,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Success response: Document processed and indexed.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.Accepted,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Accepted response: Document Intelligence request accepted and is processing.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.BadRequest,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Bad request response: Missing or invalid parameters.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiResponseWithBody(
            statusCode: System.Net.HttpStatusCode.InternalServerError,
            contentType: "application/json",
            bodyType: typeof(object),
            Description = "Internal server error response: Document Intelligence or indexing failed.")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "DocumentIntelligenceOperations")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("DocumentIntelligenceOperations function triggered.");

            // Parse the request body
            string filePath;
            string documentIntelligenceSecret = null;
            Azure.Core.TokenCredential managedIdentityCredential = null;
            Azure.AzureKeyCredential apiKeyCredential = null;
            Azure.Core.TokenCredential credential = null;

             // Read configuration
            string accountName   = Environment.GetEnvironmentVariable("ADLS_ACCOUNT_NAME")    ?? "aids4alaskastate";
            string read_from_containerName = Environment.GetEnvironmentVariable("ADLS_PARENT_CONTAINER") ?? "alaskadocuments";
            string uamiClientId  = Environment.GetEnvironmentVariable("ADLS_UAMI_CLIENT_ID")  ?? "71da2648-2dd7-423f-8436-e719faf7975c";
            string diEndpoint    = Environment.GetEnvironmentVariable("DOC_INTELLIGENCE_ENDPOINT")
                                   ?? "https://alaska-document-intelligence.cognitiveservices.azure.com/";
          

            try
            {
                using var reader = new StreamReader(req.Body);
                var bodyJson = await reader.ReadToEndAsync();
                dynamic body = JsonConvert.DeserializeObject(bodyJson);

                filePath = (string)body?.fullpathofthefile;
                documentIntelligenceSecret = (string)body?.documentIntelligenceSecret;
                if (string.IsNullOrEmpty(documentIntelligenceSecret))
                {
                    return new BadRequestObjectResult(new { error = "MissingField", message = "documentIntelligenceSecret is required." });
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to parse request body.");
                return new BadRequestObjectResult(new { error = "InvalidRequest", message = ex.Message });
            }

            if (string.IsNullOrEmpty(filePath))
            {
                return new BadRequestObjectResult(new { error = "MissingField", message = "fullpathofthefile is required." });
            }

            // Derive fileName and projectId from the filePath URL
            // Expected format: https://{account}.dfs.core.windows.net/{container}/{projectId}/{fileName}
            string fileName, projectId;
            try
            {
                var uri = new Uri(filePath);
                var pathSegments = uri.AbsolutePath.TrimStart('/').Split('/');
                // segments[0] = container, segments[1] = projectId, segments[2+] = fileName
                fileName   = pathSegments.Length > 2 ? pathSegments[pathSegments.Length - 1] : Path.GetFileName(filePath);
                projectId  = pathSegments.Length > 2 ? pathSegments[1] : "";
            //    categoryId = ""; // Not passed; may be enriched later from blob metadata
            }
            catch
            {
                fileName   = Path.GetFileName(filePath);
                projectId  = "";
              //  categoryId = "";
            }

            log.LogInformation("Received file path: {path}", filePath);

           
         
            if (!string.IsNullOrEmpty(documentIntelligenceSecret))
            {
                // Use API key credential if secret is provided
                apiKeyCredential = new Azure.AzureKeyCredential(documentIntelligenceSecret);
                // For ADLS and Search, use managed identity
                var credentialOptions = new DefaultAzureCredentialOptions();
                if (!string.IsNullOrEmpty(uamiClientId))
                {
                    credentialOptions.ManagedIdentityClientId = uamiClientId;
                }
                credential = new DefaultAzureCredential(credentialOptions);
            }
            else
            {
                var credentialOptions = new DefaultAzureCredentialOptions();
                if (!string.IsNullOrEmpty(uamiClientId))
                {
                    credentialOptions.ManagedIdentityClientId = uamiClientId;
                }
                managedIdentityCredential = new DefaultAzureCredential(credentialOptions);
                credential = managedIdentityCredential;
            }

            // Step 1: Read and validate file size
            byte[] fileBytes;
            try
            {
                fileBytes = await ReadAndValidateFileAsync(filePath, credential, log);
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning(ex.Message);
                return new ObjectResult(new { error = "FileTooLarge", message = ex.Message }) { StatusCode = 413 };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to read file from ADLS.");
                return new ObjectResult(new { error = "StorageReadFailed", message = ex.Message }) { StatusCode = 500 };
            }

            // Step 2: Extract content using Document Intelligence
            string extractedContent = "";
            bool useManagedIdentity = managedIdentityCredential != null;
            try
            {
                extractedContent = await ExtractContentAsync(
                    fileBytes,
                    diEndpoint,
                    useManagedIdentity,
                    managedIdentityCredential,
                    apiKeyCredential,
                    log,
                    fileName);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Document Intelligence extraction failed for {file}.", fileName);
                int statusCode = 500;
                if (ex is Azure.RequestFailedException rfe)
                {
                    statusCode = rfe.Status;
                }
                return new ObjectResult(new { error = "DocumentIntelligenceFailed", message = ex.Message }) { StatusCode = statusCode };
            }

            // Step 3: Write extracted content to ADLS output path
            string outputPath = $"documentchunks/{fileName}_extracted.txt";
            try
            {
                await WriteOutputToAdlsAsync(extractedContent, outputPath, credential, log);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to write extracted content to ADLS for {file}.", fileName);
                return new ObjectResult(new { error = "OutputWriteFailed", message = ex.Message }) { StatusCode = 500 };
            }

            // Step 4: Push document into Azure AI Search index (not implemented here)
            // ...existing code for search indexing if needed...

            return new OkObjectResult(new { message = "Document processed and extracted content written to ADLS.", outputPath });
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
