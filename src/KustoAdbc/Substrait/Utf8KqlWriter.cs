// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// A growable UTF-8 byte buffer optimized for building KQL query strings.
    /// All KQL operators are pre-encoded as UTF-8 byte constants. Numeric values
    /// are formatted directly to UTF-8 via Utf8Formatter. No intermediate .NET
    /// strings are allocated during query construction.
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

        /// <summary>Writes a KQL-escaped string literal: 'value'</summary>
        public void WriteKqlStringLiteral(ReadOnlySpan<byte> utf8Value)
        {
            Write((byte)'\'');
            // Scan for single quotes that need escaping
            int start = 0;
            for (int i = 0; i < utf8Value.Length; i++)
            {
                if (utf8Value[i] == (byte)'\'')
                {
                    if (i > start) Write(utf8Value.Slice(start, i - start));
                    Write(BackslashQuote);
                    start = i + 1;
                }
            }
            if (start < utf8Value.Length)
                Write(utf8Value.Slice(start));
            Write((byte)'\'');
        }

        /// <summary>Writes $field{index} as a field reference.</summary>
        public void WriteFieldRef(int index)
        {
            Write(FieldPrefix);
            WriteInt32(index);
        }

        // ── Pre-encoded KQL operator constants ─────────────────────────

        // All KQL keywords are pure ASCII, so encoding is identity.
        // Using static readonly byte[] for netstandard2.0 compat;
        // on net8.0+ these could be ReadOnlySpan<byte> via "..."u8.

        internal static readonly byte[] PipeWhere = "\n| where "u8.ToArray();
        internal static readonly byte[] PipeProject = "\n| project "u8.ToArray();
        internal static readonly byte[] PipeTake = "\n| take "u8.ToArray();
        internal static readonly byte[] PipeSortBy = "\n| sort by "u8.ToArray();
        internal static readonly byte[] PipeSummarize = "\n| summarize "u8.ToArray();
        internal static readonly byte[] PipeSerialize = "\n| serialize"u8.ToArray();
        internal static readonly byte[] PipeJoinKind = "\n| join kind="u8.ToArray();
        internal static readonly byte[] SummarizeBy = " by "u8.ToArray();
        internal static readonly byte[] JoinOn = " on "u8.ToArray();
        internal static readonly byte[] WhereRowNumber = "\n| where row_number() > "u8.ToArray();
        internal static readonly byte[] Comma = ", "u8.ToArray();
        internal static readonly byte[] Asc = " asc"u8.ToArray();
        internal static readonly byte[] Desc = " desc"u8.ToArray();
        internal static readonly byte[] True = "true"u8.ToArray();
        internal static readonly byte[] False = "false"u8.ToArray();
        internal static readonly byte[] DynamicNull = "dynamic(null)"u8.ToArray();
        internal static readonly byte[] FieldPrefix = "$field"u8.ToArray();
        internal static readonly byte[] BackslashQuote = "\\'"u8.ToArray();
        internal static readonly byte[] FuncPrefix = "func_"u8.ToArray();
        internal static readonly byte[] Iif = "iif("u8.ToArray();
        internal static readonly byte[] UnknownExpr = "/* unknown expression */"u8.ToArray();
        internal static readonly byte[] UnknownField = "/* unknown field */"u8.ToArray();
        internal static readonly byte[] UnknownArg = "/* unknown arg */"u8.ToArray();
        internal static readonly byte[] CountFunc = "count()"u8.ToArray();
        internal static readonly byte[] OpenParen = "("u8.ToArray();
        internal static readonly byte[] CloseParen = ")"u8.ToArray();

        internal static readonly byte[] JoinInner = "inner"u8.ToArray();
        internal static readonly byte[] JoinFullOuter = "fullouter"u8.ToArray();
        internal static readonly byte[] JoinLeftOuter = "leftouter"u8.ToArray();
        internal static readonly byte[] JoinRightOuter = "rightouter"u8.ToArray();
        internal static readonly byte[] JoinLeftSemi = "leftsemi"u8.ToArray();
        internal static readonly byte[] JoinLeftAnti = "leftanti"u8.ToArray();

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
