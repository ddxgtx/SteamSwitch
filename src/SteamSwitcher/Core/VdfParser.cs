using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamSwitcher.Core
{
    public class VdfParser
    {
        public Dictionary<string, object> Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"VDF file not found: {filePath}");

            var content = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseContent(content);
        }

        private Dictionary<string, object> ParseContent(string content)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            ParseObject(content, ref pos, result);
            return result;
        }

        private void ParseObject(string content, ref int pos, Dictionary<string, object> obj)
        {
            while (pos < content.Length)
            {
                SkipWhitespace(content, ref pos);
                if (pos >= content.Length) break;

                if (content[pos] == '}')
                {
                    pos++;
                    return;
                }

                var key = ReadString(content, ref pos);
                if (string.IsNullOrEmpty(key)) continue;

                SkipWhitespace(content, ref pos);
                if (pos >= content.Length) break;

                if (content[pos] == '{')
                {
                    pos++;
                    var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    ParseObject(content, ref pos, child);
                    obj[key] = child;
                }
                else
                {
                    var value = ReadString(content, ref pos);
                    obj[key] = value;
                }
            }
        }

        private string ReadString(string content, ref int pos)
        {
            SkipWhitespace(content, ref pos);
            if (pos >= content.Length) return string.Empty;

            if (content[pos] == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < content.Length && content[pos] != '"')
                {
                    if (content[pos] == '\\' && pos + 1 < content.Length)
                    {
                        pos++;
                        sb.Append(content[pos]);
                    }
                    else
                    {
                        sb.Append(content[pos]);
                    }
                    pos++;
                }
                if (pos < content.Length) pos++;
                return sb.ToString();
            }
            else
            {
                var sb = new StringBuilder();
                while (pos < content.Length && !char.IsWhiteSpace(content[pos]) && content[pos] != '}' && content[pos] != '{')
                {
                    sb.Append(content[pos]);
                    pos++;
                }
                return sb.ToString();
            }
        }

        private void SkipWhitespace(string content, ref int pos)
        {
            while (pos < content.Length)
            {
                if (char.IsWhiteSpace(content[pos]))
                {
                    pos++;
                }
                else if (pos + 1 < content.Length && content[pos] == '/' && content[pos + 1] == '/')
                {
                    while (pos < content.Length && content[pos] != '\n')
                        pos++;
                }
                else
                {
                    break;
                }
            }
        }

        public string Serialize(Dictionary<string, object> data, int indent = 0)
        {
            var sb = new StringBuilder();
            SerializeObject(data, sb, indent);
            return sb.ToString();
        }

        private void SerializeObject(Dictionary<string, object> obj, StringBuilder sb, int indent)
        {
            var indentStr = new string('\t', indent);
            foreach (var kvp in obj)
            {
                if (kvp.Value is Dictionary<string, object> child)
                {
                    sb.AppendLine($"{indentStr}\"{EscapeVdfString(kvp.Key)}\"");
                    sb.AppendLine($"{indentStr}{{");
                    SerializeObject(child, sb, indent + 1);
                    sb.AppendLine($"{indentStr}}}");
                }
                else
                {
                    sb.AppendLine($"{indentStr}\"{EscapeVdfString(kvp.Key)}\"\t\t\"{EscapeVdfString(kvp.Value?.ToString() ?? "")}\"");
                }
            }
        }

        private static string EscapeVdfString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
