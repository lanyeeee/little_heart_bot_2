using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
                Csrf = reader.GetString("csrf"),
                Completed = reader.GetInt32("completed"),
                MsgTimestamp = reader.GetString("msg_timestamp"),
                ConfigNum = reader.GetInt32("config_num")
            };
            if (await user.IsValidCookie())
            {
                await user.FetchTarget();
                user.CookieStatus = 1;
                _users.Add(user);
            }
        }
    }

    private async Task SendMsg()
    {
        List<Task> tasks = new List<Task>();
        foreach (var user in _users)
        {
            tasks.Add(user.SendMsg());
        }

        await Task.WhenAll(tasks);
    }

    private async Task PostLike()
    {
        List<Task> tasks = new List<Task>();
        tasks.Clear();
        foreach (var user in _users)
        {
            tasks.Add(user.PostLike());
        }

        await Task.WhenAll(tasks);
    }

    private async Task ShareRoom()
    {
        List<Task> tasks = new List<Task>();
        tasks.Clear();
        foreach (var user in _users)
        {
            tasks.Add(user.ShareRoom());
        }

        await Task.WhenAll(tasks);
    }

    private async Task VerifyCompleted()
    {
        foreach (var user in _users)
        {
            int completed = 1;
            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query =
                    $"select *  from target_table where uid = {user.Uid} and completed = 0";
                await using var comm = new MySqlCommand(query, conn);
                await using var reader = await comm.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    int num = reader.GetInt32("num");
                    string targetUid = reader.GetString("target_uid");
                    string targetName = reader.GetString("target_name");
                    int msgStatus = reader.GetInt32("msg_status");
                    int likeNum = reader.GetInt32("like_num");
                    int shareNum = reader.GetInt32("share_num");
                    if (msgStatus != 0 && likeNum == 3 && shareNum == 5)
                    {
                        //把对应的target标记为已完成
                        await using var conn1 = await Globals.GetOpenedMysqlConnectionAsync();
                        query = $"update target_table set completed = 1 where num = {num}";
                        await using var comm1 = new MySqlCommand(query, conn1);
                        await comm1.ExecuteNonQueryAsync();
                        await _logger.Log($"uid {user.Uid} 在 {targetName}(uid:{targetUid}) 的任务完成");
                    }
                    else
                    {
                        completed = 0;
                        string msgText = msgStatus == 1 ? "1" : "0";
                        await _logger.Log(
                            $"uid {user.Uid} 在 {targetName}(uid:{targetUid}) 的任务未完成，弹幕({msgText}/1)，点赞({likeNum}/3)，分享({shareNum}/5)");
                    }
                }
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