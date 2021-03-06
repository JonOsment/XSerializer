using System;
using System.Collections;
using System.Collections.Concurrent;

namespace XSerializer
{
    internal sealed class DynamicJsonSerializer : IJsonSerializerInternal
    {
        private static readonly ConcurrentDictionary<Tuple<bool, JsonMappings>, DynamicJsonSerializer> _cache = new ConcurrentDictionary<Tuple<bool, JsonMappings>, DynamicJsonSerializer>();
        private static readonly ConcurrentDictionary<Tuple<Type, bool>, IJsonSerializerInternal> _serializerCache = new ConcurrentDictionary<Tuple<Type, bool>, IJsonSerializerInternal>();

        private readonly JsonObjectSerializer _jsonObjectSerializer;
        private readonly JsonArraySerializer _jsonArraySerializer;

        private readonly bool _encrypt;
        private readonly JsonMappings _mappings;

        private DynamicJsonSerializer(bool encrypt, JsonMappings mappings)
        {
            _jsonObjectSerializer = new JsonObjectSerializer(this);
            _jsonArraySerializer = new JsonArraySerializer(this);

            _encrypt = encrypt;
            _mappings = mappings;
        }

        public static DynamicJsonSerializer Get(bool encrypt, JsonMappings mappings)
        {
            return _cache.GetOrAdd(Tuple.Create(encrypt, mappings), t => new DynamicJsonSerializer(t.Item1, t.Item2));
        }

        public void SerializeObject(JsonWriter writer, object instance, IJsonSerializeOperationInfo info)
        {
            if (instance == null)
            {
                writer.WriteNull();
            }
            else
            {
                var serializer = _serializerCache.GetOrAdd(Tuple.Create(instance.GetType(), _encrypt), tuple => GetSerializer(tuple.Item1));

                if (_encrypt)
                {
                    var toggler = new EncryptWritesToggler(writer);
                    toggler.Toggle();

                    serializer.SerializeObject(writer, instance, info);

                    toggler.Revert();
                }
                else
                {
                    serializer.SerializeObject(writer, instance, info);
                }
            }
        }

        private IJsonSerializerInternal GetSerializer(Type concreteType)
        {
            // Note that no checks are made for nullable types. The concreteType parameter is retrieved
            // from a call to .GetType() on a non-null instance.

            if (concreteType == typeof(string)
                || concreteType == typeof(DateTime)
                || concreteType == typeof(DateTimeOffset)
                || concreteType == typeof(Guid))
            {
                return StringJsonSerializer.Get(concreteType, _encrypt);
            }

            if (concreteType == typeof(double)
                || concreteType == typeof(int)
                || concreteType == typeof(float)
                || concreteType == typeof(long)
                || concreteType == typeof(decimal)
                || concreteType == typeof(byte)
                || concreteType == typeof(sbyte)
                || concreteType == typeof(short)
                || concreteType == typeof(ushort)
                || concreteType == typeof(uint)
                || concreteType == typeof(ulong))
            {
                return NumberJsonSerializer.Get(concreteType, _encrypt);
            }
            
            if (concreteType == typeof(bool))
            {
                return BooleanJsonSerializer.Get(_encrypt, false);
            }

            if (concreteType == typeof(JsonObject))
            {
                return _jsonObjectSerializer;
            }

            if (concreteType == typeof(JsonArray))
            {
                return _jsonArraySerializer;
            }
            
            if (concreteType.IsAssignableToGenericIDictionaryOfStringToAnything())
            {
                return DictionaryJsonSerializer.Get(concreteType, _encrypt, _mappings);
            }
            
            if (typeof(IEnumerable).IsAssignableFrom(concreteType))
            {
                return ListJsonSerializer.Get(concreteType, _encrypt, _mappings);
            }

            return CustomJsonSerializer.Get(concreteType, _encrypt, _mappings);
        }

        public object DeserializeObject(JsonReader reader, IJsonSerializeOperationInfo info)
        {
            if (!reader.ReadContent())
            {
                throw new XSerializerException("Unexpected end of input while attempting to parse beginning of value.");
            }

            if (_encrypt)
            {
                var toggler = new DecryptReadsToggler(reader);
                toggler.Toggle();

                try
                {
                    return Read(reader, info);
                }
                finally
                {
                    toggler.Revert();
                }
            }

            return Read(reader, info);
        }

        private object Read(JsonReader reader, IJsonSerializeOperationInfo info)
        {
            switch (reader.NodeType)
            {
                case JsonNodeType.Null:
                case JsonNodeType.String:
                case JsonNodeType.Boolean:
                    return reader.Value;
                case JsonNodeType.Number:
                    return new JsonNumber((string)reader.Value);
                case JsonNodeType.OpenObject:
                    return DeserializeJsonObject(reader, info);
                case JsonNodeType.OpenArray:
                    return DeserializeJsonArray(reader, info);
                default:
                    throw new XSerializerException("Invalid json.");
            }
        }

        private object DeserializeJsonObject(JsonReader reader, IJsonSerializeOperationInfo info)
        {
            var jsonObject = new JsonObject(info);

            foreach (var propertyName in reader.ReadProperties())
            {
                jsonObject.Add(propertyName, DeserializeObject(reader, info));
            }

            return jsonObject;
        }

        private object DeserializeJsonArray(JsonReader reader, IJsonSerializeOperationInfo info)
        {
            var jsonArray = new JsonArray(info);

            while (true)
            {
                if (reader.PeekNextNodeType() == JsonNodeType.CloseArray)
                {
                    // If the next content is CloseArray, read it and return the empty list.
                    reader.Read();
                    return jsonArray;
                }

                jsonArray.Add(DeserializeObject(reader, info));

                if (!reader.ReadContent())
                {
                    throw new XSerializerException("Unexpected end of input while attempting to parse ',' character.");
                }

                if (reader.NodeType == JsonNodeType.CloseArray)
                {
                    break;
                }

                if (reader.NodeType != JsonNodeType.ItemSeparator)
                {
                    throw new XSerializerException("Unexpected node type found while attempting to parse ',' character: " +
                                                   reader.NodeType + ".");
                }
            }

            return jsonArray;
        }

        private class JsonObjectSerializer : IJsonSerializerInternal
        {
            private readonly DynamicJsonSerializer _dynamicJsonSerializer;

            public JsonObjectSerializer(DynamicJsonSerializer dynamicJsonSerializer)
            {
                _dynamicJsonSerializer = dynamicJsonSerializer;
            }

            public void SerializeObject(JsonWriter writer, object instance, IJsonSerializeOperationInfo info)
            {
                var jsonObject = (JsonObject)instance;
                
                writer.WriteOpenObject();

                var first = true;

                foreach (var item in jsonObject)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        writer.WriteItemSeparator();
                    }

                    writer.WriteValue(item.Key);
                    writer.WriteNameValueSeparator();
                    _dynamicJsonSerializer.SerializeObject(writer, item.Value, info);
                }

                writer.WriteCloseObject();
            }

            public object DeserializeObject(JsonReader reader, IJsonSerializeOperationInfo info)
            {
                throw new NotImplementedException();
            }
        }

        private class JsonArraySerializer : IJsonSerializerInternal
        {
            private readonly DynamicJsonSerializer _dynamicJsonSerializer;

            public JsonArraySerializer(DynamicJsonSerializer dynamicJsonSerializer)
            {
                _dynamicJsonSerializer = dynamicJsonSerializer;
            }

            public void SerializeObject(JsonWriter writer, object instance, IJsonSerializeOperationInfo info)
            {
                var jsonArray = (JsonArray)instance;

                writer.WriteOpenArray();

                var first = true;

                foreach (var item in jsonArray)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        writer.WriteItemSeparator();
                    }

                    _dynamicJsonSerializer.SerializeObject(writer, item, info);
                }

                writer.WriteCloseArray();
            }

            public object DeserializeObject(JsonReader reader, IJsonSerializeOperationInfo info)
            {
                throw new NotImplementedException();
            }
        }
    }
}