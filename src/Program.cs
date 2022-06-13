namespace little_heart_bot_2;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var tasks = new List<Task>
        {
            App.Instance.Main(),
            Bot.Instance.Main()
        };
        await Task.WhenAll(tasks);
    }
}