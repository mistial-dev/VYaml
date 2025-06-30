using System;
using System.Buffers;

namespace VYaml.Internal
{
    /// <summary>
    /// Provides efficient character buffer management with platform-specific optimizations.
    /// Uses thread-static caching on mobile platforms to minimize allocations.
    /// </summary>
    internal static class CharBufferPool
    {
#if UNITY_ANDROID || UNITY_IOS
        // Mobile platforms: Use thread-static buffers to avoid repeated allocations
        [ThreadStatic]
        private static char[]? t_mobileBuffer;
        
        /// <summary>
        /// Rents a character buffer of at least the specified length.
        /// On mobile platforms, uses a thread-static buffer when possible.
        /// </summary>
        public static char[] Rent(int minimumLength)
        {
            // Use thread-static buffer if it's large enough
            if (t_mobileBuffer != null && t_mobileBuffer.Length >= minimumLength)
            {
                var buffer = t_mobileBuffer;
                t_mobileBuffer = null;
                return buffer;
            }
            
            // Allocate new buffer with some headroom
            var size = Math.Max(minimumLength, PlatformStackLimits.PooledBufferSize);
            return new char[size];
        }
        
        /// <summary>
        /// Returns a rented buffer to the pool.
        /// On mobile platforms, caches the buffer for reuse.
        /// </summary>
        public static void Return(char[] buffer)
        {
            if (buffer == null) return;
            
            // Keep the largest buffer for reuse
            if (t_mobileBuffer == null || buffer.Length > t_mobileBuffer.Length)
            {
                t_mobileBuffer = buffer;
            }
        }
#else
        // Desktop platforms: Use ArrayPool for better scalability
        
        /// <summary>
        /// Rents a character buffer of at least the specified length.
        /// Uses ArrayPool on desktop platforms.
        /// </summary>
        public static char[] Rent(int minimumLength)
        {
            return ArrayPool<char>.Shared.Rent(minimumLength);
        }
        
        /// <summary>
        /// Returns a rented buffer to the pool.
        /// </summary>
        public static void Return(char[] buffer)
        {
            if (buffer != null)
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
#endif
    }
}