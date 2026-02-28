namespace DocumentOperations
{
    /// <summary>
    /// Example for OpenAPI documentation of multipart/form-data upload parameters.
    /// </summary>
    using Newtonsoft.Json.Serialization;

    using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;

    using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Resolvers;

    public class UploadFilesFormDataExample : OpenApiExample<UploadFilesFormDataModel>
    {
        public override IOpenApiExample<UploadFilesFormDataModel> Build(NamingStrategy namingStrategy = null)
        {
            Examples.Add(
                OpenApiExampleResolver.Resolve(
                    "Default",
                    new UploadFilesFormDataModel
                    {
                        file = "Binary Object",
                        projectId = "ProjectA",
                        categoryId = "CategoryA1",
                        notes = "Some notes"
                    },
                    namingStrategy
                )
            );
            return this;
        }
    }
}