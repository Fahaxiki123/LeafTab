using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LeafTab.Shared
{
    internal static class LegacyConfigFile
    {
        internal const long MaxSignatureFileBytes = 16L * 1024 * 1024;

        internal static bool HasLeafTabFormat(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var input = File.OpenRead(path);
                if (input.Length == 0 || input.Length > MaxSignatureFileBytes) return false;
                // These five members and item paths have been written since the first format.
                // Check JSON types before CLR coercion can turn a string into a bool or an object into an array.
                using var reader = JsonReaderWriterFactory.CreateJsonReader(input, new XmlDictionaryReaderQuotas { MaxDepth = 128 });
                if (reader.MoveToContent() != XmlNodeType.Element || reader.GetAttribute("type") != "object") return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                reader.ReadStartElement();
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    string field = reader.LocalName;
                    switch (field)
                    {
                        case "BookmarksInitialized":
                        case "ShowBookmarkBar":
                        case "ShowPreviewPane":
                            if (!seen.Add(field) || reader.GetAttribute("type") != "boolean") return false;
                            reader.Skip();
                            break;
                        case "Bookmarks":
                        case "OpenTabs":
                            if (!seen.Add(field) || !ReadPathArray(reader, field == "Bookmarks" ? "TargetPath" : "CurrentPath"))
                                return false;
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
                reader.ReadEndElement();
                return seen.Count == 5 && reader.MoveToContent() == XmlNodeType.None;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException || ex is SerializationException ||
                                       ex is XmlException) { return false; }
        }

        private static bool ReadPathArray(XmlDictionaryReader reader, string pathField)
        {
            if (reader.GetAttribute("type") != "array") return false;
            reader.ReadStartElement();
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.GetAttribute("type") != "object") return false;
                reader.ReadStartElement();
                bool hasPath = false;
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    if (reader.LocalName == pathField)
                    {
                        if (hasPath || reader.GetAttribute("type") != "string") return false;
                        hasPath = true;
                    }
                    reader.Skip();
                }
                if (!hasPath) return false;
                reader.ReadEndElement();
            }
            reader.ReadEndElement();
            return true;
        }

        internal static bool IsConfirmedCorruptJsonFile(string path)
        {
            try
            {
                using var input = File.OpenRead(path);
                if (input.Length > MaxSignatureFileBytes) return false;
                using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                string json = reader.ReadToEnd();
                // NUL bytes also occur in BOM-less UTF-16/32. Preserve unknown encodings.
                if (json.IndexOf('\0') >= 0) return false;
                int index = 0;
                bool? valid = ReadJsonValue(json, ref index, 0);
                return valid == false || (valid == true && NextToken(json, ref index) != '\0');
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException || ex is DecoderFallbackException ||
                                       ex is RegexMatchTimeoutException) { return false; }
        }

        // DataContractJsonSerializer interprets "__type" as CLR metadata. Removal needs a syntax
        // check that accepts arbitrary JSON member names and never materializes their values.
        private static readonly Regex JsonScalar = new Regex(
            @"\G(?:""(?>[^""\\\x00-\x1f]+|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""|true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static bool? ReadJsonValue(string json, ref int index, int depth)
        {
            if (depth > 128) return null;
            char token = NextToken(json, ref index);
            if (token != '{' && token != '[')
                return ReadScalar(json, ref index);

            bool isObject = token == '{';
            char end = isObject ? '}' : ']';
            index++;
            if (NextToken(json, ref index) == end)
            {
                index++;
                return true;
            }

            while (true)
            {
                if (isObject)
                {
                    if (NextToken(json, ref index) != '"' || !ReadScalar(json, ref index) ||
                        NextToken(json, ref index) != ':') return false;
                    index++;
                }
                bool? valid = ReadJsonValue(json, ref index, depth + 1);
                if (valid != true) return valid;
                token = NextToken(json, ref index);
                if (token != end && token != ',') return false;
                index++;
                if (token == end) return true;
            }
        }

        private static bool ReadScalar(string json, ref int index)
        {
            Match match = JsonScalar.Match(json, index);
            if (!match.Success) return false;
            index += match.Length;
            return true;
        }

        private static char NextToken(string json, ref int index)
        {
            while (index < json.Length)
            {
                char value = json[index];
                if (value != ' ' && value != '\t' && value != '\r' && value != '\n') return value;
                index++;
            }
            return '\0';
        }
    }
}
