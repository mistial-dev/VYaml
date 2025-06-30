using System;

namespace VYaml.Internal
{
    /// <summary>
    /// Provides platform-specific constants for stack allocation limits and buffer sizes.
    /// These values are tuned based on typical stack sizes and memory constraints for each platform.
    /// </summary>
    internal static class PlatformStackLimits
    {
#if UNITY_ANDROID || UNITY_IOS
        // Mobile platforms have very limited stack space (512KB-1MB)
        // Be extremely conservative to avoid stack overflow
        
        /// <summary>
        /// Initial size for enum name allocations on mobile platforms.
        /// </summary>
        public const int InitialEnumNameSize = 32;
        
        /// <summary>
        /// Maximum size for stack allocations on mobile platforms.
        /// </summary>
        public const int MaxStackAllocSize = 128;
        
        /// <summary>
        /// Size for pooled buffers when stack allocation is not suitable.
        /// </summary>
        public const int PooledBufferSize = 256;
        
        /// <summary>
        /// Maximum iterations for retry loops to prevent runaway allocations.
        /// </summary>
        public const int MaxRetryIterations = 2;

#elif UNITY_2018_3_OR_NEWER
        // Unity Desktop has moderate stack limits
        // Balance between performance and safety
        
        /// <summary>
        /// Initial size for enum name allocations on Unity desktop.
        /// </summary>
        public const int InitialEnumNameSize = 64;
        
        /// <summary>
        /// Maximum size for stack allocations on Unity desktop.
        /// </summary>
        public const int MaxStackAllocSize = 256;
        
        /// <summary>
        /// Size for pooled buffers when stack allocation is not suitable.
        /// </summary>
        public const int PooledBufferSize = 512;
        
        /// <summary>
        /// Maximum iterations for retry loops to prevent runaway allocations.
        /// </summary>
        public const int MaxRetryIterations = 3;

#else
        // Full .NET runtime on desktop/server has larger stacks (1MB-8MB)
        // Optimize for performance with larger allocations
        
        /// <summary>
        /// Initial size for enum name allocations on desktop .NET.
        /// </summary>
        public const int InitialEnumNameSize = 128;
        
        /// <summary>
        /// Maximum size for stack allocations on desktop .NET.
        /// </summary>
        public const int MaxStackAllocSize = 512;
        
        /// <summary>
        /// Size for pooled buffers when stack allocation is not suitable.
        /// </summary>
        public const int PooledBufferSize = 1024;
        
        /// <summary>
        /// Maximum iterations for retry loops to prevent runaway allocations.
        /// </summary>
        public const int MaxRetryIterations = 3;
#endif

        /// <summary>
        /// Gets the appropriate initial buffer size for the given input length.
        /// </summary>
        /// <param name="inputLength">The length of the input string.</param>
        /// <returns>A buffer size that should accommodate most naming convention transformations.</returns>
        public static int GetInitialBufferSize(int inputLength)
        {
            // Most naming convention changes add at most 50% more characters
            // (e.g., "FooBar" -> "foo_bar")
            var estimatedSize = inputLength + (inputLength / 2);
            
            // Clamp to platform limits
            return Math.Min(estimatedSize, MaxStackAllocSize);
        }
        
        /// <summary>
        /// Determines if stack allocation should be used for the given size.
        /// </summary>
        /// <param name="size">The requested allocation size in characters.</param>
        /// <returns>True if stack allocation is appropriate, false if heap/pool should be used.</returns>
        public static bool ShouldUseStackAlloc(int size)
        {
            return size <= MaxStackAllocSize;
        }
    }
}