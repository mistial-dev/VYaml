#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace VYaml.Internal
{
    class InsertionQueue<T>
    {
        const int MinimumGrow = 4;
        const int GrowFactor = 200;

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            private set;
        }

        T[] array;
        int headIndex;
        int tailIndex;

        public InsertionQueue(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException("capacity");
            // Ensure capacity is power of 2 for fast bit masking
            capacity = GetNextPowerOfTwo(capacity);
            array = new T[capacity];
            headIndex = tailIndex = Count = 0;
        }

        public void Clear()
        {
            headIndex = tailIndex = Count = 0;
        }

        public T Peek()
        {
            if (Count == 0) ThrowForEmptyQueue();
            return array[headIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(T item)
        {
            if (Count == array.Length)
            {
                Grow();
            }

            array[tailIndex] = item;
            MoveNext(ref tailIndex);
            Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Dequeue()
        {
            if (Count == 0) ThrowForEmptyQueue();

            var removed = array[headIndex];
            MoveNext(ref headIndex);
            Count--;
            return removed;
        }

        public void Insert(int posTo, T item)
        {
            if (Count == array.Length)
            {
                Grow();
            }

            MoveNext(ref tailIndex);
            Count++;

            var mask = array.Length - 1;
            for (var pos = Count - 1; pos > posTo; pos--)
            {
                var index = (headIndex + pos) & mask;
                var indexPrev = index == 0 ? array.Length - 1 : index - 1;
                array[index] = array[indexPrev];
            }
            array[(posTo + headIndex) & mask] = item;
        }

        void Grow()
        {
            // Always double for power-of-2 growth
            var newCapacity = array.Length * 2;
            SetCapacity(newCapacity);
        }

        void SetCapacity(int capacity)
        {
            var newArray = new T[capacity];
            if (Count > 0)
            {
                if (headIndex < tailIndex)
                {
                    Array.Copy(array, headIndex, newArray, 0, Count);
                }
                else
                {
                    Array.Copy(array, headIndex, newArray, 0, array.Length - headIndex);
                    Array.Copy(array, 0, newArray, array.Length - headIndex, tailIndex);
                }
            }

            array = newArray;
            headIndex = 0;
            tailIndex = Count == capacity ? 0 : Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void MoveNext(ref int index)
        {
            // Use bit masking instead of modulo for power-of-2 sizes
            index = (index + 1) & (array.Length - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ThrowForEmptyQueue()
        {
            throw new InvalidOperationException("EmptyQueue");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int GetNextPowerOfTwo(int value)
        {
            if (value < 2) return 2;
            
            // Find the next power of 2
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;
            
            return value;
        }
    }
}

