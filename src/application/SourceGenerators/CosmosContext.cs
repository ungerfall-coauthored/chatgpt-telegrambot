using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.SourceGenerators;
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Database.TelegramMessage))]
[JsonSerializable(typeof(Database.TelegramMessage[]))]
[JsonSerializable(typeof(Database.TimedTaskExecution))]
[JsonSerializable(typeof(Database.TimedTaskExecution[]))]
[JsonSerializable(typeof(Database.TimedTaskQuiz))]
[JsonSerializable(typeof(Database.TimedTaskQuiz[]))]
[JsonSerializable(typeof(Database.BookQuiz))]
[JsonSerializable(typeof(Database.BookQuiz[]))]
[JsonSerializable(typeof(Database.BookQuizProgress))]
[JsonSerializable(typeof(Database.ConversationState))]
[JsonSerializable(typeof(Database.ConversationReceipt))]
[JsonSerializable(typeof(Database.ConversationBudget))]
[JsonSerializable(typeof(Database.TldrCache))]
[JsonSerializable(typeof(Database.ReleaseAnnouncementReceipt))]
[JsonSerializable(typeof(Database.ReleaseAnnouncementReceipt[]))]
[JsonSerializable(typeof(Database.ReleaseDeploymentApproval))]
[JsonSerializable(typeof(Storage.StorageOperationReceipt))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(System.Text.Json.JsonElement[]))]
[JsonSerializable(typeof(int[]))]
internal partial class CosmosContext : JsonSerializerContext;
