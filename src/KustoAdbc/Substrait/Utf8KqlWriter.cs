// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// A growable UTF-8 byte buffer for building KQL query text. Numeric values
    /// are formatted directly to UTF-8 via Utf8Formatter; identifiers and string
    /// literals are escaped as they are written.
    /// </summary>
    public sealed class Utf8KqlWriter
    {
        byte[] _buffer;
        int _length;

        public Utf8KqlWriter(int initialCapacity = 256)
        {
            _buffer = new byte[initialCapacity];
        }

        public int Length => _length;

        /// <summary>Returns the written UTF-8 bytes as a span.</summary>
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

        /// <summary>Returns the written UTF-8 bytes as memory (for async I/O).</summary>
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

        /// <summary>Returns a copy of the written bytes as a new array.</summary>
        public byte[] ToArray() => WrittenSpan.ToArray();

        /// <summary>Converts the written UTF-8 to a .NET string (for tests/debugging).</summary>
        public override string ToString()
        {
#if NETSTANDARD2_0
            return Encoding.UTF8.GetString(_buffer, 0, _length);
#else
            return Encoding.UTF8.GetString(_buffer.AsSpan(0, _length));
#endif
        }

        // ── Core write primitives ──────────────────────────────────────

        /// <summary>Appends raw UTF-8 bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<byte> utf8)
        {
            EnsureCapacity(_length + utf8.Length);
            utf8.CopyTo(_buffer.AsSpan(_length));
            _length += utf8.Length;
        }

        /// <summary>Appends a single ASCII byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte b)
        {
            EnsureCapacity(_length + 1);
            _buffer[_length++] = b;
        }

        /// <summary>Appends UTF-8 bytes directly from a source span (e.g., a protobuf string field).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8(ReadOnlySpan<byte> utf8Bytes) => Write(utf8Bytes);

        /// <summary>Formats a long directly to UTF-8.</summary>
        public void WriteInt64(long value)
        {
            EnsureCapacity(_length + 20); // max digits for int64
            Utf8Formatter.TryFormat(value, _buffer.AsSpan(_length), out int bytesWritten);
            _length += bytesWritten;
        }

        /// <summary>Formats an int directly to UTF-8.</summary>
        public void WriteInt32(int value)
        {
            EnsureCapacity(_length + 11); // max digits for int32
            Utf8Formatter.TryFormat(value, _buffer.AsSpan(_length), out int bytesWritten);
            _length += bytesWritten;
        }

        /// <summary>Formats a double directly to UTF-8.</summary>
        public void WriteDouble(double value)
        {
            EnsureCapacity(_length + 32);
            Utf8Formatter.TryFormat(value, _buffer.AsSpan(_length), out int bytesWritten, new StandardFormat('G'));
            _length += bytesWritten;
        }

        /// <summary>Formats a float directly to UTF-8.</summary>
        public void WriteFloat(float value)
        {
            EnsureCapacity(_length + 16);
            Utf8Formatter.TryFormat(value, _buffer.AsSpan(_length), out int bytesWritten, new StandardFormat('G'));
            _length += bytesWritten;
        }

        /// <summary>Appends text, encoded as UTF-8.</summary>
        public void Write(string text)
        {
            int max = Encoding.UTF8.GetMaxByteCount(text.Length);
            EnsureCapacity(_length + max);
            _length += Encoding.UTF8.GetBytes(text, 0, text.Length, _buffer, _length);
        }

        /// <summary>Writes a KQL string literal: 'value', with backslash escapes.</summary>
        public void WriteStringLiteral(string value)
        {
            Write((byte)'\'');
            WriteEscaped(value);
            Write((byte)'\'');
        }

        /// <summary>
        /// Writes a column or table name, bracket-quoted (['name']). KQL reserves many words
        /// (operators, type names, options such as 'title' or 'flags') with no published list, so
        /// names are always quoted, except the translator's own generated names (_e0, _rn3, ...).
        /// </summary>
        public void WriteIdentifier(string name)
        {
            if (IsGeneratedName(name))
            {
                Write(name);
                return;
            }
            Write((byte)'[');
            WriteStringLiteral(name);
            Write((byte)']');
        }

        /// <summary>True for names of the form _&lt;lowercase letters&gt;&lt;digits&gt;, which KQL never reserves.</summary>
        internal static bool IsGeneratedName(string name)
        {
            int i = 1;
            if (name.Length < 3 || name[0] != '_')
                return false;
            while (i < name.Length && name[i] >= 'a' && name[i] <= 'z') i++;
            if (i == 1 || i == name.Length)
                return false;
            while (i < name.Length && name[i] >= '0' && name[i] <= '9') i++;
            return i == name.Length;
        }

        void WriteEscaped(string value)
        {
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                string? escape = c switch
                {
                    '\'' => "\\'",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => c < 0x20 ? "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : null,
                };
                if (escape == null)
                    continue;
                if (i > start) Write(value.Substring(start, i - start));
                Write(escape);
                start = i + 1;
            }
            if (start < value.Length)
                Write(start == 0 ? value : value.Substring(start));
        }

        // ── Buffer management ──────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureCapacity(int required)
        {
            if (required > _buffer.Length)
                Grow(required);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int required)
        {
            int newSize = Math.Max(required, _buffer.Length * 2);
            var newBuffer = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
            _buffer = newBuffer;
        }
    }
}
