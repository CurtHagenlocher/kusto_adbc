// Copyright (c) Microsoft Corporation.  All rights reserved.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace KustoAdbc.Arrow
{
    sealed class ArrowStringBuilder : IArrowArrayBuilder
    {
        NativeBuffer<byte> _data;
        NativeBuffer<int> _offsets;
        NativeBitmapBuffer? _validity;
        int _count;
        int _nullCount;

        public int Length => _count;

        public ArrowStringBuilder(int? initialCapacity = null, int? individualSize = null)
        {
            int lenCap = initialCapacity ?? 8;
            int dataCap = lenCap * (individualSize ?? 16);
            _data = new NativeBuffer<byte>(dataCap);
            _offsets = new NativeBuffer<int>(lenCap + 1);
            _offsets.Append(0);
        }

        public void Append(ReadOnlySpan<byte> value)
        {
            _data.AppendRange(value);
            _count++;
            _offsets.Append(_data.Length);
            _validity?.Append(true);
        }

        public void Append(in ReadOnlySequence<byte> value)
        {
            foreach (var segment in value)
                _data.AppendRange(segment.Span);
            _count++;
            _offsets.Append(_data.Length);
            _validity?.Append(true);
        }

        public void Append(string value)
        {
            byte[] temp = Encoding.UTF8.GetBytes(value);
            Append(new ReadOnlySpan<byte>(temp));
        }

        public void AppendNull()
        {
            if (_validity == null)
                _validity = new NativeBitmapBuffer(_offsets.Capacity, _count);
            _validity.Append(false);
            _nullCount++;
            _count++;
            _offsets.Append(_data.Length);
        }

        public StringArray Build()
        {
            ArrowBuffer valueBuffer = _data.Build();
            ArrowBuffer offsetBuffer = _offsets.Build();
            ArrowBuffer validityBuf = _nullCount > 0 ? _validity!.Build() : ArrowBuffer.Empty;
            return new StringArray(_count, offsetBuffer, valueBuffer, validityBuf, _nullCount);
        }
    }

    abstract class Property
    {
        readonly string _name;
        readonly byte[] _nameBytes;

        protected Property(string name)
        {
            _name = name;
            _nameBytes = Encoding.UTF8.GetBytes(name);
        }

        public string Name => _name;
        public ReadOnlySpan<byte> NameBytes => _nameBytes;

        public abstract IArrowType Type { get; }
        public abstract IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null);
        public abstract void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder);
        public abstract void AddNull(IArrowArrayBuilder builder);
        public abstract IArrowArray Build(IArrowArrayBuilder builder);
    }

    sealed class StringProperty : Property
    {
        const int EstimatedItemSize = 20;

        public StringProperty(string name) : base(name) { }

        public override IArrowType Type => StringType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new ArrowStringBuilder(sizeEstimate, EstimatedItemSize);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var sb = (ArrowStringBuilder)builder;
            if (reader.TokenType == JsonTokenType.Null)
            {
                sb.AppendNull();
            }
            else if (!reader.HasValueSequence && reader.TokenType == JsonTokenType.String)
            {
                sb.Append(reader.ValueSpan);
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                sb.Append(reader.ValueSequence);
            }
            else
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                sb.Append(doc.RootElement.GetRawText());
            }
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).Build();
    }

    sealed class Int32Property : Property
    {
        public Int32Property(string name) : base(name) { }

        public override IArrowType Type => Int32Type.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<int>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<int>)builder;
            if (reader.TokenType == JsonTokenType.Null)
                pb.AppendNull();
            else
                pb.Append(reader.GetInt32());
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<int>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<int>)builder).Finish();
            return new Int32Array(new ArrayData(Int32Type.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class Int64Property : Property
    {
        public Int64Property(string name) : base(name) { }

        public override IArrowType Type => Int64Type.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<long>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<long>)builder;
            if (reader.TokenType == JsonTokenType.Null)
                pb.AppendNull();
            else
                pb.Append(reader.GetInt64());
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<long>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<long>)builder).Finish();
            return new Int64Array(new ArrayData(Int64Type.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class DoubleProperty : Property
    {
        public DoubleProperty(string name) : base(name) { }

        public override IArrowType Type => DoubleType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<double>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<double>)builder;
            if (reader.TokenType == JsonTokenType.Null)
                pb.AppendNull();
            else
                pb.Append(reader.GetDouble());
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<double>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<double>)builder).Finish();
            return new DoubleArray(new ArrayData(DoubleType.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class DateTimeOffsetProperty : Property
    {
        static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffsetProperty(string name) : base(name) { }

        public override IArrowType Type => TimestampType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<long>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<long>)builder;
            if (reader.TokenType == JsonTokenType.Null)
                pb.AppendNull();
            else
                pb.Append((reader.GetDateTimeOffset() - UnixEpoch).Ticks / 10);
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<long>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<long>)builder).Finish();
            return new TimestampArray(new ArrayData(TimestampType.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class BooleanProperty : Property
    {
        public BooleanProperty(string name) : base(name) { }

        public override IArrowType Type => BooleanType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new BooleanBuilder(sizeEstimate ?? 64);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var bb = (BooleanBuilder)builder;
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    bb.Append(true);
                    break;
                case JsonTokenType.False:
                    bb.Append(false);
                    break;
                case JsonTokenType.Null:
                    bb.AppendNull();
                    break;
                case JsonTokenType.Number:
                    bb.Append(reader.GetInt32() != 0);
                    break;
                default:
                    throw new InvalidDataException("Unexpected token for boolean column.");
            }
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((BooleanBuilder)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((BooleanBuilder)builder).Finish();
            return new BooleanArray(new ArrayData(BooleanType.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class TimespanProperty : Property
    {
        public TimespanProperty(string name) : base(name) { }

        // Kusto timespan → Arrow Duration(microsecond)
        public override IArrowType Type => DurationType.Microsecond;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<long>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<long>)builder;
            if (reader.TokenType == JsonTokenType.Null)
            {
                pb.AppendNull();
            }
            else
            {
                string value = reader.GetString()!;
                if (TimeSpan.TryParse(value, out var ts))
                    pb.Append(ts.Ticks / 10); // ticks (100ns) → microseconds
                else
                    pb.AppendNull();
            }
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<long>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<long>)builder).Finish();
            return new DurationArray(new ArrayData(DurationType.Microsecond, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class GuidProperty : Property
    {
        public GuidProperty(string name) : base(name) { }

        // Kusto guid → Arrow string (UUIDs as string representation)
        public override IArrowType Type => StringType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new ArrowStringBuilder(sizeEstimate, 36);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var sb = (ArrowStringBuilder)builder;
            if (reader.TokenType == JsonTokenType.Null)
                sb.AppendNull();
            else if (!reader.HasValueSequence)
                sb.Append(reader.ValueSpan);
            else
                sb.Append(reader.ValueSequence);
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).Build();
    }

    sealed class DecimalProperty : Property
    {
        public DecimalProperty(string name) : base(name) { }

        // Kusto decimal → Arrow double (closest practical mapping)
        public override IArrowType Type => DoubleType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new PrimitiveBuilder<double>(sizeEstimate ?? 8);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var pb = (PrimitiveBuilder<double>)builder;
            if (reader.TokenType == JsonTokenType.Null)
                pb.AppendNull();
            else
                pb.Append(reader.GetDouble());
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((PrimitiveBuilder<double>)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
        {
            var (vb, nb, len, nc) = ((PrimitiveBuilder<double>)builder).Finish();
            return new DoubleArray(new ArrayData(DoubleType.Default, len, nc, 0, new[] { nb, vb }));
        }
    }

    sealed class DynamicProperty : Property
    {
        public DynamicProperty(string name) : base(name) { }

        // Kusto dynamic → Arrow string (JSON representation)
        public override IArrowType Type => StringType.Default;

        public override IArrowArrayBuilder CreateBuilder(int? sizeEstimate = null)
            => new ArrowStringBuilder(sizeEstimate, 64);

        public override void Read(ref Utf8JsonReader reader, IArrowArrayBuilder builder)
        {
            var sb = (ArrowStringBuilder)builder;
            if (reader.TokenType == JsonTokenType.Null)
            {
                sb.AppendNull();
            }
            else
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                sb.Append(doc.RootElement.GetRawText());
            }
        }

        public override void AddNull(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).AppendNull();

        public override IArrowArray Build(IArrowArrayBuilder builder)
            => ((ArrowStringBuilder)builder).Build();
    }

    static class PropertyFactory
    {
        public static Property Create(string columnName, string columnType)
        {
            return columnType switch
            {
                "string" => new StringProperty(columnName),
                "long" => new Int64Property(columnName),
                "int" => new Int32Property(columnName),
                "real" => new DoubleProperty(columnName),
                "datetime" => new DateTimeOffsetProperty(columnName),
                "bool" => new BooleanProperty(columnName),
                "dynamic" => new DynamicProperty(columnName),
                "timespan" => new TimespanProperty(columnName),
                "guid" => new GuidProperty(columnName),
                "decimal" => new DecimalProperty(columnName),
                _ => new StringProperty(columnName), // fallback to string for unknown types
            };
        }
    }
}
