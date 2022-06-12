using Newtonsoft.Json.Linq;

namespace little_heart_bot_2;

public class Session
{
    public string Uid { get; set; }
    public string MsgTimestamp { get; set; }
    public string ConfigTimestamp { get; set; }
    public string Cookie { get; set; }
    public int ConfigNum { get; set; }
    public int TargetNum { get; set; }
}