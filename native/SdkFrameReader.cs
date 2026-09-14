using System;
using System.IO;
using System.Text;

namespace ClaudeCodeWorkbench
{
    internal static class SdkFrameReader
    {
        // Limit decoded UTF-16 code units before allocating a complete JSON line.
        internal static string ReadLine(TextReader reader, int maxCharacters = 8 * 1024 * 1024)
        {
            if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            var line = new StringBuilder();
            int value;
            while ((value = reader.Read()) != -1)
            {
                if (value == '\n') return line.ToString();
                if (value == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    return line.ToString();
                }
                if (line.Length >= maxCharacters) throw new InvalidDataException("SDK frame exceeds the character limit.");
                line.Append((char)value);
            }
            return line.Length == 0 ? null : line.ToString();
        }
    }
}
