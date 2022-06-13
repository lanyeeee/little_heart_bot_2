using System;
using System.IO;
using System.Threading.Tasks;

namespace little_heart_bot_2;

public class Logger
{
    //TextWriter是线程安全的
    private TextWriter _writer;
    private string _fileName;
    private readonly string _name;
    private int _count;

    public Logger(string name)
    {
        _name = name;
        _fileName = _name + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        Directory.CreateDirectory("log");
        _writer = File.AppendText("log/" + _fileName);
    }

    public async Task Log(params object[] args)
    {
        string text = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}]";

        foreach (var arg in args)
        {
            text += $" {arg}";
        }

        await _writer.WriteLineAsync(text);
        await _writer.FlushAsync();

        _count++;
        if (_count == 10)
        {
            if (NeedToRoll())
            {
                Roll();
            }

            _count = 0;
        }
    }

    private bool NeedToRoll()
    {
        if (new FileInfo("log/" + _fileName).Length > 1024 * 1024)
        {
            return true;
        }

        return false;
    }

    private void Roll()
    {
        _fileName = _name + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _writer = File.AppendText("log/" + _fileName);
    }
}