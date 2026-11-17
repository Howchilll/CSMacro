using UnityEngine;

namespace ColorLog.Tools
{
    /// <summary>Logs to the Unity Console in color (rich text).</summary>
    public static class ColorfulLogger
    {
        public static void Log(string message, Color color) => Debug.Log(Paint(message, color));

        public static void LogWarning(string message, Color color) => Debug.LogWarning(Paint(message, color));

        public static void LogError(string message, Color color) => Debug.LogError(Paint(message, color));

        /// <summary>Wraps the text in a rich-text color tag.</summary>
        public static string Paint(string message, Color color) =>
            "<color=#" + ColorUtility.ToHtmlStringRGBA(color) + ">" + message + "</color>";
    }
}
