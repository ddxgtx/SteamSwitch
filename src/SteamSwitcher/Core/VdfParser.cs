using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamSwitcher.Core
{
    public class VdfParser
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int MultiByteToWideChar(
            uint codePage, uint flags, byte[] bytes, int byteCount, char[]? chars, int charCount);

        public Dictionary<string, object> Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"VDF file not found: {filePath}");

            var bytes = File.ReadAllBytes(filePath);
            return ParseContent(DecodeText(bytes));
        }

        private static string DecodeText(byte[] bytes)
        {
            var offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                offset = 3;
            if (offset > 0)
            {
                var trimmed = new byte[bytes.Length - offset];
                Buffer.BlockCopy(bytes, offset, trimmed, 0, trimmed.Length);
                bytes = trimmed;
            }

            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
            }

            var gbk = TryDecodeAnsiCodePage(bytes, 936)
                      ?? TryDecodeAnsiCodePage(bytes, 54936);
            return gbk ?? DecodeWithReplacement(bytes);
        }

        private static string? TryDecodeAnsiCodePage(byte[] bytes, uint codePage)
        {
            if (bytes.Length == 0)
                return string.Empty;
            try
            {
                var charCount = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
                if (charCount <= 0)
                    return null;
                var chars = new char[charCount];
                var written = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, chars, charCount);
                if (written != charCount)
                    return null;
                return new string(chars);
            }
            catch
            {
                return null;
            }
        }

        private static string DecodeWithReplacement(byte[] bytes)
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
        }

        private Dictionary<string, object> ParseContent(string content)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            ParseObject(content, ref pos, result, nested: false, depth: 0);
            return result;
        }

        private void ParseObject(string content, ref int pos, Dictionary<string, object> obj, bool nested, int depth)
        {
            if (depth > 64) throw new InvalidDataException("VDF 对象嵌套超过 64 层上限。");
            while (pos < content.Length)
            {
                SkipWhitespace(content, ref pos);
                if (pos >= content.Length) break;

                if (content[pos] == '}')
                {
                    if (!nested) throw new InvalidDataException($"Unexpected closing brace at position {pos}.");
                    pos++;
                    return;
                }

                var key = ReadString(content, ref pos);
                if (string.IsNullOrEmpty(key))
                {
                    throw new InvalidDataException($"Empty or invalid VDF key at position {pos}.");
                }
                if (obj.ContainsKey(key)) throw new InvalidDataException($"Duplicate VDF key '{key}'.");

                SkipWhitespace(content, ref pos);
                if (pos >= content.Length || content[pos] == '}')
                    throw new InvalidDataException($"Missing value for VDF key '{key}'.");

                if (content[pos] == '{')
                {
                    pos++;
                    var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    ParseObject(content, ref pos, child, nested: true, depth: depth + 1);
                    obj[key] = child;
                }
                else
                {
                    var value = ReadString(content, ref pos);
                    obj[key] = value;
                }
            }
            if (nested) throw new InvalidDataException("Unexpected end of VDF: missing closing brace.");
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
                        var next = content[pos + 1];
                        switch (next)
                        {
                            case '\\':
                                sb.Append('\\');
                                pos += 2;
                                break;
                            case '"':
                                sb.Append('"');
                                pos += 2;
                                break;
                            case 'n':
                                sb.Append('\n');
                                pos += 2;
                                break;
                            case 't':
                                sb.Append('\t');
                                pos += 2;
                                break;
                            case 'r':
                                sb.Append('\r');
                                pos += 2;
                                break;
                            default:
                                sb.Append('\\');
                                sb.Append(next);
                                pos += 2;
                                break;
                        }
                    }
                    else
                    {
                        sb.Append(content[pos]);
                        pos++;
                    }
                }
                if (pos >= content.Length) throw new InvalidDataException("Unexpected end of VDF: unterminated quoted string.");
                pos++;
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
                    sb.Append(indentStr).Append('"').Append(EscapeVdfString(kvp.Key)).Append('"').Append('\n');
                    sb.Append(indentStr).Append('{').Append('\n');
                    SerializeObject(child, sb, indent + 1);
                    sb.Append(indentStr).Append('}').Append('\n');
                }
                else
                {
                    sb.Append(indentStr).Append('"').Append(EscapeVdfString(kvp.Key)).Append('"')
                        .Append("\t\t\"").Append(EscapeVdfString(kvp.Value?.ToString() ?? "")).Append('"').Append('\n');
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
