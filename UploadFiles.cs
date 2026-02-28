using System.Collections.Generic;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Azure;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.Logging;
using Azure.Core;
using Newtonsoft.Json;

namespace DocumentOperations
{
    public static class UploadFiles
    {
        [FunctionName("uploadfiles")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiOperation(
            operationId: "uploadfiles",
            tags: new[] { "Upload" },
            Summary = "Upload a file to ADLS",
            Description = "Preferred: Upload files using multipart/form-data (from web UI or clients). Legacy: JSON with base64 content is supported for external API clients.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiRequestBody(
            contentType: "multipart/form-data",
            bodyType: typeof(UploadFilesFormDataModel),
            Required = true,
            Description = "Form-data fields (as JSON pattern):\n{\n  \"file\": \"binary\",\n  \"projectId\": \"ProjectA\",\n  \"categoryId\": \"CategoryA1\",\n  \"notes\": \"Some notes\"\n}\n\nfile: binary (required), projectId: string (required), categoryId: string (optional), notes: string (optional).",
            Example = typeof(UploadFilesFormDataExample))]
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "uploadfiles")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("uploadfiles function triggered.");

            string projectId, categoryId, notes, fileName;
            byte[] fileBytes;

            // Preferred: multipart/form-data (file upload from UI or clients)
            // Legacy: JSON with base64 content for external API clients
            bool isJson = req.ContentType != null &&
                          req.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (!isJson)
                {
                    // Preferred: Multipart/form-data (from Angular UI or clients)
                    var file = req.Form.Files.GetFile("file");
                    projectId = req.Form["projectId"].ToString();
                    categoryId = req.Form["categoryId"].ToString();
                    notes = req.Form["notes"].ToString();

                    if (file == null || file.Length == 0)
                    {
                        return new BadRequestObjectResult(new { error = "NoFile", message = "No file provided. Include a 'file' field in the form data." });
                    }

                    fileName = file.FileName;

                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    fileBytes = ms.ToArray();

                    log.LogInformation("Form upload - ProjectId: {proj}, FileName: {file}, Category: {cat}, Size: {size} bytes",
                        projectId, fileName, categoryId, fileBytes.Length);
                }
                else
                {
                    // Legacy: JSON body with base64 content
                    using var reader = new StreamReader(req.Body);
                    var bodyJson = await reader.ReadToEndAsync();
                    dynamic body = JsonConvert.DeserializeObject(bodyJson);

                    projectId = body?.ProjectId;
                    fileName = body?.FileName;
                    categoryId = (string)body?.CategoryId ?? "";
                    notes = (string)body?.Notes ?? "";
                    string base64Content = body?.Content;

                    if (string.IsNullOrEmpty(base64Content))
                    {
                        return new BadRequestObjectResult(new { error = "MissingContent", message = "Content (base64) is required." });
                    }

                    fileBytes = Convert.FromBase64String(base64Content);
                    log.LogWarning("Legacy JSON upload - base64 content used. Prefer direct file upload via form-data for better performance.");
                    log.LogInformation("JSON upload - ProjectId: {proj}, FileName: {file}, Size: {size} bytes",
                        projectId, fileName, fileBytes.Length);
                }
            }
            catch (FormatException ex)
            {
                log.LogError(ex, "Invalid base64 Content.");
                return new BadRequestObjectResult(new { error = "InvalidBase64", message = "Content field is not valid base64." });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to read request data.");
                return new BadRequestObjectResult(new { error = "InvalidRequest", message = ex.Message });
            }

            if (string.IsNullOrEmpty(projectId))
            {
                return new BadRequestObjectResult(new { error = "MissingField", message = "ProjectId is required." });
            }
            if (string.IsNullOrEmpty(fileName))
            {
                return new BadRequestObjectResult(new { error = "MissingField", message = "FileName is required." });
            }

            // Use environment variable if available, otherwise fallback to hardcoded value
            string accountName = Environment.GetEnvironmentVariable("ADLS_ACCOUNT_NAME") ?? "aids4alaskastate";
            string containerName = Environment.GetEnvironmentVariable("ADLS_PARENT_CONTAINER") ?? "alaskadocuments";
            string clientId = Environment.GetEnvironmentVariable("ADLS_UAMI_CLIENT_ID") ?? "71da2648-2dd7-423f-8436-e719faf7975c";

            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrEmpty(clientId))
            {
                credentialOptions.ManagedIdentityClientId = clientId;
            }

            var creds = new DefaultAzureCredential(credentialOptions);

            // Debugging: Log the identity being used to help troubleshoot permissions
            try
            {
                var token = await creds.GetTokenAsync(new TokenRequestContext(new[] { "https://storage.azure.com/.default" }));
                var parts = token.Token.Split('.');
                if (parts.Length == 3)
                {
                    var payload = parts[1];
                    switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                    log.LogInformation($"Identity Token Payload: {json}");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Could not determine identity: {ex.Message}");
            }

            var serviceUri = new Uri($"https://{accountName}.dfs.core.windows.net");
            var serviceClient = new DataLakeServiceClient(serviceUri, creds);

            var filesystemClient = serviceClient.GetFileSystemClient(containerName);

            // build path using project folder
            string path = $"{projectId}/{fileName}";
            var fileClient = filesystemClient.GetFileClient(path);

            try
            {
                // Upload file bytes to ADLS
                using (var stream = new MemoryStream(fileBytes))
                {
                    // ensure the file is replaced if it already exists
                    await fileClient.DeleteIfExistsAsync();
                    await fileClient.CreateAsync();
                    await fileClient.AppendAsync(stream, offset: 0);
                    await fileClient.FlushAsync(fileBytes.Length);
                }

                // Set custom metadata so the AI Search indexer can pick up these fields
                var metadata = new Dictionary<string, string>
                {
                    { "projectId", projectId ?? "" },
                    { "categoryId", categoryId ?? "" },
                    { "notes", notes ?? "" }
                };
                await fileClient.SetMetadataAsync(metadata);
                log.LogInformation("Metadata set on blob: projectId={proj}, categoryId={cat}", projectId, categoryId);
            }
            catch (RequestFailedException ex)
            {
                log.LogError(ex, "Storage operation failed. Ensure the identity has 'Storage Blob Data Contributor' role.");
                return new ObjectResult(new { error = ex.ErrorCode, message = ex.Message }) { StatusCode = ex.Status };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unexpected error during file upload.");
                return new ObjectResult(new { error = ex.GetType().Name, message = ex.Message }) { StatusCode = 500 };
            }

            // ── Step 2: Call DocumentSearch API to extract content and index ──
            try
            {
                string functionBaseUrl = Environment.GetEnvironmentVariable("FUNCTION_APP_BASE_URL")
                    ?? $"{req.Scheme}://{req.Host.Value}";

                var indexPayload = new
                {
                    filePath = $"https://{accountName}.dfs.core.windows.net/{containerName}/{projectId}/{fileName}",
                    notes = notes ?? ""
                };

                var jsonContent = new StringContent(
                    JsonConvert.SerializeObject(indexPayload),
                    Encoding.UTF8,
                    "application/json");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                var response = await httpClient.PostAsync(
                    $"{functionBaseUrl}/api/calldocumentoperations", jsonContent);

                if (response.IsSuccessStatusCode)
                {
                    log.LogInformation("CallDocumentOperations triggered successfully for {file}", fileName);
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    log.LogWarning("CallDocumentOperations returned {status}: {body}", response.StatusCode, errorBody);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to call CallDocumentOperations API for {file}. File was uploaded successfully.", fileName);
                // Non-fatal: file is already uploaded, indexing can be retried
            }

            return new OkObjectResult("File uploaded successfully to " + path);

        }
    }
}