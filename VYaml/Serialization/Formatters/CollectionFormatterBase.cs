using System.Collections.Generic;
using VYaml.Emitter;
using VYaml.Parser;

namespace VYaml.Serialization
{
    public abstract class CollectionFormatterBase<TElement, TIntermediate, TCollection>
        : IYamlFormatter<TCollection?>
        where TCollection : IEnumerable<TElement>
    {
        public void Serialize(ref Utf8YamlEmitter emitter, TCollection? value, YamlSerializationContext context)
        {
            if (value is null)
            {
                emitter.WriteNull();
            }
            else
            {
                emitter.BeginSequence();
                var count = GetCount(value);
                if (count > 0)
                {
                    // Cache formatter lookup outside the loop
                    var elementFormatter = context.Resolver.GetFormatterWithVerify<TElement>();
                    
                    // For collections with known count, we can optimize further
                    if (count.HasValue && value is IList<TElement> list)
                    {
                        // Direct indexing is often faster than foreach for lists
                        for (int i = 0; i < count.Value; i++)
                        {
                            elementFormatter.Serialize(ref emitter, list[i], context);
                        }
                    }
                    else
                    {
                        foreach (var x in value)
                        {
                            elementFormatter.Serialize(ref emitter, x, context);
                        }
                    }
                }
                emitter.EndSequence();
            }

        }

        public TCollection? Deserialize(ref YamlParser parser, YamlDeserializationContext context)
        {
            if (parser.IsNullScalar())
            {
                parser.Read();
                return default;
            }

            parser.ReadWithVerify(ParseEventType.SequenceStart);

            var list = Create(context.Options);
            var elementFormatter = context.Resolver.GetFormatterWithVerify<TElement>();
            
            // Pre-size collections if possible to avoid resizing
            if (list is List<TElement> concreteList)
            {
                concreteList.Capacity = 16; // Reasonable default to avoid initial resizes
            }
            
            while (!parser.End && parser.CurrentEventType != ParseEventType.SequenceEnd)
            {
                var value = context.DeserializeWithAlias(elementFormatter, ref parser);
                Add(list, value, context.Options);
            }
            parser.ReadWithVerify(ParseEventType.SequenceEnd);
            return Complete(list);
        }

        // abstraction for serialize
        protected virtual int? GetCount(TCollection sequence)
        {
            if (sequence is ICollection<TElement> collection)
            {
                return collection.Count;
            }

            if (sequence is IReadOnlyCollection<TElement> readonlyCollection)
            {
                return readonlyCollection.Count;
            }

            return null;
        }


        // abstraction for deserialize
        protected abstract TIntermediate Create(YamlSerializerOptions options);
        protected abstract void Add(TIntermediate collection, TElement value, YamlSerializerOptions options);
        protected abstract TCollection Complete(TIntermediate intermediateCollection);
    }
}