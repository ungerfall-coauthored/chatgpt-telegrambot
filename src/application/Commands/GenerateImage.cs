using Microsoft.Extensions.Logging;
using OpenAI.Interfaces;
using OpenAI.ObjectModels;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Commands;
public class GenerateImage
{
    private readonly IOpenAIService? _openAiService;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<GenerateImage> _logger;

    public GenerateImage(
        ImageOpenAiClient openAiClient,
        ILogger<GenerateImage> logger,
        ITelegramBotClient botClient)
    {
        _openAiService = openAiClient.Service;
        _logger = logger;
        _botClient = botClient;
    }

    public async Task<Message> Execute(
        Message message,
        string msgWithoutCommand,
        CancellationToken cancellation,
        Func<Task>? beforeSend = default)
    {
        if (string.IsNullOrWhiteSpace(msgWithoutCommand))
        {
            if (beforeSend != null) await beforeSend();
            return await _botClient.SendResponse(
                chatId: message.Chat.Id,
                text: "Write an image description after the command.",
                replyParameters: message.Id,
                cancellationToken: cancellation);
        }

        if (_openAiService == null)
        {
            if (beforeSend != null) await beforeSend();
            return await _botClient.SendResponse(
                chatId: message.Chat.Id,
                text: "Image generation is not configured.",
                replyParameters: message.Id,
                cancellationToken: cancellation);
        }

        await _botClient.SendChatAction(
            chatId: message.Chat.Id,
            ChatAction.UploadPhoto,
            cancellationToken: cancellation);
        var image = await _openAiService.Image.CreateImage(
            new OpenAI.ObjectModels.RequestModels.ImageCreateRequest
            {
                Prompt = msgWithoutCommand,
                Size = StaticValues.ImageStatics.Size.Size1024,
                N = 1,
            },
            cancellation);
        if (image.Successful && image.Results?.Count > 0 && !string.IsNullOrWhiteSpace(image.Results[0].B64))
        {
            using var stream = new MemoryStream(Convert.FromBase64String(image.Results[0].B64));
            if (beforeSend != null) await beforeSend();
            try
            {
                return await _botClient.SendPhoto(
                    chatId: message.Chat.Id,
                    photo: InputFile.FromStream(stream, "image.png"),
                    hasSpoiler: true,
                    replyParameters: message.Id,
                    cancellationToken: cancellation);
            }
            catch (ApiRequestException ex) when (TelegramDeliveryRejectedException.IsDefiniteRejection(ex))
            {
                throw new TelegramDeliveryRejectedException(ex);
            }
            catch (RequestException ex) when (ex is not ApiRequestException)
            {
                throw new TelegramDeliveryUnknownException(ex);
            }
        }
        else
        {
            _logger.LogError("ChatGPT error: {@error}", new { image.Error?.Type, image.Error?.Message });
            if (beforeSend != null) await beforeSend();
            return await _botClient.SendResponse(
                chatId: message.Chat.Id,
                text: "ChatGPT request wasn't successful." + (image.Error?.Message ?? string.Empty),
                replyParameters: message.Id,
                cancellationToken: cancellation);
        }
    }
}
