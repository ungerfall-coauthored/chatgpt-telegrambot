using Microsoft.Extensions.Logging;
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
    private readonly IImageGenerationClient? _imageClient;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<GenerateImage> _logger;

    public GenerateImage(
        ImageOpenAiClient openAiClient,
        ILogger<GenerateImage> logger,
        ITelegramBotClient botClient)
    {
        _imageClient = openAiClient.Client;
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

        if (_imageClient == null)
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
        var image = await _imageClient.GenerateAsync(msgWithoutCommand, cancellation);
        if (image.Successful && !string.IsNullOrWhiteSpace(image.Base64))
        {
            using var stream = new MemoryStream(Convert.FromBase64String(image.Base64));
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
            _logger.LogError("ChatGPT image error: {Error}", image.ErrorMessage);
            if (beforeSend != null) await beforeSend();
            return await _botClient.SendResponse(
                chatId: message.Chat.Id,
                text: "ChatGPT request wasn't successful." + (image.ErrorMessage ?? string.Empty),
                replyParameters: message.Id,
                cancellationToken: cancellation);
        }
    }
}
