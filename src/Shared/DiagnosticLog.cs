using System;
using System.IO;
using System.Text;

namespace LeafTab.Shared
{
    internal static class DiagnosticLog
    {
        private static readonly object Gate = new object();
        internal static string DirectoryPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTab", "logs");

        internal static void Write(string operation, Exception? error = null)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    string path = Path.Combine(DirectoryPath, "diagnostic.log");
                    if (File.Exists(path) && new FileInfo(path).Length >= 512 * 1024)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    // Event names and exception types only: no folder contents, paths or bookmarks.
                    File.AppendAllText(path, $"{DateTime.UtcNow:O} {operation} {error?.GetType().Name} {error?.HResult:X8}\r\n", Encoding.UTF8);
                }
            }
            catch { /* Diagnostics must not prevent recovery. */ }
        }
    }
}
