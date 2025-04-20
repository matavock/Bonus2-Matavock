using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Bonus2
{
    public class TelegramBot
    {
        private const string BotToken = "токен";
        private const string LogFile = "bot.log";

        private static readonly Dictionary<string, (string Puzzle, string Answer)> puzzles = new()
        {
            { "p1", ("Сколько будет 2 + 2 * 2?", "6") },
            { "p2", ("Назови язык программирования, совпадающий с названием змеи", "python") },
            { "p3", ("Чему равен остаток от деления 10 на 3?", "1") }
        };

        private static readonly Dictionary<long, string> currentPuzzle = new();
        private static readonly Dictionary<long, HashSet<string>> usedPuzzles = new();
        private static readonly HashSet<long> awaitingContinue = new();
        private static readonly HashSet<long> awaitingRestart = new();
        private static readonly Dictionary<long, int> attemptedCount = new();
        private static readonly Dictionary<long, int> correctCount = new();

        private static readonly IReplyMarkup keyboardMain = new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/puzzle"), new KeyboardButton("/help") },
            new[] { new KeyboardButton("/stats"),  new KeyboardButton("/tip") }
        })
        { ResizeKeyboard = true };

        private static readonly IReplyMarkup keyboardYesNo = new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("Да"), new KeyboardButton("Нет") }
        })
        { ResizeKeyboard = true };

        private static readonly IReplyMarkup removeKeyboard = new ReplyKeyboardRemove();

        private static readonly Regex yesRegex = new(@"\b(да|давай|ok|ок|окей|согласен|договорились)\b", RegexOptions.IgnoreCase);
        private static readonly Regex noRegex = new(@"\b(нет|не хочу|неа|no)\b", RegexOptions.IgnoreCase);
        private static readonly Regex skipRegex = new(@"/skip|\b(skip|пропусти)\b", RegexOptions.IgnoreCase);

        private static readonly string[] correctResponses = {
            "Отлично, это правильный ответ! Напиши Да, чтобы получить следующую задачку, или Нет, чтобы вернуться в меню.",
            "Верно! Хочешь ещё? Жми Да или Нет.",
            "Ты молодец! Продолжим? Ответь Да или Нет."
        };
        private static readonly string[] incorrectResponses = {
            "Упс, это не так. Попробуй ещё раз или напиши /skip, чтобы пропустить вопрос.",
            "Неправильно. Можешь попробовать снова или ввести /skip.",
            "Неа. Хочешь пропустить — просто напиши /skip."
        };
        private static readonly string[] tips = {
            "Используй Git tags для версий вместо хардкода дат.",
            "LINQ — мощный инструмент для работы с коллекциями, учи его на продвинутом уровне.",
            "Всегда проверяй `null` перед обращением к объекту.",
            "Используй `async/await` вместо `Task.Result` во избежание дедлоков.",
            "Названия переменных должны быть говорящими — это экономит время на читабельность."
        };

        public async Task Run()
        {
            var botClient = new TelegramBotClient(BotToken);
            using var cts = new CancellationTokenSource();
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.EditedMessage }
            };
            botClient.StartReceiving(OnMessageReceived, OnErrorOccured, receiverOptions, cts.Token);

            var me = await botClient.GetMeAsync(cts.Token);
            Console.WriteLine($"Бот @{me.Username} запущен. Esc — для остановки.");
            while (Console.ReadKey().Key != ConsoleKey.Escape) { }
            cts.Cancel();
        }

        async Task OnMessageReceived(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            var msg = update.Message ?? update.EditedMessage;
            if (msg == null) return;
            long chatId = msg.Chat.Id;
            string text = msg.Text?.Trim() ?? "";
            Log($"[{chatId}] {msg.From.Username}: {text}");

            if (!usedPuzzles.ContainsKey(chatId)) usedPuzzles[chatId] = new HashSet<string>();
            if (!attemptedCount.ContainsKey(chatId)) attemptedCount[chatId] = 0;
            if (!correctCount.ContainsKey(chatId)) correctCount[chatId] = 0;

            // 1) Перезапуск по завершении всех
            if (awaitingRestart.Contains(chatId))
            {
                if (yesRegex.IsMatch(text))
                {
                    usedPuzzles[chatId].Clear();
                    attemptedCount[chatId] = 0;
                    correctCount[chatId] = 0;
                    awaitingRestart.Remove(chatId);

                    await SendPuzzle(bot, chatId, ct);
                }
                else if (noRegex.IsMatch(text))
                {
                    awaitingRestart.Remove(chatId);
                    await bot.SendTextMessageAsync(chatId,
                        "Окей, возвращаюсь в меню.",
                        replyMarkup: keyboardMain, cancellationToken: ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId,
                        "Не понял. Выбери Да или Нет.",
                        replyMarkup: keyboardYesNo, cancellationToken: ct);
                }
                return;
            }

            // 2) Продолжение после правильного
            if (awaitingContinue.Contains(chatId))
            {
                if (yesRegex.IsMatch(text))
                {
                    awaitingContinue.Remove(chatId);
                    await SendPuzzle(bot, chatId, ct);
                }
                else if (noRegex.IsMatch(text))
                {
                    awaitingContinue.Remove(chatId);
                    await bot.SendTextMessageAsync(chatId,
                        "Окей, возвращаюсь в меню.",
                        replyMarkup: keyboardMain, cancellationToken: ct);
                }
                else
                {
                    await bot.SendTextMessageAsync(chatId,
                        "Не понял. Нажми Да или Нет.",
                        replyMarkup: keyboardYesNo, cancellationToken: ct);
                }
                return;
            }

            // 3) Skip
            if (skipRegex.IsMatch(text) && currentPuzzle.ContainsKey(chatId))
            {
                var key = currentPuzzle[chatId];
                var answer = puzzles[key].Answer;
                usedPuzzles[chatId].Add(key);
                currentPuzzle.Remove(chatId);
                attemptedCount[chatId]++;

                await bot.SendTextMessageAsync(
                    chatId,
                    $"Вопрос пропущен\\. Правильный ответ: ||{EscapeMarkdown(answer)}||",
                    parseMode: ParseMode.MarkdownV2,
                    replyMarkup: keyboardMain,
                    cancellationToken: ct);
                return;
            }

            // 4) Проверка ответа
            if (currentPuzzle.ContainsKey(chatId))
            {
                var key = currentPuzzle[chatId];
                var correct = puzzles[key].Answer.ToLower();
                var answer = text.ToLower();

                attemptedCount[chatId]++;
                if (answer == correct)
                {
                    usedPuzzles[chatId].Add(key);
                    correctCount[chatId]++;
                    var resp = correctResponses[new Random().Next(correctResponses.Length)];
                    await bot.SendTextMessageAsync(chatId,
                        resp,
                        replyMarkup: keyboardYesNo,
                        cancellationToken: ct);
                    currentPuzzle.Remove(chatId);
                    awaitingContinue.Add(chatId);
                }
                else
                {
                    var resp = incorrectResponses[new Random().Next(incorrectResponses.Length)];
                    await bot.SendTextMessageAsync(chatId,
                        resp,
                        replyMarkup: removeKeyboard,
                        cancellationToken: ct);
                }
                return;
            }

            // 5) Команды
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendTextMessageAsync(chatId,
                    "Привет! Я Code Puzzle Bot. Жми /puzzle, чтобы начать.",
                    replyMarkup: keyboardMain, cancellationToken: ct);
            }
            else if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendTextMessageAsync(chatId,
                    "Команды:\n" +
                    "/puzzle — новая задачка\n" +
                    "/skip   — пропустить текущую\n" +
                    "/stats  — твоя статистика\n" +
                    "/tip    — совет программисту\n",
                    replyMarkup: keyboardMain, cancellationToken: ct);
            }
            else if (text.StartsWith("/stats", StringComparison.OrdinalIgnoreCase))
            {
                int shown = usedPuzzles[chatId].Count;
                int correct = correctCount[chatId];
                int total = puzzles.Count;
                await bot.SendTextMessageAsync(chatId,
                    $"Ты решил {correct} из {shown} показанных задач. Всего доступно: {total}.",
                    replyMarkup: keyboardMain, cancellationToken: ct);
            }
            else if (text.StartsWith("/tip", StringComparison.OrdinalIgnoreCase))
            {
                var tip = tips[new Random().Next(tips.Length)];
                await bot.SendTextMessageAsync(chatId,
                    $"💡 Совет: {tip}",
                    replyMarkup: keyboardMain, cancellationToken: ct);
            }
            else if (text.StartsWith("/puzzle", StringComparison.OrdinalIgnoreCase))
            {
                await SendPuzzle(bot, chatId, ct);
            }
            else
            {
                await bot.SendTextMessageAsync(chatId,
                    "Не понял. Напиши /help или /puzzle.",
                    replyMarkup: keyboardMain, cancellationToken: ct);
            }
        }

        private static async Task SendPuzzle(ITelegramBotClient bot, long chatId, CancellationToken ct)
        {
            var used = usedPuzzles[chatId];
            if (used.Count >= puzzles.Count)
            {
                await bot.SendTextMessageAsync(chatId,
                    "🎉 Поздравляю! Ты прошёл все задачи! 🎉\nХочешь начать заново?",
                    replyMarkup: keyboardYesNo, cancellationToken: ct);
                awaitingRestart.Add(chatId);
                return;
            }

            var avail = puzzles.Keys.Where(k => !used.Contains(k)).ToList();
            var key = avail[new Random().Next(avail.Count)];
            currentPuzzle[chatId] = key;

            await bot.SendTextMessageAsync(chatId,
                $"Задача:\n{puzzles[key].Puzzle}",
                replyMarkup: removeKeyboard, cancellationToken: ct);
        }

        Task OnErrorOccured(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            var msg = ex switch
            {
                ApiRequestException api => $"API Error {api.ErrorCode}: {api.Message}",
                _ => ex.ToString()
            };
            Console.WriteLine(msg);
            Log(msg);
            return Task.CompletedTask;
        }

        private static void Log(string m)
        {
            try { System.IO.File.AppendAllText(LogFile, $"{DateTime.Now}: {m}\n"); }
            catch { }
        }

        private static string EscapeMarkdown(string input)
        {
            var specials = new[] { "_", "*", "[", "]", "(", ")", "~", "`", ">", "#", "+", "-", "=", "|", "{", "}", ".", "!" };
            foreach (var s in specials)
                input = input.Replace(s, "\\" + s);
            return input;
        }
    }
}
