using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using VYaml.Internal;

namespace VYaml.Parser
{
    class ScalarPool
    {
        public static readonly ScalarPool Shared = new();

        [ThreadStatic]
        static Stack<Scalar>? threadLocalPool;
        
        readonly ConcurrentQueue<Scalar> items = new();
        Scalar? fastItem;

        public Scalar Rent()
        {
            // Fast path for thread-local access
            var localPool = threadLocalPool;
            if (localPool != null && localPool.Count > 0)
            {
                return localPool.Pop();
            }
            
            // Try fast single item
            var value = fastItem;
            if (value != null &&
                Interlocked.CompareExchange(ref fastItem, null, value) == value)
            {
                return value;
            }
            
            // Fallback to concurrent queue
            if (items.TryDequeue(out value))
            {
                return value;
            }
            return new Scalar(256);
        }

        public void Return(Scalar value)
        {
            value.Clear();

            // Fast path for thread-local return
            var localPool = threadLocalPool ??= new Stack<Scalar>(8);
            if (localPool.Count < 8) // Keep up to 8 scalars per thread
            {
                localPool.Push(value);
                return;
            }
            
            // Fallback to shared pool
            if (fastItem != null ||
                Interlocked.CompareExchange(ref fastItem, value, null) != null)
            {
                items.Enqueue(value);
            }
        }
    }

    enum ScalarTypeHint : byte
    {
        Unknown,
        Integer,
        Float,
        Boolean,
        Null,
        String
    }

    class Scalar : ITokenContent
    {
        const int MinimumGrow = 4;
        const int GrowFactor = 200;
        const int SmallBufferThreshold = 1024;
        const int MediumBufferThreshold = 8192;

        public static readonly Scalar Null = new(0);

        public int Length { get; private set; }
        public TokenType Type { get; set; }
        internal ScalarTypeHint TypeHint { get; set; }

        byte[] buffer;

        public Scalar(int capacity)
        {
            buffer = new byte[capacity];
        }

        public Scalar(ReadOnlySpan<byte> content)
        {
            buffer = new byte[content.Length];
            Write(content);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> AsSpan() => buffer.AsSpan(0, Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> AsSpan(int start, int length) => buffer.AsSpan(start, length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> AsUtf8() => buffer.AsSpan(0, Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte code)
        {
            if (Length == buffer.Length)
            {
                Grow();
            }

            buffer[Length++] = code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(LineBreakState lineBreak)
        {
            switch (lineBreak)
            {
                case LineBreakState.None:
                    break;
                case LineBreakState.Lf:
                    Write(YamlCodes.Lf);
                    break;
                case LineBreakState.CrLf:
                    Write(YamlCodes.Cr);
                    Write(YamlCodes.Lf);
                    break;
                case LineBreakState.Cr:
                    Write(YamlCodes.Cr);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lineBreak), lineBreak, null);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<byte> codes)
        {
            Grow(Length + codes.Length);
            codes.CopyTo(buffer.AsSpan(Length, codes.Length));
            Length += codes.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUnicodeCodepoint(int codepoint)
        {
            Span<char> chars = stackalloc char[] { (char)codepoint };
            var utf8ByteCount = StringEncoding.Utf8.GetByteCount(chars);
            Span<byte> utf8Bytes = stackalloc byte[utf8ByteCount];
            StringEncoding.Utf8.GetBytes(chars, utf8Bytes);
            Write(utf8Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Length = 0;
            TypeHint = ScalarTypeHint.Unknown;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
        {
            return StringEncoding.Utf8.GetString(AsSpan());
        }

        /// <summary>
        /// </summary>
        /// <remarks>
        /// null | Null | NULL | ~
        /// </remarks>
        public bool IsNull()
        {
            if (IsStringScalar())
            {
                return false;
            }

            // Don't use type hint for IsNull - it needs exact pattern matching

            var span = AsSpan();
            switch (span.Length)
            {
                case 0:
                case 1 when span[0] == YamlCodes.NullAlias:
                case 4 when span.SequenceEqual(YamlCodes.Null0) ||
                            span.SequenceEqual(YamlCodes.Null1) ||
                            span.SequenceEqual(YamlCodes.Null2):
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// </summary>
        /// <remarks>
        /// true|True|TRUE|false|False|FALSE
        /// </remarks>
        public bool TryGetBool(out bool value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            // Type hint optimization disabled for now - needs more work

            var span = AsSpan();
            switch (span.Length)
            {
                case 4 when span.SequenceEqual(YamlCodes.True0) ||
                            span.SequenceEqual(YamlCodes.True1) ||
                            span.SequenceEqual(YamlCodes.True2):
                    value = true;
                    return true;

                case 5 when span.SequenceEqual(YamlCodes.False0) ||
                            span.SequenceEqual(YamlCodes.False1) ||
                            span.SequenceEqual(YamlCodes.False2):
                    value = false;
                    return true;
            }
            value = default;
            return false;
        }

        public bool TryGetInt32(out int value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            // Type hint optimization disabled for now - needs more work

            var span = AsSpan();

            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            if (TryDetectHex(span, out var hexNumber))
            {
                return Utf8Parser.TryParse(hexNumber, out value, out bytesConsumed, 'x') &&
                       bytesConsumed == hexNumber.Length;
            }

            if (TryDetectHexNegative(span, out hexNumber) &&
                Utf8Parser.TryParse(hexNumber, out value, out bytesConsumed, 'x') &&
                bytesConsumed == hexNumber.Length)
            {
                value *= -1;
                return true;
            }
            if (TryParseOctal(span, out var octalUlong) && octalUlong <= int.MaxValue)
            {
                value = (int)octalUlong;
                return true;
            }
            return false;
        }

        public bool TryGetInt64(out long value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            var span = AsSpan();
            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            if (span.Length > YamlCodes.HexPrefix.Length && span.StartsWith(YamlCodes.HexPrefix))
            {
                var slice = span[YamlCodes.HexPrefix.Length..];
                return Utf8Parser.TryParse(slice, out value, out var bytesConsumedHex, 'x') &&
                       bytesConsumedHex == slice.Length;
            }
            if (span.Length > YamlCodes.HexPrefixNegative.Length && span.StartsWith(YamlCodes.HexPrefixNegative))
            {
                var slice = span[YamlCodes.HexPrefixNegative.Length..];
                if (Utf8Parser.TryParse(slice, out value, out var bytesConsumedHex, 'x') && bytesConsumedHex == slice.Length)
                {
                    value = -value;
                    return true;
                }
            }
            if (TryParseOctal(span, out var octalUlong) && octalUlong <= long.MaxValue)
            {
                value = (long)octalUlong;
                return true;
            }
            return false;
        }

        public bool TryGetUInt32(out uint value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            var span = AsSpan();

            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            if (TryDetectHex(span, out var hexNumber))
            {
                return Utf8Parser.TryParse(hexNumber, out value, out bytesConsumed, 'x') &&
                       bytesConsumed == hexNumber.Length;
            }
            if (TryParseOctal(span, out var octalUlong) && octalUlong <= uint.MaxValue)
            {
                value = (uint)octalUlong;
                return true;
            }
            return false;
        }

        public bool TryGetUInt64(out ulong value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            var span = AsSpan();

            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            if (TryDetectHex(span, out var hexNumber))
            {
                return Utf8Parser.TryParse(hexNumber, out value, out bytesConsumed, 'x') &&
                       bytesConsumed == hexNumber.Length;
            }
            if (TryParseOctal(span, out value))
            {
                return true;
            }
            return false;
        }

        public bool TryGetFloat(out float value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            var span = AsSpan();
            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            switch (span.Length)
            {
                case 4:
                    if (span.SequenceEqual(YamlCodes.Inf0) ||
                        span.SequenceEqual(YamlCodes.Inf1) ||
                        span.SequenceEqual(YamlCodes.Inf2))
                    {
                        value = float.PositiveInfinity;
                        return true;
                    }

                    if (span.SequenceEqual(YamlCodes.Nan0) ||
                        span.SequenceEqual(YamlCodes.Nan1) ||
                        span.SequenceEqual(YamlCodes.Nan2))
                    {
                        value = float.NaN;
                        return true;
                    }
                    break;
                case 5:
                    if (span.SequenceEqual(YamlCodes.Inf3) ||
                        span.SequenceEqual(YamlCodes.Inf4) ||
                        span.SequenceEqual(YamlCodes.Inf5))
                    {
                        value = float.PositiveInfinity;
                        return true;
                    }
                    if (span.SequenceEqual(YamlCodes.NegInf0) ||
                        span.SequenceEqual(YamlCodes.NegInf1) ||
                        span.SequenceEqual(YamlCodes.NegInf2))
                    {
                        value = float.NegativeInfinity;
                        return true;
                    }
                    break;
            }
            return false;
        }

        public bool TryGetDouble(out double value)
        {
            if (IsStringScalar())
            {
                value = default;
                return false;
            }

            // Type hint optimization disabled for now - needs more work

            var span = AsSpan();
            if (Utf8Parser.TryParse(span, out value, out var bytesConsumed) &&
                bytesConsumed == span.Length)
            {
                return true;
            }

            switch (span.Length)
            {
                case 4:
                    if (span.SequenceEqual(YamlCodes.Inf0) ||
                        span.SequenceEqual(YamlCodes.Inf1) ||
                        span.SequenceEqual(YamlCodes.Inf2))
                    {
                        value = double.PositiveInfinity;
                        return true;
                    }

                    if (span.SequenceEqual(YamlCodes.Nan0) ||
                        span.SequenceEqual(YamlCodes.Nan1) ||
                        span.SequenceEqual(YamlCodes.Nan2))
                    {
                        value = double.NaN;
                        return true;
                    }
                    break;
                case 5:
                    if (span.SequenceEqual(YamlCodes.Inf3) ||
                        span.SequenceEqual(YamlCodes.Inf4) ||
                        span.SequenceEqual(YamlCodes.Inf5))
                    {
                        value = double.PositiveInfinity;
                        return true;
                    }
                    if (span.SequenceEqual(YamlCodes.NegInf0) ||
                        span.SequenceEqual(YamlCodes.NegInf1) ||
                        span.SequenceEqual(YamlCodes.NegInf2))
                    {
                        value = double.NegativeInfinity;
                        return true;
                    }
                    break;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SequenceEqual(Scalar other)
        {
            return AsSpan().SequenceEqual(other.AsSpan());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SequenceEqual(ReadOnlySpan<byte> span)
        {
            return AsSpan().SequenceEqual(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Grow(int sizeHint)
        {
            if (sizeHint <= buffer.Length)
            {
                return;
            }
            
            // Tiered growth strategy for better memory efficiency
            int growthFactor;
            if (buffer.Length < SmallBufferThreshold)
            {
                growthFactor = 200; // 2x for small buffers
            }
            else if (buffer.Length < MediumBufferThreshold)
            {
                growthFactor = 150; // 1.5x for medium buffers
            }
            else
            {
                growthFactor = 125; // 1.25x for large buffers
            }
            
            var newCapacity = buffer.Length * growthFactor / 100;
            while (newCapacity < sizeHint)
            {
                newCapacity = newCapacity * growthFactor / 100;
            }
            SetCapacity(newCapacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool TryDetectHex(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> slice)
        {
            if (span.Length > YamlCodes.HexPrefix.Length && span.StartsWith(YamlCodes.HexPrefix))
            {
                slice = span[YamlCodes.HexPrefix.Length..];
                return true;
            }

            slice = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool TryDetectHexNegative(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> slice)
        {
            if (span.Length > YamlCodes.HexPrefixNegative.Length &&
                span.StartsWith(YamlCodes.HexPrefixNegative))
            {
                slice = span[YamlCodes.HexPrefixNegative.Length..];
                return true;
            }

            slice = default;
            return false;
        }

        static bool TryParseOctal(ReadOnlySpan<byte> span, out ulong value)
        {
            if (span.Length <= YamlCodes.OctalPrefix.Length ||
                !span.StartsWith(YamlCodes.OctalPrefix))
            {
                value = default;
                return false;
            }
            // we have more characters after the prefix
            var toSkip = YamlCodes.OctalPrefix.Length;
            while (toSkip < span.Length && span[toSkip] == (byte)'0')
            {
                toSkip++;
            }
            if (toSkip >= span.Length)
            {
                // if we skipped at least one zero and consumed all bytes,
                // then it's a valid octal 0
                value = 0;
                return toSkip == span.Length;
            }
            var octalSpan = span[toSkip..];
            // read first digit here, so all next digits run in a loop with bit shift
            var nextChar = octalSpan[0];
            var nextDigit = nextChar - (byte)'0';
            if (nextDigit is < 0 or > 7 ||
                nextDigit > 1 && octalSpan.Length == 22 ||
                octalSpan.Length > 22)
            {
                // there are at most 22 octal digits in a 64-bit unsigned number:
                // 21 * 3 + 1 = 64 // 21 digits of 7 and 1 digit of 1
                // if there are 22, the highest must only be 1 or 0 (and we skipped leading zeros)
                // we will overflow the ulong if we continue
                value = default;
                return false;
            }
            value = (ulong)nextDigit;
            for (int index = 1; index < octalSpan.Length; index++)
            {
                nextChar = octalSpan[index];
                nextDigit = nextChar - (byte)'0';
                if (nextDigit is < 0 or > 7)
                {
                    value = default;
                    return false;
                }
                else
                {
                    value = (value << 3) + (uint)nextDigit;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Grow()
        {
            // Use tiered growth strategy for single grow as well
            int growthFactor;
            if (buffer.Length < SmallBufferThreshold)
            {
                growthFactor = 200; // 2x for small buffers
            }
            else if (buffer.Length < MediumBufferThreshold)
            {
                growthFactor = 150; // 1.5x for medium buffers
            }
            else
            {
                growthFactor = 125; // 1.25x for large buffers
            }
            
            var newCapacity = buffer.Length * growthFactor / 100;
            if (newCapacity < buffer.Length + MinimumGrow)
            {
                newCapacity = buffer.Length + MinimumGrow;
            }
            SetCapacity(newCapacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetCapacity(int newCapacity)
        {
            if (buffer.Length >= newCapacity) return;

            var newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
            Array.Copy(buffer, 0, newBuffer, 0, Length);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = newBuffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsStringScalar()
        {
            return Type is TokenType.DoubleQuotedScaler or TokenType.SingleQuotedScaler;
        }

        /// <summary>
        /// Detects the scalar type based on the first few bytes for optimization
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DetectTypeHint()
        {
            if (IsStringScalar())
            {
                TypeHint = ScalarTypeHint.String;
                return;
            }

            var span = AsSpan();
            if (span.Length == 0)
            {
                TypeHint = ScalarTypeHint.Null;
                return;
            }

            var first = span[0];
            
            // Quick null check
            if (span.Length == 1 && first == YamlCodes.NullAlias)
            {
                TypeHint = ScalarTypeHint.Null;
                return;
            }
            
            // Quick boolean check
            if (span.Length >= 4 && span.Length <= 5)
            {
                if (first == (byte)'t' || first == (byte)'T' || first == (byte)'f' || first == (byte)'F')
                {
                    TypeHint = ScalarTypeHint.Boolean;
                    return;
                }
            }
            
            // Quick null word check - removed as it needs exact matching, not just first letter
            
            // Numeric check
            if (first >= (byte)'0' && first <= (byte)'9' || first == (byte)'-' || first == (byte)'+' || first == (byte)'.')
            {
                // Quick check for float starting with '.'
                if (first == (byte)'.')
                {
                    TypeHint = ScalarTypeHint.Float;
                    return;
                }
                
                // Scan for float indicators
                for (int i = 1; i < span.Length; i++)
                {
                    var b = span[i];
                    if (b == (byte)'.' || b == (byte)'e' || b == (byte)'E')
                    {
                        TypeHint = ScalarTypeHint.Float;
                        return;
                    }
                }
                TypeHint = ScalarTypeHint.Integer;
                return;
            }
            
            TypeHint = ScalarTypeHint.String;
        }
    }
}

