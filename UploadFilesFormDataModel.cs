namespace DocumentOperations
{
    /// <summary>
    /// Model for OpenAPI documentation of multipart/form-data upload parameters.
    /// </summary>
    public class UploadFilesFormDataModel
    {
        /// <summary>
        /// The file to upload (binary, required). Represented as string 'Binary Object' for Swagger compatibility.
        /// </summary>
        public string file { get; set; }

        /// <summary>
        /// The project ID associated with the upload.
        /// </summary>
        public string projectId { get; set; }

        /// <summary>
        /// The category ID for the document.
        /// </summary>
        public string categoryId { get; set; }

        /// <summary>
        /// Optional notes for the document.
        /// </summary>
        public string notes { get; set; }
    }
}