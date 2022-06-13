using MySqlConnector;
using Newtonsoft.Json.Linq;

namespace little_heart_bot_2;

public class User
{
    public string Uid { get; set; }
    public string Cookie { get; set; }
    public string Csrf { get; set; }
    public int Completed { get; set; }
    public int CookieStatus { get; set; }
    public string MsgTimestamp { get; set; }
    public int ConfigNum { get; set; }
    private List<Target> Targets { get; set; }
    private readonly Logger _logger;


    public User(Logger logger)
    {
        _logger = logger;
    }

    public async Task FetchTarget()
    {
        Targets = new List<Target>();
        await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
        string query = $"select * from target_table where  uid = {Uid} and completed = 0";
        await using var comm = new MySqlCommand(query, conn);
        await using var reader = await comm.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Target target = new Target
            {
                Uid = reader.GetString("target_uid"),
                Name = reader.GetString("target_name"),
                RoomId = reader.GetString("room_id"),
                LikeNum = reader.GetInt32("like_num"),
                ShareNum = reader.GetInt32("share_num"),
                MsgContent = reader.GetString("msg_content"),
                MsgStatus = reader.GetInt32("msg_status")
            };
            Targets.Add(target);
        }
    }

    private async Task CookieExpire()
    {
        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query = $"update user_table set cookie_status = -1 where uid = {Uid}";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }

        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query = $"update target_table set msg_status = -3 where uid = {Uid}";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }
    }

    private async Task CookieInvalid()
    {
        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query = $"update user_table set cookie_status = -1 where uid = {Uid}";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }

        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query = $"update target_table set msg_status = -3 where uid = {Uid}";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }
    }
    public async Task<bool> IsValidCookie()
    {
        try
        {
            HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri("https://api.bilibili.com/x/web-interface/nav"),
                Headers = { { "Cookie", Cookie } }
            });
            await Task.Delay(1000);
            JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
            int code = (int)responseJson["code"];

            if (code == -412)
            {
                await _logger.Log(responseJson);
                throw new ApiException();
            }

            if (code != 0)
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 提供的cookie错误或已过期");
                await CookieInvalid();
                return false;
            }

            await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
            string query = $"update user_table set cookie_status = 1 where uid = {Uid}";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
            

            return true;
        }
        catch (Exception)
        {
            await _logger.Log($"uid {Uid} 提供的cookie有错误");
            await CookieInvalid();
            return false;
        }
    }

    public async Task SendMsg()
    {
        foreach (var target in Targets)
        {
            if (target.MsgStatus != 0)
            {
                continue;
            }

            if (target.RoomId == "0")
            {
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query =
                        $"update target_table set msg_status = -4 where uid = {Uid} and target_uid = {target.Uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await _logger.Log($"{target.Name}(uid:{target.Uid}) 未开通直播间");
                continue;
            }

            var payload = new Dictionary<string, string>
            {
                { "bubble", "0" },
                { "msg", target.MsgContent },
                { "color", "16777215" },
                { "mode", "1" },
                { "fontsize", "25" },
                { "rnd", DateTimeOffset.Now.ToUnixTimeSeconds().ToString() },
                { "roomid", target.RoomId },
                { "csrf", Csrf },
                { "csrf_token", Csrf }
            };
            HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri("https://api.live.bilibili.com/msg/send"),
                Headers = { { "Cookie", Cookie } },
                Content = new FormUrlEncodedContent(payload)
            });
            await Task.Delay(1000);
            JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
            int code = (int)responseJson["code"];

            if (code == -111 || code == -101)
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 提供的cookie已过期");
                await CookieExpire();
                return;
            }

            if (code == -403)
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 无法给 {target.Name}(uid:{target.Uid}) 发送弹幕 '{target.MsgContent}'");

                await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                string query =
                    $"update target_table set msg_status = -2 where uid={Uid} and target_uid = {target.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();

                continue;
            }

            if (code == 10024 || code == 1003)
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 已被 {target.Name}(uid:{target.Uid}) 封禁");

                await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                string query =
                    $"update target_table set msg_status = -5 where uid={Uid} and target_uid = {target.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();

                continue;
            }

            if ((string)responseJson["msg"] == "k")
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 给 {target.Name}(uid:{target.Uid}) 发送的弹幕 '{target.MsgContent}' 中含有屏蔽词");

                await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                string query =
                    $"update target_table set msg_status = -1 where uid={Uid} and target_uid = {target.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();

                continue;
            }

            if (code != 0)
            {
                await _logger.Log(responseJson);
                await _logger.Log($"uid {Uid} 给 {target.Name}(uid:{target.Uid}) 发送弹幕失败");
                throw new ApiException();
            }

            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query = $"update target_table set msg_status = 1 where uid={Uid} and target_uid = {target.Uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();
            }

            // await _logger.Log($"uid {Uid} 给 {target.Name}(uid:{target.Uid}) 发送弹幕成功");
            await Task.Delay(3000);
        }
    }

    public async Task PostLike()
    {
        foreach (var target in Targets)
        {
            if (target.RoomId == "0")
            {
                continue;
            }

            var payload = new Dictionary<string, string>
            {
                { "roomid", target.RoomId },
                { "csrf", Csrf },
                { "csrf_token", Csrf }
            };
            for (int i = 0; i < 3 - target.LikeNum; i++)
            {
                HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri("https://api.live.bilibili.com/xlive/web-ucenter/v1/interact/likeInteract"),
                    Headers = { { "Cookie", Cookie } },
                    Content = new FormUrlEncodedContent(payload)
                });
                await Task.Delay(1000);
                JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                int code = (int)responseJson["code"];

                if (code != 0)
                {
                    await _logger.Log(responseJson);
                    throw new ApiException();
                }

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query =
                        $"update target_table set like_num = like_num+1 where uid = {Uid} and target_uid = {target.Uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await Task.Delay(1000);
            }
        }
    }

    public async Task ShareRoom()
    {
        foreach (var target in Targets)
        {
            var payload = new Dictionary<string, string>
            {
                { "roomid", target.RoomId },
                { "interact_type", "3" },
                { "csrf", Csrf },
                { "csrf_token", Csrf }
            };
            for (int i = 0; i < 5 - target.ShareNum; i++)
            {
                HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri("https://api.live.bilibili.com/xlive/app-room/v1/index/TrigerInteract"),
                    Headers = { { "Cookie", Cookie } },
                    Content = new FormUrlEncodedContent(payload)
                });
                await Task.Delay(1000);
                JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                int code = (int)responseJson["code"];

                if (code != 0)
                {
                    await _logger.Log(responseJson);
                    throw new ApiException();
                }

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query =
                        $"update target_table set share_num = share_num+1 where uid = {Uid} and target_uid = {target.Uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await Task.Delay(6000);
            }
        }
    }
}

class Target
{
    public string Uid { get; set; }
    public string Name { get; set; }
    public string RoomId { get; set; }
    public int LikeNum { get; set; }
    public int ShareNum { get; set; }
    public string MsgContent { get; set; }
    public int MsgStatus { get; set; }
}