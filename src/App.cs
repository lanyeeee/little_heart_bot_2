using MySqlConnector;

namespace little_heart_bot_2;

public class App
{
    private static App? _instance;
    public static App Instance => _instance ?? new App();

    private readonly List<User> _users = new();
    private readonly Logger _logger;


    private App()
    {
        _logger = new Logger("app");
        _instance = this;
    }

    private async Task FetchUser()
    {
        await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
        string query = "select * from user_table where completed = 0 and cookie_status != -1 limit 20";
        await using var comm = new MySqlCommand(query, conn);
        await using var reader = await comm.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            User user = new User(_logger)
            {
                Uid = reader.GetString("uid"),
                Cookie = reader.GetString("cookie"),
                Csrf = reader.GetString("csrf")
            };
            if (await user.IsValidCookie())
            {
                await user.FetchTarget();
                _users.Add(user);
            }
        }
    }

    private async Task SendMsg()
    {
        List<Task> tasks = new List<Task>();
        _users.ForEach(user => tasks.Add(user.SendMsg()));
        await Task.WhenAll(tasks);
    }

    private async Task PostLike()
    {
        List<Task> tasks = new List<Task>();
        _users.ForEach(user => tasks.Add(user.PostLike()));
        await Task.WhenAll(tasks);
    }

    private async Task ShareRoom()
    {
        List<Task> tasks = new List<Task>();
        _users.ForEach(user => tasks.Add(user.ShareRoom()));
        await Task.WhenAll(tasks);
    }

    private async Task VerifyCompleted()
    {
        foreach (var user in _users)
        {
            if (user.Targets == null) continue;
            int completed = 1;

            foreach (var target in user.Targets)
            {
                if (target.MsgStatus != 0 && target.LikeNum == 3 && target.ShareNum == 5)
                {
                    //把对应的target标记为已完成
                    await using var conn1 = await Globals.GetOpenedMysqlConnectionAsync();
                    string query =
                        $"update target_table set completed = 1 where uid = {user.Uid} and target_uid = {target.Uid}";
                    await using var comm1 = new MySqlCommand(query, conn1);
                    await comm1.ExecuteNonQueryAsync();
                    await _logger.Log($"uid {user.Uid} 在 {target.Name}(uid:{target.Uid}) 的任务完成");
                }
                else
                {
                    //如果有未完成的任务就将用户标记为未完成
                    completed = 0;
                    string msgText = target.MsgStatus == 1 ? "1" : "0";
                    await _logger.Log(
                        $"uid {user.Uid} 在 {target.Name}(uid:{target.Uid}) 的任务未完成，弹幕({msgText}/1)，点赞({target.LikeNum}/3)，分享({target.ShareNum}/5)");
                }
            }

            //看看这个用户还有没有未完成的任务
            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query = $"select * from target_table where  completed = 0 and uid = {user.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await using var reader = await comm.ExecuteReaderAsync();
                //如果有未完成的任务就将用户标记为未完成
                if (await reader.ReadAsync()) completed = 0;
            }

            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query = $"update user_table set completed = {completed} where uid = {user.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();
            }
        }
    }

    public async Task Main()
    {
        while (true)
        {
            try
            {
                await FetchUser();
                await SendMsg();
                await PostLike();
                await ShareRoom();
                await VerifyCompleted();

                Globals.AppStatus = 0;
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = "update bot_table set app_status = 0 where 1";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await Task.Delay(10000);
            }
            catch (ApiException)
            {
                Globals.AppStatus = -1;
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = "update bot_table set app_status = -1 where 1";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                for (int i = 0; i < 15; i++)
                {
                    await _logger.Log($"App冷却中，还需 {15 - i} 分钟");
                    await Task.Delay(1000 * 60);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                await Task.Delay(10000);
            }
            finally
            {
                _users.Clear();
            }
        }
    }
}