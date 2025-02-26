using System.Threading.Tasks;

namespace MyGeotabAPIAdapter
{
    public interface IAdlsService
    {
        Task WriteDataAsync<T>(string path, T data);
        Task<T> ReadDataAsync<T>(string path);
    }
}