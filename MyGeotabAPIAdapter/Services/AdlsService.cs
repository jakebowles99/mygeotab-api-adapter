using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Azure.Storage.Files.DataLake;
using Azure.Storage;
using Microsoft.Extensions.Configuration;
using MyGeotabAPIAdapter;

namespace MyGeotabAPIAdapter.Services
{
    public class AdlsService : IAdlsService
    {
        private readonly DataLakeServiceClient _serviceClient;
        private readonly string _fileSystemName;

        public AdlsService(IConfiguration configuration)
        {
            var accountName = configuration["AZURE_STORAGE_ACCOUNT"];
            var accountKey = configuration["AZURE_STORAGE_KEY"];
            _fileSystemName = configuration["AZURE_CONTAINER"];

            var sharedKeyCredential = new StorageSharedKeyCredential(accountName, accountKey);
            var uri = new Uri($"https://{accountName}.dfs.core.windows.net");
            _serviceClient = new DataLakeServiceClient(uri, sharedKeyCredential);
        }

        public async Task WriteDataAsync<T>(string path, T data)
        {
            var fileSystem = _serviceClient.GetFileSystemClient(_fileSystemName);
            var dirPath = Path.GetDirectoryName(path);
            var directory = fileSystem.GetDirectoryClient(dirPath);
            
            await directory.CreateIfNotExistsAsync();
            
            var file = directory.GetFileClient(Path.GetFileName(path));
            var json = JsonSerializer.Serialize(data);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await file.UploadAsync(stream, overwrite: true);
        }

        public async Task<T> ReadDataAsync<T>(string path)
        {
            var fileSystem = _serviceClient.GetFileSystemClient(_fileSystemName);
            var file = fileSystem.GetFileClient(path);
            
            var response = await file.ReadAsync();
            using var streamReader = new StreamReader(response.Value.Content);
            var json = await streamReader.ReadToEndAsync();
            return JsonSerializer.Deserialize<T>(json);
        }
    }
}