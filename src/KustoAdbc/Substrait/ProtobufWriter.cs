using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace KustoAdbc.Substrait
{
    /// <summary>
    /// Writes protobuf wire format messages. Symmetric to the reading logic
    /// in SubstraitPlanReader. Used to emit modified Substrait plans.
    /// </summary>
    sealed class ProtobufWriter
    {
        byte[] _buffer;
        int _length;

        public ProtobufWriter(int initialCapacity = 512)
        {
            _buffer = new byte[initialCapacity];
        }

        public int Length => _length;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);
        public byte[] ToArray() => WrittenSpan.ToArray();

        /// <summary>Saves the current position for potential rollback or length patching.</summary>
        public int SavePosition() => _length;

        /// <summary>Restores to a previously saved position (truncates).</summary>
        public void Restore(int position) => _length = position;

        // ── Tag + Varint ──────────────────────────────────────────

        public void WriteTag(int fieldNumber, int wireType)
            => WriteVarint32((fieldNumber << 3) | wireType);

        public void WriteVarint32(int value)
        {
            EnsureCapacity(_length + 5);
            uint v = (uint)value;
            while (v >= 0x80)
            {
                _buffer[_length++] = (byte)(v | 0x80);
                v >>= 7;
            }
            _buffer[_length++] = (byte)v;
        }

        public void WriteVarint64(long value)
        {
            EnsureCapacity(_length + 10);
            ulong v = (ulong)value;
            while (v >= 0x80)
            {
                _buffer[_length++] = (byte)(v | 0x80);
                v >>= 7;
            }
            _buffer[_length++] = (byte)v;
        }

        // ── Field writers ─────────────────────────────────────────

        /// <summary>Writes a varint field (wire type 0).</summary>
        public void WriteVarintField(int fieldNumber, int value)
        {
            WriteTag(fieldNumber, 0);
            WriteVarint32(value);
        }

        /// <summary>Writes a varint field (wire type 0, 64-bit).</summary>
        public void WriteVarintField64(int fieldNumber, long value)
        {
            WriteTag(fieldNumber, 0);
            WriteVarint64(value);
        }

        /// <summary>Writes a string field (wire type 2).</summary>
        public void WriteStringField(int fieldNumber, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            WriteBytesField(fieldNumber, utf8);
        }

        /// <summary>Writes a bytes/string field (wire type 2).</summary>
        public void WriteBytesField(int fieldNumber, ReadOnlySpan<byte> value)
        {
            WriteTag(fieldNumber, 2);
            WriteVarint32(value.Length);
            WriteRawBytes(value);
        }

        /// <summary>
        /// Writes a length-delimited field from pre-built content.
        /// </summary>
        public void WriteLengthDelimited(int fieldNumber, ProtobufWriter content)
        {
            WriteTag(fieldNumber, 2);
            WriteVarint32(content.Length);
            WriteRawBytes(content.WrittenSpan);
        }

        /// <summary>
        /// Writes a length-delimited field where the content is built by a callback.
        /// The length prefix is patched after the callback completes.
        /// NOTE: The callback cannot capture Span or ref parameters.
        /// </summary>
        public void WriteLengthDelimited(int fieldNumber, Action<ProtobufWriter> writeContent)
        {
            WriteTag(fieldNumber, 2);
            var nested = new ProtobufWriter(64);
            writeContent(nested);
            WriteVarint32(nested.Length);
            WriteRawBytes(nested.WrittenSpan);
        }

        /// <summary>
        /// Copies raw bytes from the source span into the output.
        /// Used for verbatim pass-through of protobuf fields.
        /// </summary>
        public void WriteRawBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(_length + bytes.Length);
            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        /// <summary>
        /// Copies a complete protobuf field (tag + value) from the source.
        /// </summary>
        public void CopyField(ReadOnlySpan<byte> source, int fieldStart, int fieldEnd)
        {
            WriteRawBytes(source.Slice(fieldStart, fieldEnd - fieldStart));
        }

        // ── Buffer management ─────────────────────────────────────

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
