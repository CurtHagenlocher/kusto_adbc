// Copyright (c) Microsoft Corporation.  All rights reserved.

using System.Buffers;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Memory;

namespace KustoAdbc.Arrow
{
    static class MemoryPressure
    {
        const long Mask = 0x80000 - 1; // Report every 512 KB
        static long s_tracked;
        static long s_reported;

        public static void Add(int bytes)
        {
            if (bytes <= 0) return;
            long newTracked = Interlocked.Add(ref s_tracked, bytes);
            while (true)
            {
                long currentReported = Interlocked.Read(ref s_reported);
                long delta = newTracked - currentReported;
                if (delta <= 0) return;
                long needToAdd = (delta + Mask) & ~Mask;
                if (needToAdd == 0) return;
                long newReported = currentReported + needToAdd;
                if (Interlocked.CompareExchange(ref s_reported, newReported, currentReported) == currentReported)
                {
                    GC.AddMemoryPressure(needToAdd);
                    return;
                }
                newTracked = Interlocked.Read(ref s_tracked);
            }
        }

        public static void Remove(int bytes)
        {
            if (bytes <= 0) return;
            long newTracked = Interlocked.Add(ref s_tracked, -bytes);
            while (true)
            {
                long currentReported = Interlocked.Read(ref s_reported);
                long delta = currentReported - newTracked;
                if (delta <= 0) return;
                long needToRemove = delta & ~Mask;
                if (needToRemove == 0) return;
                long newReported = currentReported - needToRemove;
                if (Interlocked.CompareExchange(ref s_reported, newReported, currentReported) == currentReported)
                {
                    GC.RemoveMemoryPressure(needToRemove);
                    return;
                }
                newTracked = Interlocked.Read(ref s_tracked);
            }
        }
    }

    sealed class NativeMemoryManager : MemoryManager<byte>
    {
        private IntPtr _ptr;
        private int _pinCount;
        private readonly int _offset;
        private readonly int _length;

        public NativeMemoryManager(IntPtr ptr, int offset, int length)
        {
            _ptr = ptr;
            _offset = offset;
            _length = length;
        }

#pragma warning disable CA2015
        ~NativeMemoryManager() => Dispose(false);
#pragma warning restore CA2015

        public override unsafe Span<byte> GetSpan()
        {
            void* ptr = CalculatePointer(0);
            return new Span<byte>(ptr, _length);
        }

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            Interlocked.Increment(ref _pinCount);
            void* ptr = CalculatePointer(elementIndex);
            return new MemoryHandle(ptr, default, this);
        }

        public override void Unpin() => Interlocked.Decrement(ref _pinCount);

        protected override void Dispose(bool disposing)
        {
            IntPtr ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                if (disposing && _pinCount > 0)
                {
                    _ptr = ptr;
                    throw new InvalidOperationException("Cannot free native memory while it is pinned.");
                }
                Marshal.FreeHGlobal(ptr);
                MemoryPressure.Remove(_length + MemoryAllocator.DefaultAlignment);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void* CalculatePointer(int index) =>
            (_ptr + _offset + index).ToPointer();
    }

    sealed class NativeAllocator : MemoryAllocator
    {
        internal static readonly NativeAllocator Instance = new();

        private static readonly Func<IMemoryOwner<byte>, ArrowBuffer> s_createBuffer;

        static NativeAllocator()
        {
            // ArrowBuffer has an internal constructor that takes IMemoryOwner<byte>.
            // We use reflection to access it across all TFMs.
            ConstructorInfo ctor = typeof(ArrowBuffer).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(IMemoryOwner<byte>) },
                null)!;
            ParameterExpression p = Expression.Parameter(typeof(IMemoryOwner<byte>), "owner");
            s_createBuffer = Expression.Lambda<Func<IMemoryOwner<byte>, ArrowBuffer>>(Expression.New(ctor, p), p).Compile();
        }

        internal static ArrowBuffer CreateBuffer(IMemoryOwner<byte> owner) => s_createBuffer(owner);

        public NativeAllocator(int alignment = DefaultAlignment) : base(alignment) { }

        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            int size = length + Alignment;
            IntPtr ptr = Marshal.AllocHGlobal(size);
            int offset = (int)(Alignment - (ptr.ToInt64() & (Alignment - 1)));
            var manager = new NativeMemoryManager(ptr, offset, length);
            bytesAllocated = size;
            MemoryPressure.Add(bytesAllocated);
            manager.Memory.Span.Fill(0);
            return manager;
        }
    }

    sealed class NativeBuffer<T> : IDisposable where T : struct
    {
        static readonly int ItemSize = Unsafe.SizeOf<T>();

        IMemoryOwner<byte>? _owner;
        int _capacity;
        int _length;

        public NativeBuffer(int capacity = 8)
        {
            capacity = Math.Max(capacity, 8);
            _owner = NativeAllocator.Instance.Allocate(capacity * ItemSize);
            _capacity = capacity;
        }

        public Span<T> Span => MemoryMarshal.Cast<byte, T>(_owner!.Memory.Span);
        public int Length => _length;
        public int Capacity => _capacity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(T value)
        {
            if (_length >= _capacity) Grow(_length + 1);
            Span[_length++] = value;
        }

        public void AppendRange(ReadOnlySpan<T> values)
        {
            int count = values.Length;
            int required = _length + count;
            if (required > _capacity) Grow(required);
            values.CopyTo(Span.Slice(_length, count));
            _length += count;
        }

        public ArrowBuffer Build()
        {
            int usedBytes = _length * ItemSize;
            int allocatedBytes = _owner!.Memory.Length;
            if (usedBytes == 0)
            {
                _owner.Dispose();
                _owner = null;
                return ArrowBuffer.Empty;
            }
            if (usedBytes >= allocatedBytes / 2)
            {
                var result = NativeAllocator.CreateBuffer(_owner);
                _owner = null;
                return result;
            }
            int exact = (int)BitUtility.RoundUpToMultipleOf64(usedBytes);
            var newOwner = NativeAllocator.Instance.Allocate(exact);
            _owner.Memory.Span.Slice(0, usedBytes).CopyTo(newOwner.Memory.Span);
            _owner.Dispose();
            _owner = null;
            return NativeAllocator.CreateBuffer(newOwner);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int required)
        {
            int newCapacity = Math.Max(required, _capacity * 2);
            var newOwner = NativeAllocator.Instance.Allocate(newCapacity * ItemSize);
            _owner!.Memory.Span.Slice(0, _length * ItemSize).CopyTo(newOwner.Memory.Span);
            _owner.Dispose();
            _owner = newOwner;
            _capacity = newCapacity;
        }

        public void Dispose() => _owner?.Dispose();
    }

    sealed class NativeBitmapBuffer : IDisposable
    {
        IMemoryOwner<byte>? _owner;
        int _capacityBits;
        int _length;

        public NativeBitmapBuffer(int bitCapacity = 64)
        {
            int bytes = Math.Max(BitUtility.ByteCount(bitCapacity), 8);
            _owner = NativeAllocator.Instance.Allocate(bytes);
            _capacityBits = bytes * 8;
        }

        public NativeBitmapBuffer(int bitCapacity, int prefillTrueCount)
        {
            bitCapacity = Math.Max(bitCapacity, prefillTrueCount + 1);
            int bytes = Math.Max(BitUtility.ByteCount(bitCapacity), 8);
            _owner = NativeAllocator.Instance.Allocate(bytes);
            _capacityBits = bytes * 8;
            if (prefillTrueCount > 0)
            {
                var span = _owner.Memory.Span;
                int fullBytes = prefillTrueCount >> 3;
                int remainBits = prefillTrueCount & 7;
                if (fullBytes > 0) span.Slice(0, fullBytes).Fill(0xFF);
                if (remainBits > 0) span[fullBytes] = (byte)((1 << remainBits) - 1);
                _length = prefillTrueCount;
            }
        }

        public int Length => _length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Append(bool value)
        {
            if (_length >= _capacityBits) Grow(_length + 1);
            if (value) BitUtility.SetBit(_owner!.Memory.Span, _length);
            _length++;
        }

        public ArrowBuffer Build()
        {
            int usedBytes = BitUtility.ByteCount(_length);
            int allocatedBytes = _owner!.Memory.Length;
            if (usedBytes == 0)
            {
                _owner.Dispose();
                _owner = null;
                return ArrowBuffer.Empty;
            }
            if (usedBytes >= allocatedBytes / 2)
            {
                var result = NativeAllocator.CreateBuffer(_owner);
                _owner = null;
                return result;
            }
            int exact = (int)BitUtility.RoundUpToMultipleOf64(usedBytes);
            var newOwner = NativeAllocator.Instance.Allocate(exact);
            _owner.Memory.Span.Slice(0, usedBytes).CopyTo(newOwner.Memory.Span);
            _owner.Dispose();
            _owner = null;
            return NativeAllocator.CreateBuffer(newOwner);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int requiredBits)
        {
            int requiredBytes = BitUtility.ByteCount(requiredBits);
            int newBytes = Math.Max(requiredBytes, _owner!.Memory.Length * 2);
            var newOwner = NativeAllocator.Instance.Allocate(newBytes);
            _owner.Memory.Span.CopyTo(newOwner.Memory.Span);
            _owner.Dispose();
            _owner = newOwner;
            _capacityBits = newBytes * 8;
        }

        public void Dispose() => _owner?.Dispose();
    }

    interface IArrowArrayBuilder
    {
        int Length { get; }
    }

    sealed class PrimitiveBuilder<T> : IArrowArrayBuilder, IDisposable where T : struct
    {
        NativeBuffer<T> _values;
        NativeBitmapBuffer? _validity;
        int _nullCount;

        public PrimitiveBuilder(int capacity = 8) => _values = new NativeBuffer<T>(capacity);
        public int Length => _values.Length;

        public void Append(T value)
        {
            _values.Append(value);
            _validity?.Append(true);
        }

        public void AppendNull()
        {
            int count = _values.Length;
            _values.Append(default);
            if (_validity == null)
                _validity = new NativeBitmapBuffer(_values.Capacity, count);
            _validity.Append(false);
            _nullCount++;
        }

        public (ArrowBuffer valueBuffer, ArrowBuffer validityBuffer, int length, int nullCount) Finish()
        {
            int len = _values.Length;
            var vb = _values.Build();
            var nb = _nullCount > 0 ? _validity!.Build() : ArrowBuffer.Empty;
            return (vb, nb, len, _nullCount);
        }

        public void Dispose()
        {
            _values?.Dispose();
            _validity?.Dispose();
        }
    }

    sealed class BooleanBuilder : IArrowArrayBuilder, IDisposable
    {
        NativeBitmapBuffer _values;
        NativeBitmapBuffer? _validity;
        int _nullCount;

        public BooleanBuilder(int capacity = 64) => _values = new NativeBitmapBuffer(capacity);
        public int Length => _values.Length;

        public void Append(bool value)
        {
            _values.Append(value);
            _validity?.Append(true);
        }

        public void AppendNull()
        {
            int count = _values.Length;
            _values.Append(false);
            if (_validity == null)
                _validity = new NativeBitmapBuffer(_values.Length, count);
            _validity.Append(false);
            _nullCount++;
        }

        public (ArrowBuffer valueBuffer, ArrowBuffer validityBuffer, int length, int nullCount) Finish()
        {
            int len = _values.Length;
            var vb = _values.Build();
            var nb = _nullCount > 0 ? _validity!.Build() : ArrowBuffer.Empty;
            return (vb, nb, len, _nullCount);
        }

        public void Dispose()
        {
            _values?.Dispose();
            _validity?.Dispose();
        }
    }

    sealed class NullSentinelBuilder : IArrowArrayBuilder
    {
        public static readonly NullSentinelBuilder Instance = new();
        public int Length => 0;
    }
}
