using UnityEngine;
using System.Diagnostics;
using System.Reflection;
using System;

public static class ConsoleMessage
{
    public static event Action<string> OnMessageSent;

    public static void Send(bool debugMode, string message, Color _color)
    {
        if (!debugMode) return;

        StackTrace stackTrace = new StackTrace();
        MethodBase method = stackTrace.GetFrame(1).GetMethod();
        string callerClass = method.DeclaringType.Name;

        string colorCode = ColorUtility.ToHtmlStringRGB(_color);
        string formattedMessage = $"<b>[{callerClass}]</b> | <color=#{colorCode}>{message}</color>";

        UnityEngine.Debug.Log(formattedMessage);
        OnMessageSent?.Invoke(formattedMessage);
    }
}
