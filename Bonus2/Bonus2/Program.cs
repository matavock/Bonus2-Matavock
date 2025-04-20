namespace Bonus2
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            TelegramBot telegramBot = new TelegramBot();
            await telegramBot.Run();
        }
    }
}
