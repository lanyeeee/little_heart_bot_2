namespace little_heart_bot_2;

public class Session
{
    public string? Uid { get; init; }
    public string? MsgTimestamp { get; set; }
    public string? ConfigTimestamp { get; set; }
    public int ConfigNum { get; set; }
    public int TargetNum { get; set; }
}