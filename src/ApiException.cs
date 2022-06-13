using System;

namespace little_heart_bot_2;

public class ApiException : Exception
{
    public ApiException()
    {
    }

    public ApiException(string message)
        : base(message)
    {
    }

    public ApiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}