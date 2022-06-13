using MySqlConnector;
using Newtonsoft.Json.Linq;

namespace little_heart_bot_2;

public static class Globals
{
    public static int AppStatus { get; set; }
    public static int ReceiveStatus { get; set; }
    public static int SendStatus { get; set; }
    public static HttpClient HttpClient { get; }

    private static readonly string ConnectionString;

    static Globals()
    {
        string jsonString = File.ReadAllText("MysqlOption.json");
        JObject json = JObject.Parse(jsonString);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = (string?)json["host"],
            Database = (string?)json["database"],
            UserID = (string?)json["user"],
            Password = (string?)json["password"]
        };
        ConnectionString = builder.ConnectionString;

        HttpClient = new HttpClient();
    }


    public static async Task<MySqlConnection> GetOpenedMysqlConnectionAsync()
    {
        MySqlConnection connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static MySqlConnection GetOpenedMysqlConnection()
    {
        MySqlConnection connection = new MySqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }


    public static bool IsNumeric(string value)
    {
        return value.All(char.IsNumber);
    }
}