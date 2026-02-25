using System.Collections.Generic;
using System;
using System.IO;
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
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "uploadfiles")] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("uploadfiles function triggered.");

            string projectId, categoryId, notes, fileName;
            byte[] fileBytes;

            // Determine input format: JSON or multipart/form-data
            bool isJson = req.ContentType != null &&
                          req.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (isJson)
                {
                    // JSON body: { "ProjectId": "...", "FileName": "...", "Content": "<base64>", "CategoryId": "...", "Notes": "..." }
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
                    log.LogInformation("JSON upload - ProjectId: {proj}, FileName: {file}, Size: {size} bytes",
                        projectId, fileName, fileBytes.Length);
                }
                else
                {
                    // Multipart/form-data (from Angular UI)
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

            return new OkObjectResult("File uploaded successfully to " + path);

        }
    }
}