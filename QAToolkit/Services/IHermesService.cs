using QAToolkit.Models;

namespace QAToolkit.Services
{
    public interface IHermesService
    {
        Task<string> ChatAsync(string userName, int chatId, string userMessage);
        Task LogActivityAsync(string activityType, string userName, int entityId, string entityName, string? tags, string? extra = null);
        Task<HermesUserSettings?> GetUserSettingsAsync(string userName);
        Task SaveUserSettingsAsync(string userName, string provider, string apiKey, string? model);
    }
}
