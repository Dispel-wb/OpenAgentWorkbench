using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCodeWorkbench
{
    // Bounded, UTF-8-safe SSE frames; a data field may span multiple lines.
    internal sealed class SseFrameReader
    {
        private const int MaximumCharacters = 2 * 1024 * 1024;
        private readonly TextReader _reader;
        private readonly char[] _buffer = new char[4096];
        private int _offset, _count;
        public SseFrameReader(TextReader reader) { _reader = reader; }
        private async Task<string> LineAsync()
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_offset >= _count) { _count = await _reader.ReadAsync(_buffer, 0, _buffer.Length); _offset = 0; }
                if (_count == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
                var start = _offset;
                while (_offset < _count && _buffer[_offset] != '\n') _offset++;
                if (line.Length + _offset - start > MaximumCharacters) throw new InvalidDataException("上游 SSE 单行超过安全上限");
                line.Append(_buffer, start, _offset - start);
                if (_offset < _count) { _offset++; return line.ToString().TrimEnd('\r'); }
            }
        }
        public async Task<string> ReadDataAsync()
        {
            var data = new StringBuilder(); string line;
            while ((line = await LineAsync()) != null)
            {
                if (line.Length == 0) return data.ToString().TrimEnd('\n');
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var value = line.Substring(5); if (value.StartsWith(" ")) value = value.Substring(1);
                if (data.Length + value.Length > MaximumCharacters) throw new InvalidDataException("上游 SSE 事件超过安全上限");
                data.Append(value).Append('\n');
            }
            if (data.Length > 0) throw new InvalidDataException("上游 SSE 事件没有完整结束");
            return null;
        }
    }
}
