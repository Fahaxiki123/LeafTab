using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;

namespace LeafTab.Services
{
    internal static class LaunchRequestCodec
    {
        internal const int MaxPayloadBytes = 512 * 1024;
        private const int MaxTargets = 128;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(LaunchActionRequest request)
        {
            if (request.Targets.Count > MaxTargets) throw new InvalidDataException("Too many launch targets.");
            var text = new StringBuilder("LEAFTAB_IPC_V1\n");
            if (request.OpenInNewWindow) text.AppendLine("OPEN_NEW_WINDOW");
            if (request.IsQuitProcess) text.AppendLine("QUIT_PROCESS");
            else if (request.IsPassiveStartup) text.AppendLine("BACKGROUND_NOOP");
            else if (request.Targets.Count == 0) text.AppendLine("ACTIVATE_NEW_TAB");
            else foreach (var target in request.Targets)
            {
                if (target.FolderOrAlias.Length > 32768 || (target.SelectItemName?.Length ?? 0) > 1024) throw new InvalidDataException("Launch target is too long.");
                byte[] pidl = target.PidlBytes ?? Array.Empty<byte>();
                if (pidl.Length > 0 && !PidlManager.IsValidPidlBytes(pidl)) throw new InvalidDataException("Invalid PIDL.");
                text.Append("TARGET|").Append(Convert.ToBase64String(Utf8.GetBytes(target.FolderOrAlias))).Append('|')
                    .Append(Convert.ToBase64String(Utf8.GetBytes(target.SelectItemName ?? string.Empty))).Append('|')
                    .Append(Convert.ToBase64String(pidl)).Append('\n');
                if (text.Length > MaxPayloadBytes) throw new InvalidDataException("Launch payload is too large.");
            }
            byte[] result = Utf8.GetBytes(text.ToString());
            if (result.Length > MaxPayloadBytes) throw new InvalidDataException("Launch payload is too large.");
            return result;
        }

        internal static LaunchActionRequest? Decode(byte[] payload)
        {
            if (payload.Length > MaxPayloadBytes) throw new InvalidDataException("Launch payload is too large.");
            string[] lines = Utf8.GetString(payload).Split('\n');
            if (lines.Length < 2 || lines[0].TrimEnd('\r') != "LEAFTAB_IPC_V1") throw new InvalidDataException("Unknown IPC protocol.");
            var request = new LaunchActionRequest();
            bool background = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || line == "ACTIVATE_NEW_TAB") continue;
                if (line == "OPEN_NEW_WINDOW") request.OpenInNewWindow = true;
                else if (line == "QUIT_PROCESS") request.IsQuitProcess = true;
                else if (line == "BACKGROUND_NOOP") background = true;
                else if (line.StartsWith("TARGET|", StringComparison.Ordinal))
                {
                    if (request.Targets.Count >= MaxTargets) throw new InvalidDataException("Too many launch targets.");
                    string[] parts = line.Split('|');
                    if (parts.Length < 3 || parts.Length > 4) throw new InvalidDataException("Invalid target frame.");
                    string folder = Utf8.GetString(Convert.FromBase64String(parts[1]));
                    string selected = Utf8.GetString(Convert.FromBase64String(parts[2]));
                    byte[] pidl = parts.Length == 4 ? Convert.FromBase64String(parts[3]) : Array.Empty<byte>();
                    if (folder.Length > 32768 || selected.Length > 1024 || string.IsNullOrWhiteSpace(folder) ||
                        (pidl.Length > 0 && !PidlManager.IsValidPidlBytes(pidl))) throw new InvalidDataException("Invalid target data.");
                    request.Targets.Add(new LaunchTargetItem(folder, selected, pidl));
                }
                else throw new InvalidDataException("Unknown IPC message.");
            }
            return background && !request.IsQuitProcess && request.Targets.Count == 0 ? null : request;
        }

        internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int timeoutMs, CancellationToken token)
        {
            using var data = new MemoryStream();
            byte[] buffer = new byte[4096];
            Task deadline = Task.Delay(timeoutMs, token);
            while (true)
            {
                Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (await Task.WhenAny(read, deadline).ConfigureAwait(false) != read)
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("IPC input timed out.");
                }
                int count = await read.ConfigureAwait(false);
                if (count == 0) return data.ToArray();
                if (data.Length + count > MaxPayloadBytes) throw new InvalidDataException("Launch payload is too large.");
                data.Write(buffer, 0, count);
            }
        }
    }
}
