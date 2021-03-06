using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace little_heart_bot_2;

public class Bot
{
    private static Bot? _instance;
    public static Bot Instance => _instance ?? new Bot();

    private readonly string? _uid;
    private readonly string? _cookie;
    private readonly string? _csrf;
    private readonly string? _devId;
    private readonly Logger _logger;

    private bool _talking = true;
    private int _talkNum;
    private readonly Dictionary<string, Session> _sessions = new();
    private long _midnight; //今天0点的分钟时间戳

    private Bot()
    {
        _instance = this;
        DateTimeOffset today = DateTime.Today;
        _midnight = today.ToUnixTimeSeconds();

        _logger = new Logger("bot");

        using var conn = Globals.GetOpenedMysqlConnection();
        string query = "select * from bot_table";
        using var comm = new MySqlCommand(query, conn);
        using var reader = comm.ExecuteReader();
        if (!reader.Read()) return;
        _uid = reader.GetString("uid");
        _cookie = reader.GetString("cookie");
        _csrf = reader.GetString("csrf");
        _devId = reader.GetString("dev_id");
        Globals.AppStatus = reader.GetInt32("app_status");
        Globals.ReceiveStatus = reader.GetInt32("receive_status");
        Globals.SendStatus = reader.GetInt32("send_status");
    }

    private async Task FetchSession()
    {
        await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
        string query = "select * from user_table";
        await using var comm = new MySqlCommand(query, conn);
        await using var reader = await comm.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Session session = new Session
            {
                Uid = reader.GetString("uid"),
                MsgTimestamp = reader.GetString("msg_timestamp"),
                ConfigTimestamp = reader.GetString("config_timestamp"),
                ConfigNum = reader.GetInt32("config_num"),
                TargetNum = reader.GetInt32("target_num")
            };
            _sessions.Add(session.Uid, session);
        }
    }

    private string? GetCsrf(string? cookie)
    {
        return cookie?.Substring(cookie.IndexOf("bili_jct=", StringComparison.Ordinal) + 9, 32);
    }

    private async Task<JToken?> GetSessionList()
    {
        //普通的私信session
        HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri("https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions?session_type=1"),
            Headers = { { "Cookie", _cookie } },
        });
        await Task.Delay(1000);
        JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
        int? code = (int?)responseJson["code"];
        if (code != 0)
        {
            await _logger.Log(responseJson);
            await _logger.Log("获取普通的session_list失败");
            throw new ApiException();
        }

        JArray? sessionList = (JArray?)responseJson["data"]!["session_list"];

        //被屏蔽的私信session
        response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri("https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions?session_type=5"),
            Headers = { { "Cookie", _cookie } },
        });
        responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
        code = (int?)responseJson["code"];
        if (code != 0)
        {
            await _logger.Log(responseJson);
            await _logger.Log("获取被屏蔽的session_list失败");
            throw new ApiException();
        }

        JToken? blockedList = responseJson["data"]!["session_list"];

        if (blockedList == null) return sessionList;

        foreach (var blockedSession in blockedList)
        {
            sessionList?.Add(blockedSession);
        }

        return sessionList;
    }

    private async Task SendOverallConfig(string uid)
    {
        long timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        //查询次数超过5次或者查询频率小于1分钟就忽略这次查询
        if (_sessions[uid].ConfigNum >= 5 || timestamp - Int64.Parse(_sessions[uid].ConfigTimestamp!) < 60)
            return;


        var payload = new Dictionary<string, string?>
        {
            { "msg[sender_uid]", _uid },
            { "msg[receiver_id]", uid },
            { "msg[receiver_type]", "1" },
            { "msg[msg_type]", "1" },
            { "msg[dev_id]", _devId },
            { "msg[timestamp]", timestamp.ToString() },
            { "msg[content]", "" },
            { "csrf", _csrf }
        };

        await using var conn1 = await Globals.GetOpenedMysqlConnectionAsync();
        string query = $"select * from user_table where uid = {uid}";
        await using var comm1 = new MySqlCommand(query, conn1);
        await using var reader1 = await comm1.ExecuteReaderAsync();

        await using var conn2 = await Globals.GetOpenedMysqlConnectionAsync();
        query = $"select * from target_table where  uid = {uid}";
        await using var comm2 = new MySqlCommand(query, conn2);
        await using var reader2 = await comm2.ExecuteReaderAsync();

        await reader1.ReadAsync();

        string cookie = reader1.GetString("cookie");
        int cookieStatus = reader1.GetInt32("cookie_status");
        int completed = reader1.GetInt32("completed");
        int configNum = reader1.GetInt32("config_num");
        string cookieText = String.IsNullOrEmpty(cookie) ? "无" : "有";
        string completedText = completed == 0 ? "未完成" : "已完成";
        string configNumText = $"({configNum + 1}/5)";

        string cookieStatusText = "";
        if (cookieStatus == 0)
            cookieStatusText = "还未被使用";
        else if (cookieStatus == -1)
            cookieStatusText = "错误或已过期";
        else if (cookieStatus == 1)
            cookieStatusText = "直到上次使用还有效";


        List<Target> targets = new();
        while (await reader2.ReadAsync())
        {
            Target target = new Target
            {
                Uid = reader2.GetString("target_uid"),
                Name = reader2.GetString("target_name"),
                RoomId = reader2.GetString("room_id"),
                LikeNum = reader2.GetInt32("like_num"),
                ShareNum = reader2.GetInt32("share_num"),
                MsgContent = reader2.GetString("msg_content"),
                MsgStatus = reader2.GetInt32("msg_status")
            };
            targets.Add(target);
        }

        string targetText = "";
        targets.ForEach(target => targetText += target.GetTargetText());

        string msg = "所有任务状态：\n" +
                     targetText + "\n" +
                     $"cookie状态：{cookieText}，{cookieStatusText}\n" +
                     $"今日任务状态：{completedText}\n" +
                     $"已用查询次数：{configNumText}\n";

        if (msg.Length > 470) //上限提升后文字可能会过长，如果太长则化简
        {
            targetText = "";

            foreach (var target in targets)
            {
                int msgNum = target.MsgStatus == 1 ? 1 : 0;
                targetText +=
                    $"{target.Name}：弹幕({msgNum}/1) 点赞({target.LikeNum}/3) 分享({target.ShareNum}/5)\n";
            }

            msg = "所有任务状态(简略版)：\n" +
                  targetText + "\n" +
                  $"cookie状态：{cookieText}，{cookieStatusText}\n" +
                  $"今日任务状态：{completedText}\n" +
                  $"已用查询次数：{configNumText}\n";
        }

        if (msg.Length > 470) //如果化简后还太长，则极简
        {
            targetText = "";

            targets.ForEach(target => targetText += $"{target.Name}\n");

            msg = "所有任务状态(极简版)：\n" +
                  targetText + "\n" +
                  $"cookie状态：{cookieText}，{cookieStatusText}\n" +
                  $"今日任务状态：{completedText}\n" +
                  $"已用查询次数：{configNumText}\n";
        }

        if (msg.Length > 470) //如果极简还是太长，就直接省略
        {
            msg = "所有任务状态：\n" +
                  "目标太多，光是列出名字长度都会超过私信限制，因此直接省略\n" + "\n" +
                  $"cookie状态：{cookieText}，{cookieStatusText}\n" +
                  $"今日任务状态：{completedText}\n" +
                  $"已用查询次数：{configNumText}\n";
        }

        JObject obj = new JObject();
        obj["content"] = msg;
        payload["msg[content]"] = obj.ToString(Formatting.None);

        HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri("https://api.vc.bilibili.com/web_im/v1/web_im/send_msg"),
            Headers = { { "Cookie", _cookie } },
            Content = new FormUrlEncodedContent(payload)
        });
        await Task.Delay(1000);
        JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
        int? code = (int?)responseJson["code"];

        if (code != 0)
        {
            await _logger.Log("私信发送失败");
            if (code == 21024)
            {
                await _logger.Log(responseJson);
            }
            else
            {
                await _logger.Log($"今日发送的私信已到上限，今天总共发了{_talkNum}条私信");
                _talking = false;
                _talkNum = 0;
            }

            return;
        }

        _talkNum++;
        _sessions[uid].ConfigTimestamp = timestamp.ToString();
        _sessions[uid].ConfigNum++;

        await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
        string sql =
            $"update user_table set config_num = config_num+1,config_timestamp = {_sessions[uid].ConfigTimestamp} where uid = {uid}";
        MySqlCommand sqlCommand = new MySqlCommand(sql, conn);
        await sqlCommand.ExecuteNonQueryAsync();
    }

    private async Task SendTargetConfig(string uid, string targetUid)
    {
        long timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        //查询次数超过5次或者查询频率小于1分钟就忽略这次查询
        if (_sessions[uid].ConfigNum >= 5 || timestamp - Int64.Parse(_sessions[uid].ConfigTimestamp!) < 60 ||
            !Globals.IsNumeric(targetUid))
            return;


        var payload = new Dictionary<string, string?>
        {
            { "msg[sender_uid]", _uid },
            { "msg[receiver_id]", uid },
            { "msg[receiver_type]", "1" },
            { "msg[msg_type]", "1" },
            { "msg[dev_id]", _devId },
            { "msg[timestamp]", timestamp.ToString() },
            { "msg[content]", "" },
            { "csrf", _csrf }
        };

        await using var conn1 = await Globals.GetOpenedMysqlConnectionAsync();
        string query = $"select * from user_table where uid = {uid}";
        await using var comm1 = new MySqlCommand(query, conn1);
        await using var reader1 = await comm1.ExecuteReaderAsync();

        await using var conn2 = await Globals.GetOpenedMysqlConnectionAsync();
        query = $"select * from target_table where  uid = {uid} and target_uid = {targetUid}";
        await using var comm2 = new MySqlCommand(query, conn2);
        await using var reader2 = await comm2.ExecuteReaderAsync();

        await reader1.ReadAsync();
        string cookie = reader1.GetString("cookie");
        int cookieStatus = reader1.GetInt32("cookie_status");
        int completed = reader1.GetInt32("completed");
        int configNum = reader1.GetInt32("config_num");

        string cookieText = String.IsNullOrEmpty(cookie) ? "无" : "有";
        string completedText = completed == 0 ? "未完成" : "已完成";
        string configNumText = $"({configNum + 1}/5)";

        string cookieStatusText = "";
        if (cookieStatus == 0)
        {
            cookieStatusText = "还未被使用";
        }
        else if (cookieStatus == -1)
        {
            cookieStatusText = "错误或已过期";
        }
        else if (cookieStatus == 1)
        {
            cookieStatusText = "直到上次使用还有效";
        }

        string? targetText;
        if (await reader2.ReadAsync())
        {
            Target target = new Target
            {
                Uid = reader2.GetString("target_uid"),
                Name = reader2.GetString("target_name"),
                RoomId = reader2.GetString("room_id"),
                LikeNum = reader2.GetInt32("like_num"),
                ShareNum = reader2.GetInt32("share_num"),
                MsgContent = reader2.GetString("msg_content"),
                MsgStatus = reader2.GetInt32("msg_status")
            };

            targetText = target.GetTargetText();
        }
        else
        {
            targetText = $"你没有设置发送给 uid：{targetUid} 的弹幕";
        }

        string msg = $"uid：{targetUid} 的任务状态：\n" +
                     targetText + "\n" +
                     $"cookie状态：{cookieText}，{cookieStatusText}\n" +
                     $"今日任务状态：{completedText}\n" +
                     $"已用查询次数：{configNumText}\n";
        JObject obj = new JObject();
        obj["content"] = msg;
        payload["msg[content]"] = obj.ToString(Formatting.None);

        HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri("https://api.vc.bilibili.com/web_im/v1/web_im/send_msg"),
            Headers = { { "Cookie", _cookie } },
            Content = new FormUrlEncodedContent(payload)
        });
        await Task.Delay(1000);
        JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
        int? code = (int?)responseJson["code"];

        if (code != 0)
        {
            await _logger.Log("私信发送失败");
            if (code == 21024)
            {
                await _logger.Log(responseJson);
            }
            else
            {
                await _logger.Log($"今日发送的私信已到上限，今天总共发了{_talkNum}条私信");
                _talking = false;
                _talkNum = 0;
            }

            return;
        }

        _talkNum++;
        _sessions[uid].ConfigTimestamp = timestamp.ToString();
        _sessions[uid].ConfigNum++;

        await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
        string sql =
            $"update user_table set config_num = config_num+1,config_timestamp = {_sessions[uid].ConfigTimestamp} where uid = {uid}";
        MySqlCommand sqlCommand = new MySqlCommand(sql, conn);
        await sqlCommand.ExecuteNonQueryAsync();
    }

    private async Task HandleCommand(string uid, string command, string? parameter)
    {
        if (command == "/target_set")
        {
            if (parameter == null) return;
            string[] pair = parameter.Split(" ", 2);
            if (pair.Length == 2)
            {
                string targetUid = pair[0].Trim();
                string msg = pair[1].Trim();
                //targetUid不是数字 或者 弹幕太长 或者 设置的目标太多，就忽略掉
                if (!Globals.IsNumeric(targetUid) || msg.Length > 20 ||
                    _sessions[uid].TargetNum >= 50) //TODO  暂时将上限开到50看看效果
                {
                    return;
                }

                HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri($"https://api.bilibili.com/x/space/acc/info?mid={targetUid}")
                });
                await Task.Delay(1000);
                JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                int? code = (int?)responseJson["code"];

                if (code == -400) //忽略掉错误的targetUid
                {
                    return;
                }

                if (code != 0)
                {
                    await _logger.Log(responseJson);
                    await _logger.Log($"uid {uid} 获取 {targetUid} 的直播间数据失败");
                    throw new ApiException();
                }

                JToken? data = responseJson["data"];
                if (data == null) return;

                string? targetName = (string?)data["name"];
                string? roomId = (string?)data["live_room"]!["roomid"];

                await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                string query = $"select * from target_table where uid = {uid} and target_uid = {targetUid}";
                await using var comm = new MySqlCommand(query, conn);
                await using var reader = await comm.ExecuteReaderAsync();
                //如果目标已存在
                if (await reader.ReadAsync())
                {
                    await using (var conn1 = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        string sql =
                            $"update target_table set target_name = '{targetName}',room_id={roomId},msg_content = @msg,completed = 0,msg_status = 0 where uid = {uid} and target_uid = {targetUid}";
                        await using var comm1 = new MySqlCommand(sql, conn1);
                        comm1.Parameters.AddWithValue("@msg", msg);
                        await comm1.ExecuteNonQueryAsync();
                    }

                    await using (var conn1 = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        string sql = $"update user_table set completed = 0 where uid = {uid}";
                        await using var comm1 = new MySqlCommand(sql, conn1);
                        await comm1.ExecuteNonQueryAsync();
                    }
                }
                else //如果目标不存在
                {
                    await using (var conn1 = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        string sql =
                            $"insert into target_table(uid,target_uid,target_name,room_id,msg_content) values({uid},{targetUid},'{targetName}',{roomId},@msg)";
                        await using var comm1 = new MySqlCommand(sql, conn1);
                        comm1.Parameters.AddWithValue("@msg", msg);
                        await comm1.ExecuteNonQueryAsync();
                    }

                    await using (var conn1 = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        _sessions[uid].TargetNum++;
                        string sql = $"update user_table set completed = 0,target_num=target_num+1 where uid = {uid}";
                        await using var comm1 = new MySqlCommand(sql, conn1);
                        await comm1.ExecuteNonQueryAsync();
                    }
                }
            }
        }
        else if (command == "/target_delete")
        {
            if (parameter == null) return;

            if (parameter == "all")
            {
                _sessions[uid].TargetNum = 0;
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = $"delete from target_table where uid = {uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = $"update user_table set target_num = 0 where uid = {uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }
            }
            else if (Globals.IsNumeric(parameter))
            {
                _sessions[uid].TargetNum--;
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = $"delete from target_table where uid = {uid} and target_uid = @target_uid";
                    await using var comm = new MySqlCommand(query, conn);
                    comm.Parameters.AddWithValue("@target_uid", parameter);
                    await comm.PrepareAsync();
                    await comm.ExecuteNonQueryAsync();
                }

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = $"update user_table set target_num = {_sessions[uid].TargetNum} where uid = {uid}";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }
            }
        }
        else if (command == "/config")
        {
            if (!_talking) return;

            if (string.IsNullOrWhiteSpace(parameter))
            {
                await SendOverallConfig(uid);
            }
            else
            {
                await SendTargetConfig(uid, parameter);
            }
        }
        else if (command == "/delete")
        {
            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query = $"delete from target_table where uid = {uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();
            }

            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                string query = $"delete from user_table where uid = {uid}";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();
            }

            await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
            {
                Session session = _sessions[uid];
                string query =
                    $"insert into user_table(uid,config_num,msg_timestamp,config_timestamp) " +
                    $"values({uid},{session.ConfigNum},{session.MsgTimestamp},{session.ConfigTimestamp})";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();
            }
        }
        else if (command == "/cookie_commit")
        {
            try
            {
                string? cookie = parameter?.Replace("\n", "");
                string? csrf = GetCsrf(cookie);
                if (csrf == null) return;

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query =
                        "update user_table set cookie = @cookie,csrf=@csrf,cookie_status = 0 where uid = @uid;";
                    await using var comm = new MySqlCommand(query, conn);
                    comm.Parameters.AddWithValue("@cookie", cookie);
                    comm.Parameters.AddWithValue("@csrf", csrf);
                    comm.Parameters.AddWithValue("@uid", uid);
                    await comm.PrepareAsync();
                    await comm.ExecuteNonQueryAsync();
                }

                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = "update target_table set msg_status = 0 where uid = @uid";
                    await using var comm = new MySqlCommand(query, conn);
                    comm.Parameters.AddWithValue("@uid", uid);
                    await comm.PrepareAsync();
                    await comm.ExecuteNonQueryAsync();
                }
            }
            catch (Exception)
            {
                await _logger.Log($"uid {uid} 提交的cookie有误");
            }
        }
    }

    private async Task HandleMessage(string uid, int lastTimestamp, IEnumerable<JToken>? messages)
    {
        if (messages == null) return;
        foreach (var msg in messages)
        {
            if ((int?)msg["timestamp"] <= lastTimestamp || (string?)msg["sender_uid"] == _uid ||
                (int?)msg["msg_type"] != 1) continue;

            try
            {
                string? timestamp = (string?)msg["timestamp"];
                string? contentJson = (string?)msg["content"];
                if (timestamp == null || contentJson == null) return;

                _sessions[uid].MsgTimestamp = timestamp;
                string? content = (string?)JObject.Parse(contentJson)["content"];
                content = content?.Trim();
                await _logger.Log($"{uid}：{content}");
                if (content?.StartsWith("/") ?? false)
                {
                    string[] pair = content.Split(" ", 2);
                    if (pair.Length == 2)
                    {
                        string command = pair[0].Trim();
                        string parameter = pair[1].Trim();
                        await HandleCommand(uid, command, parameter);
                    }
                    else
                    {
                        string command = pair[0].Trim();
                        await HandleCommand(uid, command, null);
                    }
                }
            }
            catch (JsonReaderException)
            {
            }
        }
    }

    private async Task HandleIncomingMessage()
    {
        JToken? sessionList = await GetSessionList();
        if (sessionList == null) return;

        foreach (var session in sessionList)
        {
            string? uid = (string?)session["talker_id"];
            if (uid == null) continue;

            int? timestamp = session["last_msg"]!.HasValues ? (int?)session["last_msg"]!["timestamp"] : 0;

            //新用户或发新消息的用户
            if (!_sessions.ContainsKey(uid) || timestamp > Int32.Parse(_sessions[uid].MsgTimestamp!))
            {
                HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri =
                        new Uri(
                            $"https://api.vc.bilibili.com/svr_sync/v1/svr_sync/fetch_session_msgs?talker_id={uid}&session_type=1"),
                    Headers = { { "Cookie", _cookie } },
                });
                await Task.Delay(1000);
                JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                int? code = (int?)responseJson["code"];
                if (code != 0)
                {
                    await _logger.Log(responseJson);
                    await _logger.Log($"与 {uid} 的聊天记录获取失败");
                    throw new ApiException();
                }

                IEnumerable<JToken>? messages = responseJson["data"]!["messages"]?.Reverse();

                if (_sessions.ContainsKey(uid))
                {
                    await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        string query = $"update user_table set msg_timestamp = {timestamp} where uid = {uid}";
                        await using var comm = new MySqlCommand(query, conn);
                        await comm.ExecuteNonQueryAsync();
                    }

                    int lastTimestamp = Int32.Parse(_sessions[uid].MsgTimestamp!);
                    await HandleMessage(uid, lastTimestamp, messages);
                }
                else
                {
                    Session s = new Session
                    {
                        Uid = uid,
                        MsgTimestamp = "0",
                        ConfigTimestamp = "0",
                        ConfigNum = 0,
                        TargetNum = 0
                    };
                    _sessions.Add(uid, s);
                    await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                    {
                        string query = $"insert into user_table(uid,msg_timestamp) values({uid},{timestamp})";
                        await using var comm = new MySqlCommand(query, conn);
                        await comm.ExecuteNonQueryAsync();
                    }

                    await HandleMessage(uid, 0, messages);
                }
            }
        }
    }

    private async Task<bool> IsNewDay()
    {
        if (DateTimeOffset.Now.ToUnixTimeSeconds() - _midnight < 24 * 60 * 60 + 10 * 60)
            return false;

        //新的一天要把一些数据重置
        DateTimeOffset today = DateTime.Today;
        _midnight = today.ToUnixTimeSeconds();

        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query =
                "update target_table set msg_status = 0,completed = 0,share_num = 0,like_num = 0 where 1";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }

        await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
        {
            string query = "update user_table set completed = 0,config_num = 0 where 1";
            await using var comm = new MySqlCommand(query, conn);
            await comm.ExecuteNonQueryAsync();
        }

        return true;
    }

    private string MakeSign()
    {
        string sign = "给你【";
        if (Globals.AppStatus == 0)
        {
            sign += "弹幕、点赞、分享正常";
        }
        else if (Globals.AppStatus == -1)
        {
            sign += "弹幕、点赞、分享正常冷却中";
        }

        sign += "，";


        if (Globals.ReceiveStatus == 0)
        {
            sign += "接收私信正常";
        }
        else if (Globals.ReceiveStatus == -1)
        {
            sign += "接收私信冷却中";
        }

        sign += "，";

        if (Globals.SendStatus == 0)
        {
            sign += "发送私信正常";
        }
        else if (Globals.SendStatus == -1)
        {
            sign += "发送私信冷却中";
        }
        else if (Globals.SendStatus == -2)
        {
            sign += "发送私信已禁言";
        }

        sign += "】";
        return sign;
    }


    private async Task UpdateSign()
    {
        int lastAppStatus = 1;
        int lastReceiveStatus = 1;
        int lastSendStatus = 1;

        while (true)
        {
            try
            {
                if (lastAppStatus != Globals.AppStatus || lastReceiveStatus != Globals.ReceiveStatus ||
                    lastSendStatus != Globals.SendStatus)
                {
                    string sign = MakeSign();

                    var payload = new Dictionary<string, string?>
                    {
                        { "user_sign", sign },
                        { "jsonp", "jsonp" },
                        { "csrf", _csrf }
                    };

                    HttpResponseMessage response = await Globals.HttpClient.SendAsync(new HttpRequestMessage
                    {
                        Method = HttpMethod.Post,
                        RequestUri = new Uri("https://api.bilibili.com/x/member/web/sign/update"),
                        Headers = { { "Cookie", _cookie } },
                        Content = new FormUrlEncodedContent(payload)
                    });
                    await Task.Delay(1000);
                    JObject responseJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                    await _logger.Log(responseJson);
                    await _logger.Log("签名改为：" + sign);
                    lastAppStatus = Globals.AppStatus;
                    lastReceiveStatus = Globals.ReceiveStatus;
                    lastSendStatus = Globals.SendStatus;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

            await Task.Delay(1000);
        }
    }

    private async Task BotMain()
    {
        while (true)
        {
            try
            {
                await FetchSession();

                if (await IsNewDay())
                {
                    continue;
                }

                await HandleIncomingMessage();

                Globals.ReceiveStatus = 0;
                await using (var conn = await Globals.GetOpenedMysqlConnectionAsync())
                {
                    string query = "update bot_table set receive_status = 0 where 1";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                    await comm.ExecuteNonQueryAsync();
                }

                if (_talking)
                {
                    Globals.SendStatus = 0;
                    await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                    string query = "update bot_table set send_status = 0 where 1";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }
                else
                {
                    Globals.SendStatus = -2;
                    await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                    string query = "update bot_table set send_status = -2 where 1";
                    await using var comm = new MySqlCommand(query, conn);
                    await comm.ExecuteNonQueryAsync();
                }

                await Task.Delay(2000);
            }
            catch (ApiException)
            {
                Globals.ReceiveStatus = -1;
                Globals.SendStatus = -1;
                await using var conn = await Globals.GetOpenedMysqlConnectionAsync();
                string query = "update bot_table set receive_status = -1,send_status = -1 where 1";
                await using var comm = new MySqlCommand(query, conn);
                await comm.ExecuteNonQueryAsync();

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
                _sessions.Clear();
            }
        }
    }

    public async Task Main()
    {
        var tasks = new List<Task>
        {
            UpdateSign(), BotMain()
        };
        await Task.WhenAll(tasks);
    }
}