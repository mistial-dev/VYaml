using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using VYaml.Annotations;
using VYaml.Emitter;
using VYaml.Internal;
using VYaml.Parser;

namespace VYaml.Serialization
{
    // TODO:
    static class EnumAsStringNonGenericHelper
    {
        static readonly ConcurrentDictionary<object, string> AliasStringValues = new();
        static readonly ConcurrentDictionary<Type, NamingConvention?> NamingConventionsByType = new();

        static readonly Func<object, Type, string?> AliasStringValueFactory = AnalyzeAliasStringValue;
        static readonly Func<Type, NamingConvention?> NamingConventionFactory = AnalyzeNamingConventionByType;

        public static string? GetAliasStringValue(Type type, object value) => AliasStringValues.GetOrAdd(value, AliasStringValueFactory!, type);
        public static NamingConvention? GetNamingConventionByType(Type type) => NamingConventionsByType.GetOrAdd(type, NamingConventionFactory);

        public static void Serialize(ref Utf8YamlEmitter emitter, Type type, object value, YamlSerializationContext context)
        {
            var aliasStringValue = GetAliasStringValue(type, value);
            if (aliasStringValue != null)
            {
                emitter.WriteString(aliasStringValue);
                return;
            }

            var name = Enum.GetName(type, value)!;
            var namingConvention = GetNamingConventionByType(type) ?? context.Options.NamingConvention;
            var mutator = NamingConventionMutator.Of(namingConvention);
            
            // Try stack allocation for small names
            var bufferSize = PlatformStackLimits.GetInitialBufferSize(name.Length);
            if (PlatformStackLimits.ShouldUseStackAlloc(bufferSize))
            {
                Span<char> destination = stackalloc char[bufferSize];
                if (mutator.TryMutate(name.AsSpan(), destination, out var written))
                {
                    emitter.WriteString(destination[..written].ToString());
                    return;
                }
            }
            
            // Fall back to pooled buffer for larger names or failed attempts
            var buffer = CharBufferPool.Rent(name.Length * 3);
            try
            {
                if (!mutator.TryMutate(name.AsSpan(), buffer, out var written))
                {
                    throw new InvalidOperationException($"Failed to mutate enum name: {name}");
                }
                emitter.WriteString(new string(buffer, 0, written));
            }
            finally
            {
                CharBufferPool.Return(buffer);
            }
        }

        static NamingConvention? AnalyzeNamingConventionByType(Type type)
        {
            return type.GetCustomAttribute<YamlObjectAttribute>()?.NamingConvention;
        }

        static string? AnalyzeAliasStringValue(object value, Type type)
        {
            var name = Enum.GetName(type, value)!;
            var fieldInfo = type.GetField(name)!;

            var attributes = fieldInfo.GetCustomAttributes(inherit: true);
            if (attributes.OfType<EnumMemberAttribute>().FirstOrDefault() is { Value: { } enumMemberValue })
            {
                return enumMemberValue;
            }
            if (attributes.OfType<DataMemberAttribute>().FirstOrDefault() is { Name: { } dataMemberName })
            {
                return dataMemberName;
            }
            return null;
        }
    }

    public class EnumAsStringFormatter<T> : IYamlFormatter<T> where T : Enum
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly NamingConvention? NamingConventionByType;

        static readonly Dictionary<T, (string Value, bool Alias)> StringValues = new();
        static readonly Dictionary<string, T> Values = new();

        static EnumAsStringFormatter()
        {
            var type = typeof(T);
            NamingConventionByType = EnumAsStringNonGenericHelper.GetNamingConventionByType(type);

            foreach (var item in type.GetFields().Where(x => x.FieldType == type))
            {
                var value = item.GetValue(null)!;
                var aliasValue = EnumAsStringNonGenericHelper.GetAliasStringValue(type, value);
                if (aliasValue != null)
                {
                    StringValues.Add((T)value, (aliasValue, true));
                    Values.Add(aliasValue, (T)value);
                }
                else
                {
                    var mutator = NamingConventionMutator.Of(NamingConventionByType ?? YamlSerializerOptions.DefaultNamingConvention);
                    var name = Enum.GetName(type, value)!;
                    
                    // Static constructor runs once, so we can use a larger buffer
                    var buffer = new char[name.Length * 3];
                    if (!mutator.TryMutate(name.AsSpan(), buffer, out var written))
                    {
                        throw new InvalidOperationException($"Failed to mutate enum name: {name}");
                    }
                    
                    var stringValue = new string(buffer, 0, written);
                    StringValues.Add((T)value, (stringValue, false));
                    Values.Add(stringValue, (T)value);
                }
            }
        }

        public void Serialize(ref Utf8YamlEmitter emitter, T value, YamlSerializationContext context)
        {
            if (!StringValues.TryGetValue(value, out var t))
            {
                YamlSerializerException.ThrowInvalidType<T>(value.ToString());
                return;
            }

            var (stringValue, alias) = t;
            if (alias || context.Options.NamingConvention == (NamingConventionByType ?? YamlSerializerOptions.DefaultNamingConvention))
            {
                emitter.WriteString(stringValue);
                return;
            }

            var mutator = NamingConventionMutator.Of(NamingConventionByType ?? context.Options.NamingConvention);
            
            // Try stack allocation first
            var bufferSize = PlatformStackLimits.GetInitialBufferSize(stringValue.Length);
            if (PlatformStackLimits.ShouldUseStackAlloc(bufferSize))
            {
                Span<char> stackBuffer = stackalloc char[bufferSize];
                if (mutator.TryMutate(stringValue.AsSpan(), stackBuffer, out var written))
                {
                    unsafe
                    {
                        fixed (char* ptr = stackBuffer)
                        {
                            emitter.WriteString(ptr, written);
                        }
                    }
                    return;
                }
            }
            
            // Fall back to pooled buffer
            var buffer = CharBufferPool.Rent(stringValue.Length * 3);
            try
            {
                if (!mutator.TryMutate(stringValue.AsSpan(), buffer, out var bytesWritten))
                {
                    throw new InvalidOperationException($"Failed to mutate string value: {stringValue}");
                }
                
                unsafe
                {
                    fixed (char* ptr = buffer)
                    {
                        emitter.WriteString(ptr, bytesWritten);
                    }
                }
            }
            finally
            {
                CharBufferPool.Return(buffer);
            }
        }

        public T Deserialize(ref YamlParser parser, YamlDeserializationContext context)
        {
            var scalar = parser.ReadScalarAsString();
            if (scalar == null)
            {
                YamlSerializerException.ThrowInvalidType<T>("null");
                return default!;
            }

            if (Values.TryGetValue(scalar, out var value))
            {
                return value;
            }


            var mutator = NamingConventionMutator.Of(NamingConventionByType ?? YamlSerializerOptions.DefaultNamingConvention);
            
            string mutatedScalar;
            var bufferSize = PlatformStackLimits.GetInitialBufferSize(scalar.Length);
            if (PlatformStackLimits.ShouldUseStackAlloc(bufferSize))
            {
                Span<char> stackBuffer = stackalloc char[bufferSize];
                if (mutator.TryMutate(scalar.AsSpan(), stackBuffer, out var written))
                {
                    mutatedScalar = stackBuffer[..written].ToString();
                }
                else
                {
                    // Fall back to pooled buffer
                    var pooledBuffer = CharBufferPool.Rent(scalar.Length * 3);
                    try
                    {
                        if (!mutator.TryMutate(scalar.AsSpan(), pooledBuffer, out written))
                        {
                            throw new InvalidOperationException($"Failed to mutate scalar: {scalar}");
                        }
                        mutatedScalar = new string(pooledBuffer, 0, written);
                    }
                    finally
                    {
                        CharBufferPool.Return(pooledBuffer);
                    }
                }
            }
            else
            {
                // Use pooled buffer for large scalars
                var buffer = CharBufferPool.Rent(scalar.Length * 3);
                try
                {
                    if (!mutator.TryMutate(scalar.AsSpan(), buffer, out var bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to mutate scalar: {scalar}");
                    }
                    mutatedScalar = new string(buffer, 0, bytesWritten);
                }
                finally
                {
                    CharBufferPool.Return(buffer);
                }
            }
            parser.Read();
            if (Values.TryGetValue(mutatedScalar, out value))
            {
                return value;
            }
            YamlSerializerException.ThrowInvalidType<T>(mutatedScalar);
            return default!;
        }
    }
}