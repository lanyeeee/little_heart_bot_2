namespace little_heart_bot_2;

public class Target
{
    public string? Uid { get; init; }
    public string? Name { get; init; }
    public string? RoomId { get; init; }
    public int LikeNum { get; set; }
    public int ShareNum { get; set; }
    public string? MsgContent { get; init; }
    public int MsgStatus { get; set; }

    public string GetTargetText()
    {
        string targetText = "";
        int msgNum = MsgStatus == 1 ? 1 : 0;
        targetText +=
            $"{Name}(uid:{Uid})：弹幕({msgNum}/1) 点赞({LikeNum}/3) 分享({ShareNum}/5)\n";
        targetText += $"弹幕：{MsgContent}\n";

        if (MsgStatus == 1) return targetText;

        string msgText = "";
        if (MsgStatus == 0) //还未发送
        {
            msgText = "排队中";
        }
        else if (MsgStatus == -1) //屏蔽词
        {
            msgText = "弹幕中含有屏蔽词";
        }
        else if (MsgStatus == -2) // 可能是UL等级不够
        {
            msgText = "可能是UL等级低于目标直播间屏蔽等级";
        }
        else if (MsgStatus == -3) //cookie错误或过期
        {
            msgText = "cookie错误或已过期";
        }
        else if (MsgStatus == -4) //没直播间
        {
            msgText = "目标未开通直播间";
        }
        else if (MsgStatus == -5) //被封
        {
            msgText = "被目标直播间禁言";
        }
        else if (MsgStatus == -6) //连着发了3条都说太频繁
        {
            msgText = "尝试了3次，每次间隔10秒，依然提示弹幕发送太过频繁";
        }
        else if (MsgStatus == -400)
        {
            msgText = "未知错误";
        }

        targetText += "未发送原因：" + msgText + "\n";
        return targetText;
    }
}