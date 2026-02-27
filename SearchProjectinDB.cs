using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DocumentOperations
{
    public static class ProjectSearchinDB
    {
        [FunctionName("SearchProjectinDB")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiOperation(
            operationId: "SearchProjectinDB",
            tags: new[] { "DBSearch" },
            Summary = "Search project in DB",
            Description = "Searches for project metadata in SQL database by search query.")]
        [Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes.OpenApiParameter(
            name: "search",
            In = Microsoft.OpenApi.Models.ParameterLocation.Query,
            Required = true,
            Type = typeof(string),
            Description = "Search query string.")]
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "SearchDBForDocumentMetadata")] HttpRequest req,
            ILogger log)
        {
            string search = req.Query["search"];
            var results = new List<string>();

            string connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
            if (string.IsNullOrEmpty(connStr))
                return new ObjectResult(new { error = "MissingConnectionString" }) { StatusCode = 500 };

            using (var conn = new SqlConnection(connStr))
            {
                await conn.OpenAsync();
                string sql = "SELECT ProjectName FROM Projects WHERE ProjectName LIKE @search ORDER BY ProjectName";
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@search", "%" + (search ?? "") + "%");
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(reader.GetString(0));
                        }
                    }
                }
            }

            return new OkObjectResult(results);
        }
    }
}
