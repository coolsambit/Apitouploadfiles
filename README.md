# DocumentOperations-uami Function App

This Azure Functions project is configured to run under a user-assigned managed identity (UAMI). When you deploy the function app to Azure, name the resource **DocumentOperations-uami** and assign it the managed identity you will use to access ADLS.

## Configuration

- `ADLS_ACCOUNT_NAME` – your ADLS Gen2 account name
- `ADLS_UAMI_CLIENT_ID` – client id of the user-assigned managed identity attached to the function app

Local development uses the same environment variables (see `local.settings.json`).

## Code

The `Functions/UploadFiles.cs` function uses `DefaultAzureCredential`, optionally scoped to the UAMI client ID, to authenticate to ADLS.

## Deployment

1. Create a Function App in Azure with the name **DocumentOperations-uami**.
2. Enable and assign your user-managed identity to the app (or create one via Azure portal/CLI).
3. Grant the UAMI `Storage Blob Data Contributor` (or appropriate) role on the ADLS account.
4. Publish the project using `func azure functionapp publish DocumentOperations-uami`.

Once deployed, POST to `https://<your-app>.azurewebsites.net/api/uploadfiles` with a JSON body:
```json
{
  "filesystem": "myfs",
  "path": "folder/file.txt",
  "content": "Hello from UAMI!"
}
```

> **Local development note:** This project is now targeting **.NET 8.0** (the latest
> .NET Core release). Make sure you have the corresponding .NET SDK installed on
> your machine (check with `dotnet --list-sdks`). If you only have .NET 5/6 or
> earlier, the project will fail to build; install the latest SDK from
> https://dotnet.microsoft.com/download to compile and run locally.